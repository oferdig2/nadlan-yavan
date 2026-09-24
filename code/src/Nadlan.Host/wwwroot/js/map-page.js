// Map page composition: wires components together and owns the query state. No component talks to another directly.
(function (window, document, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var $status = $("#map-status");

  function setStatus(text, isError) {
    $status.text(text || "").toggleClass("error", !!isError);
  }

  function loadGoogleMaps(apiKey) {
    return new Promise(function (resolve, reject) {
      if (!apiKey) {
        reject(new Error("Google Maps API key is not configured: .\\config.ps1 set ms:host Nadlan:Maps:GoogleApiKey <key>"));
        return;
      }
      window.__nadlanMapsReady = resolve;
      var script = document.createElement("script");
      script.src = "https://maps.googleapis.com/maps/api/js?key=" + encodeURIComponent(apiKey) + "&v=weekly&callback=__nadlanMapsReady";
      script.async = true;
      script.onerror = function () { reject(new Error("Google Maps failed to load.")); };
      document.head.appendChild(script);
    });
  }

  function start(config, ref) {
    Nadlan.clientConfig = config; // read-only settings for components (storage on/off, max file size)
    var map = new google.maps.Map(document.getElementById("map"), {
      center: config.defaultCenter, zoom: config.defaultZoom, mapTypeId: "hybrid",
      streetViewControl: false, fullscreenControl: false, gestureHandling: "greedy"
    });

    var state = { mode: "parcels", scope: "view", rectangle: null, items: [] };
    var selection = Nadlan.createAssetSelection();
    var parcelOverlay, assetOverlay;

    var popups = Nadlan.createMapEntityPopupManager({
      container: document.getElementById("popup-layer"),
      selection: selection,
      onClose: function () { parcelOverlay.clearSelection(); assetOverlay.clearFocus(); },
      onCreateAsset: function (parcel) {
        Nadlan.assetEditor.open({ parcel: parcel }).then(function (saved) {
          if (!saved) { return; }
          popups.refreshParcel(parcel.parcelId);
          if (state.mode === "assets") { reload(); }
          setStatus("Asset #" + saved.assetId + " created.");
        });
      },
      onEditAsset: function (assetId, parcel) {
        Nadlan.assetEditor.open({ parcel: parcel, assetId: assetId }).then(function (saved) {
          if (!saved) { return; }
          popups.refreshParcel(parcel.parcelId);
          if (state.mode === "assets") { reload(); }
        });
      },
      onOpenFiles: function (type, id, title, parcelId) {
        Nadlan.filesDialog.open({
          attachedToType: type, attachedToId: id, title: title,
          onChanged: function () { popups.refreshParcel(parcelId); }
        }).catch(function (err) { setStatus(err.message, true); });
      }
    });

    parcelOverlay = Nadlan.createParcelMapOverlay(map, { onClick: function (p) { popups.showParcel(p); } });
    assetOverlay = Nadlan.createAssetMapOverlay(map, {
      onClick: function (group) { popups.showParcel(group); },
      isSelected: function (id) { return selection.has(id); }
    });
    assetOverlay.setVisible(false);
    selection.onChange(function () { assetOverlay.refreshStyles(); });

    // While a drawing tool is active, polygons must not swallow the clicks.
    function setDrawing(active) {
      parcelOverlay.setInteractive(!active);
      assetOverlay.setInteractive(!active);
      $("body").toggleClass("is-drawing", active);
    }

    var rectangle = Nadlan.createMapRectangleSelector(map, {
      onDrawingChange: setDrawing,
      onChange: function (bounds) {
        state.rectangle = bounds;
        filters.setRectangleActive(!!bounds);
        if (bounds) { setScope("rectangle"); } else if (state.scope === "rectangle") { setScope("view"); }
        reload();
      }
    });

    var drawTool = Nadlan.createParcelDrawTool(map, {
      onDrawingChange: setDrawing,
      onShapeChange: function (hasShape) { Nadlan.parcelEditor.notifyShapeChange(hasShape); }
    });

    var filters = Nadlan.initializeMapFilterPanel(document.getElementById("filters"), {
      reference: ref,
      onApply: reload,
      onScopeChange: function (scope) {
        state.scope = scope;
        if (scope === "rectangle" && !state.rectangle) { rectangle.start(); setStatus("Drag on the map to draw the search rectangle."); return; }
        reload();
      },
      onDrawRectangle: function () { rectangle.start(); setStatus("Drag on the map to draw the search rectangle."); },
      onClearRectangle: function () { rectangle.clear(); }
    });

    var results = Nadlan.initializeResultsPanel(document.getElementById("results"), {
      selection: selection,
      onItemClick: function (it) {
        fitGeometry(it.geometry, 17);
        if (state.mode === "assets") { assetOverlay.focus(it.summary.parcelId); } else { parcelOverlay.select(it.summary.parcelId); }
        popups.showParcel(it.summary);
      },
      onZoomToResults: function () { fitItems(state.items); },
      onAddToPortfolio: function () {
        Nadlan.portfolioPicker.open(selection.values()).then(function (r) {
          if (!r) { return; }
          setStatus(r.added + " asset(s) added to \"" + r.name + "\".");
          selection.clear();
        });
      }
    });

    function setScope(scope) {
      state.scope = scope;
      filters.setScope(scope);
    }

    function setMode(mode) {
      state.mode = mode;
      $("[data-view]").removeClass("active").filter("[data-view=" + mode + "]").addClass("active");
      parcelOverlay.setVisible(mode === "parcels");
      assetOverlay.setVisible(mode === "assets");
      filters.setMode(mode);
      results.setMode(mode);
      reload();
    }

    function currentArea() {
      if (state.scope === "rectangle") { return state.rectangle; }
      if (state.scope === "everywhere") { return null; }
      var b = map.getBounds();
      return b ? { west: b.getSouthWest().lng(), south: b.getSouthWest().lat(), east: b.getNorthEast().lng(), north: b.getNorthEast().lat() } : null;
    }

    var requestSeq = 0;
    function reload() {
      var filter = filters.getFilter(state.mode);
      if (!filter) { return; }
      if (state.scope === "rectangle" && !state.rectangle) { results.setMessage("Draw a rectangle to search in."); return; }

      var query = $.extend({}, filter, currentArea() || {});
      var url = state.mode === "assets" ? "/api/assets" : "/api/parcels";
      var seq = ++requestSeq;
      setStatus("Searching…");
      Nadlan.api.get(url, query).then(function (res) {
        if (seq !== requestSeq) { return; } // superseded by a newer search
        state.items = res.items;
        (state.mode === "assets" ? assetOverlay : parcelOverlay).setItems(res.items);
        results.setItems(res.items, res.truncated);
        setStatus("");
      }, function (err) {
        if (seq === requestSeq) { setStatus("Search failed: " + err.message, true); }
      });
    }

    function fitGeometry(geometry, maxZoom) {
      var bounds = new google.maps.LatLngBounds();
      geometry.coordinates[0].forEach(function (p) { bounds.extend({ lng: p[0], lat: p[1] }); });
      map.fitBounds(bounds, 120);
      google.maps.event.addListenerOnce(map, "idle", function () { if (map.getZoom() > maxZoom) { map.setZoom(maxZoom); } });
    }

    function fitItems(items) {
      if (!items.length) { return; }
      var bounds = new google.maps.LatLngBounds();
      items.forEach(function (it) { it.geometry.coordinates[0].forEach(function (p) { bounds.extend({ lng: p[0], lat: p[1] }); }); });
      map.fitBounds(bounds, 60);
    }

    function showParcelById(parcelId) {
      Nadlan.api.get("/api/parcels/" + parcelId).then(function (d) {
        fitGeometry(d.geometry, 18);
        popups.showParcel(d.summary);
      }, function (err) { setStatus(err.message, true); });
    }

    // Only the "visible map" scope follows panning/zooming; the others are fixed searches.
    map.addListener("idle", function () { if (state.scope === "view") { reload(); } });

    $("[data-view]").on("click", function () { setMode($(this).data("view")); });
    $("[data-tool=new-parcel]").on("click", function () {
      if (state.mode !== "parcels") { setMode("parcels"); }
      Nadlan.parcelEditor.open({
        drawTool: drawTool,
        onCreated: function (parcelId) {
          setStatus("Parcel created.");
          reload();
          showParcelById(parcelId);
        },
        onShowParcel: showParcelById
      });
    });

    filters.setMode("parcels");
    results.setMode("parcels");
  }

  Promise.all([Nadlan.api.get("/api/config/client"), Nadlan.reference.load()])
    .then(function (loaded) {
      return loadGoogleMaps(loaded[0].googleMapsApiKey).then(function () { start(loaded[0], loaded[1]); });
    })
    .catch(function (err) { setStatus(err.message, true); });
})(window, document, jQuery);
