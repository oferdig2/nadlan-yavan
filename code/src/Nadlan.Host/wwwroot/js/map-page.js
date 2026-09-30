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
      // Google calls this when it rejects the key (wrong key, API not enabled, referrer not allowed).
      window.gm_authFailure = function () {
        setStatus("Google Maps rejected the API key - check it is enabled for the Maps JavaScript API and allows " + window.location.origin, true);
      };
      var script = document.createElement("script");
      script.src = "https://maps.googleapis.com/maps/api/js?key=" + encodeURIComponent(apiKey) + "&v=weekly&loading=async&callback=__nadlanMapsReady";
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

    var state = { mode: "parcels", scope: "view", items: [], fitAfterLoad: false }; // the rectangle lives in rectangle.getBounds()
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
        }).catch(function (err) { setStatus("Could not open the asset editor: " + err.message, true); });
      },
      onEditAsset: function (assetId, parcel) {
        Nadlan.assetEditor.open({ parcel: parcel, assetId: assetId }).then(function (saved) {
          if (!saved) { return; }
          popups.refreshParcel(parcel.parcelId);
          if (state.mode === "assets") { reload(); }
        }).catch(function (err) { setStatus("Could not open the asset editor: " + err.message, true); });
      },
      onEditParcel: function (parcelId) { editParcel(parcelId); },
      onOpenFiles: function (type, id, title, parcelId) {
        Nadlan.filesDialog.open({
          attachedToType: type, attachedToId: id, title: title,
          onChanged: function () { popups.refreshParcel(parcelId); }
        }).catch(function (err) { setStatus(err.message, true); });
      }
    });

    parcelOverlay = Nadlan.createParcelMapOverlay(map, {
      onClick: function (p) { popups.showParcel(p); },
      onSurfaceClick: function (latLng) { map.setCenter(latLng); map.setZoom(config.parcelDetailMinZoom || 15); }
    });
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
      onCancel: function () { reload(); }, // Escape: search again with whatever rectangle (if any) is left
      onDrawingChange: setDrawing,
      onChange: function (bounds) {
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
        if (scope !== "rectangle") { rectangle.cancel(); } // leaving rectangle mode must give the map its gestures back
        if (scope === "rectangle" && !rectangle.getBounds()) { startRectangle(); return; }
        reload();
      },
      onDrawRectangle: startRectangle,
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
        }).catch(function (err) { setStatus(err.message, true); });
      }
    });

    // start() drops the old rectangle silently, so bring the panel and (in rectangle scope) the results in line.
    function startRectangle() {
      rectangle.start();
      filters.setRectangleActive(false);
      if (state.scope === "rectangle") { reload(); }
      setStatus("Drag on the map to draw the search rectangle (Esc cancels).");
    }

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
      if (state.scope === "rectangle") { return rectangle.getBounds(); }
      if (state.scope === "everywhere") { return null; }
      var b = map.getBounds();
      return b ? { west: b.getSouthWest().lng(), south: b.getSouthWest().lat(), east: b.getNorthEast().lng(), north: b.getNorthEast().lat() } : null;
    }

    var requestSeq = 0;
    function reload() {
      // Claim a sequence number first: any search still in flight (maybe for the other mode) is now stale,
      // even if this reload can't run a search of its own.
      var seq = ++requestSeq;
      var mode = state.mode;
      var filter = filters.getFilter(mode);
      if (!filter) { clearResults("Fix the filter to search."); return; }
      if (state.scope === "rectangle" && !rectangle.getBounds()) { clearResults("Draw a rectangle to search in."); return; }

      var query = $.extend({}, filter, currentArea() || {});
      if (overview(mode, filter)) { loadOverview(seq, query); return; }
      showSurface(null);
      var url = mode === "assets" ? "/api/assets" : "/api/parcels";
      setStatus("Searching…");
      Nadlan.api.get(url, query).then(function (res) {
        if (seq !== requestSeq || mode !== state.mode) { return; } // superseded by a newer search / mode switch
        state.items = res.items;
        (mode === "assets" ? assetOverlay : parcelOverlay).setItems(res.items);
        results.setItems(res.items, res.truncated);
        if (state.fitAfterLoad) { state.fitAfterLoad = false; fitItems(res.items); }
        setStatus("");
      }, function (err) {
        if (seq === requestSeq) { setStatus("Search failed: " + err.message, true); }
      });
    }

    // ---- Zoomed out: one surface instead of thousands of polygons (drawing them all is what slows the map) --------
    var detailZoom = config.parcelDetailMinZoom || 15;
    var seesAllParcels = Nadlan.session.canAny(["VIEW_ALL_PARCELS", "EDIT_ALL_PARCELS"]);
    var surfaces = {};      // level -> { version, surface } from GET /api/parcels/coverage
    var shownSurface = null; // "level|version" on the map now

    // Not with a KAEK/area filter (that set is small, and the surface would show every Parcel), and not for users who
    // see only some Parcels: they get their own polygons at any zoom.
    function overview(mode, filter) {
      return mode === "parcels" && seesAllParcels && map.getZoom() < detailZoom && !filter.registryId && !filter.areaIds.length;
    }

    function showSurface(level) {
      var key = level ? level + "|" + surfaces[level].version : null;
      if (key === shownSurface) { return; }
      shownSurface = key;
      parcelOverlay.setSurface(level ? surfaces[level].surface : null);
    }

    function loadOverview(seq, query) {
      var level = map.getZoom() < 12 ? "overview" : "mid";
      setStatus("Searching…");
      Nadlan.api.get("/api/parcels/count", query).then(function (res) {
        if (seq !== requestSeq || state.mode !== "parcels") { return; }
        state.items = [];
        parcelOverlay.setItems([]);
        results.setMessage(res.count.toLocaleString("en-US") + " parcel(s) here. Zoom in to see them one by one and to list them - or click the surface.");
        setStatus("");
        var cached = surfaces[level];
        if (cached && cached.version === res.coverageVersion) { showSurface(level); return; }
        loadSurface(seq, level);
      }, function (err) { if (seq === requestSeq) { setStatus("Search failed: " + err.message, true); } });
    }

    function loadSurface(seq, level) {
      Nadlan.api.get("/api/parcels/coverage", { level: level }).then(function (res) {
        if (seq !== requestSeq || state.mode !== "parcels") { return; }
        if (res.pending) { // the server is computing it (just after a start)
          setStatus("Preparing the overview…");
          setTimeout(function () { if (seq === requestSeq) { loadSurface(seq, level); } }, 2000);
          return;
        }
        surfaces[level] = { version: res.version, surface: res.surface };
        showSurface(level);
        setStatus("");
      }, function (err) { if (seq === requestSeq) { setStatus("Overview failed: " + err.message, true); } });
    }

    // Nothing from another mode or an older filter may stay on screen (e.g. Parcels shown as Assets).
    function clearResults(message) {
      state.items = [];
      (state.mode === "assets" ? assetOverlay : parcelOverlay).setItems([]);
      results.setMessage(message);
      setStatus("");
    }

    function fitGeometry(geometry, maxZoom) {
      var bounds = new google.maps.LatLngBounds();
      geometry.coordinates[0].forEach(function (p) { bounds.extend({ lng: p[0], lat: p[1] }); });
      map.fitBounds(bounds, 120);
      // If the view didn't change, "idle" never fires for this fit - drop the listener so it can't snap
      // the zoom on the user's next pan.
      var once = google.maps.event.addListenerOnce(map, "idle", function () { if (map.getZoom() > maxZoom) { map.setZoom(maxZoom); } });
      setTimeout(function () { google.maps.event.removeListener(once); }, 1500);
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
    // "Show on map" from a Portfolio: Assets view, only that Portfolio, searched everywhere, then zoom to it.
    function showPortfolio(portfolio) {
      filters.showOnlyPortfolio(portfolio);
      setScope("everywhere");
      state.fitAfterLoad = true;
      if (state.mode === "assets") { reload(); } else { setMode("assets"); }
      setStatus("Showing portfolio \"" + portfolio.name + "\".");
    }

    $("[data-tool=portfolios]").on("click", function () {
      Nadlan.portfolioPanel.openList({ onShowOnMap: showPortfolio });
    });

    $("[data-tool=new-parcel]").on("click", function () {
      if (state.mode !== "parcels") { setMode("parcels"); }
      Nadlan.parcelEditor.open({
        drawTool: drawTool,
        onSaved: function (parcelId) {
          setStatus("Parcel created.");
          reload();
          showParcelById(parcelId);
        },
        onShowParcel: showParcelById
      }).catch(function (err) { setStatus(err.message, true); });
    });

    function editParcel(parcelId) {
      Nadlan.parcelEditor.open({
        drawTool: drawTool,
        parcelId: parcelId,
        onDeleted: function (id, name) {
          popups.closeParcel(id);
          parcelOverlay.clearSelection();
          setStatus("Parcel " + name + " deleted.");
          reload();
        },
        onSaved: function () {
          setStatus("Parcel saved.");
          popups.refreshParcel(parcelId);
          reload();
        },
        onShowParcel: showParcelById
      }).catch(function (err) { setStatus(err.message, true); });
    }

    filters.setMode("parcels");
    results.setMode("parcels");
    // Without all Parcels (Attorney, Buyer, ...) the Parcels view shows little: start where their Assets are.
    if (!Nadlan.session.canAny(["VIEW_ALL_PARCELS", "EDIT_ALL_PARCELS"])) { setMode("assets"); }
  }

  Promise.all([Nadlan.api.get("/api/config/client"), Nadlan.reference.load(), Nadlan.session.ready])
    .then(function (loaded) {
      return loadGoogleMaps(loaded[0].googleMapsApiKey).then(function () { start(loaded[0], loaded[1]); });
    })
    .catch(function (err) { setStatus(err.message, true); });
})(window, document, jQuery);
