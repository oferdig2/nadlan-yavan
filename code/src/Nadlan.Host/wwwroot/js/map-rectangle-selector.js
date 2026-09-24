// MapRectangleSelector: drag a rectangle on the map; emits its bounds. It knows nothing about Parcels/Assets.
// (Google's Drawing library was removed in May 2026, so drawing uses plain map mouse events.)
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {google.maps.Map} map
   * @param {{ onChange: function(object|null): void, onDrawingChange: function(boolean): void }} options
   *        onChange gets { west, south, east, north } or null when cleared.
   */
  function createMapRectangleSelector(map, options) {
    var rect = null;
    var listeners = [];
    var start = null;
    var debounce = null;

    function toBounds(r) {
      var b = r.getBounds();
      return { west: b.getSouthWest().lng(), south: b.getSouthWest().lat(), east: b.getNorthEast().lng(), north: b.getNorthEast().lat() };
    }

    function stopListening() {
      listeners.forEach(function (l) { l.remove(); });
      listeners = [];
    }

    function endDrawing() {
      stopListening();
      map.setOptions({ gestureHandling: "greedy", draggableCursor: null });
      options.onDrawingChange(false);
    }

    function clear(silent) {
      clearTimeout(debounce); // a pending resize must not report a rectangle that no longer exists
      if (rect) { rect.setMap(null); rect = null; }
      if (!silent) { options.onChange(null); }
    }

    return {
      start: function () {
        clear(true);
        options.onDrawingChange(true);
        map.setOptions({ gestureHandling: "none", draggableCursor: "crosshair" });

        listeners.push(map.addListener("mousedown", function (e) {
          start = e.latLng;
          rect = new google.maps.Rectangle({
            map: map, bounds: new google.maps.LatLngBounds(start, start), clickable: false,
            strokeColor: "#111827", strokeWeight: 2, fillColor: "#111827", fillOpacity: 0.08
          });
        }));
        listeners.push(map.addListener("mousemove", function (e) {
          if (!start || !rect) { return; }
          rect.setBounds(new google.maps.LatLngBounds(
            { lat: Math.min(start.lat(), e.latLng.lat()), lng: Math.min(start.lng(), e.latLng.lng()) },
            { lat: Math.max(start.lat(), e.latLng.lat()), lng: Math.max(start.lng(), e.latLng.lng()) }));
        }));
        listeners.push(map.addListener("mouseup", function () {
          if (!rect) { return; }
          start = null;
          endDrawing();
          var b = rect.getBounds();
          if (b.getNorthEast().equals(b.getSouthWest())) { clear(); return; } // a click, not a drag
          var drawn = rect;
          drawn.setEditable(true);
          drawn.addListener("bounds_changed", function () {
            clearTimeout(debounce);
            debounce = setTimeout(function () { if (rect === drawn) { options.onChange(toBounds(drawn)); } }, 400);
          });
          options.onChange(toBounds(rect));
        }));
      },
      cancel: function () { if (listeners.length) { endDrawing(); } },
      clear: function () { clear(false); },
      getBounds: function () { return rect ? toBounds(rect) : null; }
    };
  }

  Nadlan.createMapRectangleSelector = createMapRectangleSelector;
})(window);
