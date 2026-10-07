// Renders Parcel polygons on a Google Map. Rendering and selection only - no business logic.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  // Colour = how far the OT / plot data entry is (summary.kind, server ParcelKinds; customer change request #1):
  // green = OT and plot entered, amber = only one of them, blue = still to do. Each has its own fill opacity (legend
  // sliders, remembered per browser). Real vs provisional KAEK is a filter in the side panel, not a colour.
  var KINDS = [
    { key: "done", label: "OT + plot entered", strokeColor: "#15803d", strokeWeight: 1.5, fillColor: "#22c55e", zIndex: 1, defaultOpacity: 0.35 },
    { key: "partial", label: "Only OT or only plot", strokeColor: "#b45309", strokeWeight: 2, fillColor: "#f59e0b", zIndex: 2, defaultOpacity: 0.35 },
    { key: "todo", label: "No OT / plot yet", strokeColor: "#1d4ed8", strokeWeight: 2, fillColor: "#3b82f6", zIndex: 2, defaultOpacity: 0.3 }
  ];
  var BY_KEY = {};
  KINDS.forEach(function (k) { BY_KEY[k.key] = k; });
  // A Parcel carrying an Asset the user may see: a purple outline over its colour.
  var ASSET_OUTLINE = { strokeColor: "#7c3aed", strokeWeight: 3.5 };
  var STYLE_SELECTED = { strokeColor: "#111827", strokeWeight: 3 };
  // Picked for a row entry (Ctrl+click in quick entry): dark outline, filled a bit more.
  var STYLE_GROUP = { strokeColor: "#0f172a", strokeWeight: 4 };

  function kindOf(summary) { return BY_KEY[summary && summary.kind] || BY_KEY.todo; }

  function loadOpacity(kind) {
    try {
      var v = parseFloat(window.localStorage.getItem("nadlan.parcelFill.entry." + kind.key));
      return v >= 0 && v <= 0.8 ? v : kind.defaultOpacity;
    } catch (e) { return kind.defaultOpacity; } // storage blocked: just use the default
  }

  function saveOpacity(kind, v) {
    try { window.localStorage.setItem("nadlan.parcelFill.entry." + kind.key, String(v)); } catch (e) { /* per-browser convenience only */ }
  }

  function rgba(hex, alpha) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + (n >> 16) + "," + (n >> 8 & 255) + "," + (n & 255) + "," + alpha + ")";
  }

  /** Where a label goes: the outer ring's area centroid (computed around its first corner, so lon/lat stay precise). */
  function centroid(geometry) {
    var r = geometry.coordinates[0], x0 = r[0][0], y0 = r[0][1], a = 0, cx = 0, cy = 0;
    for (var i = 0; i < r.length - 1; i++) {
      var x1 = r[i][0] - x0, y1 = r[i][1] - y0, x2 = r[i + 1][0] - x0, y2 = r[i + 1][1] - y0;
      var f = x1 * y2 - x2 * y1;
      a += f; cx += (x1 + x2) * f; cy += (y1 + y2) * f;
    }
    if (Math.abs(a) < 1e-18) { return { lng: x0, lat: y0 }; }
    return { lng: x0 + cx / (3 * a), lat: y0 + cy / (3 * a) };
  }

  /** HTML labels pinned to map points (e.g. "171a / 3" inside each polygon of a row entry). Clicks pass through. */
  function createLabelLayer(map) {
    var box = document.createElement("div");
    box.className = "parcel-labels";
    var entries = [];
    var view = new google.maps.OverlayView();
    view.onAdd = function () { view.getPanes().floatPane.appendChild(box); };
    view.onRemove = function () { if (box.parentNode) { box.parentNode.removeChild(box); } };
    view.draw = function () {
      var projection = view.getProjection();
      if (!projection) { return; }
      entries.forEach(function (e) {
        var pt = projection.fromLatLngToDivPixel(new google.maps.LatLng(e.at.lat, e.at.lng));
        e.el.style.left = pt.x + "px";
        e.el.style.top = pt.y + "px";
      });
    };
    view.setMap(map);
    return {
      /** @param {Array<{ at: {lat:number,lng:number}, text: string, badge?: string, warn?: boolean }>} list */
      set: function (list) {
        box.innerHTML = "";
        entries = list.map(function (l) {
          var el = document.createElement("div");
          el.className = "parcel-label" + (l.warn ? " warn" : "") + (l.text ? "" : " empty");
          if (l.badge) { var b = document.createElement("span"); b.className = "parcel-label-badge"; b.textContent = l.badge; el.appendChild(b); }
          el.appendChild(document.createTextNode(l.text || "…"));
          box.appendChild(el);
          return { at: l.at, el: el };
        });
        view.draw();
      }
    };
  }

  /** Hover card at the cursor: OT / plot big, then KAEK, area, size, Asset. */
  function createTooltip() {
    var esc = Nadlan.format.escapeHtml;
    var tip = document.createElement("div");
    tip.className = "parcel-tip";
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
    document.addEventListener("mousedown", function () { tip.hidden = true; }, { passive: true }); // dragging the map
    return {
      show: function (s) {
        var kind = kindOf(s);
        var numbers = s.ot || s.plot
          ? "<span class=\"tip-label\">OT</span> <b>" + esc(s.ot || "—") + "</b> <span class=\"tip-sep\">/</span> <span class=\"tip-label\">Plot</span> <b>" + esc(s.plot || "—") + "</b>"
          : "<span class=\"tip-missing\">No OT / plot yet</span>";
        var facts = [];
        if (s.registryId) { facts.push((s.registryIdIsProvisional ? "Provisional " : "KAEK ") + esc(s.registryId)); }
        if (s.geographicArea) { facts.push(esc(s.geographicArea)); }
        if (s.officialAreaSqm) { facts.push(esc(Nadlan.format.sqm(s.officialAreaSqm))); }
        tip.style.borderLeftColor = kind.strokeColor;
        tip.innerHTML = "<div class=\"tip-numbers\">" + numbers + "</div>" +
          (facts.length ? "<div class=\"tip-facts\">" + facts.join(" · ") + "</div>" : "") +
          (s.hasAssets ? "<div class=\"tip-tags\"><span class=\"tip-tag\">Has Asset</span></div>" : "");
        tip.hidden = false;
        place();
      },
      hide: function () { tip.hidden = true; }
    };
  }

  /**
   * @param {google.maps.Map} map
   * @param {{ onClick: function(object, Event): void, onSurfaceClick?: function(google.maps.LatLng): void,
   *           onFilterChange?: function(): void }} options
   *        onClick receives the Parcel summary and the click; onSurfaceClick the point clicked on the zoomed-out surface;
   *        onFilterChange fires when a legend checkbox changes (the page reloads with getFilter()).
   */
  function createParcelMapOverlay(map, options) {
    var layer = new google.maps.Data({ map: map });
    // Zoomed out: all Parcels united into one surface per kind (server-side, see ParcelCoverageService) instead of
    // thousands of polygons. Same colours and opacity sliders; a click zooms in there.
    var surface = new google.maps.Data({ map: map });
    var labels = createLabelLayer(map);
    var tooltip = createTooltip();
    var selectedId = null;
    var group = {};        // parcelId -> true: picked for a row entry
    var centroids = {};    // parcelId -> label position, kept while the Parcel scrolls out of the results
    var geometries = {};   // parcelId -> geometry of the Parcels drawn now
    var interactive = true;
    var visible = true;
    var opacity = {};
    KINDS.forEach(function (k) { opacity[k.key] = loadOpacity(k); });
    // Legend checkboxes = the colour filter. Not remembered: a reload shows everything again.
    var shown = { done: true, partial: true, todo: true, withAssets: true, withoutAssets: true };

    layer.setStyle(function (feature) {
      var summary = feature.getProperty("summary");
      var kind = kindOf(summary);
      var fill = opacity[kind.key];
      var style = {
        clickable: interactive, strokeColor: kind.strokeColor, strokeWeight: kind.strokeWeight,
        fillColor: kind.fillColor, fillOpacity: fill, zIndex: kind.zIndex
      };
      if (summary && summary.hasAssets) { Object.assign(style, ASSET_OUTLINE, { zIndex: kind.zIndex + 2 }); }
      if (group[feature.getId()]) { Object.assign(style, STYLE_GROUP, { fillOpacity: Math.min(0.85, fill + 0.3), zIndex: 10 }); }
      else if (feature.getId() === selectedId) { Object.assign(style, STYLE_SELECTED, { fillOpacity: Math.min(0.85, fill + 0.25) }); }
      return style;
    });

    // Per-feature override (Google's hover pattern): touches one polygon, not a restyle of the whole layer.
    layer.addListener("mouseover", function (e) {
      var summary = e.feature.getProperty("summary");
      var fill = opacity[kindOf(summary).key];
      layer.overrideStyle(e.feature, { strokeWeight: 3.5, fillOpacity: Math.min(0.85, fill + 0.2) });
      if (interactive) { tooltip.show(summary); }
    });
    layer.addListener("mouseout", function (e) { layer.revertStyle(e.feature); tooltip.hide(); });

    surface.setStyle(function (feature) {
      var kind = BY_KEY[feature.getProperty("kind")] || BY_KEY.todo;
      return { visible: shown[kind.key], clickable: interactive, strokeColor: kind.strokeColor, strokeWeight: 1, fillColor: kind.fillColor,
        fillOpacity: Math.max(opacity[kind.key], 0.15), zIndex: kind.zIndex };
    });
    surface.addListener("click", function (e) { if (options.onSurfaceClick) { options.onSurfaceClick(e.latLng); } });
    layer.addListener("click", function (e) {
      tooltip.hide();
      selectedId = e.feature.getId();
      refresh();
      options.onClick(e.feature.getProperty("summary"), e.domEvent);
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
        "<span class=\"legend-count\" data-count=\"" + kind.key + "\" title=\"How many match the search (visible map / rectangle / everywhere, with the filters)\"></span>" +
        "<input type=\"range\" min=\"0\" max=\"80\" step=\"5\" data-fill=\"" + kind.key + "\" title=\"Fill opacity\">" +
        "<span class=\"pct\" data-pct=\"" + kind.key + "\"></span></div>";
    }
    legend.innerHTML = "<div class=\"legend-title\">OT / plot entry</div>" + KINDS.map(kindRow).join("") +
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

    function paintLegend() {
      KINDS.forEach(function (k) {
        legend.querySelector("[data-swatch=" + k.key + "]").style.cssText = "background:" + rgba(k.fillColor, Math.max(opacity[k.key], 0.25)) + ";border-color:" + k.strokeColor;
        legend.querySelector("[data-pct=" + k.key + "]").textContent = Math.round(opacity[k.key] * 100) + "%";
      });
    }

    function showGroupLabels(list) {
      labels.set(list.filter(function (l) { return centroids[l.parcelId]; }).map(function (l) {
        return { at: centroids[l.parcelId], text: l.text, badge: l.badge, warn: l.warn };
      }));
    }
    var groupLabels = [];

    return {
      /** @param {Array<{ summary: object, geometry: object }>} items  as returned by GET /api/parcels */
      setItems: function (items) {
        var existing = [];
        layer.forEach(function (f) { existing.push(f); });
        existing.forEach(function (f) { layer.remove(f); });
        geometries = {};
        items.forEach(function (it) { geometries[it.summary.parcelId] = it.geometry; });
        layer.addGeoJson({
          type: "FeatureCollection",
          features: items.map(function (it) {
            return { type: "Feature", id: it.summary.parcelId, geometry: it.geometry, properties: { summary: it.summary } };
          })
        });
        tooltip.hide();
      },
      /** @param {{ done: object, partial: object, todo: object }|null} geo  MultiPolygons from GET /api/parcels/coverage; null = none */
      setSurface: function (geo) {
        surface.forEach(function (f) { surface.remove(f); });
        if (!geo) { return; }
        surface.addGeoJson({ type: "FeatureCollection", features: KINDS.map(function (k) {
          return { type: "Feature", geometry: geo[k.key], properties: { kind: k.key } };
        }).filter(function (f) { return f.geometry && f.geometry.coordinates.length > 0; }) });
      },
      /** @param {object|null} counts  kindCounts of the last search ({ done: n, ... }); null = unknown (blank) */
      setCounts: function (counts) {
        KINDS.forEach(function (k) {
          legend.querySelector("[data-count=" + k.key + "]").textContent = counts ? (counts[k.key] || 0).toLocaleString("en-US") : "";
        });
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
      /**
       * The Parcels picked for a row entry, outlined, each with its planned "OT / plot" inside.
       * @param {Array<{ parcelId: number, text: string, badge?: string, warn?: boolean }>} list  empty = none
       */
      setGroup: function (list) {
        group = {};
        list.forEach(function (l) {
          group[l.parcelId] = true;
          if (!centroids[l.parcelId] && geometries[l.parcelId]) { centroids[l.parcelId] = centroid(geometries[l.parcelId]); }
        });
        groupLabels = list;
        showGroupLabels(visible ? list : []);
        refresh();
      },
      select: function (parcelId) { selectedId = parcelId; refresh(); },
      clearSelection: function () { selectedId = null; refresh(); },
      // Off while drawing, so clicks reach the map instead of the polygons.
      setInteractive: function (value) { interactive = value; if (!value) { tooltip.hide(); } refresh(); },
      setVisible: function (value) {
        visible = value;
        layer.setMap(value ? map : null);
        surface.setMap(value ? map : null);
        legend.style.display = value ? "" : "none";
        showGroupLabels(value ? groupLabels : []);
        if (!value) { tooltip.hide(); }
      }
    };
  }

  Nadlan.createParcelMapOverlay = createParcelMapOverlay;
  Nadlan.parcelCentroid = centroid;
})(window);
