// A 2D map of a work request's Parcels (Google Maps JavaScript): outlines coloured by the Parcel's status, tap to choose
// one. Used by the contractor's page and the staff's request page (later the agents' map may reuse it). Roadmap /
// satellite only - no 3D, no tilt.
//   Nadlan.workMap.load(apiKey)       -> Promise (Google Maps loaded once; rejects without a key)
//   Nadlan.workMap.create(el, opts)   -> { setParcels(list), select(parcelId), fit(parcelId?), map,
//                                         setPosition(pos, label), panTo(pos), setTrail(points), showPin(pos, label) }
//   Nadlan.workMap.contains / distanceM(geometry, lat, lng) - "Where am I" and "not in the right place"
//     opts: { center: {lat,lng}, zoom, onParcelClick(parcelId), onMapClick(latLng) }
//     list: [{ parcelId, status, geometry (GeoJSON Polygon), label }]
(function (window, document) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  // The Parcel's own status (Nadlan.Core.Work.ParcelWorkStatuses); the same colours as the status badges.
  var COLORS = {
    OPEN_OFFERS: "#f59e0b",
    APPROVED: "#3b82f6",
    IN_WORK: "#a855f7",
    DONE: "#22c55e",
    PAID: "#14b8a6"
  };

  var loading = null;

  function load(apiKey) {
    if (window.google && window.google.maps && window.google.maps.Map) { return Promise.resolve(); }
    if (loading) { return loading; }
    loading = new Promise(function (resolve, reject) {
      if (!apiKey) { reject(new Error("NO_MAPS_KEY")); return; }
      window.__nadlanWorkMapsReady = resolve;
      var script = document.createElement("script");
      script.src = "https://maps.googleapis.com/maps/api/js?key=" + encodeURIComponent(apiKey) + "&v=weekly&loading=async&callback=__nadlanWorkMapsReady";
      script.async = true;
      script.onerror = function () { loading = null; reject(new Error("MAPS_FAILED")); };
      document.head.appendChild(script);
    });
    return loading;
  }

  function paths(geometry) {
    return (geometry && geometry.coordinates || []).map(function (ring) {
      return ring.map(function (c) { return { lat: c[1], lng: c[0] }; });
    });
  }

  function create(el, opts) {
    var g = window.google.maps;
    var map = new g.Map(el, {
      center: opts.center || { lat: 38.62, lng: 23.28 },
      zoom: opts.zoom || 14,
      mapTypeId: "hybrid",
      tilt: 0,
      rotateControl: false,
      streetViewControl: false,
      fullscreenControl: false,
      clickableIcons: false,
      gestureHandling: "greedy",
      mapTypeControl: true,
      mapTypeControlOptions: { mapTypeIds: ["roadmap", "hybrid"] }
    });
    var shapes = {}; // parcelId -> { polygon, item }
    var selected = null;

    if (opts.onMapClick) {
      map.addListener("click", function (e) { opts.onMapClick({ lat: e.latLng.lat(), lng: e.latLng.lng() }); });
    }

    function style(id) {
      var s = shapes[id];
      var color = COLORS[s.item.status] || "#64748b";
      var on = id === selected;
      s.polygon.setOptions({
        strokeColor: on ? "#ffffff" : color, strokeWeight: on ? 4 : 2, strokeOpacity: 1,
        fillColor: color, fillOpacity: on ? 0.45 : 0.28, zIndex: on ? 2 : 1
      });
    }

    function setParcels(list) {
      Object.keys(shapes).forEach(function (id) { shapes[id].polygon.setMap(null); });
      shapes = {};
      (list || []).forEach(function (item) {
        if (!item.geometry) { return; }
        var polygon = new g.Polygon({ paths: paths(item.geometry), map: map });
        shapes[item.parcelId] = { polygon: polygon, item: item };
        polygon.addListener("click", function (e) {
          if (opts.onParcelClick) { opts.onParcelClick(item.parcelId, e && e.latLng ? { lat: e.latLng.lat(), lng: e.latLng.lng() } : null); }
        });
        style(item.parcelId);
      });
      if (selected && !shapes[selected]) { selected = null; }
    }

    function select(parcelId) {
      var before = selected;
      selected = parcelId && shapes[parcelId] ? parcelId : null;
      if (before && shapes[before]) { style(before); }
      if (selected) { style(selected); }
    }

    // Around one Parcel, or around all of them.
    function fit(parcelId) {
      var bounds = new g.LatLngBounds();
      var any = false;
      Object.keys(shapes).forEach(function (id) {
        if (parcelId && String(id) !== String(parcelId)) { return; }
        shapes[id].polygon.getPaths().forEach(function (path) { path.forEach(function (p) { bounds.extend(p); any = true; }); });
      });
      if (!any) { return; }
      map.fitBounds(bounds, 40);
      g.event.addListenerOnce(map, "idle", function () { if (map.getZoom() > 19) { map.setZoom(19); } });
    }

    // ---- piece 3: a person on the map (the contractor's own blue dot, or staff's view of him), a trail, a pin ----

    var dot = null, ring = null, trail = null, pin = null;

    /** The position as a blue dot with its accuracy circle; null removes it. label: a tooltip (e.g. "3 min ago"). */
    function setPosition(pos, label) {
      if (!pos) {
        if (dot) { dot.setMap(null); ring.setMap(null); dot = ring = null; }
        return;
      }
      var at = { lat: pos.lat, lng: pos.lng };
      if (!dot) {
        ring = new g.Circle({ map: map, center: at, radius: pos.accuracy || 0, strokeColor: "#1a73e8", strokeOpacity: 0.4, strokeWeight: 1,
          fillColor: "#1a73e8", fillOpacity: 0.12, clickable: false, zIndex: 3 });
        dot = new g.Marker({ map: map, position: at, zIndex: 4, clickable: !!label,
          icon: { path: g.SymbolPath.CIRCLE, scale: 8, fillColor: "#1a73e8", fillOpacity: 1, strokeColor: "#ffffff", strokeWeight: 3 } });
      }
      dot.setPosition(at);
      dot.setTitle(label || "");
      ring.setCenter(at);
      ring.setRadius(pos.accuracy || 0);
    }

    function panTo(pos) { map.panTo({ lat: pos.lat, lng: pos.lng }); }

    /** A day's track: [[lat, lng], ...] as a line; empty removes it. */
    function setTrail(points) {
      if (trail) { trail.setMap(null); trail = null; }
      if (!points || points.length < 2) { return; }
      trail = new g.Polyline({ map: map, path: points.map(function (p) { return { lat: p[0], lng: p[1] }; }),
        strokeColor: "#1a73e8", strokeOpacity: 0.85, strokeWeight: 3, clickable: false, zIndex: 2 });
    }

    /** One pin (e.g. where a picture was taken), centred; null removes it. */
    function showPin(pos, label) {
      if (pin) { pin.setMap(null); pin = null; }
      if (!pos) { return; }
      pin = new g.Marker({ map: map, position: { lat: pos.lat, lng: pos.lng }, title: label || "", zIndex: 5 });
      map.panTo({ lat: pos.lat, lng: pos.lng });
      if (map.getZoom() < 17) { map.setZoom(17); }
    }

    return { map: map, setParcels: setParcels, select: select, fit: fit, setPosition: setPosition, panTo: panTo, setTrail: setTrail, showPin: showPin };
  }

  // ---- geometry on GeoJSON polygons (no map needed) ---------------------------------------------------------------

  function inRing(ring, lng, lat) {
    var inside = false;
    for (var i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      var xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
      if ((yi > lat) !== (yj > lat) && lng < (xj - xi) * (lat - yi) / (yj - yi) + xi) { inside = !inside; }
    }
    return inside;
  }

  /** Inside the polygon (holes excluded) - the same rule as my-location.js containsPoint. */
  function contains(geometry, lat, lng) {
    var rings = geometry && geometry.coordinates;
    if (!rings || !rings.length || !inRing(rings[0], lng, lat)) { return false; }
    for (var h = 1; h < rings.length; h++) { if (inRing(rings[h], lng, lat)) { return false; } }
    return true;
  }

  /** Metres from the point to the polygon's edge; 0 inside. Flat-earth maths: exact enough for a few km. */
  function distanceM(geometry, lat, lng) {
    if (!geometry || !geometry.coordinates || !geometry.coordinates.length) { return Infinity; }
    if (contains(geometry, lat, lng)) { return 0; }
    var kx = 111320 * Math.cos(lat * Math.PI / 180), ky = 110574;
    var best = Infinity;
    geometry.coordinates.forEach(function (ring) {
      for (var i = 0; i < ring.length - 1; i++) {
        var ax = (ring[i][0] - lng) * kx, ay = (ring[i][1] - lat) * ky;
        var bx = (ring[i + 1][0] - lng) * kx, by = (ring[i + 1][1] - lat) * ky;
        var dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
        var t = len ? Math.max(0, Math.min(1, -(ax * dx + ay * dy) / len)) : 0;
        var px = ax + t * dx, py = ay + t * dy;
        best = Math.min(best, Math.sqrt(px * px + py * py));
      }
    });
    return best;
  }

  Nadlan.workMap = { load: load, create: create, COLORS: COLORS, contains: contains, distanceM: distanceM };
})(window, document);
