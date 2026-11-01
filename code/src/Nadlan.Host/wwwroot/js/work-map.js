// A 2D map of a work request's Parcels (Google Maps JavaScript): outlines coloured by the Parcel's status, tap to choose
// one. Used by the contractor's page and the staff's request page (later the agents' map may reuse it). Roadmap /
// satellite only - no 3D, no tilt.
//   Nadlan.workMap.load(apiKey)       -> Promise (Google Maps loaded once; rejects without a key)
//   Nadlan.workMap.create(el, opts)   -> { setParcels(list), select(parcelId), fit(parcelId?), map }
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

    return { map: map, setParcels: setParcels, select: select, fit: fit };
  }

  Nadlan.workMap = { load: load, create: create, COLORS: COLORS };
})(window, document);
