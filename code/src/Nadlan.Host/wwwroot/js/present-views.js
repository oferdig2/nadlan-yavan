// The two map views of the customer presentation, behind one interface:
//   create3dView - Google's photorealistic 3D (Maps JavaScript "maps3d": Map3DElement), the show-off view
//   create2dView - the satellite map, when 3D can't load (key/API not enabled, old browser, no WebGL)
// Both: { showAll(): Promise, fly(i): Promise, orbit(i, ms): Promise, stop(), highlight(i), showMe(position, fly): Promise }.
// Items come from GET /api/presentation: { polygons: [GeoJSON Polygon], statusColor, ... }.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function safeColor(c) { return /^#[0-9a-fA-F]{6}$/.test(c || "") ? c : "#f59e0b"; }

  function rgba(hex, alpha) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + (n >> 16) + "," + (n >> 8 & 255) + "," + (n & 255) + "," + alpha + ")";
  }

  function boundsOf(polygons) {
    var b = { west: 180, south: 90, east: -180, north: -90 };
    polygons.forEach(function (g) {
      g.coordinates[0].forEach(function (p) {
        b.west = Math.min(b.west, p[0]); b.east = Math.max(b.east, p[0]);
        b.south = Math.min(b.south, p[1]); b.north = Math.max(b.north, p[1]);
      });
    });
    return b;
  }

  function allBounds(items) {
    return boundsOf(items.reduce(function (all, it) { return all.concat(it.polygons); }, []));
  }

  // Size of a box in metres (good enough at these scales).
  function diagonalMetres(b) {
    var lat = (b.south + b.north) / 2 * Math.PI / 180;
    var dx = (b.east - b.west) * 111320 * Math.cos(lat), dy = (b.north - b.south) * 110540;
    return Math.sqrt(dx * dx + dy * dy);
  }

  function centerOf(b) { return { lat: (b.south + b.north) / 2, lng: (b.west + b.east) / 2 }; }

  // A camera that frames the box, looking down at 60° from the south-west-ish (heading 30°).
  function cameraFor(b, wide) {
    var c = centerOf(b);
    var range = Math.min(60000, Math.max(wide ? 600 : 220, diagonalMetres(b) * (wide ? 1.6 : 3.2)));
    return { center: { lat: c.lat, lng: c.lng, altitude: 0 }, range: range, tilt: wide ? 50 : 62, heading: 30 };
  }

  function ring(coords) { return coords.map(function (p) { return { lat: p[1], lng: p[0] }; }); }

  // Resolves when the map reports the animation ended, or after the expected time (the event isn't guaranteed).
  function animation(target, start, ms) {
    return new Promise(function (resolve) {
      var done = false;
      function end() {
        if (done) { return; }
        done = true;
        target.removeEventListener("gmp-animationend", end);
        resolve();
      }
      target.addEventListener("gmp-animationend", end);
      setTimeout(end, ms + 2000);
      try { start(); } catch (e) { end(); }
    });
  }

  /**
   * @param {object} lib    google.maps.importLibrary("maps3d")
   * @param {HTMLElement} el
   * @param {Array} items
   * @param {{ onPick: function(number): void }} options
   */
  function create3dView(lib, el, items, options) {
    var overview = cameraFor(allBounds(items), true);
    var map = new lib.Map3DElement({ center: overview.center, range: overview.range, tilt: overview.tilt, heading: overview.heading });
    if (lib.MapMode && "mode" in map) { map.mode = lib.MapMode.HYBRID; } // place names and roads over the 3D imagery
    el.appendChild(map);

    var clampToGround = lib.AltitudeMode ? lib.AltitudeMode.CLAMP_TO_GROUND : "CLAMP_TO_GROUND";
    var relativeToGround = lib.AltitudeMode ? lib.AltitudeMode.RELATIVE_TO_GROUND : "RELATIVE_TO_GROUND";
    var Polygon = lib.Polygon3DInteractiveElement || lib.Polygon3DElement;
    var Marker = lib.Marker3DInteractiveElement || lib.Marker3DElement;
    var shapes = [];

    items.forEach(function (it, i) {
      var color = safeColor(it.statusColor);
      var mine = [];
      it.polygons.forEach(function (g) {
        var poly = new Polygon({ strokeColor: color, strokeWidth: 4, fillColor: rgba(color, 0.3), altitudeMode: clampToGround, drawsOccludedSegments: true });
        // The property names changed during Google's beta (outer/innerCoordinates -> path/innerPaths): set what exists.
        if ("path" in poly) { poly.path = ring(g.coordinates[0]); } else { poly.outerCoordinates = ring(g.coordinates[0]); }
        if (g.coordinates.length > 1) {
          var holes = g.coordinates.slice(1).map(ring);
          if ("innerPaths" in poly) { poly.innerPaths = holes; } else { poly.innerCoordinates = holes; }
        }
        if (lib.Polygon3DInteractiveElement) { poly.addEventListener("gmp-click", function () { options.onPick(i); }); }
        map.append(poly);
        mine.push({ poly: poly, color: color });
      });

      // A numbered pin above the Parcel, so it can be found (and clicked) from far away.
      var c = centerOf(boundsOf(it.polygons));
      var marker = new Marker({ position: { lat: c.lat, lng: c.lng, altitude: 35 }, altitudeMode: relativeToGround, extruded: true, label: String(i + 1) });
      if (lib.Marker3DInteractiveElement) { marker.addEventListener("gmp-click", function () { options.onPick(i); }); }
      map.append(marker);
      shapes.push(mine);
    });

    function orbitOptions(camera, ms) {
      return { camera: camera, durationMillis: ms, repeatCount: 1 };
    }

    var me = null; // "you are here" pin
    var MePin = lib.Marker3DElement || lib.Marker3DInteractiveElement;

    // Loading the 3D library is not enough: without graphics acceleration, with the Map Tiles API off on the key, or
    // with its quota used up, the element stays black. Only a fully drawn view ("steady") proves it works - a "not
    // steady yet" can come from a globe that will never draw.
    var steady = false, failed = false, onErrors = [];
    map.addEventListener("gmp-steadychange", function (e) {
      if (e.isSteady || (e.detail && e.detail.isSteady)) { steady = true; }
    });
    map.addEventListener("gmp-error", function () {
      failed = true;
      onErrors.forEach(function (f) { f(); });
    });

    return {
      kind: "3d",
      /**
       * True once the 3D map has drawn a view; false on an error, or when it hasn't within ms. Only time while the
       * tab is visible counts: a background tab doesn't draw, which says nothing about 3D working.
       */
      ready: function (ms) {
        return new Promise(function (resolve) {
          var waited = 0;
          (function poll() {
            if (failed) { resolve(false); return; }
            if (steady) { resolve(true); return; }
            if (!document.hidden) { waited += 250; }
            if (waited > ms) { resolve(false); return; }
            setTimeout(poll, 250);
          })();
        });
      },
      /** An error after it worked (e.g. quota used up mid-meeting): the page then switches to the satellite map. */
      onError: function (f) { onErrors.push(f); },
      destroy: function () { onErrors = []; map.remove(); },
      /** The viewer's own position (my-location.js); fly = also bring the camera there. */
      showMe: function (p, fly) {
        var position = { lat: p.lat, lng: p.lng, altitude: 3 };
        if (!me) {
          me = new MePin({ position: position, altitudeMode: relativeToGround, extruded: true, label: "You" });
          map.append(me);
        } else {
          me.position = position;
        }
        if (!fly) { return Promise.resolve(); }
        var cam = { center: { lat: p.lat, lng: p.lng, altitude: 0 }, range: Math.min(20000, Math.max(350, p.accuracy * 4)), tilt: 60, heading: 30 };
        return animation(map, function () { map.flyCameraTo({ endCamera: cam, durationMillis: 2500 }); }, 2500);
      },
      showAll: function () {
        return animation(map, function () { map.flyCameraTo({ endCamera: overview, durationMillis: 3000 }); }, 3000);
      },
      fly: function (i) {
        var cam = cameraFor(boundsOf(items[i].polygons), false);
        return animation(map, function () { map.flyCameraTo({ endCamera: cam, durationMillis: 3500 }); }, 3500);
      },
      orbit: function (i, ms) {
        var cam = cameraFor(boundsOf(items[i].polygons), false);
        return animation(map, function () {
          try { map.flyCameraAround(orbitOptions(cam, ms)); } catch (e) { map.flyCameraAround({ camera: cam, durationMillis: ms, rounds: 1 }); } // older beta
        }, ms);
      },
      stop: function () { if (map.stopCameraAnimation) { map.stopCameraAnimation(); } },
      highlight: function (index) {
        shapes.forEach(function (mine, i) {
          mine.forEach(function (s) {
            s.poly.strokeWidth = i === index ? 7 : 4;
            s.poly.fillColor = rgba(s.color, i === index ? 0.45 : 0.25);
          });
        });
      }
    };
  }

  /** The satellite map (google.maps, already loaded): same interface, no 3D flight. */
  function create2dView(el, items, options) {
    var map = new google.maps.Map(el, { mapTypeId: "hybrid", streetViewControl: false, fullscreenControl: false, gestureHandling: "greedy",
      mapTypeControl: false, tilt: 45 });
    var layer = new google.maps.Data({ map: map });
    var highlighted = -1;
    items.forEach(function (it, i) {
      it.polygons.forEach(function (g) {
        layer.add(new google.maps.Data.Feature({ geometry: new google.maps.Data.Polygon(g.coordinates.map(ring)), properties: { index: i, color: safeColor(it.statusColor) } }));
      });
      var c = centerOf(boundsOf(it.polygons));
      var marker = new google.maps.Marker({ map: map, position: c, label: { text: String(i + 1), color: "#fff", fontWeight: "700" } });
      marker.addListener("click", function () { options.onPick(i); });
    });
    layer.setStyle(function (f) {
      var on = f.getProperty("index") === highlighted;
      return { strokeColor: f.getProperty("color"), strokeWeight: on ? 5 : 3, fillColor: f.getProperty("color"), fillOpacity: on ? 0.45 : 0.25 };
    });
    layer.addListener("click", function (e) { options.onPick(e.feature.getProperty("index")); });

    function fit(b, padding) {
      map.fitBounds(new google.maps.LatLngBounds({ lat: b.south, lng: b.west }, { lat: b.north, lng: b.east }), padding);
      return new Promise(function (r) { setTimeout(r, 1200); });
    }
    fit(allBounds(items), 80);

    var dot = null, accuracyRing = null; // not "ring": that name is the helper above (a var here would hide it)

    return {
      kind: "2d",
      ready: function () { return Promise.resolve(true); },
      destroy: function () { },
      showMe: function (p, fly) {
        var here = { lat: p.lat, lng: p.lng };
        if (!dot) {
          accuracyRing = new google.maps.Circle({ map: map, center: here, radius: p.accuracy, clickable: false,
            strokeColor: "#2563eb", strokeOpacity: 0.5, strokeWeight: 1, fillColor: "#3b82f6", fillOpacity: 0.15 });
          dot = new google.maps.Marker({ map: map, position: here, clickable: false, zIndex: 9999, title: "You are here",
            icon: { path: google.maps.SymbolPath.CIRCLE, scale: 8, fillColor: "#2563eb", fillOpacity: 1, strokeColor: "#ffffff", strokeWeight: 3 } });
        } else {
          dot.setPosition(here);
          accuracyRing.setCenter(here);
          accuracyRing.setRadius(p.accuracy);
        }
        if (!fly) { return Promise.resolve(); }
        if (p.accuracy > 150) { map.fitBounds(accuracyRing.getBounds()); } else { map.panTo(here); if (map.getZoom() < 18) { map.setZoom(18); } }
        return new Promise(function (r) { setTimeout(r, 1200); });
      },
      showAll: function () { return fit(allBounds(items), 80); },
      fly: function (i) {
        return fit(boundsOf(items[i].polygons), 160).then(function () { if (map.getZoom() > 19) { map.setZoom(19); } });
      },
      orbit: function (i, ms) { return new Promise(function (r) { setTimeout(r, Math.min(ms, 6000)); }); }, // no flight in 2D: a pause
      stop: function () { },
      highlight: function (index) { highlighted = index; layer.setStyle(layer.getStyle()); }
    };
  }

  Nadlan.presentViews = { create3dView: create3dView, create2dView: create2dView };
})(window);
