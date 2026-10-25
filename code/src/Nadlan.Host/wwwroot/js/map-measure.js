// Distance tool (remark 17, 2026-10-24): a ruler button with the round map buttons. While it's on, each map click adds a
// point to a line; the total length shows beside the line and in the status line. Double-click or Enter finishes (the
// next click starts a new line); Esc or the button again clears it and turns the tool off. Nothing is saved.
// The length is computed here (great-circle distance) - Google's geometry library isn't loaded.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var EARTH_M = 6371008.8;

  /** Metres between two { lat, lng } points (haversine; plenty accurate for a few km). */
  function distance(a, b) {
    var rad = Math.PI / 180;
    var dLat = (b.lat - a.lat) * rad, dLng = (b.lng - a.lng) * rad;
    var h = Math.sin(dLat / 2) * Math.sin(dLat / 2) + Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(dLng / 2) * Math.sin(dLng / 2);
    return 2 * EARTH_M * Math.asin(Math.min(1, Math.sqrt(h)));
  }

  function length(points) {
    var total = 0;
    for (var i = 1; i < points.length; i++) { total += distance(points[i - 1], points[i]); }
    return total;
  }

  /** "350 m" / "1.24 km" (km from 1,000 m). */
  function formatLength(m) {
    return m < 1000 ? Math.round(m) + " m" : (m / 1000).toFixed(m < 10000 ? 2 : 1) + " km";
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ setStatus: function(string): void, onActiveChange: function(boolean): void }} options
   *        onActiveChange(true) while measuring: the page stops parcel clicks (cards) until it is turned off
   */
  function createMeasureControl(map, options) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "map-locate map-measure";
    button.title = "Measure a distance (click points on the map; double-click or Enter finishes, Esc clears)";
    button.setAttribute("aria-label", "Measure a distance");
    button.setAttribute("aria-pressed", "false");
    button.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 17 17 3l4 4L7 21z\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linejoin=\"round\"/>" +
      "<path d=\"M7 13l2 2M10 10l2 2M13 7l2 2\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"/></svg>";
    map.controls[google.maps.ControlPosition.RIGHT_BOTTOM].push(button);

    var active = false, finished = false, points = [];
    var line = null, label = null, clickListener = null, dblListener = null;

    function draw() {
      if (!line) {
        line = new google.maps.Polyline({ map: map, clickable: false, strokeColor: "#facc15", strokeOpacity: 1, strokeWeight: 3, zIndex: 9990,
          icons: [{ icon: { path: google.maps.SymbolPath.CIRCLE, scale: 3, fillColor: "#111827", fillOpacity: 1, strokeColor: "#facc15", strokeWeight: 2 }, offset: "0%", repeat: "100%" }] });
      }
      line.setPath(points);
      var total = length(points);
      var text = formatLength(total);
      if (points.length) {
        var at = points[points.length - 1];
        if (!label) {
          label = new google.maps.Marker({ map: map, clickable: false, zIndex: 9991,
            icon: { path: google.maps.SymbolPath.CIRCLE, scale: 0 }, label: { text: text, className: "map-measure-label" } });
        }
        label.setPosition(at);
        label.setLabel({ text: points.length > 1 ? text : "0 m", className: "map-measure-label" });
      }
      options.setStatus(points.length < 2 ? "Measuring: click the next point (Esc clears)."
        : "Distance: " + text + (finished ? " - click to start a new line, Esc clears." : " - double-click or Enter to finish, Esc clears."));
    }

    function add(latLng) {
      if (finished) { points = []; finished = false; }
      var p = { lat: latLng.lat(), lng: latLng.lng() };
      var last = points[points.length - 1];
      if (last && distance(last, p) < 0.01) { return; } // a double-click's two clicks: one point
      points.push(p);
      draw();
    }

    function finish() {
      if (points.length < 2) { return; }
      finished = true;
      draw();
    }

    function onKey(e) {
      if (e.key === "Escape") { e.preventDefault(); setActive(false); }
      else if (e.key === "Enter" && !/^(INPUT|TEXTAREA|SELECT)$/.test((e.target && e.target.tagName) || "")) { e.preventDefault(); finish(); }
    }

    function setActive(on) {
      if (on === active) { return; }
      active = on;
      button.classList.toggle("on", on);
      button.setAttribute("aria-pressed", String(on));
      points = [];
      finished = false;
      if (on) {
        map.setOptions({ disableDoubleClickZoom: true, draggableCursor: "crosshair" });
        clickListener = map.addListener("click", function (e) { add(e.latLng); });
        dblListener = map.addListener("dblclick", function () { finish(); });
        document.addEventListener("keydown", onKey);
        options.setStatus("Measuring: click points on the map (double-click or Enter finishes, Esc clears).");
      } else {
        map.setOptions({ disableDoubleClickZoom: false, draggableCursor: null });
        if (clickListener) { clickListener.remove(); clickListener = null; }
        if (dblListener) { dblListener.remove(); dblListener = null; }
        document.removeEventListener("keydown", onKey);
        if (line) { line.setMap(null); line = null; }
        if (label) { label.setMap(null); label = null; }
        options.setStatus("");
      }
      options.onActiveChange(on);
    }

    button.addEventListener("click", function () { setActive(!active); });

    return { isActive: function () { return active; }, stop: function () { setActive(false); } };
  }

  Nadlan.mapMeasure = { createMeasureControl: createMeasureControl, distance: distance, length: length, formatLength: formatLength };
})(window);
