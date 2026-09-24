// Renders Parcel polygons on a Google Map. Rendering and selection only - no business logic.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var STYLE_NORMAL = { strokeColor: "#1d4ed8", strokeWeight: 1.5, fillColor: "#3b82f6", fillOpacity: 0.18 };
  var STYLE_PROVISIONAL = { strokeColor: "#c2410c", strokeWeight: 1.5, fillColor: "#fb923c", fillOpacity: 0.18 };
  var STYLE_HOVER = { strokeWeight: 3, fillOpacity: 0.32 };
  var STYLE_SELECTED = { strokeColor: "#111827", strokeWeight: 3, fillOpacity: 0.4 };

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object): void }} options  onClick receives the Parcel summary.
   */
  function createParcelMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    var selectedId = null;
    var interactive = true;

    layer.setStyle(function (feature) {
      var id = feature.getId();
      var base = feature.getProperty("registryIdIsProvisional") ? STYLE_PROVISIONAL : STYLE_NORMAL;
      var style = Object.assign({ clickable: interactive }, base);
      if (id === selectedId) { Object.assign(style, STYLE_SELECTED); }
      return style;
    });

    // Per-feature override (Google's hover pattern): touches one polygon, not a restyle of the whole layer.
    layer.addListener("mouseover", function (e) { layer.overrideStyle(e.feature, STYLE_HOVER); });
    layer.addListener("mouseout", function (e) { layer.revertStyle(e.feature); });
    layer.addListener("click", function (e) {
      selectedId = e.feature.getId();
      refresh();
      options.onClick(e.feature.getProperty("summary"));
    });

    function refresh() {
      layer.setStyle(layer.getStyle()); // re-evaluates the style function
    }

    return {
      /** @param {Array<{ summary: object, geometry: object }>} items  as returned by GET /api/parcels */
      setItems: function (items) {
        var existing = [];
        layer.forEach(function (f) { existing.push(f); });
        existing.forEach(function (f) { layer.remove(f); });
        layer.addGeoJson({
          type: "FeatureCollection",
          features: items.map(function (it) {
            return {
              type: "Feature", id: it.summary.parcelId, geometry: it.geometry,
              properties: { summary: it.summary, registryIdIsProvisional: it.summary.registryIdIsProvisional }
            };
          })
        });
      },
      select: function (parcelId) { selectedId = parcelId; refresh(); },
      clearSelection: function () { selectedId = null; refresh(); },
      // Off while drawing, so clicks reach the map instead of the polygons.
      setInteractive: function (value) { interactive = value; refresh(); },
      setVisible: function (visible) { layer.setMap(visible ? map : null); }
    };
  }

  Nadlan.createParcelMapOverlay = createParcelMapOverlay;
})(window);
