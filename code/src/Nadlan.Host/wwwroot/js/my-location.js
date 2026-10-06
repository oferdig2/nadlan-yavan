// "My location": centres the map on where the user stands (browser geolocation) and keeps a blue dot there while the
// page is open. Browsers allow it only on https:// (or localhost) and after the user says yes.
//   createLocator(onFix, onError)          - the geolocation part, shared by the map and the presentation
//   createMyLocationControl(map, options)  - the round button on a google.maps.Map, with the dot and accuracy circle
//   containsPoint(geometry, lat, lng)      - is the point inside a GeoJSON Polygon (holes excluded)
//   standingOn(position, items, geometriesOf) - the items under the user, if the fix is good enough to say
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function errorMessage(err) {
    switch (err && err.code) {
      case 1: return "Location is blocked for this site. Allow it in the browser's site settings (the icon left of the address), then try again.";
      case 2: return "Your location isn't available right now (no GPS or Wi-Fi fix).";
      case 3: return "Finding your location took too long - try again (outdoors works best).";
      default: return "Your location couldn't be found.";
    }
  }

  // GPS is a battery drain on a phone or iPad in a meeting: tracking ends this long after the last button press, and
  // pauses while the page is in the background.
  var TRACK_FOR_MS = 5 * 60 * 1000;

  /**
   * @param {function({ lat: number, lng: number, accuracy: number }): void} onFix  every new position
   * @param {function(string): void} onError  a message for the user (only while no position is known, or when blocked)
   * @param {function(): void=} onStopped  tracking ended by itself (time up); the last position is now old
   */
  function createLocator(onFix, onError, onStopped) {
    var watchId = null, last = null, stopTimer = null, active = false;

    function watch() {
      if (watchId !== null) { return; }
      watchId = navigator.geolocation.watchPosition(function (pos) {
        last = { lat: pos.coords.latitude, lng: pos.coords.longitude, accuracy: pos.coords.accuracy || 0 };
        onFix(last);
      }, function (err) {
        if (err.code === 1) { stop(); last = null; onError(errorMessage(err)); return; }
        if (!last) { onError(errorMessage(err)); } // a slow update once we have a position is not worth a message
      }, { enableHighAccuracy: true, maximumAge: 10000, timeout: 20000 });
    }
    function unwatch() {
      if (watchId !== null) { navigator.geolocation.clearWatch(watchId); watchId = null; }
    }
    function stop() {
      active = false;
      clearTimeout(stopTimer);
      unwatch();
    }

    document.addEventListener("visibilitychange", function () {
      if (!active) { return; }
      if (document.hidden) { unwatch(); } else { watch(); }
    });

    return {
      /** Starts (or keeps) tracking for the next few minutes. False when location can't be used here at all. */
      start: function () {
        if (!window.isSecureContext) { onError("Your location needs the site's secure address (https://)."); return false; }
        if (!navigator.geolocation) { onError("This browser can't share its location."); return false; }
        if (!active) { last = null; } // after a stop the old position is stale: wait for a fresh one
        active = true;
        clearTimeout(stopTimer);
        stopTimer = setTimeout(function () { stop(); if (onStopped) { onStopped(); } }, TRACK_FOR_MS);
        watch();
        return true;
      },
      stop: stop,
      last: function () { return last; }
    };
  }

  // Naming the Parcel under the user needs a good fix: at ± 100 m a phone could be on any of a dozen neighbours.
  var STANDING_MAX_ACCURACY = 30;

  /**
   * Which of the items the position falls in. { rough: true } when the fix is too uncertain to say; otherwise
   * { hits: [items] } - none, one, or several where Parcels overlap (all named, not just the first).
   * @param {function(object): Array} geometriesOf  an item's GeoJSON Polygons
   */
  function standingOn(p, items, geometriesOf) {
    if (p.accuracy > STANDING_MAX_ACCURACY) { return { rough: true, hits: [] }; }
    return {
      rough: false,
      hits: items.filter(function (it) {
        return geometriesOf(it).some(function (g) { return containsPoint(g, p.lat, p.lng); });
      })
    };
  }

  /**
   * Round button above the zoom buttons. A click centres on the user (zoomed in, or to the accuracy circle when the
   * fix is rough, e.g. Wi-Fi on a desktop); the dot then follows them.
   * @param {google.maps.Map} map
   * @param {{ onError: function(string): void, onLocated?: function(object): void }} options  onLocated after each centring
   */
  function createMyLocationControl(map, options) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "map-locate";
    button.title = "Show where I am";
    button.setAttribute("aria-label", "Show where I am");
    button.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"4\" fill=\"currentColor\"/>" +
      "<path d=\"M12 2v3M12 19v3M2 12h3M19 12h3\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"/>" +
      "<circle cx=\"12\" cy=\"12\" r=\"7.5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>";
    map.controls[google.maps.ControlPosition.RIGHT_BOTTOM].push(button);

    var dot = null, ring = null, wantCenter = false;

    function center(p) {
      var here = { lat: p.lat, lng: p.lng };
      if (p.accuracy > 150) { map.fitBounds(ring.getBounds()); } else { map.panTo(here); if (map.getZoom() < 18) { map.setZoom(18); } }
      if (options.onLocated) { options.onLocated(p); }
    }

    function dotIcon(live) {
      return { path: google.maps.SymbolPath.CIRCLE, scale: 8, fillColor: live ? "#2563eb" : "#94a3b8", fillOpacity: 1, strokeColor: "#ffffff", strokeWeight: 3 };
    }

    var locator = createLocator(function (p) {
      var here = { lat: p.lat, lng: p.lng };
      if (!dot) {
        ring = new google.maps.Circle({ map: map, center: here, radius: p.accuracy, clickable: false, zIndex: 50,
          strokeColor: "#2563eb", strokeOpacity: 0.5, strokeWeight: 1, fillColor: "#3b82f6", fillOpacity: 0.15 });
        dot = new google.maps.Marker({ map: map, position: here, clickable: false, zIndex: 9999, title: "You are here", icon: dotIcon(true) });
      } else {
        dot.setPosition(here);
        dot.setIcon(dotIcon(true));
        ring.setCenter(here);
        ring.setRadius(p.accuracy);
      }
      button.classList.remove("busy");
      button.classList.add("on");
      if (wantCenter) { wantCenter = false; center(p); }
    }, function (message) {
      wantCenter = false;
      button.classList.remove("busy", "on");
      options.onError(message);
    }, function () {
      // Tracking ended (battery): the dot turns grey - where the user was, not where they are.
      button.classList.remove("on");
      if (dot) { dot.setIcon(dotIcon(false)); dot.setTitle("Your last known position - press the button to follow again"); }
    });

    button.addEventListener("click", function () {
      if (!locator.start()) { return; } // (re)starts tracking; after a stop the old position is dropped
      var known = locator.last();
      if (known && dot) { center(known); } else { wantCenter = true; button.classList.add("busy"); }
    });

    return { button: button, last: locator.last };
  }

  // Ray casting on [lon, lat] rings; inside the outer ring and in none of the holes.
  function inRing(ring, x, y) {
    var inside = false;
    for (var i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      var xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
      if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) { inside = !inside; }
    }
    return inside;
  }

  function containsPoint(geometry, lat, lng) {
    var rings = geometry && geometry.coordinates;
    if (!rings || !rings.length || !inRing(rings[0], lng, lat)) { return false; }
    for (var h = 1; h < rings.length; h++) { if (inRing(rings[h], lng, lat)) { return false; } }
    return true;
  }

  Nadlan.myLocation = { createLocator: createLocator, createMyLocationControl: createMyLocationControl, containsPoint: containsPoint,
    standingOn: standingOn, STANDING_MAX_ACCURACY: STANDING_MAX_ACCURACY };
})(window);
