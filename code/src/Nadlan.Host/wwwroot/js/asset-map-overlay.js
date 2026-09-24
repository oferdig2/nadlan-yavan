// Renders Assets on the map. Asset geometry = its Parcel's geometry, and several Assets can share one Parcel,
// so we draw one polygon per Parcel, coloured by the status of its Assets (mixed statuses = purple).
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var MIXED_COLOR = "#7c3aed";

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object): void, isSelected: function(number): boolean }} options
   *        onClick receives { parcelId, registryId, registryIdIsProvisional, geographicArea, assets: [...] }
   */
  function createAssetMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    var focusedParcelId = null;
    var hoveredId = null;
    var interactive = true;

    layer.setStyle(function (feature) {
      var assets = feature.getProperty("assets");
      var color = feature.getProperty("color");
      var anySelected = assets.some(function (a) { return options.isSelected(a.assetId); });
      var id = feature.getId();
      return {
        clickable: interactive,
        strokeColor: anySelected ? "#111827" : color,
        strokeWeight: id === focusedParcelId || anySelected ? 3 : 1.5,
        fillColor: color,
        fillOpacity: id === hoveredId || id === focusedParcelId ? 0.5 : 0.3
      };
    });

    layer.addListener("mouseover", function (e) { hoveredId = e.feature.getId(); refresh(); });
    layer.addListener("mouseout", function () { hoveredId = null; refresh(); });
    layer.addListener("click", function (e) {
      focusedParcelId = e.feature.getId();
      refresh();
      options.onClick(e.feature.getProperty("group"));
    });

    function refresh() { layer.setStyle(layer.getStyle()); }

    return {
      /** @param {Array<{ summary: object, geometry: object }>} items  as returned by GET /api/assets */
      setItems: function (items) {
        var byParcel = {};
        var order = [];
        items.forEach(function (it) {
          var s = it.summary;
          if (!byParcel[s.parcelId]) {
            byParcel[s.parcelId] = {
              geometry: it.geometry,
              group: {
                parcelId: s.parcelId, registryId: s.registryId, registryIdIsProvisional: s.registryIdIsProvisional,
                geographicArea: s.geographicArea, assets: []
              }
            };
            order.push(s.parcelId);
          }
          byParcel[s.parcelId].group.assets.push(s);
        });

        var existing = [];
        layer.forEach(function (f) { existing.push(f); });
        existing.forEach(function (f) { layer.remove(f); });
        layer.addGeoJson({
          type: "FeatureCollection",
          features: order.map(function (parcelId) {
            var entry = byParcel[parcelId];
            var colors = entry.group.assets.map(function (a) { return a.statusColor; });
            var same = colors.every(function (c) { return c === colors[0]; });
            return {
              type: "Feature", id: parcelId, geometry: entry.geometry,
              properties: { group: entry.group, assets: entry.group.assets, color: same ? colors[0] : MIXED_COLOR }
            };
          })
        });
      },
      focus: function (parcelId) { focusedParcelId = parcelId; refresh(); },
      clearFocus: function () { focusedParcelId = null; refresh(); },
      refreshStyles: refresh,
      setInteractive: function (value) { interactive = value; refresh(); },
      setVisible: function (visible) { layer.setMap(visible ? map : null); }
    };
  }

  Nadlan.createAssetMapOverlay = createAssetMapOverlay;
})(window);
