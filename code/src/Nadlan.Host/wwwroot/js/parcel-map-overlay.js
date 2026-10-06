// Renders Parcel polygons on a Google Map. Rendering and selection only - no business logic.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  // Colour = how the Parcel is identified (summary.kind, server ParcelKinds): a real KAEK, a provisional (TMP-) KAEK whose
  // OT (building block) is known, or a provisional one without OT. Provisional ones sit on top with a bold outline;
  // each kind has its own fill opacity (legend sliders): real Parcels filled and provisional ones as outlines shows
  // exactly where the two disagree. Storage keys "normal"/"provisional" are the older names of kaek/ot.
  var KINDS = [
    { key: "kaek", store: "normal", label: "Real KAEK", strokeColor: "#1d4ed8", strokeWeight: 1.5, fillColor: "#3b82f6", zIndex: 1, defaultOpacity: 0.4 },
    { key: "ot", store: "provisional", label: "Provisional, OT known", strokeColor: "#ea580c", strokeWeight: 2.5, fillColor: "#fb923c", zIndex: 2, defaultOpacity: 0.1 },
    { key: "noid", store: "noid", label: "Provisional, no OT", strokeColor: "#be123c", strokeWeight: 2.5, fillColor: "#f43f5e", zIndex: 2, defaultOpacity: 0.15 }
  ];
  var BY_KEY = {};
  KINDS.forEach(function (k) { BY_KEY[k.key] = k; });
  // A Parcel carrying an Asset the user may see: a purple outline over its kind's fill.
  var ASSET_OUTLINE = { strokeColor: "#7c3aed", strokeWeight: 3.5 };
  var STYLE_SELECTED = { strokeColor: "#111827", strokeWeight: 3 };

  function kindOf(summary) { return BY_KEY[summary && summary.kind] || (summary && summary.registryIdIsProvisional ? BY_KEY.ot : BY_KEY.kaek); }

  function loadOpacity(kind) {
    try {
      var v = parseFloat(window.localStorage.getItem("nadlan.parcelFill." + kind.store));
      return v >= 0 && v <= 0.8 ? v : kind.defaultOpacity;
    } catch (e) { return kind.defaultOpacity; } // storage blocked: just use the default
  }

  function saveOpacity(kind, v) {
    try { window.localStorage.setItem("nadlan.parcelFill." + kind.store, String(v)); } catch (e) { /* per-browser convenience only */ }
  }

  function rgba(hex, alpha) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + (n >> 16) + "," + (n >> 8 & 255) + "," + (n & 255) + "," + alpha + ")";
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object): void, onSurfaceClick?: function(google.maps.LatLng): void,
   *           onFilterChange?: function(): void }} options
   *        onClick receives the Parcel summary; onSurfaceClick the point clicked on the zoomed-out surface;
   *        onFilterChange fires when a legend checkbox changes (the page reloads with getFilter()).
   */
  function createParcelMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    // Zoomed out: all Parcels united into one surface per kind (server-side, see ParcelCoverageService) instead of
    // thousands of polygons. Same colours and opacity sliders; a click zooms in there.
    var surface = new google.maps.Data({ map: map });
    var selectedId = null;
    var interactive = true;
    var opacity = {};
    KINDS.forEach(function (k) { opacity[k.key] = loadOpacity(k); });
    // Legend checkboxes = the colour filter. Not remembered: a reload shows everything again.
    var shown = { kaek: true, ot: true, noid: true, withAssets: true, withoutAssets: true };

    layer.setStyle(function (feature) {
      var summary = feature.getProperty("summary");
      var kind = kindOf(summary);
      var fill = opacity[kind.key];
      var style = {
        clickable: interactive, strokeColor: kind.strokeColor, strokeWeight: kind.strokeWeight,
        fillColor: kind.fillColor, fillOpacity: fill, zIndex: kind.zIndex
      };
      if (summary && summary.hasAssets) { Object.assign(style, ASSET_OUTLINE, { zIndex: kind.zIndex + 2 }); }
      if (feature.getId() === selectedId) { Object.assign(style, STYLE_SELECTED, { fillOpacity: Math.min(0.85, fill + 0.25) }); }
      return style;
    });

    // Per-feature override (Google's hover pattern): touches one polygon, not a restyle of the whole layer.
    layer.addListener("mouseover", function (e) {
      var fill = opacity[kindOf(e.feature.getProperty("summary")).key];
      layer.overrideStyle(e.feature, { strokeWeight: 3.5, fillOpacity: Math.min(0.85, fill + 0.2) });
    });
    layer.addListener("mouseout", function (e) { layer.revertStyle(e.feature); });

    surface.setStyle(function (feature) {
      var kind = BY_KEY[feature.getProperty("kind")] || BY_KEY.kaek;
      return { visible: shown[kind.key], clickable: interactive, strokeColor: kind.strokeColor, strokeWeight: 1, fillColor: kind.fillColor,
        fillOpacity: Math.max(opacity[kind.key], 0.15), zIndex: kind.zIndex };
    });
    surface.addListener("click", function (e) { if (options.onSurfaceClick) { options.onSurfaceClick(e.latLng); } });
    layer.addListener("click", function (e) {
      selectedId = e.feature.getId();
      refresh();
      options.onClick(e.feature.getProperty("summary"));
    });

    function refresh() {
      layer.setStyle(layer.getStyle()); // re-evaluates the style function
      surface.setStyle(surface.getStyle());
    }

    var legend = document.createElement("div");
    legend.className = "map-legend";
    function check(key, title) {
      return "<input type=\"checkbox\" class=\"legend-check\" data-show=\"" + key + "\" checked title=\"" + title + "\">";
    }
    function kindRow(kind) {
      return "<div class=\"legend-row\">" + check(kind.key, "Show these Parcels") +
        "<span class=\"swatch\" data-swatch=\"" + kind.key + "\"></span><span>" + kind.label + "</span>" +
        "<input type=\"range\" min=\"0\" max=\"80\" step=\"5\" data-fill=\"" + kind.key + "\" title=\"Fill opacity\">" +
        "<span class=\"pct\" data-pct=\"" + kind.key + "\"></span></div>";
    }
    legend.innerHTML = KINDS.map(kindRow).join("") +
      "<div class=\"legend-row\"><span class=\"legend-check-gap\"></span><span class=\"swatch\" data-swatch=\"overlap\"></span><span>Real + provisional overlap</span></div>" +
      "<div class=\"legend-sep\"></div>" +
      "<div class=\"legend-row\">" + check("withAssets", "Show Parcels with an Asset") +
        "<span class=\"swatch\" style=\"background:transparent;border:3px solid " + ASSET_OUTLINE.strokeColor + "\"></span><span>Has Asset(s)</span></div>" +
      "<div class=\"legend-row\">" + check("withoutAssets", "Show Parcels without an Asset") +
        "<span class=\"swatch\" style=\"background:transparent\"></span><span>No Asset</span></div>";
    map.controls[google.maps.ControlPosition.LEFT_BOTTOM].push(legend);
    KINDS.forEach(function (kind) {
      var slider = legend.querySelector("[data-fill=" + kind.key + "]");
      slider.value = Math.round(opacity[kind.key] * 100);
      slider.addEventListener("input", function () {
        opacity[kind.key] = Number(slider.value) / 100;
        saveOpacity(kind, opacity[kind.key]);
        paintLegend();
        refresh();
      });
    });
    Array.prototype.forEach.call(legend.querySelectorAll("[data-show]"), function (box) {
      box.addEventListener("change", function () {
        shown[box.getAttribute("data-show")] = box.checked;
        refresh(); // the surface hides unchecked kinds at once; the list follows on reload
        if (options.onFilterChange) { options.onFilterChange(); }
      });
    });
    paintLegend();

    // The overlap swatch is the provisional fill laid over the real one, as on the map.
    function paintLegend() {
      KINDS.forEach(function (k) {
        legend.querySelector("[data-swatch=" + k.key + "]").style.cssText = "background:" + rgba(k.fillColor, opacity[k.key]) + ";border-color:" + k.strokeColor;
        legend.querySelector("[data-pct=" + k.key + "]").textContent = Math.round(opacity[k.key] * 100) + "%";
      });
      var normal = rgba(BY_KEY.kaek.fillColor, opacity.kaek), provisional = rgba(BY_KEY.ot.fillColor, opacity.ot);
      legend.querySelector("[data-swatch=overlap]").style.cssText =
        "background:linear-gradient(" + provisional + "," + provisional + ")," + normal + ";border-color:" + BY_KEY.ot.strokeColor;
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
            return { type: "Feature", id: it.summary.parcelId, geometry: it.geometry, properties: { summary: it.summary } };
          })
        });
      },
      /** @param {{ kaek: object, ot: object, noid: object }|null} geo  MultiPolygons from GET /api/parcels/coverage; null = none */
      setSurface: function (geo) {
        surface.forEach(function (f) { surface.remove(f); });
        if (!geo) { return; }
        surface.addGeoJson({ type: "FeatureCollection", features: KINDS.map(function (k) {
          return { type: "Feature", geometry: geo[k.key], properties: { kind: k.key } };
        }).filter(function (f) { return f.geometry && f.geometry.coordinates.length > 0; }) });
      },
      /**
       * The legend's colour filter for GET /api/parcels: { kinds: [...] } when some kinds are unchecked, hasAssets
       * true/false when only one Asset box is; { nothing: true } when a whole group is unchecked (nothing can match).
       */
      getFilter: function () {
        var kinds = KINDS.filter(function (k) { return shown[k.key]; }).map(function (k) { return k.key; });
        if (!kinds.length || (!shown.withAssets && !shown.withoutAssets)) { return { nothing: true }; }
        var f = {};
        if (kinds.length < KINDS.length) { f.kinds = kinds; }
        if (shown.withAssets !== shown.withoutAssets) { f.hasAssets = shown.withAssets; }
        return f;
      },
      select: function (parcelId) { selectedId = parcelId; refresh(); },
      clearSelection: function () { selectedId = null; refresh(); },
      // Off while drawing, so clicks reach the map instead of the polygons.
      setInteractive: function (value) { interactive = value; refresh(); },
      setVisible: function (value) {
        layer.setMap(value ? map : null);
        surface.setMap(value ? map : null);
        legend.style.display = value ? "" : "none";
      }
    };
  }

  Nadlan.createParcelMapOverlay = createParcelMapOverlay;
})(window);
