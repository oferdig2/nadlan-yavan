// "Paste a location": a small 🔗 button on the map. Paste a Google Maps link (or plain coordinates), press Enter, and a
// pin shows where it points; the page then says which parcel the pin falls in.
//   parseLocation(text)                 - { lat, lng, exact } from a link or "lat, lng", or null
//   createMapLinkPinControl(map, opts)  - the button, the paste box and the pin
//   pointInside(geometry), googleMapsLink(p), copyText(text) - the other way: a Parcel's "Location" link (card button)
// Short share links (maps.app.goo.gl/…) carry no coordinates: the server follows them (GET /api/maps/resolve-link -
// a browser can't, Google sends no CORS headers) and the address they lead to is read here.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var SHORT_HOSTS = /^(maps\.app\.goo\.gl|goo\.gl|g\.co)$/i;
  var NUM = "(-?\\d{1,3}(?:\\.\\d+)?)";

  function valid(lat, lng) { return isFinite(lat) && isFinite(lng) && Math.abs(lat) <= 90 && Math.abs(lng) <= 180 && !(lat === 0 && lng === 0); }
  function point(lat, lng, exact) { lat = Number(lat); lng = Number(lng); return valid(lat, lng) ? { lat: lat, lng: lng, exact: exact } : null; }

  /**
   * Where a Google Maps address points. In order of trust: the place's own pin (!3d…!4d…), a query or destination with
   * coordinates (q=, query=, ll=, destination=, daddr=, /place/…, /search/…), plain "lat, lng" or geo:lat,lng - and last
   * the map view's centre (@lat,lng,zoom), which is only where the map was looking (exact: false).
   */
  function parseLocation(text) {
    var t = String(text || "").trim();
    if (!t) { return null; }
    var decoded = t;
    try { decoded = decodeURIComponent(t.replace(/\+/g, " ")); } catch (e) { /* keep as typed */ }

    var plain = new RegExp("^(?:geo:)?\\s*" + NUM + "\\s*[,;\\s]\\s*" + NUM + "\\s*$").exec(decoded);
    if (plain) { return point(plain[1], plain[2], true); }

    var pins = decoded.match(new RegExp("!3d" + NUM + "!4d" + NUM, "g"));
    if (pins) {
      var last = new RegExp("!3d" + NUM + "!4d" + NUM).exec(pins[pins.length - 1]);
      var p = point(last[1], last[2], true);
      if (p) { return p; }
    }

    var param = new RegExp("[?&](?:q|query|ll|sll|center|destination|daddr)=(?:loc:)?\\s*" + NUM + "\\s*,\\s*" + NUM).exec(decoded);
    if (param) { var q = point(param[1], param[2], true); if (q) { return q; } }

    var path = new RegExp("/(?:place|search|dir)/(?:[^/]*/)*?" + NUM + "\\s*,\\s*" + NUM + "(?:[/?,]|$)").exec(decoded);
    if (path) { var s = point(path[1], path[2], true); if (s) { return s; } }

    var view = new RegExp("@" + NUM + "," + NUM).exec(decoded);
    if (view) { return point(view[1], view[2], false); }
    return null;
  }

  function isShortLink(text) {
    try { return SHORT_HOSTS.test(new URL(String(text).trim()).hostname); } catch (e) { return false; }
  }

  /** The location in a pasted text, following a short share link through the server first. */
  function locate(text) {
    if (isShortLink(text)) {
      return Nadlan.api.get("/api/maps/resolve-link", { url: String(text).trim() }).then(function (res) { return parseLocation(res.url); });
    }
    return Promise.resolve(parseLocation(text));
  }

  /**
   * A point inside a GeoJSON Polygon, near its middle: the centre of gravity when that lies inside (most Parcels), else
   * the middle of the widest stretch across the Parcel at that latitude (L-shapes, bends - the centre can fall outside).
   * @returns {{ lat: number, lng: number }}
   */
  function pointInside(geometry) {
    var ring = geometry.coordinates[0];
    var a = 0, cx = 0, cy = 0;
    for (var i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      var cross = ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
      a += cross;
      cx += (ring[j][0] + ring[i][0]) * cross;
      cy += (ring[j][1] + ring[i][1]) * cross;
    }
    var c = a ? { lng: cx / (3 * a), lat: cy / (3 * a) } : { lng: ring[0][0], lat: ring[0][1] };
    if (Nadlan.myLocation.containsPoint(geometry, c.lat, c.lng)) { return c; }
    // Across the Parcel at the centre's latitude (holes included): inside between each pair of edge crossings.
    var xs = [];
    geometry.coordinates.forEach(function (r) {
      for (var i = 0, j = r.length - 1; i < r.length; j = i++) {
        var y1 = r[j][1], y2 = r[i][1];
        if ((y1 > c.lat) !== (y2 > c.lat)) { xs.push(r[j][0] + (c.lat - y1) / (y2 - y1) * (r[i][0] - r[j][0])); }
      }
    });
    xs.sort(function (p, q) { return p - q; });
    var best = null;
    for (var k = 0; k + 1 < xs.length; k += 2) { if (!best || xs[k + 1] - xs[k] > best[1] - best[0]) { best = [xs[k], xs[k + 1]]; } }
    return best ? { lat: c.lat, lng: (best[0] + best[1]) / 2 } : { lat: ring[0][1], lng: ring[0][0] };
  }

  /** A Google Maps link with a pin at the point - opens in the Maps app on phones; no GreekPlot account needed. */
  function googleMapsLink(p) {
    return "https://www.google.com/maps/search/?api=1&query=" + p.lat.toFixed(6) + "%2C" + p.lng.toFixed(6);
  }

  /** Puts the text on the clipboard (resolves false when the browser won't, e.g. not https). */
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return legacyCopy(text); });
    }
    return Promise.resolve(legacyCopy(text));
  }

  function legacyCopy(text) {
    var area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    var ok = false;
    try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
    document.body.removeChild(area);
    return ok;
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ onPin: function({ lat: number, lng: number, exact: boolean }): void, onError: function(string): void,
   *           onClear?: function(): void }} options  onPin after the map is centred on the new pin
   */
  function createMapLinkPinControl(map, options) {
    var box = document.createElement("div");
    box.className = "map-linkpin";
    box.innerHTML =
      "<form class=\"map-linkpin-form\" hidden autocomplete=\"off\">" +
        "<input class=\"input\" type=\"text\" placeholder=\"Paste a Google Maps link or lat, lng - Enter\" aria-label=\"Google Maps link or coordinates\">" +
        "<button type=\"button\" class=\"map-linkpin-clear\" title=\"Remove the pin\" hidden>×</button>" +
      "</form>" +
      "<button type=\"button\" class=\"map-linkpin-toggle\" title=\"Show a location from a Google Maps link\" aria-label=\"Show a location from a Google Maps link\">🔗</button>";
    map.controls[google.maps.ControlPosition.RIGHT_BOTTOM].push(box);
    var form = box.querySelector("form");
    var input = box.querySelector("input");
    var clearButton = box.querySelector(".map-linkpin-clear");
    var toggle = box.querySelector(".map-linkpin-toggle");
    var marker = null;
    var busy = false;

    function open() { form.hidden = false; input.focus(); input.select(); }
    function close() { form.hidden = true; }

    toggle.addEventListener("click", function () { if (form.hidden) { open(); } else { close(); } });
    input.addEventListener("keydown", function (e) { if (e.key === "Escape") { e.preventDefault(); close(); } });
    // The map would take the paste's Enter, keys and clicks for itself (zoom, pan) - keep them in the box.
    ["keydown", "click", "dblclick", "mousedown", "pointerdown", "wheel"].forEach(function (name) {
      box.addEventListener(name, function (e) { e.stopPropagation(); });
    });

    clearButton.addEventListener("click", function () {
      if (marker) { marker.setMap(null); marker = null; }
      clearButton.hidden = true;
      input.value = "";
      if (options.onClear) { options.onClear(); }
    });

    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var text = input.value;
      if (!text.trim() || busy) { return; }
      busy = true;
      box.classList.add("busy");
      locate(text).then(function (p) {
        if (!p) {
          options.onError("No location found in that. Paste a Google Maps link (Share → Copy link) or coordinates such as 38.62, 23.28.");
          return;
        }
        var here = { lat: p.lat, lng: p.lng };
        if (!marker) {
          marker = new google.maps.Marker({ map: map, position: here, zIndex: 9998, title: "Pasted location" });
        } else {
          marker.setPosition(here);
          marker.setMap(map);
        }
        marker.setTitle((p.exact ? "Pasted location " : "Pasted map view (centre) ") + p.lat.toFixed(6) + ", " + p.lng.toFixed(6));
        clearButton.hidden = false;
        map.panTo(here);
        if (map.getZoom() < 18) { map.setZoom(18); }
        options.onPin(p);
      }).catch(function (err) {
        options.onError(err.message);
      }).then(function () { busy = false; box.classList.remove("busy"); }); // after any error too: the box never stays "busy"
    });

    return { open: open, close: close };
  }

  Nadlan.mapLinkPin = {
    parseLocation: parseLocation, isShortLink: isShortLink, createMapLinkPinControl: createMapLinkPinControl,
    pointInside: pointInside, googleMapsLink: googleMapsLink, copyText: copyText
  };
})(window, jQuery);
