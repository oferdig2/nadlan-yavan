// The Work page (work.html, MANAGE_WORK): the list of work requests, "+ New request", and one request opened
// (work-request.js). The address keeps the open request (#request=12), so Back and a reload come back to it.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var W = Nadlan.work;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };
  var $panel;

  function requestIdFromHash() {
    var m = /^#request=(\d+)$/.exec(window.location.hash);
    return m ? Number(m[1]) : null;
  }

  function route() {
    var id = requestIdFromHash();
    W.setStatus("");
    if (id) {
      Nadlan.workRequest.open($panel, id);
    } else {
      showList();
    }
  }

  // ---- the list -----------------------------------------------------------------------------------------------

  function worksSummary(w) {
    var parts = [
      [w.requested, "requested"], [w.offered, "offered"], [w.approved, "approved"],
      [w.inWork, "in work"], [w.done, "done"], [w.paid, "paid"]
    ].filter(function (p) { return p[0] > 0; }).map(function (p) { return p[0] + " " + p[1]; });
    return parts.length ? parts.join(" · ") : "<span class=\"muted\">No works yet</span>";
  }

  function showList() {
    $panel.off();
    $panel.html(
      "<div class=\"meta-toolbar\"><h3>Work requests</h3>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"new\">+ New request</button>" +
        "<input type=\"search\" class=\"input\" data-act=\"filter\" placeholder=\"Find by name or contractor\" aria-label=\"Find by name or contractor\">" +
      "</div>" +
      "<div class=\"work-table-wrap\"><div class=\"muted\">Loading…</div></div>");
    var rows = [];

    function render() {
      var q = $.trim($panel.find("[data-act=filter]").val() || "").toLowerCase();
      var shown = rows.filter(function (r) {
        return !q || (r.name + " " + r.contractor.displayName).toLowerCase().indexOf(q) >= 0;
      });
      var $wrap = $panel.find(".work-table-wrap");
      if (!rows.length) {
        $wrap.html("<div class=\"table-empty muted\">No work requests yet. Press \"+ New request\" to make the first one.</div>");
        return;
      }

      $wrap.html(
        "<table class=\"meta-table work-requests\"><thead><tr>" +
          "<th>Name</th><th>Contractor</th><th class=\"num\">Parcels</th><th>Works</th><th>Link</th><th>Created</th><th>Last activity</th>" +
        "</tr></thead><tbody>" +
        shown.map(function (r) {
          return "<tr data-id=\"" + r.workRequestId + "\" tabindex=\"0\">" +
            "<td><a href=\"#request=" + r.workRequestId + "\">" + esc(r.name) + "</a></td>" +
            "<td>" + esc(r.contractor.displayName) + "</td>" +
            "<td class=\"num\">" + r.parcels + "</td>" +
            "<td>" + worksSummary(r.works) + "</td>" +
            "<td>" + W.linkBadge(r.link) + (r.hasPassword ? " <span class=\"muted\" title=\"The link asks for a password\">🔒</span>" : "") + "</td>" +
            "<td>" + W.date(r.createdUtc) + "</td>" +
            "<td>" + W.dateTime(r.lastActivityUtc) + "</td>" +
          "</tr>";
        }).join("") +
        "</tbody></table>" +
        (shown.length ? "" : "<div class=\"muted\">No request matches.</div>"));
    }

    $panel.on("input", "[data-act=filter]", render);
    $panel.on("click", "[data-act=new]", openNew);
    $panel.on("click", "tr[data-id]", function (e) {
      if ($(e.target).is("a")) { return; }
      window.location.hash = "request=" + $(this).data("id");
    });
    $panel.on("keydown", "tr[data-id]", function (e) {
      if (e.key === "Enter") { window.location.hash = "request=" + $(this).data("id"); }
    });

    Nadlan.api.get("/api/work/requests").then(function (list) { rows = list; render(); },
      function (err) { $panel.find(".work-table-wrap").html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  // ---- + New request ------------------------------------------------------------------------------------------

  function openNew() {
    var $form = $(
      "<form class=\"form\">" +
        "<label>Name<input class=\"input\" name=\"name\" maxlength=\"200\" placeholder=\"e.g. Skroponeria clearing Nov 2026\" autocomplete=\"off\"></label>" +
        "<label class=\"contractor-label\">Contractor</label>" +
        "<div class=\"muted\">Then add the parcels and the works to price. The link to send is on the request.</div>" +
      "</form>");
    var picker = W.contractorPicker({});
    $form.find(".contractor-label").append(picker.$el);
    var d = Nadlan.dialog.open({
      title: "New work request",
      content: $form,
      width: Math.min(520, window.innerWidth - 24),
      buttons: [
        { text: "Create", primary: true, click: create },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    $form.find("[name=name]").trigger("focus");

    function create() {
      var name = $.trim($form.find("[name=name]").val() || "");
      var contractorContactId = picker.value();
      if (!name) { d.showError("Give the request a name."); return; }
      if (!contractorContactId) { d.showError("Choose the contractor."); return; }
      d.showError("");
      d.busy(true);
      Nadlan.api.post("/api/work/requests", { name: name, contractorContactId: contractorContactId }).then(function (res) {
        d.close();
        window.location.hash = "request=" + res.workRequestId;
      }, function (err) { d.busy(false); d.showError(err.message); });
    }
  }

  // ---- start --------------------------------------------------------------------------------------------------

  $(function () {
    $panel = $("#work-panel");
    $("[data-tab=requests]").on("click", function () {
      if (window.location.hash) { window.location.hash = ""; } else { route(); }
    });

    Nadlan.session.ready.then(function () {
      if (!Nadlan.session.can("MANAGE_WORK")) {
        $panel.html("<div class=\"muted\">Your role doesn't include work requests. " +
          (Nadlan.session.can("USE_MAP") ? "<a href=\"/\">Back to the map</a>" : "<a href=\"/assets.html\">Back to the table</a>") + "</div>");
        return;
      }

      $(window).on("hashchange", route);
      route();
    }, function (err) {
      $panel.html("<div class=\"error\">" + esc(err.message) + "</div>");
    });
  });

  Nadlan.workPage = { showList: function () { window.location.hash = ""; } };
})(window, jQuery);
