// The work report (Work page, piece 4, owner 2026-11-01): one row per work across every request, filters, and the money -
// Offered, Approved, Done, Paid, To pay - worked out on the server, per currency (two are never added together). Grouped
// by contractor / work type / request / area / parcel, each group opens its works. This is also where staff compare
// contractors' offers. Prices stay per work: no price per m².
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var W = Nadlan.work;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  var $panel, options = null, last = null, sort = null, open = {};
  var filter = { statuses: [], groupBy: "None", dateOf: "Offered" }; // kept while the page is open

  var GROUPS = [
    { key: "None", name: "No grouping" },
    { key: "Contractor", name: "Contractor", field: "contractorContactId" },
    { key: "WorkType", name: "Work type", field: "workTypeId" },
    { key: "Request", name: "Request", field: "workRequestId" },
    { key: "Area", name: "Area", field: "geographicAreaId" },
    { key: "Parcel", name: "Parcel", field: "parcelId" }
  ];

  var MONEY = [
    { key: "offered", name: "Offered", hint: "Waiting for our decision" },
    { key: "approved", name: "Approved", hint: "Every approved work, whatever happened since (approved, in work, done, paid)" },
    { key: "done", name: "Done", hint: "Done or paid" },
    { key: "paid", name: "Paid", hint: "Paid" },
    { key: "toPay", name: "To pay", hint: "Done, not paid yet" }
  ];

  var COLUMNS = [
    { key: "request", label: "Request", value: function (r) { return r.requestName; } },
    { key: "contractor", label: "Contractor", value: function (r) { return r.contractorName; } },
    { key: "kaek", label: "KAEK", value: function (r) { return r.registryId || ""; } },
    { key: "otPlot", label: "OT / plot", value: function (r) { return (r.ot || "") + " / " + (r.plot || ""); } },
    { key: "area", label: "Area", value: function (r) { return r.geographicArea || ""; } },
    { key: "sqm", label: "m²", num: true, value: function (r) { return r.officialAreaSqm; } },
    { key: "work", label: "Work", value: function (r) { return r.workTypeName; } },
    { key: "status", label: "Status", value: function (r) { return Object.keys(W.WORK).indexOf(r.status); } },
    { key: "price", label: "Price", num: true, value: function (r) { return r.price; } },
    { key: "estStart", label: "Est. start", value: function (r) { return r.estStartDate || ""; } },
    { key: "offered", label: "Offered", value: function (r) { return r.offeredUtc || ""; } },
    { key: "approved", label: "Approved", value: function (r) { return r.approvedUtc || ""; } },
    { key: "started", label: "Started", value: function (r) { return r.startedUtc || ""; } },
    { key: "done", label: "Done", value: function (r) { return r.doneUtc || ""; } },
    { key: "paid", label: "Paid", value: function (r) { return r.paidUtc || ""; } }
  ];

  // ---- the filters --------------------------------------------------------------------------------------------

  function selectHtml(name, label, items, idOf, nameOf, all) {
    return "<label>" + esc(label) + "<select class=\"input\" name=\"" + name + "\"><option value=\"\">" + esc(all) + "</option>" +
      items.map(function (x) { return "<option value=\"" + idOf(x) + "\">" + esc(nameOf(x)) + "</option>"; }).join("") + "</select></label>";
  }

  function formHtml() {
    return "<form class=\"form work-report-filters\" autocomplete=\"off\">" +
      "<fieldset class=\"checks\"><legend>Status</legend>" + Object.keys(W.WORK).map(function (code) {
        return "<label class=\"check\"><input type=\"checkbox\" name=\"status\" value=\"" + code + "\"> " + esc(W.WORK[code].name) + "</label>";
      }).join("") + "</fieldset>" +
      "<div class=\"work-report-row\">" +
        selectHtml("contractorId", "Contractor", options.contractors, function (c) { return c.contactId; }, function (c) { return c.displayName; }, "All") +
        selectHtml("workTypeId", "Work type", options.workTypes, function (t) { return t.workTypeId; }, function (t) { return t.name + (t.isActive ? "" : " (inactive)"); }, "All") +
        selectHtml("requestId", "Request", options.requests, function (r) { return r.workRequestId; }, function (r) { return r.name + " · " + r.contractor; }, "All") +
        selectHtml("areaId", "Area", options.areas.concat([{ geographicAreaId: 0, name: "(no area)" }]), function (a) { return a.geographicAreaId; }, function (a) { return a.name; }, "All") +
      "</div>" +
      "<div class=\"work-report-row\">" +
        "<label>KAEK<input class=\"input\" name=\"kaek\" placeholder=\"Part of it\"></label>" +
        "<label class=\"narrow\">OT<input class=\"input\" name=\"ot\"></label>" +
        "<label class=\"narrow\">Plot<input class=\"input\" name=\"plot\"></label>" +
        "<label class=\"narrow\">m² from<input class=\"input\" name=\"minSqm\" inputmode=\"decimal\"></label>" +
        "<label class=\"narrow\">m² to<input class=\"input\" name=\"maxSqm\" inputmode=\"decimal\"></label>" +
        "<label>Dates of<select class=\"input\" name=\"dateOf\"><option>Offered</option><option>Approved</option><option>Done</option><option>Paid</option></select></label>" +
        "<label>From<input class=\"input\" type=\"date\" name=\"from\"></label>" +
        "<label>To<input class=\"input\" type=\"date\" name=\"to\"></label>" +
        "<label>Group by<select class=\"input\" name=\"groupBy\">" + GROUPS.map(function (g) { return "<option value=\"" + g.key + "\">" + esc(g.name) + "</option>"; }).join("") + "</select></label>" +
      "</div>" +
      "<div class=\"btn-row\"><button type=\"submit\" class=\"btn btn-primary\">Show</button><button type=\"button\" class=\"btn\" data-act=\"clear\">Clear the filters</button></div>" +
    "</form>";
  }

  function writeForm($f) {
    $f.find("[name=status]").each(function () { this.checked = filter.statuses.indexOf(this.value) >= 0; });
    ["contractorId", "workTypeId", "requestId", "areaId", "kaek", "ot", "plot", "minSqm", "maxSqm", "dateOf", "from", "to", "groupBy"].forEach(function (k) {
      $f.find("[name=" + k + "]").val(filter[k] === undefined || filter[k] === null ? "" : filter[k]);
    });
    if (!$f.find("[name=dateOf]").val()) { $f.find("[name=dateOf]").val("Offered"); }
    if (!$f.find("[name=groupBy]").val()) { $f.find("[name=groupBy]").val("None"); }
  }

  function readForm($f) {
    var f = { statuses: $f.find("[name=status]:checked").map(function () { return this.value; }).get() };
    ["contractorId", "workTypeId", "requestId", "areaId", "kaek", "ot", "plot", "minSqm", "maxSqm", "dateOf", "from", "to", "groupBy"].forEach(function (k) {
      f[k] = $.trim($f.find("[name=" + k + "]").val() || "");
    });
    return f;
  }

  // The user's own days, midnight to midnight, as UTC moments for the server.
  function dayStart(day) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day || "");
    return m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : null;
  }

  function query(f) {
    var number = function (text) { var n = Nadlan.format.parseNumber(text); return n === null || isNaN(n) ? null : n; };
    var from = dayStart(f.from), to = dayStart(f.to);
    if (to) { to.setDate(to.getDate() + 1); }
    return {
      statuses: f.statuses, contractorId: f.contractorId, workTypeId: f.workTypeId, requestId: f.requestId, areaId: f.areaId,
      kaek: f.kaek, ot: f.ot, plot: f.plot, minSqm: number(f.minSqm), maxSqm: number(f.maxSqm), dateOf: f.dateOf,
      fromUtc: from ? from.toISOString() : null, toUtc: to ? to.toISOString() : null, groupBy: f.groupBy
    };
  }

  // ---- the results --------------------------------------------------------------------------------------------

  function totalsHtml(totals) {
    if (!totals.length) { return "<div class=\"work-card muted\">No work matches.</div>"; }
    return totals.map(function (t) {
      return "<div class=\"work-card work-totals\">" +
        (totals.length > 1 ? "<div class=\"work-totals-cur\">" + esc(t.currencyCode) + "</div>" : "") +
        MONEY.map(function (m) {
          return "<div class=\"work-total\" title=\"" + esc(m.hint) + "\"><div class=\"work-total-name\">" + esc(m.name) + "</div>" +
            "<div class=\"work-total-sum\">" + W.money(t[m.key + "Sum"], t.currencyCode) + "</div>" +
            "<div class=\"muted work-total-count\">" + t[m.key + "Count"] + " work" + (t[m.key + "Count"] === 1 ? "" : "s") + "</div></div>";
        }).join("") +
        "<div class=\"work-total\"><div class=\"work-total-name\">Works</div><div class=\"work-total-sum\">" + t.works + "</div><div class=\"muted work-total-count\">listed</div></div>" +
      "</div>";
    }).join("");
  }

  function sorted(rows) {
    if (!sort) { return rows; }
    var col = COLUMNS.filter(function (c) { return c.key === sort.key; })[0];
    return rows.map(function (r, i) { return { r: r, i: i }; }).sort(function (a, b) {
      var x = col.value(a.r), y = col.value(b.r);
      var empty = function (v) { return v === null || v === undefined || v === ""; };
      if (empty(x) !== empty(y)) { return empty(x) ? 1 : -1; }
      var c = col.num ? Number(x) - Number(y) : String(x).localeCompare(String(y), undefined, { numeric: true });
      return (c || a.i - b.i) * (c ? sort.dir : 1);
    }).map(function (x) { return x.r; });
  }

  function rowsHtml(rows) {
    if (!rows.length) { return "<div class=\"table-empty muted\">No works.</div>"; }
    return "<table class=\"meta-table work-items work-report-table\"><thead><tr>" + COLUMNS.map(function (c) {
      var on = sort && sort.key === c.key;
      return "<th class=\"" + (c.num ? "num " : "") + "sortable\" data-sort=\"" + c.key + "\" tabindex=\"0\" aria-sort=\"" +
        (on ? (sort.dir > 0 ? "ascending" : "descending") : "none") + "\">" + esc(c.label) + (on ? (sort.dir > 0 ? " ▲" : " ▼") : "") + "</th>";
    }).join("") + "</tr></thead><tbody>" + sorted(rows).map(function (r) {
      return "<tr>" +
        "<td><a href=\"#request=" + r.workRequestId + "\">" + esc(r.requestName) + "</a></td>" +
        "<td>" + esc(r.contractorName) + "</td>" +
        "<td>" + Nadlan.format.registryId(r.registryId, false) + "</td>" +
        "<td>" + Nadlan.format.text(r.ot) + " / " + Nadlan.format.text(r.plot) + "</td>" +
        "<td>" + Nadlan.format.text(r.geographicArea) + "</td>" +
        "<td class=\"num\">" + (r.officialAreaSqm === null ? "—" : esc(Number(r.officialAreaSqm).toLocaleString("en-US", { maximumFractionDigits: 0 }))) + "</td>" +
        "<td>" + esc(r.workTypeName) + "</td>" +
        "<td>" + W.workBadge(r.status) + "</td>" +
        "<td class=\"num\">" + W.money(r.price, r.currencyCode) + "</td>" +
        "<td>" + W.date(r.estStartDate) + "</td>" +
        "<td>" + W.dateTime(r.offeredUtc) + "</td><td>" + W.dateTime(r.approvedUtc) + "</td><td>" + W.dateTime(r.startedUtc) + "</td>" +
        "<td>" + W.dateTime(r.doneUtc) + "</td><td>" + W.dateTime(r.paidUtc) + "</td>" +
      "</tr>";
    }).join("") + "</tbody></table>";
  }

  function groupsHtml(res, group) {
    if (!res.groups.length) { return ""; }
    return "<table class=\"meta-table work-groups\"><thead><tr><th>" + esc(group.name) + "</th>" + (res.totals.length > 1 ? "<th>Currency</th>" : "") +
      MONEY.map(function (m) { return "<th class=\"num\" title=\"" + esc(m.hint) + "\">" + esc(m.name) + "</th>"; }).join("") +
      "<th class=\"num\">Works</th><th></th></tr></thead><tbody>" +
      res.groups.map(function (g) {
        var key = g.groupKey + "|" + g.currencyCode;
        var label = g.groupLabel || (group.key === "Area" ? "(no area)" : "—");
        var mine = res.rows.filter(function (r) { return String(r[group.field]) === g.groupKey && r.currencyCode === g.currencyCode; });
        return "<tr class=\"work-group\" data-group=\"" + esc(key) + "\"><td><strong>" + esc(label) + "</strong></td>" +
          (res.totals.length > 1 ? "<td>" + esc(g.currencyCode) + "</td>" : "") +
          MONEY.map(function (m) {
            return "<td class=\"num\">" + W.money(g[m.key + "Sum"], g.currencyCode) + " <span class=\"muted\">(" + g[m.key + "Count"] + ")</span></td>";
          }).join("") +
          "<td class=\"num\">" + g.works + "</td>" +
          "<td><button type=\"button\" class=\"btn btn-small\" data-act=\"toggle-group\" aria-expanded=\"" + (open[key] ? "true" : "false") + "\">" +
            (open[key] ? "Hide works" : "Show works") + "</button></td></tr>" +
          (open[key] ? "<tr class=\"work-group-rows\"><td colspan=\"" + (MONEY.length + 3 + (res.totals.length > 1 ? 1 : 0)) + "\"><div class=\"work-table-wrap\">" + rowsHtml(mine) + "</div></td></tr>" : "");
      }).join("") + "</tbody></table>";
  }

  function renderResults() {
    var $out = $panel.find(".work-report-results");
    if (!last) { $out.html("<div class=\"muted\">Loading…</div>"); return; }
    var group = GROUPS.filter(function (g) { return g.key === (filter.groupBy || "None"); })[0];
    $out.html(
      totalsHtml(last.totals) +
      (last.truncated ? "<div class=\"warn\">Showing the first 5,000 works; the totals above count every matching work.</div>" : "") +
      "<div class=\"work-card\"><div class=\"work-table-wrap\">" + (last.groups ? groupsHtml(last, group) : rowsHtml(last.rows)) + "</div></div>");
  }

  function run() {
    last = null;
    open = {};
    renderResults();
    Nadlan.api.get("/api/work/report", query(filter)).then(function (res) { last = res; renderResults(); },
      function (err) { $panel.find(".work-report-results").html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  // ---- the page -----------------------------------------------------------------------------------------------

  function openReport($target) {
    $panel = $target;
    $panel.off().html("<div class=\"muted\">Loading…</div>");
    Nadlan.api.get("/api/work/report/options").then(function (o) {
      options = o;
      $panel.html("<div class=\"meta-toolbar\"><h3>Work report</h3><span class=\"muted\">Every work across the requests, with the money. " +
        "Prices are per work (no price per m²).</span></div>" +
        "<div class=\"work-card\">" + formHtml() + "</div><div class=\"work-report-results\"></div>");
      var $f = $panel.find(".work-report-filters");
      writeForm($f);
      $f.on("submit", function (e) { e.preventDefault(); filter = readForm($f); run(); });
      $f.on("change", "select, [type=checkbox]", function () { filter = readForm($f); run(); });
      $f.on("click", "[data-act=clear]", function () {
        filter = { statuses: [], groupBy: filter.groupBy || "None", dateOf: "Offered" };
        writeForm($f);
        run();
      });
      $panel.on("click", "[data-act=toggle-group]", function () {
        var key = $(this).closest("tr").data("group");
        open[key] = !open[key];
        renderResults();
      });
      $panel.on("click keydown", "th[data-sort]", function (e) {
        if (e.type === "keydown" && e.key !== "Enter" && e.key !== " ") { return; }
        e.preventDefault();
        var key = $(this).data("sort");
        sort = sort && sort.key === key ? (sort.dir > 0 ? { key: key, dir: -1 } : null) : { key: key, dir: 1 };
        renderResults();
      });
      run();
    }, function (err) { $panel.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  Nadlan.workReport = { open: openReport };
})(window, jQuery);
