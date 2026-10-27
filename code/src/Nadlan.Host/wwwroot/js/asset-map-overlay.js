// Renders Assets on the map. Asset geometry = its Parcel's geometry, and several Assets can share one Parcel,
// so we draw one polygon per Parcel, coloured by the status of its Assets (mixed statuses = purple).
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var MIXED_COLOR = "#7c3aed";
  var TIP_MAX_ASSETS = 4; // more on one Parcel: "+N more" (the card lists them all)

  // Beaches in their own colour (a sea blue that no status uses), whatever their status (owner, 2026-10-27).
  var BEACH_COLOR = "#0ea5e9";
  function colorOf(a) { return a.isBeach ? BEACH_COLOR : a.statusColor; }

  // Hover card of a Parcel in the Assets view (same look as the Parcels view's): the asking price first, then status,
  // type and manager of each Asset on it, and the Parcel's KAEK and area. Follows the mouse; hidden on press (map drag).
  function createTooltip() {
    var f = Nadlan.format;
    var tip = document.createElement("div");
    tip.className = "parcel-tip asset-tip";
    tip.hidden = true;
    document.body.appendChild(tip);
    var mouse = { x: 0, y: 0 };
    function place() {
      if (tip.hidden) { return; }
      var w = tip.offsetWidth, h = tip.offsetHeight, gap = 16;
      var x = mouse.x + gap + w > window.innerWidth ? mouse.x - gap - w : mouse.x + gap;
      var y = mouse.y + gap + h > window.innerHeight ? mouse.y - gap - h : mouse.y + gap;
      tip.style.left = Math.max(4, x) + "px";
      tip.style.top = Math.max(4, y) + "px";
    }
    document.addEventListener("mousemove", function (e) { mouse.x = e.clientX; mouse.y = e.clientY; place(); }, { passive: true });
    document.addEventListener("mousedown", function () { tip.hidden = true; }, { passive: true });

    function safeColor(c) { return /^#[0-9a-fA-F]{6}$/.test(c || "") ? c : "#64748b"; }


    function assetHtml(a) {
      var facts = [];
      if (a.propertyType) { facts.push(f.escapeHtml(a.propertyType)); }
      if (a.managingContactName) { facts.push(f.escapeHtml(a.managingContactName)); }
      return "<div class=\"tip-asset\">" +
        "<div class=\"tip-price\">" + (a.priceHidden ? "<span class=\"tip-missing\">Price hidden</span>"
          : a.askPrice === null || a.askPrice === undefined ? "<span class=\"tip-missing\">No price yet</span>"
          : "<b>" + f.assetPrice(a) + "</b>") + "</div>" +
        "<div class=\"tip-facts\"><span class=\"tip-dot\" style=\"background:" + safeColor(colorOf(a)) + "\"></span>" +
          f.text(a.statusName) + (facts.length ? " · " + facts.join(" · ") : "") + "</div>" +
        "</div>";
    }

    return {
      show: function (group, color) {
        var assets = group.assets || [];
        var parcel = [];
        if (group.registryId) { parcel.push((group.registryIdIsProvisional ? "Provisional " : "KAEK ") + f.escapeHtml(group.registryId)); }
        if (group.geographicArea) { parcel.push(f.escapeHtml(group.geographicArea)); }
        tip.style.borderLeftColor = safeColor(color);
        tip.innerHTML = assets.slice(0, TIP_MAX_ASSETS).map(assetHtml).join("") +
          (assets.length > TIP_MAX_ASSETS ? "<div class=\"tip-facts\">+" + (assets.length - TIP_MAX_ASSETS) + " more - click for all</div>" : "") +
          (parcel.length ? "<div class=\"tip-parcel\">" + parcel.join(" · ") + "</div>" : "");
        tip.hidden = false;
        place();
      },
      hide: function () { tip.hidden = true; }
    };
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object): void, isSelected: function(number): boolean }} options
   *        onClick receives { parcelId, registryId, registryIdIsProvisional, geographicArea, assets: [...] }
   */
  function createAssetMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    var highlight = Nadlan.createMapHighlight(map); // the focused Parcel: yellow over a dark halo, never cut by neighbours
    var geometries = {};
    var focusedParcelId = null;
    var interactive = true;

    layer.setStyle(function (feature) {
      var assets = feature.getProperty("assets");
      var color = feature.getProperty("color");
      var anySelected = assets.some(function (a) { return options.isSelected(a.assetId); });
      var id = feature.getId();
      return {
        clickable: interactive,
        strokeColor: anySelected ? "#111827" : color,
        strokeWeight: anySelected ? 3 : 1.5,
        fillColor: color,
        fillOpacity: id === focusedParcelId ? 0.5 : 0.3
      };
    });

    var tooltip = createTooltip();

    // Per-feature override (Google's hover pattern): touches one polygon, not a restyle of the whole layer.
    layer.addListener("mouseover", function (e) {
      layer.overrideStyle(e.feature, { fillOpacity: 0.5 });
      if (interactive) { tooltip.show(e.feature.getProperty("group"), e.feature.getProperty("color")); }
    });
    layer.addListener("mouseout", function (e) { layer.revertStyle(e.feature); tooltip.hide(); });
    layer.addListener("click", function (e) {
      focusedParcelId = e.feature.getId();
      refresh();
      options.onClick(e.feature.getProperty("group"));
    });

    function refresh() {
      layer.setStyle(layer.getStyle());
      highlight.set(focusedParcelId !== null && geometries[focusedParcelId]
        ? [{ id: focusedParcelId, geometry: geometries[focusedParcelId], color: Nadlan.mapHighlightColors.selected }] : []);
    }

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
        tooltip.hide(); // the hovered polygon may be gone (no mouseout then)
        geometries = {};
        order.forEach(function (id) { geometries[id] = byParcel[id].geometry; });
        layer.addGeoJson({
          type: "FeatureCollection",
          features: order.map(function (parcelId) {
            var entry = byParcel[parcelId];
            var colors = entry.group.assets.map(colorOf);
            var same = colors.every(function (c) { return c === colors[0]; });
            return {
              type: "Feature", id: parcelId, geometry: entry.geometry,
              properties: { group: entry.group, assets: entry.group.assets, color: same ? colors[0] : MIXED_COLOR }
            };
          })
        });
        refresh();
      },
      focus: function (parcelId) { focusedParcelId = parcelId; refresh(); },
      clearFocus: function () { focusedParcelId = null; refresh(); },
      refreshStyles: refresh,
      setInteractive: function (value) { interactive = value; refresh(); },
      setVisible: function (visible) { layer.setMap(visible ? map : null); highlight.setVisible(visible); if (!visible) { tooltip.hide(); } }
    };
  }

  Nadlan.createAssetMapOverlay = createAssetMapOverlay;
})(window);
