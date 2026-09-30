// Renders Parcel polygons on a Google Map. Rendering and selection only - no business logic.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  // Provisional (TMP-) Parcels sit on top with a bold outline. Each kind has its own fill opacity (legend sliders):
  // real Parcels filled and provisional ones as outlines shows exactly where the two disagree.
  var STYLE_NORMAL = { key: "normal", strokeColor: "#1d4ed8", strokeWeight: 1.5, fillColor: "#3b82f6", zIndex: 1, defaultOpacity: 0.4 };
  var STYLE_PROVISIONAL = { key: "provisional", strokeColor: "#ea580c", strokeWeight: 2.5, fillColor: "#fb923c", zIndex: 2, defaultOpacity: 0.1 };
  var STYLE_SELECTED = { strokeColor: "#111827", strokeWeight: 3 };

  function loadOpacity(style) {
    try {
      var v = parseFloat(window.localStorage.getItem("nadlan.parcelFill." + style.key));
      return v >= 0 && v <= 0.8 ? v : style.defaultOpacity;
    } catch (e) { return style.defaultOpacity; } // storage blocked: just use the default
  }

  function saveOpacity(style, v) {
    try { window.localStorage.setItem("nadlan.parcelFill." + style.key, String(v)); } catch (e) { /* per-browser convenience only */ }
  }

  function rgba(hex, alpha) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + (n >> 16) + "," + (n >> 8 & 255) + "," + (n & 255) + "," + alpha + ")";
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object): void, onSurfaceClick?: function(google.maps.LatLng): void }} options
   *        onClick receives the Parcel summary; onSurfaceClick the point clicked on the zoomed-out surface.
   */
  function createParcelMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    // Zoomed out: all Parcels united into one surface per kind (server-side, see ParcelCoverageService) instead of
    // thousands of polygons. Same colours and opacity sliders; a click zooms in there.
    var surface = new google.maps.Data({ map: map });
    var selectedId = null;
    var interactive = true;
    var opacity = { normal: loadOpacity(STYLE_NORMAL), provisional: loadOpacity(STYLE_PROVISIONAL) };

    layer.setStyle(function (feature) {
      var id = feature.getId();
      var base = feature.getProperty("registryIdIsProvisional") ? STYLE_PROVISIONAL : STYLE_NORMAL;
      var fill = opacity[base.key];
      var style = {
        clickable: interactive, strokeColor: base.strokeColor, strokeWeight: base.strokeWeight,
        fillColor: base.fillColor, fillOpacity: fill, zIndex: base.zIndex
      };
      if (id === selectedId) { Object.assign(style, STYLE_SELECTED, { fillOpacity: Math.min(0.85, fill + 0.25) }); }
      return style;
    });

    // Per-feature override (Google's hover pattern): touches one polygon, not a restyle of the whole layer.
    layer.addListener("mouseover", function (e) {
      var fill = opacity[e.feature.getProperty("registryIdIsProvisional") ? "provisional" : "normal"];
      layer.overrideStyle(e.feature, { strokeWeight: 3, fillOpacity: Math.min(0.85, fill + 0.2) });
    });
    layer.addListener("mouseout", function (e) { layer.revertStyle(e.feature); });

    surface.setStyle(function (feature) {
      var base = feature.getProperty("kind") === "provisional" ? STYLE_PROVISIONAL : STYLE_NORMAL;
      return { clickable: interactive, strokeColor: base.strokeColor, strokeWeight: 1, fillColor: base.fillColor,
        fillOpacity: Math.max(opacity[base.key], 0.15), zIndex: base.zIndex };
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
    function row(style, label) {
      return "<div class=\"legend-row\"><span class=\"swatch\" data-swatch=\"" + style.key + "\"></span><span>" + label + "</span>" +
        "<input type=\"range\" min=\"0\" max=\"80\" step=\"5\" data-fill=\"" + style.key + "\" title=\"Fill opacity\">" +
        "<span class=\"pct\" data-pct=\"" + style.key + "\"></span></div>";
    }
    legend.innerHTML = row(STYLE_NORMAL, "Real KAEK") + row(STYLE_PROVISIONAL, "Provisional (TMP-)") +
      "<div class=\"legend-row\"><span class=\"swatch\" data-swatch=\"overlap\"></span><span>Both (overlap)</span></div>";
    map.controls[google.maps.ControlPosition.LEFT_BOTTOM].push(legend);
    [STYLE_NORMAL, STYLE_PROVISIONAL].forEach(function (style) {
      var slider = legend.querySelector("[data-fill=" + style.key + "]");
      slider.value = Math.round(opacity[style.key] * 100);
      slider.addEventListener("input", function () {
        opacity[style.key] = Number(slider.value) / 100;
        saveOpacity(style, opacity[style.key]);
        paintLegend();
        refresh();
      });
    });
    paintLegend();

    // The overlap swatch is the provisional fill laid over the real one, as on the map.
    function paintLegend() {
      var normal = rgba(STYLE_NORMAL.fillColor, opacity.normal), provisional = rgba(STYLE_PROVISIONAL.fillColor, opacity.provisional);
      legend.querySelector("[data-swatch=normal]").style.cssText = "background:" + normal + ";border-color:" + STYLE_NORMAL.strokeColor;
      legend.querySelector("[data-swatch=provisional]").style.cssText = "background:" + provisional + ";border-color:" + STYLE_PROVISIONAL.strokeColor;
      legend.querySelector("[data-swatch=overlap]").style.cssText =
        "background:linear-gradient(" + provisional + "," + provisional + ")," + normal + ";border-color:" + STYLE_PROVISIONAL.strokeColor;
      legend.querySelector("[data-pct=normal]").textContent = Math.round(opacity.normal * 100) + "%";
      legend.querySelector("[data-pct=provisional]").textContent = Math.round(opacity.provisional * 100) + "%";
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
      /** @param {{ real: object, provisional: object }|null} geo  MultiPolygons from GET /api/parcels/coverage; null = none */
      setSurface: function (geo) {
        surface.forEach(function (f) { surface.remove(f); });
        if (!geo) { return; }
        surface.addGeoJson({ type: "FeatureCollection", features: [
          { type: "Feature", geometry: geo.real, properties: { kind: "normal" } },
          { type: "Feature", geometry: geo.provisional, properties: { kind: "provisional" } }
        ].filter(function (f) { return f.geometry.coordinates.length > 0; }) });
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
