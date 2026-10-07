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

  /** @param {object|null} extent  from GET /api/parcels/extent: where the user's Parcels lie (Skroponeria first) */
  function start(config, ref, extent) {
    Nadlan.clientConfig = config; // read-only settings for components (storage on/off, max file size)
    var map = new google.maps.Map(document.getElementById("map"), {
      center: config.defaultCenter, zoom: config.defaultZoom, mapTypeId: "hybrid",
      streetViewControl: false, fullscreenControl: false, gestureHandling: "greedy"
    });

    // Open on the Parcels, not on a fixed point: fitted before the first search runs (that waits for "idle").
    if (extent && extent.count) {
      map.fitBounds({ west: extent.west, south: extent.south, east: extent.east, north: extent.north }, 40);
      google.maps.event.addListenerOnce(map, "idle", function () { if (map.getZoom() > 17) { map.setZoom(17); } }); // one Parcel: not max zoom
    }

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
          reload("Asset #" + saved.assetId + " created."); // both views: in Parcels view its outline and the "Has Asset" filter change
        }).catch(function (err) { setStatus("Could not open the asset editor: " + err.message, true); });
      },
      onEditAsset: function (assetId, parcel) {
        Nadlan.assetEditor.open({ parcel: parcel, assetId: assetId }).then(function (saved) {
          if (!saved) { return; }
          popups.refreshParcel(parcel.parcelId);
          reload(); // both views: price/status in Assets view, the "Has Asset" colour in Parcels view
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
      onClick: function (p, domEvent) { if (quickEntryOn()) { openQuickEntry(p, domEvent); } else { popups.showParcel(p); } },
      // Zoom in where the surface was clicked: to the detail zoom, or further when already there ("too many" view).
      onSurfaceClick: function (latLng) { map.setCenter(latLng); map.setZoom(Math.min(20, Math.max(config.parcelDetailMinZoom || 15, map.getZoom() + 2))); },
      // Legend checkboxes (colour filter): search again with them.
      onFilterChange: function () { if (state.mode === "parcels") { reload(); } }
    });
    assetOverlay = Nadlan.createAssetMapOverlay(map, {
      onClick: function (group) { popups.showParcel(group); },
      isSelected: function (id) { return selection.has(id); }
    });
    assetOverlay.setVisible(false);
    selection.onChange(function () { assetOverlay.refreshStyles(); });

    // "Where am I": centre on the user; once the Parcels there are loaded, say (and highlight) which one they stand on.
    Nadlan.myLocation.createMyLocationControl(map, {
      onError: function (message) { setStatus(message, true); },
      onLocated: function (p) {
        state.standingAt = p;
        setStatus(p.accuracy > 150 ? "Your location is rough (± " + Math.round(p.accuracy) + " m) - GPS outdoors is more exact." : "");
        // Already centred there (the map didn't move, so no new search): use the Parcels already shown.
        var seqAtFix = requestSeq;
        setTimeout(function () { if (state.standingAt === p && requestSeq === seqAtFix) { showWhereIStand(state.items); } }, 1500);
      }
    });

    // Quick OT/plot entry (Parcels view, Parcel editors): a click opens the small OT/plot form instead of the card.
    var $quick = $("[data-role=quick-entry]");
    var $digits = $("[data-role=ot-digits]").val(Nadlan.parcelQuickEntry.getDigits() || "");
    function quickEntryOn() { return $quick.prop("checked") && !$quick.closest("[hidden]").length; }
    $quick.on("change", function () {
      $(".quick-digits").prop("hidden", !this.checked);
      if (!this.checked) { Nadlan.parcelQuickEntry.close(); }
      setStatus(this.checked ? "Quick entry: click a parcel, type OT and plot, Enter saves." : "");
    });
    $digits.on("input", function () {
      var v = String(this.value).replace(/\D/g, "").replace(/^0+/, "");
      this.value = v;
      Nadlan.parcelQuickEntry.setDigits(v ? Number(v) : null);
    });

    function openQuickEntry(p, domEvent) {
      Nadlan.parcelQuickEntry.open(p, domEvent, {
        onSaved: function (parcelId, message) {
          popups.refreshParcel(parcelId);
          reload(message); // its colour can change (an OT turns a red provisional parcel orange)
        },
        onOpenCard: function (summary) { popups.showParcel(summary); },
        onClose: function () { parcelOverlay.clearSelection(); }
      });
    }

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
      $(".quick-entry-setting").toggleClass("off-view", mode !== "parcels"); // Parcels view only
      if (mode !== "parcels") { Nadlan.parcelQuickEntry.close(); }
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
    // doneMessage: shown once the search finished (e.g. "Asset #12 created."), instead of clearing the status line.
    function reload(doneMessage) {
      // Claim a sequence number first: any search still in flight (maybe for the other mode) is now stale,
      // even if this reload can't run a search of its own.
      var seq = ++requestSeq;
      var mode = state.mode;
      var filter = filters.getFilter(mode);
      if (!filter) { clearResults("Fix the filter to search."); return; }
      if (mode === "parcels") {
        var colours = parcelOverlay.getFilter(); // the legend's checkboxes
        if (colours.nothing) { showSurface(null); clearResults("Tick at least one colour in the legend (and one of Has Asset / No Asset)."); return; }
        filter = $.extend({}, filter, colours);
      }
      if (state.scope === "rectangle" && !rectangle.getBounds()) { clearResults("Draw a rectangle to search in."); return; }

      var query = $.extend({}, filter, currentArea() || {});
      if (overview(mode, filter)) { loadOverview(seq, query); return; }
      var url = mode === "assets" ? "/api/assets" : "/api/parcels";
      // Parcels are never capped: past Maps:MaxParcelPolygons in view the server answers "too many" and the surface is drawn.
      if (mode === "parcels" && surfaceAllowed(filter)) { query.allowSurface = true; }
      setStatus("Searching…");
      Nadlan.api.get(url, query).then(function (res) {
        if (seq !== requestSeq || mode !== state.mode) { return; } // superseded by a newer search / mode switch
        if (res.tooMany) { showTooMany(seq, res); return; }
        if (mode === "parcels") { showSurface(null); }
        state.items = res.items;
        (mode === "assets" ? assetOverlay : parcelOverlay).setItems(res.items);
        results.setItems(res.items, res.truncated);
        if (state.fitAfterLoad) { state.fitAfterLoad = false; fitItems(res.items); }
        setStatus(typeof doneMessage === "string" ? doneMessage : "");
        if (state.standingAt) { showWhereIStand(res.items); }
      }, function (err) {
        if (seq === requestSeq) { setStatus("Search failed: " + err.message, true); }
      });
    }

    // ---- Zoomed out: one surface instead of thousands of polygons (drawing them all is what slows the map) --------
    var detailZoom = config.parcelDetailMinZoom || 15;
    var seesAllParcels = Nadlan.session.canAny(["VIEW_ALL_PARCELS", "EDIT_ALL_PARCELS"]);
    var surfaces = {};      // level -> { version, surface } from GET /api/parcels/coverage
    var shownSurface = null; // "level|version" on the map now

    // Not with a KAEK/area/Asset filter (that set is small, and the surface would show every Parcel), and not for users
    // who see only some Parcels: they get their own polygons at any zoom. A colour (kind) filter is fine: the surface
    // has one layer per colour and hides the unticked ones.
    function surfaceAllowed(filter) {
      return seesAllParcels && !filter.registryId && !filter.ot && !filter.plot && !filter.areaIds.length && filter.hasAssets === undefined;
    }

    function overview(mode, filter) {
      return mode === "parcels" && surfaceAllowed(filter) && map.getZoom() < detailZoom;
    }

    // Detail zoom, but more Parcels in view than are drawn one by one: the fine surface instead of dropping any.
    function showTooMany(seq, res) {
      state.items = [];
      parcelOverlay.setItems([]);
      if (!res.surface) { // a filter or a partial view: no surface stands for this set, so ask to narrow it
        showSurface(null);
        results.setMessage(res.count.toLocaleString("en-US") + " parcels match - too many to draw. " +
          (state.scope === "everywhere" ? "Search the visible map or a drawn rectangle, or narrow the filter." : "Zoom in, or narrow the filter."));
        setStatus("");
        return;
      }
      results.setMessage(res.count.toLocaleString("en-US") + " parcels - too many to draw one by one, so they show as one surface. " +
        (state.scope === "view" ? "Zoom in a little to see them separately." : "Search the visible map, or draw a smaller rectangle, to see them separately."));
      setStatus("");
      var cached = surfaces.fine;
      if (cached && cached.version === res.coverageVersion) { showSurface("fine"); return; }
      loadSurface(seq, "fine");
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
        results.setMessage(res.count.toLocaleString("en-US") + " parcel(s). Zoom in to see them one by one and to list them - or click the surface.");
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

    // After centring on the user: the Parcel (or Asset's Parcel) under their feet, if any is in the results.
    function showWhereIStand(items) {
      var p = state.standingAt;
      state.standingAt = null;
      var found = Nadlan.myLocation.standingOn(p, items, function (it) { return [it.geometry]; });
      if (found.rough) {
        setStatus("Your location is ± " + Math.round(p.accuracy) + " m - too rough to tell which Parcel you're on (GPS outdoors is more exact).");
        return;
      }
      // One row per Parcel (in Assets view several Assets can stand on the same one).
      var parcels = [];
      found.hits.forEach(function (it) { if (!parcels.some(function (x) { return x.parcelId === it.summary.parcelId; })) { parcels.push(it.summary); } });
      if (!parcels.length) { setStatus("You're not on a Parcel in the results here."); return; }
      var name = function (s) { return s.registryId || "#" + s.parcelId; };
      if (parcels.length > 1) {
        // Overlapping Parcels (e.g. a provisional one over a real KAEK): say so instead of picking one.
        setStatus("You're where " + parcels.length + " Parcels overlap: " + parcels.map(name).join(", ") + ".");
        return;
      }
      if (state.mode === "assets") { assetOverlay.focus(parcels[0].parcelId); } else { parcelOverlay.select(parcels[0].parcelId); }
      setStatus("You're standing on Parcel " + name(parcels[0]) + (p.accuracy > 10 ? " (± " + Math.round(p.accuracy) + " m)" : "") + ".");
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

    // The "visible map" scope follows panning/zooming. The others are fixed searches, but what is drawn still depends on
    // the zoom (a surface below the detail zoom, polygons above it): reload those when that changes.
    function drawKind() {
      if (state.mode !== "parcels" || map.getZoom() >= detailZoom) { return "detail"; }
      return map.getZoom() < 12 ? "overview" : "mid";
    }
    var lastDrawKind = null;
    map.addListener("idle", function () {
      var kind = drawKind();
      var changed = kind !== lastDrawKind;
      lastDrawKind = kind;
      if (state.scope === "view" || changed) { reload(); }
    });

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

  Promise.all([Nadlan.api.get("/api/config/client"), Nadlan.reference.load(), Nadlan.session.ready,
    Nadlan.api.get("/api/parcels/extent").catch(function () { return null; })]) // no extent: the default view
    .then(function (loaded) {
      return loadGoogleMaps(loaded[0].googleMapsApiKey).then(function () { start(loaded[0], loaded[1], loaded[3]); });
    })
    .catch(function (err) { setStatus(err.message, true); });
})(window, document, jQuery);
