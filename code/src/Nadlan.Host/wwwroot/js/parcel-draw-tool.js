// ParcelDrawTool: draw a polygon corner by corner (click), finish with double-click or Finish, then
// fine-tune by dragging corners. Also shows pasted coordinates as an editable polygon.
// Geometry entry is isolated here; the Parcel form (parcel-editor.js) only asks for coordinates.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var SHAPE = { strokeColor: "#be123c", strokeWeight: 2, fillColor: "#fb7185", fillOpacity: 0.25 };

  /**
   * @param {google.maps.Map} map
   * @param {{ onDrawingChange: function(boolean): void, onShapeChange: function(boolean): void }} options
   *        onShapeChange(true) once a closed polygon exists, false when it is removed.
   */
  function createParcelDrawTool(map, options) {
    var points = [];
    var line = null;
    var polygon = null;
    var listeners = [];

    function stopListening() {
      listeners.forEach(function (l) { l.remove(); });
      listeners = [];
    }

    function endDrawing() {
      stopListening();
      map.setOptions({ draggableCursor: null, disableDoubleClickZoom: false });
      if (line) { line.setMap(null); line = null; }
      options.onDrawingChange(false);
    }

    function showPolygon(path) {
      if (polygon) { polygon.setMap(null); }
      polygon = new google.maps.Polygon(Object.assign({ map: map, paths: path, editable: true }, SHAPE));
      // Dragging/adding/removing a corner is a new shape: any earlier overlap answer no longer applies.
      var ring = polygon.getPath();
      ["set_at", "insert_at", "remove_at"].forEach(function (evt) {
        ring.addListener(evt, function () { options.onShapeChange(true); });
      });
      options.onShapeChange(true);
    }

    function dedupe(list) {
      return list.filter(function (p, i) { return i === 0 || !p.equals(list[i - 1]); });
    }

    var api = {
      start: function () {
        api.clear();
        options.onDrawingChange(true);
        map.setOptions({ draggableCursor: "crosshair", disableDoubleClickZoom: true });
        line = new google.maps.Polyline({ map: map, path: [], clickable: false, strokeColor: SHAPE.strokeColor, strokeWeight: 2 });
        listeners.push(map.addListener("click", function (e) {
          points.push(e.latLng);
          line.setPath(points);
        }));
        listeners.push(map.addListener("dblclick", function () { api.finish(); }));
      },

      /** Closes the polygon from the clicked corners. Returns false if there are fewer than 3. */
      finish: function () {
        var corners = dedupe(points); // a double-click also fires two clicks on the same spot
        if (corners.length < 3) { return false; }
        endDrawing();
        showPolygon(corners);
        points = [];
        return true;
      },

      undo: function () {
        points.pop();
        if (line) { line.setPath(points); }
      },

      cancel: function () {
        if (listeners.length) { endDrawing(); }
        points = [];
      },

      clear: function () {
        api.cancel();
        if (polygon) { polygon.setMap(null); polygon = null; options.onShapeChange(false); }
      },

      /** @param {Array<Array<number>>} ring  [[lon, lat], ...] */
      setCoordinates: function (ring) {
        api.cancel();
        var path = ring.map(function (p) { return { lng: p[0], lat: p[1] }; });
        if (path.length > 1 && path[0].lat === path[path.length - 1].lat && path[0].lng === path[path.length - 1].lng) {
          path.pop(); // Google polygons are implicitly closed
        }
        showPolygon(path);
        var bounds = new google.maps.LatLngBounds();
        path.forEach(function (p) { bounds.extend(p); });
        map.fitBounds(bounds, 80);
      },

      /** GeoJSON Polygon coordinates ([[[lon, lat], ...]]), closed, or null when nothing is drawn. */
      getCoordinates: function () {
        if (!polygon) { return null; }
        var ring = polygon.getPath().getArray().map(function (ll) { return [ll.lng(), ll.lat()]; });
        ring.push(ring[0]);
        return [ring];
      },

      isDrawing: function () { return listeners.length > 0; }
    };
    return api;
  }

  /**
   * Parses pasted coordinates into a ring of [lon, lat]:
   * - KML style: "lon,lat[,alt] lon,lat[,alt] ..." (what the cadastre/KML workflow produces)
   * - GeoJSON: a Polygon, or a Feature holding one
   * Throws Error with a readable message when the text can't be used.
   */
  function parseCoordinates(text) {
    var t = (text || "").trim();
    if (!t) { throw new Error("Paste coordinates first."); }

    if (t.charAt(0) === "{" || t.charAt(0) === "[") {
      var json;
      try { json = JSON.parse(t); } catch (e) { throw new Error("That is not valid JSON."); }
      var geom = json.type === "Feature" ? json.geometry : json;
      var coords = geom && geom.type === "Polygon" ? geom.coordinates : Array.isArray(json) ? json : null;
      if (!coords || !Array.isArray(coords[0])) { throw new Error("Expected a GeoJSON Polygon."); }
      return Array.isArray(coords[0][0]) ? coords[0] : coords;
    }

    var ring = t.split(/\s+/).map(function (token) {
      var parts = token.split(",");
      var lon = Number(parts[0]);
      var lat = Number(parts[1]);
      if (parts.length < 2 || isNaN(lon) || isNaN(lat)) { throw new Error("Can't read \"" + token + "\". Use lon,lat pairs separated by spaces."); }
      return [lon, lat];
    });
    if (ring.length < 3) { throw new Error("A polygon needs at least 3 corners."); }
    // Greece: longitude 19-30, latitude 34-42. A first number above 30 means the pair is lat,lon.
    if (ring[0][0] > 30 && ring[0][1] < 30) {
      throw new Error("These look like lat,lon. Use lon,lat (e.g. 23.36,38.50) - KML order.");
    }
    return ring;
  }

  Nadlan.createParcelDrawTool = createParcelDrawTool;
  Nadlan.parseCoordinates = parseCoordinates;
})(window);
