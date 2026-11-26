// "Cadastre changes" (Admin only, migration 034): what the KAEK importer's update mode found on Parcels already in GreekPlot -
// the same KAEK with another polygon, another KAEK at the point, or nothing there any more. A changed polygon never replaces
// ours by itself (OT/plot, Assets, work requests and deals hang on the Parcel): an Admin looks at it here.
//   Show = the Parcel on the map, our outline dashed and the Cadastre's solid;
//   Accept = the normal polygon edit (same checks, the old polygon kept in the history, the official area from the Cadastre);
//   Keep as is = reject (only the decision is kept); Open parcel = its card.
//   "Accept all small changes in this area" = under 2 %; Parcels with Assets or in a work request always one by one.
// The server answers 404 to anyone but an Admin.
//   Nadlan.cadastreChanges.init({ map, areas, setStatus, showParcel(parcelId), onChanged(parcelId|null) }) -> { open() }
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var OURS = "#facc15";      // our polygon, dashed
  var CADASTRE = "#22d3ee";  // the Cadastre's, solid

  function init(options) {
    var map = options.map;
    var esc = function (s) { return Nadlan.format.escapeHtml(s); };
    var d = null;
    var shown = [];      // google.maps overlays of the change on show
    var items = [];
    var gen = 0;

    function clearShown() {
      shown.forEach(function (o) { o.setMap(null); });
      shown = [];
    }

    function path(ring) { return ring.map(function (p) { return { lng: p[0], lat: p[1] }; }); }

    // Ours dashed (a line of short dashes over each ring), the Cadastre's solid with a light fill.
    function draw(current, proposed) {
      clearShown();
      var bounds = new google.maps.LatLngBounds();
      if (proposed) {
        shown.push(new google.maps.Polygon({ map: map, paths: proposed.coordinates.map(path), strokeColor: CADASTRE, strokeWeight: 3,
          strokeOpacity: 1, fillColor: CADASTRE, fillOpacity: 0.15, clickable: false, zIndex: 1100 }));
        proposed.coordinates[0].forEach(function (p) { bounds.extend({ lng: p[0], lat: p[1] }); });
      }
      if (current) {
        current.coordinates.forEach(function (ring) {
          shown.push(new google.maps.Polyline({ map: map, path: path(ring), strokeOpacity: 0, clickable: false, zIndex: 1101,
            icons: [{ icon: { path: "M 0,-1 0,1", strokeOpacity: 1, strokeColor: OURS, strokeWeight: 3, scale: 3 }, offset: "0", repeat: "12px" }] }));
        });
        current.coordinates[0].forEach(function (p) { bounds.extend({ lng: p[0], lat: p[1] }); });
      }
      if (!bounds.isEmpty()) { map.fitBounds(bounds, 120); }
    }

    function kindHtml(p) {
      if (p.kind === "GeometryChanged") {
        return "Polygon differs by <b>" + esc(Number(p.differenceSqm).toLocaleString("en-US", { maximumFractionDigits: 1 })) + " m²</b>" +
          (p.differencePercent !== null ? " (" + esc(p.differencePercent) + " %)" : "") +
          (p.areaSqm !== null ? " · Cadastre " + esc(Number(p.areaSqm).toLocaleString("en-US")) + " m²" : "");
      }
      if (p.kind === "KaekMismatch") { return "Another KAEK at the point: <span class=\"kaek\">" + esc(p.kaekSeen) + "</span>"; }
      return "Not found in the " + esc(Nadlan.cadastreStage ? Nadlan.cadastreStage.label(p.stageSeen).toLowerCase() : p.stageSeen) + " layer";
    }

    function rowHtml(p) {
      var flags = [];
      if (p.hasAssets) { flags.push("has Assets"); }
      if (p.inWorkRequest) { flags.push("in a work request"); }
      if (p.parcelEditedSince) { flags.push("saved since the check"); }
      var pending = p.status === "Pending";
      return "<li class=\"cad-change\" data-id=\"" + p.proposalId + "\">" +
        "<div><span class=\"kaek\">" + esc(p.registryId || "#" + p.parcelId) + "</span>" +
          (p.ot || p.plot ? " <span class=\"muted\">OT " + esc(p.ot || "—") + " / " + esc(p.plot || "—") + "</span>" : "") +
          " <span class=\"muted\">· " + esc(Nadlan.cadastreStage ? Nadlan.cadastreStage.day(p.seenUtc) : p.seenUtc) + "</span></div>" +
        "<div>" + kindHtml(p) + "</div>" +
        (flags.length ? "<div class=\"warn\">" + esc(flags.join(", ")) + "</div>" : "") +
        (pending ? "" : "<div class=\"muted\">" + esc(p.status) + (p.decidedBy ? " by " + esc(p.decidedBy) : "") + "</div>") +
        "<div class=\"btn-row\">" +
          "<button type=\"button\" class=\"btn btn-small\" data-show>Show</button>" +
          (pending && p.kind === "GeometryChanged" ? "<button type=\"button\" class=\"btn btn-small btn-primary\" data-accept>Accept</button>" : "") +
          (pending ? "<button type=\"button\" class=\"btn btn-small\" data-reject title=\"Reject: keep our polygon and KAEK as they are\">Keep as is</button>" : "") +
          "<button type=\"button\" class=\"btn btn-small\" data-open>Open parcel</button>" +
        "</div></li>";
    }

    function $content() { return d ? d.$el.find(".cad-changes") : $(); }

    function load() {
      var mine = ++gen;
      var $c = $content();
      var areaId = $c.find("[name=area]").val();
      $c.find("[data-accept-small]").prop("disabled", !areaId);
      $c.find(".cad-changes-list").html("<li class=\"muted\">Loading…</li>");
      Nadlan.api.get("/api/parcel-proposals", { areaId: areaId || null, status: $c.find("[name=status]").val() }).then(function (r) {
        if (mine !== gen) { return; }
        items = r.items;
        $c.find(".cad-changes-list").html(items.length ? items.map(rowHtml).join("") : "<li class=\"muted\">Nothing here.</li>");
        $c.find(".cad-changes-count").text(items.length + (r.truncated ? "+" : "") + " change(s)");
      }, function (err) { if (mine === gen) { $c.find(".cad-changes-list").html("<li class=\"error\">" + esc(err.message) + "</li>"); } });
    }

    function byId(id) { return items.filter(function (x) { return x.proposalId === id; })[0]; }

    function show(id) {
      return Nadlan.api.get("/api/parcel-proposals/" + id).then(function (r) {
        draw(r.current, r.proposal.geometry);
        options.setStatus("Dashed yellow: GreekPlot's polygon. Solid blue: the Cadastre's" + (r.proposal.geometry ? "." : " (none: nothing there)."));
      }, function (err) { options.setStatus(err.message, true); });
    }

    function accept(id, acceptOverlaps) {
      var url = "/api/parcel-proposals/" + id + "/accept" + (acceptOverlaps ? "?acceptOverlaps=true" : "");
      return Nadlan.api.post(url).then(function (r) {
        clearShown();
        options.setStatus("Polygon replaced by the Cadastre's." + (r.warning ? " " + r.warning : ""));
        options.onChanged(r.parcelId);
        load();
      }, function (err) {
        if (err.code === "PARCEL_OVERLAPS" && !acceptOverlaps) {
          var list = (err.overlaps || []).map(function (o) { return esc(o.registryId || "#" + o.parcelId) + " (" + Math.round(o.overlapSqm) + " m²)"; }).join(", ");
          return Nadlan.dialog.confirm("Overlaps other Parcels", "The Cadastre's polygon overlaps " + (list || "other Parcels") + ". Accept it anyway?", "Accept anyway")
            .then(function (yes) { return yes ? accept(id, true) : null; });
        }
        Nadlan.dialog.showError("Could not accept", err);
      });
    }

    function open() {
      if (d) { load(); return; }
      var areas = (options.areas || []).slice().sort(function (a, b) { return String(a.name).localeCompare(String(b.name)); });
      var $c = $("<div class=\"form cad-changes\">" +
        "<div class=\"form-row\"><label>Area<select class=\"input\" name=\"area\"><option value=\"\">All areas</option>" +
          areas.map(function (a) { return "<option value=\"" + a.id + "\">" + esc(a.name) + "</option>"; }).join("") + "</select></label>" +
        "<label>Show<select class=\"input\" name=\"status\"><option value=\"Pending\">Waiting for review</option><option value=\"Accepted\">Accepted</option>" +
          "<option value=\"Rejected\">Kept as is</option><option value=\"Superseded\">Superseded</option><option value=\"all\">All</option></select></label></div>" +
        "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-small\" data-accept-small disabled title=\"Accept every change under 2 % of the parcel in this area. " +
          "Parcels with Assets or in a work request, saved since the check, or overlapping others are left for one-by-one review.\">Accept all small changes in this area</button>" +
          "<span class=\"muted cad-changes-count\"></span></div>" +
        "<ul class=\"cad-changes-list\"></ul></div>");
      d = Nadlan.dialog.open({
        title: "Cadastre changes",
        content: $c,
        modal: false,
        width: Math.min(520, window.innerWidth - 24),
        position: { my: "right top", at: "right-12 top+70", of: window },
        buttons: [{ text: "Close", click: function () { d.close(); } }],
        onClose: function () { clearShown(); d = null; gen++; }
      });
      $c.on("change", "select", load);
      $c.on("click", "[data-show]", function () { show(Number($(this).closest("[data-id]").data("id"))); });
      $c.on("click", "[data-open]", function () {
        var p = byId(Number($(this).closest("[data-id]").data("id")));
        if (p) { options.showParcel(p.parcelId); }
      });
      $c.on("click", "[data-accept]", function () {
        var id = Number($(this).closest("[data-id]").data("id"));
        show(id).then(function () {
          return Nadlan.dialog.confirm("Accept the Cadastre's polygon", "Replace GreekPlot's polygon (dashed) with the Cadastre's (solid)? " +
            "The old one stays in the parcel's history, and the official area becomes the Cadastre's.", "Accept");
        }).then(function (yes) { if (yes) { return accept(id, false); } });
      });
      $c.on("click", "[data-reject]", function () {
        var id = Number($(this).closest("[data-id]").data("id"));
        Nadlan.api.post("/api/parcel-proposals/" + id + "/reject").then(function () { clearShown(); load(); },
          function (err) { Nadlan.dialog.showError("Could not save", err); });
      });
      $c.on("click", "[data-accept-small]", function () {
        var areaId = $c.find("[name=area]").val();
        if (!areaId) { return; }
        Nadlan.dialog.confirm("Accept all small changes", "Accept every polygon change under 2 % in " + esc($c.find("[name=area] option:selected").text()) +
          "? Parcels with Assets or in a work request are left for one-by-one review.", "Accept").then(function (yes) {
          if (!yes) { return; }
          return Nadlan.api.post("/api/parcel-proposals/accept-small?areaId=" + encodeURIComponent(areaId)).then(function (r) {
            options.setStatus(r.accepted + " change(s) accepted" + (r.skipped.length ? ", " + r.skipped.length + " left for review." : "."));
            if (r.accepted) { options.onChanged(null); }
            load();
          }, function (err) { Nadlan.dialog.showError("Could not accept", err); });
        });
      });
      load();
    }

    return { open: open };
  }

  Nadlan.cadastreChanges = { init: init };
})(window, jQuery);
