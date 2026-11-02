// Work on the map, stage 1 (owner, 2026-11-02): collect a work request's Parcels on the map and choose their works in a
// small window over the map. "Work" (Parcels view, MANAGE_WORK) opens a chooser - an existing request or "+ New request" -
// then the window. While it is open, a click on a Parcel selects / deselects it (no Parcel card):
//   - a Parcel already in the work opens in the window's list (tick / untick its works, remove it);
//   - other selected Parcels wait under "Add to this work" with the works to give them.
// The work's Parcels are outlined in their status colour, the selected ones in yellow. Statuses, files and messages stay on
// the Work page (work.html) for now - "Work page" in the window opens it. The server checks every rule (areas, Requested).
//   Nadlan.workMapPanel.init({ map, setStatus }) -> { isOpen(), clicked(parcelSummary), close() }
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var LAST_WORKS_KEY = "nadlan.work.lastWorks"; // shared with the Work page: a new Parcel starts with the last works chosen

  // The Parcel's own status in the request (ParcelWorkStatuses), as on the contractor's map.
  var STATUS_COLORS = { OPEN_OFFERS: "#f59e0b", APPROVED: "#3b82f6", IN_WORK: "#a855f7", DONE: "#22c55e", PAID: "#14b8a6" };

  function init(options) {
    var map = options.map;
    var setStatus = options.setStatus;
    var W = Nadlan.work;
    var esc = function (s) { return Nadlan.format.escapeHtml(s); };

    var req = null;          // the open request (GET /api/work/requests/{id})
    var types = [];          // work types
    var selected = [];       // selected Parcels, in click order: { parcelId, registryId }
    var shapes = {};         // parcelId -> geometry (the work's, and the selected ones once loaded)
    var shapeStatus = {};    // parcelId -> the Parcel's status in the work
    var busy = false;
    var $win = null;
    var workLayer = Nadlan.createMapHighlight(map);  // the work's Parcels, in their status colour
    var pickLayer = Nadlan.createMapHighlight(map);  // the selected ones, yellow, on top

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
        "<div class=\"work-chooser-list\"><div class=\"muted\">Loading…</div></div></div>");
      var d = Nadlan.dialog.open({
        title: "Work: choose a request",
        content: $content,
        width: Math.min(520, window.innerWidth - 24),
        buttons: [
          { text: "+ New request", primary: true, click: function () { d.close(); createNew(); } },
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
      Promise.all([Nadlan.api.get("/api/work/requests/" + id), Nadlan.api.get("/api/work/work-types")]).then(function (loaded) {
        req = loaded[0];
        types = loaded[1];
        selected = [];
        if (!$win) { build(); }
        render();
        loadShapes(true);
        setStatus("Work \"" + req.name + "\": click parcels on the map to select them.");
      }, fail);
    }

    function build() {
      $win = $("<section class=\"map-card work-window\" role=\"dialog\" aria-label=\"Work\">" +
        "<header class=\"card-header\"><span class=\"card-title\">Work</span><span class=\"card-actions\">" +
          "<button type=\"button\" data-act=\"change\" title=\"Another work request\">Change</button>" +
          "<a class=\"work-window-page\" target=\"_blank\" rel=\"noopener\" title=\"The request on the Work page (link, statuses, pictures, messages)\">Work page</a>" +
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

    function isSelected(parcelId) { return selected.some(function (s) { return s.parcelId === parcelId; }); }

    function checksHtml(name, checked, disabledIds) {
      return types.filter(function (t) { return t.isActive || checked.indexOf(t.workTypeId) >= 0; }).map(function (t) {
        var locked = disabledIds && disabledIds[t.workTypeId];
        return "<label class=\"check\"" + (locked ? " title=\"" + esc(locked) + "\"" : "") + "><input type=\"checkbox\" data-" + name + "=\"" + t.workTypeId + "\"" +
          (checked.indexOf(t.workTypeId) >= 0 ? " checked" : "") + (locked ? " disabled" : "") + "> " + esc(t.name) + "</label>";
      }).join("");
    }

    function render() {
      if (!$win || !req) { return; }
      $win.find(".work-chip-name").text("Work: " + req.name);
      $win.find(".work-window-page").attr("href", "/work.html#request=" + req.workRequestId);
      var parcels = parcelsOfWork();
      var pending = selected.filter(function (s) { return !hasWork(s.parcelId); });

      var html = "<div class=\"work-window-name\"></div><div class=\"muted work-window-who\"></div>" +
        "<div class=\"muted work-window-hint\">Click parcels on the map to select or deselect them.</div>";

      // Selected Parcels not in the work yet: the works to give them, then Add.
      if (pending.length) {
        html += "<div class=\"card-section work-pending\"><div class=\"card-section-head\"><span>Add to this work (" + pending.length + ")</span>" +
          "<button type=\"button\" class=\"btn btn-small\" data-act=\"clear-pending\">Clear</button></div>" +
          "<ul class=\"work-pending-list\">" + pending.map(function (s) {
            return "<li><span class=\"kaek\"></span><button type=\"button\" class=\"card-icon\" data-unpick=\"" + s.parcelId + "\" title=\"Deselect\">×</button></li>";
          }).join("") + "</ul>" +
          "<div class=\"work-checks\" role=\"group\" aria-label=\"Works for the parcels\">" + checksHtml("add-type", lastWorks()) + "</div>" +
          "<button type=\"button\" class=\"btn btn-primary work-add-btn\" data-act=\"add\"" + (busy ? " disabled" : "") + ">Add " + pending.length +
            " parcel" + (pending.length === 1 ? "" : "s") + " to the work</button></div>";
      }

      // The work's Parcels: collapsed; the selected ones open.
      html += "<div class=\"card-section\"><div class=\"card-section-head\"><span>Parcels in this work (" + parcels.length + ")</span></div>" +
        (parcels.length ? "<ul class=\"work-parcels\">" + parcels.map(function (p) {
          var open = isSelected(p.parcelId);
          var removable = p.works.every(function (w) { return w.status === "REQUESTED"; });
          var has = p.works.map(function (w) { return w.workTypeId; });
          var locked = {};
          p.works.forEach(function (w) { if (w.status !== "REQUESTED") { locked[w.workTypeId] = W.WORK[w.status].name + ": correct it on the Work page to change it"; } });
          return "<li class=\"work-parcel" + (open ? " open" : "") + "\" data-parcel=\"" + p.parcelId + "\">" +
            "<button type=\"button\" class=\"work-parcel-head\" data-act=\"toggle\" aria-expanded=\"" + open + "\">" +
              "<span class=\"kaek\"></span>" + W.parcelBadge(p.status) +
              "<span class=\"muted work-parcel-works\"></span></button>" +
            (open ? "<div class=\"work-parcel-body\">" +
              "<div class=\"work-checks\" role=\"group\" aria-label=\"Works on this parcel\">" + checksHtml("work-type", has, locked) + "</div>" +
              (p.works.some(function (w) { return w.status !== "REQUESTED"; })
                ? "<div class=\"muted work-parcel-statuses\">" + p.works.map(function (w) { return esc(w.workTypeName) + " " + W.workBadge(w.status); }).join(" ") + "</div>" : "") +
              (removable ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"remove-parcel\">Remove from the work</button>" : "") +
            "</div>" : "") +
          "</li>";
        }).join("") + "</ul>" : "<div class=\"muted\">None yet: click parcels on the map, choose the works, Add.</div>") + "</div>";

      $win.find(".work-window-body").html(html);
      // What people typed (names, KAEKs) goes in as text.
      $win.find(".work-window-name").text(req.name);
      $win.find(".work-window-who").text(req.contractor.displayName + (req.link.state !== "active" ? " · link " + req.link.state : ""));
      $win.find(".work-pending-list li").each(function (i) { $(this).find(".kaek").text(pending[i].registryId || "#" + pending[i].parcelId); });
      $win.find(".work-parcel").each(function (i) {
        var p = parcels[i];
        $(this).find(".work-parcel-head .kaek").text(p.registryId || "#" + p.parcelId);
        $(this).find(".work-parcel-works").text(p.works.map(function (w) { return w.workTypeName; }).join(", "));
      });
      drawMap();
    }

    // ---- the map ----------------------------------------------------------------------------------------------------

    function drawMap() {
      if (!req) { workLayer.set([]); pickLayer.set([]); return; }
      workLayer.set(Object.keys(shapeStatus).map(function (id) {
        return { id: Number(id), geometry: shapes[id], color: STATUS_COLORS[shapeStatus[id]] || "#64748b" };
      }));
      pickLayer.set(selected.map(function (s) { return { id: s.parcelId, geometry: shapes[s.parcelId], color: Nadlan.mapHighlightColors.selected }; }));
    }

    function loadShapes(fit) {
      Nadlan.api.get(url("/shapes")).then(function (list) {
        shapeStatus = {};
        list.forEach(function (s) { shapes[s.parcelId] = s.geometry; shapeStatus[s.parcelId] = s.status; });
        drawMap();
        if (fit && list.length) {
          var bounds = new google.maps.LatLngBounds();
          list.forEach(function (s) { s.geometry.coordinates[0].forEach(function (c) { bounds.extend({ lat: c[1], lng: c[0] }); }); });
          map.fitBounds(bounds, 60);
        }
      }, fail);
    }

    function shapeOf(parcelId) {
      if (shapes[parcelId]) { return; }
      Nadlan.api.get("/api/parcels/" + parcelId).then(function (d) { shapes[parcelId] = d.geometry; drawMap(); }, function () { /* outlined once loaded */ });
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
      busy = true;
      render();
      return promise.then(function (detail) {
        busy = false;
        req = detail.request || detail;
        render();
        loadShapes(false);
        if (message) { setStatus(message); }
      }, fail);
    }

    function addPending() {
      var ids = $win.find("[data-add-type]:checked").map(function () { return Number($(this).data("addType")); }).get();
      if (!ids.length) { setStatus("Tick at least one work for the parcels.", true); return; }
      rememberWorks(ids);
      var pending = selected.filter(function (s) { return !hasWork(s.parcelId); });
      busy = true;
      render();
      var added = 0, failed = [];
      // One by one: each is checked on the server (areas); one refused doesn't stop the others.
      pending.reduce(function (chain, s) {
        return chain.then(function () {
          return Nadlan.api.post(url("/parcels"), { parcelId: s.parcelId, workTypeIds: ids }).then(function (res) { req = res.request; added++; },
            function (err) { failed.push((s.registryId || "#" + s.parcelId) + ": " + err.message); });
        });
      }, Promise.resolve()).then(function () {
        busy = false;
        render();
        loadShapes(false);
        setStatus(failed.length ? "Added " + added + "; not added - " + failed.join("; ") : "Added " + added + " parcel" + (added === 1 ? "" : "s") + " to the work.", failed.length > 0);
      });
    }

    function toggleWork(parcelId, workTypeId, on) {
      if (on) {
        return after(Nadlan.api.post(url("/parcels"), { parcelId: parcelId, workTypeIds: [workTypeId] }), "Work added.");
      }
      var row = req.items.filter(function (r) { return r.parcelId === parcelId && r.workTypeId === workTypeId; })[0];
      if (!row) { return null; }
      var last = req.items.filter(function (r) { return r.parcelId === parcelId; }).length === 1;
      if (!last) { return after(Nadlan.api.del(url("/works/" + row.workItemId)), row.workTypeName + " removed."); }
      return Nadlan.dialog.confirm("Remove the parcel", "It is the parcel's last work: the parcel leaves the work too.", "Remove").then(function (ok) {
        if (ok) { return after(Nadlan.api.del(url("/works/" + row.workItemId)), "Parcel removed from the work."); }
        render(); // put the tick back
        return null;
      });
    }

    function removeParcel(parcelId) {
      var p = parcelsOfWork().filter(function (x) { return x.parcelId === parcelId; })[0];
      Nadlan.dialog.confirm("Remove from the work", "Remove parcel " + esc(p.registryId || "") + " and its works from \"" + esc(req.name) + "\"?", "Remove").then(function (ok) {
        if (!ok) { return; }
        after(Nadlan.api.del(url("/parcels/" + parcelId)), "Parcel removed from the work.").then(function () {
          delete shapeStatus[parcelId];
          drawMap();
        });
      });
    }

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
      $win.on("click", "[data-act=clear-pending]", function () {
        selected = selected.filter(function (s) { return hasWork(s.parcelId); });
        render();
      });
      $win.on("click", "[data-unpick]", function () {
        var id = Number($(this).data("unpick"));
        selected = selected.filter(function (s) { return s.parcelId !== id; });
        render();
      });
      $win.on("click", "[data-act=add]", addPending);
      // A Parcel of the work: open / close it in the list and select / deselect it on the map, like a click there.
      $win.on("click", "[data-act=toggle]", function () {
        var id = Number($(this).closest(".work-parcel").data("parcel"));
        var p = parcelsOfWork().filter(function (x) { return x.parcelId === id; })[0];
        clicked({ parcelId: id, registryId: p.registryId });
        if (isSelected(id) && shapes[id]) {
          var bounds = new google.maps.LatLngBounds();
          shapes[id].coordinates[0].forEach(function (c) { bounds.extend({ lat: c[1], lng: c[0] }); });
          map.panTo(bounds.getCenter());
        }
      });
      $win.on("change", "[data-work-type]", function () {
        var id = Number($(this).closest(".work-parcel").data("parcel"));
        toggleWork(id, Number($(this).data("workType")), this.checked);
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
      if ($win) { $win.remove(); $win = null; }
      req = null;
      selected = [];
      shapeStatus = {};
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
