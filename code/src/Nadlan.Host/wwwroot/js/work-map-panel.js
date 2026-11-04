// Work on the map (owner, 2026-11-02, step by step): collect a work request's Parcels on the map and choose their works in a
// small window over the map. "Work" (Parcels view, MANAGE_WORK) opens a chooser - an existing request or "+ New request" -
// then the window. While it is open, a click on a Parcel selects / deselects it (no Parcel card):
//   - a Parcel already in the work opens in the window's list: its works (a multi-select), a remark per work, remove it;
//   - other selected Parcels wait under "Add to this work" with the works to give them.
// Hovering a KAEK in the window lights that Parcel up on the map; clicking it there brings the map to it.
// The work's Parcels are outlined in their status colour, the selected ones in yellow. "Prepare link" opens the contractor's
// link (status, password, send) over the map. Statuses, files and messages stay on the Work page (work.html) for now.
// The server checks every rule (areas, Requested).
//   Nadlan.workMapPanel.init({ map, setStatus }) -> { open(), isOpen(), clicked(parcelSummary), close() }
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var LAST_WORKS_KEY = "nadlan.work.lastWorks"; // shared with the Work page: a new Parcel starts with the last works chosen

  // The Parcel's own status in the request (ParcelWorkStatuses), as on the contractor's map.
  var STATUS_COLORS = { OPEN_OFFERS: "#f59e0b", APPROVED: "#3b82f6", IN_WORK: "#a855f7", DONE: "#22c55e", PAID: "#14b8a6" };
  var HOVER_COLOR = "#22d3ee";

  function init(options) {
    var map = options.map;
    var setStatus = options.setStatus;
    var W = Nadlan.work;
    var esc = function (s) { return Nadlan.format.escapeHtml(s); };

    var req = null;          // the open request (GET /api/work/requests/{id})
    var types = [];          // work types
    var selected = [];       // selected Parcels, in click order: { parcelId, registryId }
    var addTypes = null;     // the works for the Parcels about to be added (starts with the last ones chosen)
    var openPicker = null;   // the works dropdown kept open across redraws ("add" or a parcelId)
    var shapes = {};         // parcelId -> geometry (the work's, and the selected ones once loaded)
    var shapeStatus = {};    // parcelId -> the Parcel's status in the work
    var hovered = null;
    var busy = false;
    var gen = 0;             // bumped when the window closes or another request opens: late replies are dropped
    var $win = null;
    var workLayer = Nadlan.createMapHighlight(map);   // the work's Parcels, in their status colour
    var pickLayer = Nadlan.createMapHighlight(map);   // the selected ones, yellow
    var hoverLayer = Nadlan.createMapHighlight(map);  // the one under the mouse in the window, on top

    function url(path) { return "/api/work/requests/" + req.workRequestId + (path || ""); }

    function lastWorks() {
      try { return JSON.parse(window.localStorage.getItem(LAST_WORKS_KEY) || "[]") || []; } catch (e) { return []; }
    }

    function rememberWorks(ids) {
      try { window.localStorage.setItem(LAST_WORKS_KEY, JSON.stringify(ids)); } catch (e) { /* private mode: fine */ }
    }

    function fail(err) {
      busy = false;
      setStatus(err && err.message ? err.message : "Something went wrong.", true);
      render();
    }

    // ---- the chooser: an existing request, or a new one -------------------------------------------------------------

    function choose() {
      var $content = $("<div class=\"form work-chooser\"><input type=\"search\" class=\"input\" placeholder=\"Find by name or contractor\" aria-label=\"Find a request\">" +
        "<div class=\"work-chooser-list\"><div class=\"muted\">Loading…</div></div>" +
        "<a class=\"work-chooser-page\" href=\"/work.html\" target=\"_blank\" rel=\"noopener\">The Work page: every request, the report, the work types ↗</a></div>");
      var d = Nadlan.dialog.open({
        title: "Work: choose a request",
        content: $content,
        width: Math.min(520, window.innerWidth - 24),
        buttons: [
          { text: "+ New request", click: function () { d.close(); createNew(); } },
          { text: "Cancel", click: function () { d.close(); } }
        ]
      });
      var list = [];
      function draw() {
        var q = $.trim($content.find("input").val() || "").toLowerCase();
        var shown = list.filter(function (r) { return !q || (r.name + " " + r.contractor.displayName).toLowerCase().indexOf(q) >= 0; });
        $content.find(".work-chooser-list").html(shown.length ? shown.map(function (r) {
          return "<button type=\"button\" class=\"work-chooser-item\" data-id=\"" + r.workRequestId + "\"><strong></strong><span class=\"muted\"></span></button>";
        }).join("") : "<div class=\"muted\">" + (list.length ? "No request matches." : "No work requests yet: press \"+ New request\".") + "</div>");
        $content.find(".work-chooser-item").each(function (i) {
          var r = shown[i];
          $(this).find("strong").text(r.name);
          $(this).find("span").text(r.contractor.displayName + " · " + r.parcels + " parcel" + (r.parcels === 1 ? "" : "s"));
        });
      }
      $content.on("input", "input", draw);
      // Enter in the search: the first request shown (never "+ New request").
      $content.on("keydown", "input", function (e) {
        if (e.key !== "Enter") { return; }
        e.preventDefault();
        var first = $content.find("[data-id]").first();
        if (first.length) { d.close(); openRequest(Number(first.data("id"))); }
      });
      $content.on("click", "[data-id]", function () { d.close(); openRequest(Number($(this).data("id"))); });
      Nadlan.api.get("/api/work/requests").then(function (rows) { list = rows; draw(); },
        function (err) { $content.find(".work-chooser-list").html("<div class=\"error\">" + esc(err.message) + "</div>"); });
      $content.find("input").trigger("focus");
    }

    function createNew() {
      var $form = $("<form class=\"form\"><label>Name<input class=\"input\" name=\"name\" maxlength=\"200\" placeholder=\"e.g. Skroponeria clearing Nov 2026\"></label>" +
        "<label class=\"contractor-label\">Contractor</label></form>");
      var picker = W.contractorPicker({});
      $form.find(".contractor-label").append(picker.$el);
      var d = Nadlan.dialog.open({
        title: "New work request",
        content: $form,
        width: Math.min(520, window.innerWidth - 24),
        buttons: [
          { text: "Create", primary: true, click: function () {
            var name = $.trim($form.find("[name=name]").val() || "");
            if (!name) { d.showError("Give the request a name."); return; }
            if (!picker.value()) { d.showError("Choose the contractor."); return; }
            d.busy(true);
            Nadlan.api.post("/api/work/requests", { name: name, contractorContactId: picker.value() }).then(function (res) {
              d.close();
              openRequest(res.workRequestId);
            }, function (err) { d.busy(false); d.showError(err.message); });
          } },
          { text: "Back", click: function () { d.close(); choose(); } }
        ]
      });
      $form.find("[name=name]").trigger("focus");
    }

    // ---- the window -------------------------------------------------------------------------------------------------

    function openRequest(id) {
      var g = ++gen;
      Promise.all([Nadlan.api.get("/api/work/requests/" + id), Nadlan.api.get("/api/work/work-types")]).then(function (loaded) {
        if (g !== gen) { return; }
        req = loaded[0];
        types = loaded[1];
        selected = [];
        addTypes = null;
        openPicker = null;
        shapeStatus = {};
        if (!$win) { build(); }
        render();
        syncShapes(true);
        setStatus("Work \"" + req.name + "\": click parcels on the map to select them.");
      }, fail);
    }

    function build() {
      $win = $("<section class=\"map-card work-window\" role=\"dialog\" aria-label=\"Work\">" +
        "<header class=\"card-header\"><span class=\"card-title\">Work</span><span class=\"card-actions\">" +
          "<button type=\"button\" data-act=\"change\" title=\"Another work request\">Change</button>" +
          "<a class=\"work-window-page\" target=\"_blank\" rel=\"noopener\" title=\"The request on the Work page (statuses, pictures, messages)\">Work page</a>" +
          "<button type=\"button\" class=\"card-icon\" data-act=\"minimize\" title=\"Minimize to a small chip\">▁</button>" +
          "<button type=\"button\" class=\"card-icon\" data-act=\"close\" title=\"Close Work: clicks open parcel cards again\">×</button>" +
        "</span></header>" +
        "<div class=\"card-chip\" title=\"Drag to move\"><span class=\"work-chip-name\"></span>" +
          "<button type=\"button\" class=\"card-icon\" data-act=\"maximize\" title=\"Maximize\">⤢</button></div>" +
        "<div class=\"work-window-body\"></div>" +
      "</section>");
      $("#popup-layer").prepend($win);
      bind();
    }

    function hasWork(parcelId) { return req.items.some(function (r) { return r.parcelId === parcelId; }); }

    function parcelsOfWork() {
      var seen = {}, list = [];
      req.items.forEach(function (r) {
        if (!seen[r.parcelId]) { seen[r.parcelId] = { parcelId: r.parcelId, registryId: r.registryId, area: r.geographicArea, status: r.parcelStatus, works: [] }; list.push(seen[r.parcelId]); }
        seen[r.parcelId].works.push(r);
      });
      return list;
    }

    function parcelOfWork(parcelId) { return parcelsOfWork().filter(function (p) { return p.parcelId === parcelId; })[0] || null; }

    function isSelected(parcelId) { return selected.some(function (s) { return s.parcelId === parcelId; }); }

    function typeName(id) { var t = types.filter(function (x) { return x.workTypeId === id; })[0]; return t ? t.name : "#" + id; }

    // A multi-select dropdown of the work types: the button names the chosen ones; the list opens under it.
    // key: "add" or a parcelId; checked: ids; locked: { id: why } (shown ticked, can't be changed here).
    function pickerHtml(key, checked, locked) {
      var names = checked.map(typeName);
      var isOpen = openPicker === key;
      return "<div class=\"work-picker" + (isOpen ? " open" : "") + "\" data-picker=\"" + key + "\">" +
        "<button type=\"button\" class=\"input work-picker-button\" aria-haspopup=\"listbox\" aria-expanded=\"" + isOpen + "\">" +
          "<span class=\"work-picker-text" + (names.length ? "" : " muted") + "\"></span><span aria-hidden=\"true\">▾</span></button>" +
        (isOpen ? "<div class=\"work-picker-list\" role=\"listbox\" aria-multiselectable=\"true\">" +
          types.filter(function (t) { return t.isActive || checked.indexOf(t.workTypeId) >= 0; }).map(function (t) {
            var why = locked && locked[t.workTypeId];
            var off = why || (busy && key !== "add");
            return "<label class=\"check\"" + (why ? " title=\"" + esc(why) + "\"" : "") + "><input type=\"checkbox\" data-type=\"" + t.workTypeId + "\"" +
              (checked.indexOf(t.workTypeId) >= 0 ? " checked" : "") + (off ? " disabled" : "") + "> " + esc(t.name) + "</label>";
          }).join("") + "</div>" : "") +
      "</div>";
    }

    function render() {
      if (!$win || !req) { return; }
      $win.find(".work-chip-name").text("Work: " + req.name);
      $win.find(".work-window-page").attr("href", "/work.html#request=" + req.workRequestId);
      var parcels = parcelsOfWork();
      var pending = selected.filter(function (s) { return !hasWork(s.parcelId); });
      if (addTypes === null) {
        var active = types.filter(function (t) { return t.isActive; }).map(function (t) { return t.workTypeId; });
        addTypes = lastWorks().filter(function (id) { return active.indexOf(id) >= 0; });
      }

      var html = "<div class=\"work-window-name\"></div><div class=\"muted work-window-who\"></div>" +
        "<div class=\"muted work-window-hint\">Click parcels on the map to select or deselect them.</div>";

      // Selected Parcels not in the work yet: the works to give them, then Add.
      if (pending.length) {
        html += "<div class=\"card-section work-pending\"><div class=\"card-section-head\"><span>Add to this work (" + pending.length + ")</span>" +
          "<button type=\"button\" class=\"btn btn-small\" data-act=\"clear-pending\">Clear</button></div>" +
          "<ul class=\"work-pending-list\">" + pending.map(function (s) {
            return "<li data-parcel=\"" + s.parcelId + "\"><button type=\"button\" class=\"kaek work-kaek\" data-act=\"goto\" title=\"Show it on the map\"></button>" +
              "<button type=\"button\" class=\"card-icon\" data-unpick=\"" + s.parcelId + "\" title=\"Deselect\">×</button></li>";
          }).join("") + "</ul>" +
          "<div class=\"work-field-label\">Works</div>" + pickerHtml("add", addTypes, null) +
          "<button type=\"button\" class=\"btn btn-primary work-add-btn\" data-act=\"add\"" + (busy ? " disabled" : "") + ">Add " + pending.length +
            " parcel" + (pending.length === 1 ? "" : "s") + " to the work</button></div>";
      }

      // The work's Parcels: collapsed; the selected ones open.
      html += "<div class=\"card-section\"><div class=\"card-section-head\"><span>Parcels in this work (" + parcels.length + ")</span></div>" +
        (parcels.length ? "<ul class=\"work-parcels\">" + parcels.map(function (p) {
          var open = isSelected(p.parcelId);
          var removable = p.works.every(function (w) { return w.status === "REQUESTED"; });
          var locked = {};
          p.works.forEach(function (w) { if (w.status !== "REQUESTED") { locked[w.workTypeId] = W.WORK[w.status].name + ": correct it on the Work page to change it"; } });
          return "<li class=\"work-parcel" + (open ? " open" : "") + "\" data-parcel=\"" + p.parcelId + "\">" +
            "<button type=\"button\" class=\"work-parcel-head\" data-act=\"toggle\" aria-expanded=\"" + open + "\">" +
              "<span class=\"kaek\"></span>" + W.parcelBadge(p.status) +
              (open ? "" : "<span class=\"muted work-parcel-works\"></span>") + "</button>" +
            (open ? "<div class=\"work-parcel-body\">" +
              pickerHtml(String(p.parcelId), p.works.map(function (w) { return w.workTypeId; }), locked) +
              "<ul class=\"work-items-mini\">" + p.works.map(function (w) {
                return "<li data-work=\"" + w.workItemId + "\"><div class=\"work-item-line\"><span class=\"work-item-name\"></span>" +
                  (w.status !== "REQUESTED" ? W.workBadge(w.status) : "") +
                  "<button type=\"button\" class=\"card-icon work-remark-btn" + (w.remarks ? " has-remark" : "") + "\" data-act=\"remark\" title=\"" +
                    (w.remarks ? "Edit the remark (the contractor sees it)" : "Add a remark for the contractor") + "\" aria-label=\"Remark\">📝</button>" +
                  (w.status === "REQUESTED"
                    ? "<button type=\"button\" class=\"card-icon work-trash-btn\" data-act=\"remove-work\" title=\"Remove this work from the parcel\" aria-label=\"Remove\">🗑</button>"
                    : "<span class=\"card-icon work-trash-btn is-locked\" title=\"" + esc(W.WORK[w.status].name) + ": correct it on the Work page to remove it\">🗑</span>") +
                  "</div>" +
                  (w.remarks ? "<div class=\"work-remark\"></div>" : "") + "</li>";
              }).join("") + "</ul>" +
              (removable ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"remove-parcel\">Remove from the work</button>" : "") +
            "</div>" : "") +
          "</li>";
        }).join("") + "</ul>" : "<div class=\"muted\">None yet: click parcels on the map, choose the works, Add.</div>") + "</div>";

      // Next step: the contractor's link.
      html += "<div class=\"work-window-next\"><button type=\"button\" class=\"btn btn-primary\" data-act=\"link\"" + (parcels.length ? "" : " disabled") +
        ">Prepare link for the contractor →</button></div>";

      $win.find(".work-window-body").html(html);
      // What people typed (names, KAEKs, remarks) goes in as text.
      $win.find(".work-window-name").text(req.name);
      $win.find(".work-window-who").text(req.contractor.displayName + (req.link.state !== "active" ? " · link " + req.link.state : ""));
      $win.find(".work-pending-list li").each(function (i) { $(this).find(".work-kaek").text(pending[i].registryId || "#" + pending[i].parcelId); });
      $win.find(".work-picker").each(function () {
        var key = String($(this).data("picker"));
        var ids = key === "add" ? addTypes : (parcelOfWork(Number(key)) || { works: [] }).works.map(function (w) { return w.workTypeId; });
        $(this).find(".work-picker-text").text(ids.length ? ids.map(typeName).join(", ") : "Choose works");
      });
      $win.find(".work-parcel").each(function (i) {
        var p = parcels[i];
        $(this).find(".work-parcel-head .kaek").text(p.registryId || "#" + p.parcelId);
        $(this).find(".work-parcel-works").text(p.works.map(function (w) { return w.workTypeName; }).join(", "));
        $(this).find(".work-items-mini li").each(function (j) {
          $(this).find(".work-item-name").text(p.works[j].workTypeName);
          $(this).find(".work-remark").text(p.works[j].remarks || "");
        });
      });
      drawMap();
    }

    // ---- the map ----------------------------------------------------------------------------------------------------

    // Three layers, each drawn only when it changes: the work's Parcels, the selected ones, the one under the mouse.
    var drawn = { work: "", pick: "" };
    function drawMap() {
      var work = req ? Object.keys(shapeStatus).filter(function (id) { return shapes[id]; }).map(function (id) {
        return { id: Number(id), geometry: shapes[id], color: STATUS_COLORS[shapeStatus[id]] || "#64748b" };
      }) : [];
      var pick = req ? selected.filter(function (s) { return shapes[s.parcelId]; }).map(function (s) {
        return { id: s.parcelId, geometry: shapes[s.parcelId], color: Nadlan.mapHighlightColors.selected };
      }) : [];
      var workKey = work.map(function (x) { return x.id + x.color; }).join(), pickKey = pick.map(function (x) { return x.id; }).join();
      if (workKey !== drawn.work) { workLayer.set(work); drawn.work = workKey; }
      if (pickKey !== drawn.pick) { pickLayer.set(pick); drawn.pick = pickKey; }
      drawHover();
    }

    function drawHover() {
      hoverLayer.set(req && hovered && shapes[hovered] ? [{ id: hovered, geometry: shapes[hovered], color: HOVER_COLOR }] : []);
    }

    // The Parcels' statuses come with the request; outlines are fetched only for Parcels the map doesn't have yet.
    function syncShapes(fit) {
      shapeStatus = {};
      req.items.forEach(function (r) { shapeStatus[r.parcelId] = r.parcelStatus; });
      var missing = Object.keys(shapeStatus).some(function (id) { return !shapes[id]; });
      drawMap();
      if (!missing) {
        if (fit && Object.keys(shapeStatus).length) { fitTo(Object.keys(shapeStatus).map(function (id) { return shapes[id]; })); }
        return;
      }
      var g = gen;
      Nadlan.api.get(url("/shapes")).then(function (list) {
        if (g !== gen) { return; }
        list.forEach(function (s) { shapes[s.parcelId] = s.geometry; });
        drawMap();
        if (fit && list.length) { fitTo(list.map(function (s) { return s.geometry; })); }
      }, fail);
    }

    function shapeOf(parcelId) {
      if (shapes[parcelId]) { return; }
      Nadlan.api.get("/api/parcels/" + parcelId).then(function (d) { shapes[parcelId] = d.geometry; if (req) { drawMap(); } }, function () { /* outlined once loaded */ });
    }

    function boundsOf(geometries) {
      var bounds = new google.maps.LatLngBounds();
      geometries.forEach(function (g) { (g.coordinates[0] || []).forEach(function (c) { bounds.extend({ lat: c[1], lng: c[0] }); }); });
      return bounds;
    }

    function fitTo(geometries) { map.fitBounds(boundsOf(geometries), 60); }

    // Brings one Parcel into view: centred, and close enough to see it.
    function goTo(parcelId) {
      if (!shapes[parcelId]) { return; }
      fitTo([shapes[parcelId]]);
      var once = google.maps.event.addListenerOnce(map, "idle", function () { if (map.getZoom() > 19) { map.setZoom(19); } });
      setTimeout(function () { google.maps.event.removeListener(once); }, 1500); // no move = no "idle": never keep it
    }

    function hover(parcelId) {
      hovered = parcelId;
      drawHover();
    }

    // A click on the map while Work is open: select / deselect.
    function clicked(summary) {
      var id = summary.parcelId;
      if (isSelected(id)) {
        selected = selected.filter(function (s) { return s.parcelId !== id; });
      } else {
        selected.push({ parcelId: id, registryId: summary.registryId });
        shapeOf(id);
      }
      if ($win && $win.hasClass("minimized")) { maximize(); }
      render();
      var row = $win && $win.find(".work-parcel[data-parcel=" + id + "]")[0];
      if (row) { row.scrollIntoView({ block: "nearest" }); }
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    function after(promise, message) {
      var g = gen;
      busy = true;
      render();
      return promise.then(function (detail) {
        if (g !== gen) { return false; }
        busy = false;
        req = detail.request || detail;
        render();
        syncShapes(false);
        if (message) { setStatus(message); }
        return true;
      }, function (err) {
        if (g !== gen) { return false; }
        fail(err);
        return false;
      });
    }

    function addPending() {
      var ids = addTypes.slice();
      if (!ids.length) { setStatus("Choose at least one work for the parcels.", true); openPicker = "add"; render(); return; }
      rememberWorks(ids);
      openPicker = null;
      var pending = selected.filter(function (s) { return !hasWork(s.parcelId); });
      busy = true;
      render();
      var added = 0, failed = [], g = gen;
      // One by one: each is checked on the server (areas); one refused doesn't stop the others.
      pending.reduce(function (chain, s) {
        return chain.then(function () {
          if (g !== gen) { return null; }
          return Nadlan.api.post(url("/parcels"), { parcelId: s.parcelId, workTypeIds: ids }).then(function (res) { if (g === gen) { req = res.request; added++; } },
            function (err) { failed.push((s.registryId || "#" + s.parcelId) + ": " + err.message); });
        });
      }, Promise.resolve()).then(function () {
        if (g !== gen) { return; }
        busy = false;
        render();
        syncShapes(false);
        setStatus(failed.length ? "Added " + added + "; not added - " + failed.join("; ") : "Added " + added + " parcel" + (added === 1 ? "" : "s") + " to the work.", failed.length > 0);
      });
    }

    function toggleWork(parcelId, workTypeId, on) {
      if (on) {
        return after(Nadlan.api.post(url("/parcels"), { parcelId: parcelId, workTypeIds: [workTypeId] }), typeName(workTypeId) + " added.");
      }
      var row = req.items.filter(function (r) { return r.parcelId === parcelId && r.workTypeId === workTypeId; })[0];
      if (!row) { return null; }
      var last = req.items.filter(function (r) { return r.parcelId === parcelId; }).length === 1;
      if (!last) { return after(Nadlan.api.del(url("/works/" + row.workItemId)), row.workTypeName + " removed."); }
      return Nadlan.dialog.confirm("Remove the parcel", "It is the parcel's last work: the parcel leaves the work too.", "Remove").then(function (ok) {
        if (ok) { openPicker = null; return after(Nadlan.api.del(url("/works/" + row.workItemId)), "Parcel removed from the work."); }
        render(); // put the tick back
        return null;
      });
    }

    function removeParcel(parcelId) {
      var p = parcelOfWork(parcelId);
      Nadlan.dialog.confirm("Remove from the work", "Remove parcel " + esc(p.registryId || "") + " and its works from \"" + esc(req.name) + "\"?", "Remove").then(function (ok) {
        if (!ok) { return; }
        after(Nadlan.api.del(url("/parcels/" + parcelId)), "Parcel removed from the work.");
      });
    }

    // A remark on one work (owner, 2026-11-02): several lines, shown to the contractor under that work.
    function editRemark(workItemId) {
      var w = req.items.filter(function (r) { return r.workItemId === workItemId; })[0];
      if (!w) { return; }
      var $form = $("<form class=\"form\"><div class=\"muted\"></div>" +
        "<label>Remark<textarea class=\"input work-remark-input\" name=\"remarks\" rows=\"6\" maxlength=\"2000\" " +
        "placeholder=\"e.g. Leave the olive trees. The gate key is with the neighbour.\"></textarea></label>" +
        "<div class=\"muted\">The contractor sees it under this work on the phone.</div></form>");
      $form.find(".muted").first().text(w.workTypeName + " on parcel " + (w.registryId || ""));
      $form.find("[name=remarks]").val(w.remarks || "");
      var buttons = [
        { text: "Save", primary: true, click: function () { save($.trim($form.find("[name=remarks]").val() || "")); } },
        { text: "Cancel", click: function () { d.close(); } }
      ];
      if (w.remarks) { buttons.splice(1, 0, { text: "Remove the remark", click: function () { save(""); } }); }
      var d = Nadlan.dialog.open({ title: "Remark for the contractor", content: $form, width: Math.min(520, window.innerWidth - 24), buttons: buttons });
      $form.find("[name=remarks]").trigger("focus");

      function save(text) {
        d.busy(true);
        var g = gen;
        Nadlan.api.put(url("/works/" + workItemId + "/remarks"), { remarks: text || null }).then(function (detail) {
          d.close();
          if (g !== gen) { return; }
          req = detail;
          render();
          setStatus(text ? "Remark saved." : "Remark removed.");
        }, function (err) { d.busy(false); d.showError(err.message); });
      }
    }

    // ---- the contractor's link, over the map ------------------------------------------------------------------------

    function openLink() {
      var $box = $("<div class=\"work-link-dialog\"></div>");
      var shownPassword = null; // shown once, right after it is set
      var linkUrl = null;
      var d = Nadlan.dialog.open({
        title: req.contractor.displayName + "'s link",
        content: $box,
        width: Math.min(560, window.innerWidth - 24),
        buttons: [{ text: "Close", click: function () { d.close(); } }]
      });

      function draw() {
        var l = req.link;
        var parcels = parcelsOfWork().length;
        $box.html(
          "<div class=\"work-link-status\">" + W.linkBadge(l.state) +
            "<label>" + (l.state === "expired" ? "Expired - works until " : "Works until ") +
              "<input type=\"date\" class=\"input input-small\" data-act=\"last-day\" value=\"" + W.lastDay(l.expiresUtc) + "\"></label>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"extend\">Extend 1 year</button>" +
            (l.lockedUntilUtc ? "<span class=\"warn\">Locked after wrong passwords until " + W.time(l.lockedUntilUtc) + "</span>" : "") +
          "</div>" +
          "<p class=\"muted\">One link per contractor, no login: on the phone he sees these " + parcels + " parcel" + (parcels === 1 ? "" : "s") +
            " and those of his other open requests on one map, sends prices, starts and finishes the works.</p>" +

          "<section class=\"work-link-step\"><h4><span class=\"work-step-no\">1</span> Password <span class=\"muted\">(optional)</span></h4>" +
            (l.hasPassword
              ? "<p>Set. The contractor types it once on the phone (remembered 30 days). A new one signs phones out: they type the new one.</p>" +
                "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-small\" data-act=\"password\">Change…</button>" +
                "<button type=\"button\" class=\"btn btn-small\" data-act=\"generate\">Generate new</button>" +
                "<button type=\"button\" class=\"btn btn-small\" data-act=\"remove-password\">Remove</button></div>"
              : "<p class=\"muted\">None: anyone with the link can open it. A password makes passing the link on less likely.</p>" +
                "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-small\" data-act=\"generate\">Generate (6 digits)</button>" +
                "<button type=\"button\" class=\"btn btn-small\" data-act=\"password\">Type one…</button></div>") +
            (shownPassword ? "<div class=\"work-password-once\"><span class=\"work-password\"></span>" +
              "<button type=\"button\" class=\"btn btn-small\" data-act=\"copy-password\">Copy</button>" +
              "<div class=\"muted\">Shown only now. Send it separately from the link (e.g. by phone).</div></div>" : "") +
          "</section>" +

          "<section class=\"work-link-step\"><h4><span class=\"work-step-no\">2</span> Send the link</h4>" +
            (l.enabled
              ? "<div class=\"work-link-row\"><input class=\"input work-link-url\" readonly aria-label=\"The link\">" +
                "<button type=\"button\" class=\"btn btn-primary\" data-act=\"copy-link\">Copy link</button>" +
                "<button type=\"button\" class=\"btn\" data-act=\"renew\" title=\"A new address; the current one stops at once\">New link</button></div>" +
                "<div class=\"muted\">Send it by WhatsApp, SMS or e-mail" + (l.hasPassword ? "; the password separately." : ".") + "</div>"
              : "<p class=\"warn\">The link is disabled: it opens nothing. Enable it to send it.</p>") +
          "</section>" +

          "<section class=\"work-link-step work-link-off\"><h4><span class=\"work-step-no\">3</span> " + (l.enabled ? "Stop the link" : "Turn it on again") + "</h4>" +
            (l.enabled
              ? "<div class=\"muted\">Disabling stops it at once; enabling it again later gives the same link.</div>" +
                "<button type=\"button\" class=\"btn btn-small\" data-act=\"disable\">Disable the link</button>"
              : "<button type=\"button\" class=\"btn btn-small btn-primary\" data-act=\"enable\">Enable the link</button>") +
          "</section>");
        $box.find(".work-link-url").val(linkUrl || "Loading…");
        $box.find(".work-password").text(shownPassword || "");
      }

      var g = gen;
      function act(promise, message) {
        $box.find("button").prop("disabled", true);
        return promise.then(function (res) {
          if (g !== gen) { d.close(); return null; }
          req = res.request || res;
          render();
          draw();
          if (message) { setStatus(message); }
          return res;
        }, function (err) { d.showError(err.message); draw(); });
      }

      function copy(text, what) {
        W.copyText(what, text).then(function (ok) { if (ok) { setStatus(what + " copied."); } });
      }

      $box.on("click", "[data-act=extend]", function () { act(Nadlan.api.post(url("/link/extend")), "Link extended."); });
      $box.on("change", "[data-act=last-day]", function () {
        var day = $(this).val();
        if (day) { act(Nadlan.api.put(url("/link/expiry"), { lastDay: day }), "The link works until " + W.date(day) + "."); }
      });
      $box.on("click", "[data-act=renew]", function () {
        W.confirmNewLink(req.contractor.displayName).then(function (ok) {
          if (!ok) { return; }
          act(Nadlan.api.post(url("/link/renew")), "New link made: the old address stopped.").then(function (res) {
            if (!res) { return; }
            linkUrl = window.location.origin + "/work-link.html#" + res.token;
            $box.find(".work-link-url").val(linkUrl);
            copy(linkUrl, "New link");
          });
        });
      });
      $box.on("click", "[data-act=disable]", function () { act(Nadlan.api.post(url("/link/disable")), "Link disabled: it opens nothing now."); });
      $box.on("click", "[data-act=enable]", function () { act(Nadlan.api.post(url("/link/enable")), "Link enabled."); });
      $box.on("click", "[data-act=generate]", function () {
        act(Nadlan.api.post(url("/password"), { password: null, generate: true }), "Password set.").then(function (res) { if (res) { shownPassword = res.password; draw(); } });
      });
      $box.on("click", "[data-act=password]", function () {
        var $form = $("<form class=\"form\"><label>Password for the link<input class=\"input\" name=\"password\" autocomplete=\"off\" maxlength=\"64\" " +
          "placeholder=\"At least 4 characters\"></label></form>");
        var pd = Nadlan.dialog.open({
          title: req.link.hasPassword ? "Change the password" : "Type a password",
          content: $form,
          width: Math.min(420, window.innerWidth - 24),
          buttons: [
            { text: "Save", primary: true, click: function () {
              pd.busy(true);
              Nadlan.api.post(url("/password"), { password: $form.find("[name=password]").val() || "", generate: false }).then(function (res) {
                pd.close();
                req = res.request;
                shownPassword = res.password;
                render();
                draw();
                setStatus("Password set.");
              }, function (err) { pd.busy(false); pd.showError(err.message); });
            } },
            { text: "Cancel", click: function () { pd.close(); } }
          ]
        });
        $form.find("[name=password]").trigger("focus");
      });
      $box.on("click", "[data-act=remove-password]", function () {
        Nadlan.dialog.confirm("Remove the password", "The link will open without a password.", "Remove").then(function (ok) {
          if (!ok) { return; }
          shownPassword = null;
          act(Nadlan.api.del(url("/password")), "Password removed.");
        });
      });
      $box.on("click", "[data-act=copy-password]", function () { copy(shownPassword, "Password"); });
      $box.on("click", "[data-act=copy-link]", function () { if (linkUrl) { copy(linkUrl, "Link"); } });
      $box.on("focus click", ".work-link-url", function () { this.select(); });

      draw();
      Nadlan.api.get(url("/link")).then(function (res) {
        linkUrl = window.location.origin + "/work-link.html#" + res.token;
        $box.find(".work-link-url").val(linkUrl);
      }, function (err) { d.showError(err.message); });
    }

    // ---- window controls ----------------------------------------------------------------------------------------------

    function minimize() {
      var rect = $win[0].getBoundingClientRect();
      var pos = $win.data("chipPos") || { left: rect.left, top: rect.top };
      $win.addClass("minimized").css({ left: pos.left + "px", top: pos.top + "px" });
    }

    function maximize() { $win.removeClass("minimized").css({ left: "", top: "" }); }

    function bind() {
      $win.on("click", "[data-act=close]", close);
      $win.on("click", "[data-act=change]", choose);
      $win.on("click", "[data-act=minimize]", minimize);
      $win.on("click", "[data-act=maximize]", maximize);
      $win.on("click", "[data-act=link]", openLink);
      $win.on("click", "[data-act=clear-pending]", function () {
        selected = selected.filter(function (s) { return hasWork(s.parcelId); });
        render();
      });
      $win.on("click", "[data-unpick]", function () {
        var id = Number($(this).data("unpick"));
        selected = selected.filter(function (s) { return s.parcelId !== id; });
        hover(null);
        render();
      });
      $win.on("click", "[data-act=add]", addPending);
      // Hover a KAEK (or a parcel's row): that Parcel lights up on the map. Click a waiting one: the map goes to it.
      $win.on("mouseenter", ".work-pending-list li, .work-parcel-head", function () { hover(Number($(this).closest("[data-parcel]").data("parcel"))); });
      $win.on("mouseleave", ".work-pending-list li, .work-parcel-head", function () { hover(null); });
      $win.on("click", "[data-act=goto]", function () { goTo(Number($(this).closest("[data-parcel]").data("parcel"))); });
      // A Parcel of the work: open / close it in the list and select / deselect it on the map, like a click there.
      $win.on("click", "[data-act=toggle]", function () {
        var id = Number($(this).closest(".work-parcel").data("parcel"));
        var p = parcelOfWork(id);
        clicked({ parcelId: id, registryId: p.registryId });
        if (isSelected(id)) { goTo(id); }
      });
      // The works dropdowns.
      $win.on("click", ".work-picker-button", function () {
        var key = String($(this).closest(".work-picker").data("picker"));
        openPicker = openPicker === key ? null : key;
        render();
      });
      $win.on("change", ".work-picker input[data-type]", function () {
        var key = String($(this).closest(".work-picker").data("picker"));
        var id = Number($(this).data("type"));
        if (key === "add") {
          addTypes = this.checked ? addTypes.concat([id]) : addTypes.filter(function (x) { return x !== id; });
          render();
        } else if (busy) {
          this.checked = !this.checked; // a save is running: one change at a time
        } else {
          toggleWork(Number(key), id, this.checked);
        }
      });
      // Closed after the click is handled (not on mousedown): the button clicked (Add, 📝, ×) still gets its click.
      $(document).on("click.workpicker", function (e) {
        if (openPicker === null || !$win || $(e.target).closest(".work-picker").length) { return; }
        openPicker = null;
        $win.find(".work-picker").removeClass("open").find(".work-picker-list").remove();
        $win.find(".work-picker-button").attr("aria-expanded", "false");
      });
      $win.on("click", "[data-act=remark]", function () { editRemark(Number($(this).closest("[data-work]").data("work"))); });
      $win.on("click", "[data-act=remove-work]", function () {
        if (busy) { return; }
        var row = req.items.filter(function (r) { return r.workItemId === Number($(this).closest("[data-work]").data("work")); }, this)[0];
        if (row) { toggleWork(row.parcelId, row.workTypeId, false); }
      });
      $win.on("click", "[data-act=remove-parcel]", function () { removeParcel(Number($(this).closest(".work-parcel").data("parcel"))); });
      // The chip drags anywhere (as the parcel cards' chip).
      $win.on("pointerdown", ".card-chip", function (e) {
        if (!$win.hasClass("minimized") || $(e.target).closest("button").length) { return; }
        var el = $win[0];
        var rect = el.getBoundingClientRect();
        var dx = e.clientX - rect.left, dy = e.clientY - rect.top;
        el.setPointerCapture(e.pointerId);
        function move(ev) {
          var left = Math.min(Math.max(0, ev.clientX - dx), window.innerWidth - rect.width);
          var top = Math.min(Math.max(0, ev.clientY - dy), window.innerHeight - rect.height);
          $win.data("chipPos", { left: left, top: top });
          el.style.left = left + "px";
          el.style.top = top + "px";
        }
        function up(ev) {
          el.releasePointerCapture(ev.pointerId);
          el.removeEventListener("pointermove", move);
          el.removeEventListener("pointerup", up);
        }
        el.addEventListener("pointermove", move);
        el.addEventListener("pointerup", up);
        e.preventDefault();
      });
    }

    function close() {
      gen++;
      busy = false;
      $(document).off("click.workpicker");
      if ($win) { $win.remove(); $win = null; }
      req = null;
      selected = [];
      shapeStatus = {};
      hovered = null;
      drawMap();
      setStatus("");
    }

    return {
      open: choose,
      isOpen: function () { return !!req; },
      clicked: clicked,
      close: close
    };
  }

  Nadlan.workMapPanel = { init: init };
})(window, jQuery);
