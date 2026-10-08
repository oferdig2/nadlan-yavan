// MapHighlight: outlines that stand out on any fill and on the satellite photo - a wide dark halo under a bright line,
// drawn above every Parcel polygon, so a selected Parcel is always outlined all the way round (a Parcel's own stroke is
// partly covered by its neighbours' strokes, which share its draw order). Never catches clicks or hover.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var HALO = { color: "#0f172a", opacity: 0.85, weight: 7, zIndex: 1000 };
  var LINE_WEIGHT = 3;

  /** @param {google.maps.Map} map */
  function createMapHighlight(map) {
    var halo = new google.maps.Data({ map: map });
    var line = new google.maps.Data({ map: map });
    halo.setStyle({ clickable: false, fillOpacity: 0, strokeColor: HALO.color, strokeOpacity: HALO.opacity, strokeWeight: HALO.weight, zIndex: HALO.zIndex });
    line.setStyle(function (f) {
      return { clickable: false, fillOpacity: 0, strokeColor: f.getProperty("color"), strokeOpacity: 1, strokeWeight: LINE_WEIGHT, zIndex: HALO.zIndex + 1 };
    });

    function clear(layer) {
      var all = [];
      layer.forEach(function (f) { all.push(f); });
      all.forEach(function (f) { layer.remove(f); });
    }

    return {
      /** @param {Array<{ id: number, geometry: object, color: string }>} list  GeoJSON polygons; empty = none */
      set: function (list) {
        var features = list.filter(function (h) { return h.geometry; }).map(function (h) {
          return { type: "Feature", id: h.id, geometry: h.geometry, properties: { color: h.color } };
        });
        [halo, line].forEach(function (layer) {
          clear(layer);
          if (features.length) { layer.addGeoJson({ type: "FeatureCollection", features: features }); }
        });
      },
      setVisible: function (visible) {
        halo.setMap(visible ? map : null);
        line.setMap(visible ? map : null);
      }
    };
  }

  Nadlan.createMapHighlight = createMapHighlight;
  Nadlan.mapHighlightColors = { selected: "#facc15", picked: "#ffffff" };
})(window);
