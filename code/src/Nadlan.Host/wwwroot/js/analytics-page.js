// The analytics page (analytics.html, owner 2026-11-10; AI/Remarks/Design-Analytics-Page-2026-11-10.md) - Admin only
// (VIEW_ANALYTICS_PAGE). Left: find public links (deleted ones too) and tick them. Right: the bars for the ticked links -
// totals, hot properties, over time, the path, hours - aggregated, by link or by customer. Hover a bar for its top places
// (city level, text), click it for all of them, then a place for its visits, then a visit for its timeline.
// Plain HTML bars (no chart library). The choice and the filters live in the address, so a view can be bookmarked or sent.
// Wording: "visits on links sent to X", never "X visited" - a link can be forwarded.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var esc = function (s) { return Nadlan.format.escapeHtml(s === null || s === undefined ? "" : String(s)); };
  var MAX_LINKS = 50;
  var COLORS = ["#2563eb", "#16a34a", "#d97706", "#dc2626", "#7c3aed", "#0891b2", "#db2777", "#65a30d", "#ea580c", "#475569"];
  var VIEWS = [["metrics", "Totals"], ["assets", "Properties"], ["time", "Over time"], ["path", "Path"], ["hours", "Hours"]];
  var SPLITS = [["none", "Aggregated"], ["link", "By link"], ["customer", "By customer"]];
  var ASSET_METRICS = [["asset_clicks", "Asset clicks"], ["card_minutes", "Time on card"], ["photo_opens", "Photo opens"], ["photo_navs", "Photo navs"],
    ["measures", "Measures"], ["tour_stops", "Tour stops"]];
  var STATE_NAMES = { active: "Active", expired: "Expired", off: "Off", deleted: "Deleted" };

  var $main, $status;
  var state = {
    filters: { q: "", kind: "all", state: "all", createdFrom: "", createdTo: "", visitedFrom: "", visitedTo: "", createdBy: "", contactId: null, contactName: "" },
    page: 1, chosen: [], from: "", to: "", view: "metrics", split: "none", metric: "asset_clicks", rates: false, hot: 3, showAll: false
  };
  var shownRows = [], lastChart = null, whoCache = {};

  function status(text, isError) { $status.text(text || "").toggleClass("error", !!isError); }

  // ---- days (Athens) ---------------------------------------------------------------------------------------------

  function athensToday() {
    try { return new Intl.DateTimeFormat("en-CA", { timeZone: "Europe/Athens" }).format(new Date()); } catch (e) { return new Date().toISOString().slice(0, 10); }
  }

  function addDays(day, n) { var d = new Date(day + "T12:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); }

  function when(utc) { return new Date(utc).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" }); }

  function dayText(utc) { return new Date(utc).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }); }

  // ---- the address -----------------------------------------------------------------------------------------------

  function readAddress() {
    var q = new URLSearchParams(window.location.search), f = state.filters;
    f.q = q.get("q") || ""; f.kind = q.get("kind") || "all"; f.state = q.get("state") || "all";
    f.createdFrom = q.get("cfrom") || ""; f.createdTo = q.get("cto") || ""; f.visitedFrom = q.get("vfrom") || ""; f.visitedTo = q.get("vto") || "";
    f.createdBy = q.get("by") || ""; f.contactId = Number(q.get("customer")) || null;
    state.to = q.get("to") || athensToday();
    state.from = q.get("from") || addDays(state.to, -29);
    state.view = q.get("view") || "metrics";
    state.split = q.get("split") || "none";
    state.metric = q.get("metric") || "asset_clicks";
    state.rates = q.get("rates") === "1";
    state.hot = Number(q.get("hot")) > 0 ? Number(q.get("hot")) : 3;
    return (q.get("links") || "").split(",").map(Number).filter(function (n) { return n > 0; }).slice(0, MAX_LINKS);
  }

  function writeAddress() {
    var f = state.filters, q = new URLSearchParams();
    function put(k, v) { if (v !== "" && v !== null && v !== undefined) { q.set(k, v); } }
    put("links", state.chosen.map(function (l) { return l.id; }).join(","));
    put("from", state.from); put("to", state.to);
    if (state.view !== "metrics") { put("view", state.view); }
    if (state.split !== "none") { put("split", state.split); }
    if (state.view === "assets") { put("metric", state.metric); if (state.hot !== 3) { put("hot", state.hot); } }
    if (state.rates) { put("rates", "1"); }
    put("q", f.q); if (f.kind !== "all") { put("kind", f.kind); } if (f.state !== "all") { put("state", f.state); }
    put("cfrom", f.createdFrom); put("cto", f.createdTo); put("vfrom", f.visitedFrom); put("vto", f.visitedTo); put("by", f.createdBy); put("customer", f.contactId);
    window.history.replaceState(null, "", "/analytics.html" + (q.toString() ? "?" + q.toString() : ""));
  }

  // ---- layout ----------------------------------------------------------------------------------------------------

  function layout() {
    $main.html(
      "<div class=\"ap-layout\">" +
        "<aside class=\"ap-finder\">" +
          "<form class=\"form ap-filters\" autocomplete=\"off\">" +
            "<label>Text<input class=\"input\" name=\"q\" placeholder=\"Title, Portfolio, made by, customer\"></label>" +
            "<div class=\"form-row\"><label>Kind<select class=\"input\" name=\"kind\"><option value=\"all\">All</option><option value=\"portfolio\">Portfolio</option>" +
              "<option value=\"assets\">Chosen properties</option></select></label>" +
            "<label>State<select class=\"input\" name=\"state\"><option value=\"all\">All</option><option value=\"active\">Active</option><option value=\"expired\">Expired</option>" +
              "<option value=\"off\">Off</option><option value=\"deleted\">Deleted</option></select></label></div>" +
            "<div class=\"form-row\"><label>Made from<input class=\"input\" type=\"date\" name=\"createdFrom\"></label><label>to<input class=\"input\" type=\"date\" name=\"createdTo\"></label></div>" +
            "<div class=\"form-row\"><label>Visited from<input class=\"input\" type=\"date\" name=\"visitedFrom\"></label><label>to<input class=\"input\" type=\"date\" name=\"visitedTo\"></label></div>" +
            "<label>Made by<select class=\"input\" name=\"createdBy\"><option value=\"\">Anyone</option></select></label>" +
            "<div class=\"ap-customer-filter\"><span class=\"ap-label\">Customer</span></div>" +
          "</form>" +
          "<div class=\"ap-results-head\"><span class=\"ap-count muted\"></span><button type=\"button\" class=\"btn btn-small\" data-ap=\"all\">Select all shown</button></div>" +
          "<div class=\"ap-results\"></div><div class=\"ap-pager\"></div>" +
        "</aside>" +
        "<section class=\"ap-right\">" +
          "<div class=\"ap-chips\"></div>" +
          "<div class=\"ap-controls\">" +
            "<div class=\"ap-range\"><label>From<input class=\"input\" type=\"date\" name=\"from\"></label><label>to<input class=\"input\" type=\"date\" name=\"to\"></label>" +
              "<span class=\"ap-quick\">" + [["7", "7 d"], ["30", "30 d"], ["90", "90 d"], ["all", "All"]].map(function (q) {
                return "<button type=\"button\" class=\"btn btn-small\" data-quick=\"" + q[0] + "\">" + q[1] + "</button>"; }).join("") + "</span>" +
              "<span class=\"muted ap-tz\">Days in Athens time</span></div>" +
            "<div class=\"ap-switches\">" +
              "<span class=\"ap-seg\" data-seg=\"view\">" + VIEWS.map(function (v) { return "<button type=\"button\" data-v=\"" + v[0] + "\">" + v[1] + "</button>"; }).join("") + "</span>" +
              "<span class=\"ap-seg\" data-seg=\"split\">" + SPLITS.map(function (v) { return "<button type=\"button\" data-v=\"" + v[0] + "\">" + v[1] + "</button>"; }).join("") + "</span>" +
              "<label class=\"check ap-rates\"><input type=\"checkbox\" name=\"rates\"> Per visit</label>" +
              "<label class=\"ap-metric\">Sort by <select class=\"input input-small\" name=\"metric\">" + ASSET_METRICS.map(function (m) {
                return "<option value=\"" + m[0] + "\">" + m[1] + "</option>"; }).join("") + "</select></label>" +
              "<label class=\"ap-hot\" title=\"A property opened on at least this many different devices gets a 🔥\">🔥 from <input class=\"input input-small\" type=\"number\" min=\"1\" max=\"99\" name=\"hot\"> devices</label>" +
            "</div>" +
          "</div>" +
          "<div class=\"ap-chart\"></div>" +
          "<p class=\"an-attribution muted\">" + Nadlan.analyticsReport.attribution + " Only anonymous visitors of public links are recorded; a link can be forwarded, " +
            "so the visits on a link sent to a customer aren't always that person.</p>" +
        "</section>" +
        "<aside class=\"ap-side\" hidden><div class=\"ap-side-head\"><strong class=\"ap-side-title\"></strong>" +
          "<button type=\"button\" class=\"btn btn-small\" data-ap=\"close-side\" title=\"Close\">×</button></div><div class=\"ap-side-body\"></div></aside>" +
      "</div><div class=\"ap-tip\" hidden></div>");

    var $f = $main.find(".ap-filters"), f = state.filters;
    $f.find("[name=q]").val(f.q); $f.find("[name=kind]").val(f.kind); $f.find("[name=state]").val(f.state);
    $f.find("[name=createdFrom]").val(f.createdFrom); $f.find("[name=createdTo]").val(f.createdTo);
    $f.find("[name=visitedFrom]").val(f.visitedFrom); $f.find("[name=visitedTo]").val(f.visitedTo);
    var customer = Nadlan.linkCustomer.field({ contactId: f.contactId, contactName: f.contactName, onChange: function (c) {
      f.contactId = c ? c.contactId : null; f.contactName = c ? c.displayName : ""; state.page = 1; search();
    } });
    customer.$el.find("[data-lc=new]").remove(); // a filter: choosing, not creating
    $f.find(".ap-customer-filter").append(customer.$el);

    Nadlan.api.get("/api/admin/users").then(function (users) {
      var $by = $f.find("[name=createdBy]");
      users.forEach(function (u) { $by.append($("<option>").val(u.userId).text(u.displayName || u.email)); });
      $by.val(f.createdBy);
    }, function () { $f.find("[name=createdBy]").closest("label").remove(); });

    var timer = null;
    $f.on("input change", "input:not(.ui-autocomplete-input), select", function () {
      f.q = $.trim($f.find("[name=q]").val()); f.kind = $f.find("[name=kind]").val(); f.state = $f.find("[name=state]").val();
      f.createdFrom = $f.find("[name=createdFrom]").val(); f.createdTo = $f.find("[name=createdTo]").val();
      f.visitedFrom = $f.find("[name=visitedFrom]").val(); f.visitedTo = $f.find("[name=visitedTo]").val(); f.createdBy = $f.find("[name=createdBy]").val() || "";
      state.page = 1;
      clearTimeout(timer);
      timer = setTimeout(search, 300);
    });

    var $c = $main.find(".ap-controls");
    $c.find("[name=from]").val(state.from); $c.find("[name=to]").val(state.to);
    $c.find("[name=rates]").prop("checked", state.rates); $c.find("[name=metric]").val(state.metric); $c.find("[name=hot]").val(state.hot);
    $c.on("change", "[name=from], [name=to]", function () {
      state.from = $c.find("[name=from]").val() || state.from; state.to = $c.find("[name=to]").val() || state.to;
      chart();
    });
    $c.on("click", "[data-quick]", function () {
      var q = $(this).data("quick");
      state.to = athensToday();
      state.from = q === "all" ? addDays(state.to, -730) : addDays(state.to, -(Number(q) - 1)); // "All" = the 2 years one chart may span
      $c.find("[name=from]").val(state.from); $c.find("[name=to]").val(state.to);
      chart();
    });
    $c.on("click", ".ap-seg button", function () {
      var seg = $(this).closest(".ap-seg").data("seg");
      state[seg] = $(this).data("v");
      state.showAll = false;
      chart();
    });
    $c.on("change", "[name=rates]", function () { state.rates = this.checked; render(); writeAddress(); });
    $c.on("change", "[name=metric]", function () { state.metric = $(this).val(); chart(); });
    $c.on("change", "[name=hot]", function () { state.hot = Math.max(1, Number($(this).val()) || 3); render(); writeAddress(); });

    $main.on("click", "[data-ap=all]", function () {
      var room = MAX_LINKS - state.chosen.length;
      var add = shownRows.filter(function (r) { return !isChosen(r.id); });
      if (add.length > room) { status("At most " + MAX_LINKS + " links at once: " + room + " more can be added.", true); add = add.slice(0, room); }
      add.forEach(function (r) { state.chosen.push(r); });
      chosenChanged();
    });
    $main.on("change", ".ap-row input[type=checkbox]", function () {
      var id = Number($(this).closest(".ap-row").data("id"));
      if (this.checked) {
        if (state.chosen.length >= MAX_LINKS) { this.checked = false; status("At most " + MAX_LINKS + " links at once - remove one first.", true); return; }
        state.chosen.push(shownRows.filter(function (r) { return r.id === id; })[0]);
      } else {
        state.chosen = state.chosen.filter(function (l) { return l.id !== id; });
      }
      chosenChanged();
    });
    $main.on("click", ".ap-chip [data-remove]", function () {
      var id = Number($(this).data("remove"));
      state.chosen = state.chosen.filter(function (l) { return l.id !== id; });
      chosenChanged();
    });
    $main.on("click", "[data-page]", function () { state.page = Number($(this).data("page")); search(); });
    $main.on("click", "[data-lc-change]", function () {
      var id = Number($(this).data("lcChange")), row = shownRows.filter(function (r) { return r.id === id; })[0];
      if (!row) { return; }
      Nadlan.linkCustomer.change(row).then(function (saved) {
        if (!saved) { return; }
        state.chosen.forEach(function (l) { if (l.id === id) { l.contactId = saved.contactId; l.contactName = saved.contactName; } });
        search();
        if (state.split === "customer") { chart(); } else { chips(); }
      });
    });
    $main.on("click", "[data-ap=show-all]", function () { state.showAll = true; render(); });

    // A bar: hover = top 5 places, click = all of them in the side panel.
    var hoverTimer = null;
    $main.on("mouseenter", "[data-bar]", function (e) {
      var el = this;
      clearTimeout(hoverTimer);
      hoverTimer = setTimeout(function () { tooltip(el, e); }, 200);
    });
    $main.on("mouseleave", "[data-bar]", function () { clearTimeout(hoverTimer); $main.find(".ap-tip").prop("hidden", true); });
    $main.on("click", "[data-bar]", function () { $main.find(".ap-tip").prop("hidden", true); side(this); });
    $main.on("click", "[data-ap=close-side]", function () { $main.find(".ap-side").prop("hidden", true); $main.find(".ap-layout").removeClass("with-side"); });
    $main.on("click", ".ap-place-head", function () { $(this).closest(".ap-place").toggleClass("open"); });
    $main.on("click", ".ap-visit", function () {
      var $v = $(this), id = Number($v.data("visit"));
      if ($v.next(".ap-visit-detail").length) { $v.next(".ap-visit-detail").remove(); return; }
      var $d = $("<div class=\"ap-visit-detail\"><span class=\"muted\">Loading…</span></div>").insertAfter($v);
      Nadlan.api.get("/api/analytics/visits/" + id).then(function (v) { $d.html(Nadlan.analyticsReport.visitHtml(v)); },
        function (err) { $d.html("<span class=\"error\">" + esc(err.message) + "</span>"); });
    });
  }

  // ---- finding links ---------------------------------------------------------------------------------------------

  function isChosen(id) { return state.chosen.some(function (l) { return l.id === id; }); }

  function kindText(r) { return r.portfolioId ? "Portfolio: " + (r.portfolioName || "#" + r.portfolioId) : r.assetCount + " chosen propert" + (r.assetCount === 1 ? "y" : "ies"); }

  function search() {
    var f = state.filters;
    writeAddress();
    var $results = $main.find(".ap-results").html("<div class=\"muted\">Searching…</div>");
    return Nadlan.api.get("/api/analytics/links/search", {
      q: f.q, kind: f.kind, state: f.state, createdFrom: f.createdFrom, createdTo: f.createdTo, visitedFrom: f.visitedFrom, visitedTo: f.visitedTo,
      createdBy: f.createdBy, contactId: f.contactId, page: state.page
    }).then(function (res) {
      shownRows = res.rows;
      $main.find(".ap-count").text(res.total + " link" + (res.total === 1 ? "" : "s"));
      if (!res.rows.length) { $results.html("<div class=\"muted\">No links match.</div>"); $main.find(".ap-pager").html(""); return; }
      $results.html(res.rows.map(function (r) {
        return "<div class=\"ap-row" + (r.state === "deleted" ? " deleted" : "") + "\" data-id=\"" + r.id + "\"><label class=\"ap-row-main\">" +
          "<input type=\"checkbox\"" + (isChosen(r.id) ? " checked" : "") + "><span><strong>" + esc(r.name) + "</strong>" +
          " <span class=\"badge ap-state ap-state-" + esc(r.state) + "\">" + esc(STATE_NAMES[r.state] || r.state) + "</span><br>" +
          "<span class=\"muted\">" + esc(kindText(r)) + " · made " + esc(dayText(r.createdUtc)) + " by " + esc(r.createdByName) + " · " +
          r.visits + " visit" + (r.visits === 1 ? "" : "s") + "</span></span></label>" +
          "<div class=\"ap-row-customer muted\">" + Nadlan.linkCustomer.lineHtml(r) + "</div></div>";
      }).join(""));
      var pages = Math.ceil(res.total / res.pageSize);
      $main.find(".ap-pager").html(pages <= 1 ? "" : (res.page > 1 ? "<button type=\"button\" class=\"btn btn-small\" data-page=\"" + (res.page - 1) + "\">‹ Newer</button>" : "") +
        " <span class=\"muted\">page " + res.page + " of " + pages + "</span> " +
        (res.page < pages ? "<button type=\"button\" class=\"btn btn-small\" data-page=\"" + (res.page + 1) + "\">Older ›</button>" : ""));
    }, function (err) { $results.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  function chips() {
    var $chips = $main.find(".ap-chips");
    if (!state.chosen.length) { $chips.html("<span class=\"muted\">Tick links on the left (at most " + MAX_LINKS + ").</span>"); return; }
    $chips.html(state.chosen.map(function (l, i) {
      return "<span class=\"ap-chip\" style=\"--c:" + COLORS[i % COLORS.length] + "\" title=\"" + esc(l.contactName ? "Sent to " + l.contactName : "") + "\">" + esc(l.name) +
        (l.contactName ? " <span class=\"muted\">· " + esc(l.contactName) + "</span>" : "") +
        "<button type=\"button\" data-remove=\"" + l.id + "\" aria-label=\"Remove\">×</button></span>";
    }).join("") + (state.chosen.length > 1 ? "<span class=\"muted\">" + state.chosen.length + " links</span>" : ""));
  }

  function chosenChanged() {
    $main.find(".ap-row").each(function () { $(this).find("input[type=checkbox]").prop("checked", isChosen(Number($(this).data("id")))); });
    chips();
    chart();
  }

  // ---- the bars --------------------------------------------------------------------------------------------------

  function request() {
    return { linkIds: state.chosen.map(function (l) { return l.id; }), from: state.from, to: state.to, view: state.view, split: state.split, metric: state.metric };
  }

  var chartSeq = 0;
  function chart() {
    writeAddress();
    $main.find(".ap-seg[data-seg=view] button").each(function () { $(this).toggleClass("active", $(this).data("v") === state.view); });
    $main.find(".ap-seg[data-seg=split] button").each(function () { $(this).toggleClass("active", $(this).data("v") === state.split); });
    $main.find(".ap-rates").prop("hidden", state.view !== "metrics");
    $main.find(".ap-metric, .ap-hot").prop("hidden", state.view !== "assets");
    whoCache = {};
    var $chart = $main.find(".ap-chart");
    if (!state.chosen.length) { lastChart = null; $chart.html("<div class=\"muted ap-empty\">Choose links on the left to see their visits.</div>"); return; }
    var seq = ++chartSeq;
    $chart.addClass("busy");
    Nadlan.api.post("/api/analytics/chart", request()).then(function (res) {
      if (seq !== chartSeq) { return; }
      $chart.removeClass("busy");
      lastChart = res;
      render();
      status(res.truncated ? "A lot of events: only the newest are counted - choose fewer links or a shorter range." : "");
    }, function (err) {
      if (seq !== chartSeq) { return; }
      $chart.removeClass("busy");
      $chart.html("<div class=\"error\">" + esc(err.message) + "</div>");
    });
  }

  function seriesColor(res, key) {
    if (res.split === "none") { return COLORS[0]; }
    var i = res.series.map(function (s) { return s.key; }).indexOf(key);
    return i < 0 || i >= COLORS.length - 1 && res.series.length > COLORS.length ? "#94a3b8" : COLORS[i]; // past 10: grey "others"
  }

  function value(res, x, series, metric) {
    var v = 0;
    res.rows.forEach(function (r) { if (r.x === x && r.series === series && r.metric === metric) { v += r.value; } });
    return v;
  }

  function fmt(v, unit, rate) {
    if (rate) { return (Math.round(v * 10) / 10).toLocaleString() + (unit === "min" ? " min" : "") + " / visit"; }
    if (unit === "min") { return (Math.round(v * 10) / 10).toLocaleString() + " min"; }
    return Math.round(v).toLocaleString();
  }

  function legend(res) {
    if (res.split === "none") { return ""; }
    return "<div class=\"ap-legend\">" + res.series.map(function (s) {
      return "<span><i style=\"background:" + seriesColor(res, s.key) + "\"></i>" + esc(s.label) + (s.note ? " <span class=\"muted\">(" + esc(s.note) + ")</span>" : "") + "</span>";
    }).join("") + "</div>";
  }

  function barAttrs(x, series, metric) {
    return " data-bar=\"1\" data-x=\"" + esc(x) + "\" data-series=\"" + esc(series) + "\" data-metric=\"" + esc(metric) + "\"";
  }

  // Grouped: a row per metric (or step), one bar per series under it. The counts share one scale (minutes have their own),
  // so bars compare across rows: 7 asset clicks is longer than 1 tour start.
  function grouped(res, items, opts) {
    var series = res.split === "none" ? [{ key: "all" }] : res.series;
    function shown(m, s) {
      var v = value(res, m.key, s.key, m.key), base = opts.base ? opts.base(s.key) : null;
      return { s: s, raw: v, shown: base === null ? v : base ? v / base : 0 };
    }
    function scale(unit) {
      return Math.max.apply(null, items.filter(function (m) { return (m.note === "min") === (unit === "min"); })
        .map(function (m) { return Math.max.apply(null, series.map(function (s) { return shown(m, s).shown; })); }).concat([0.0001]));
    }
    var scales = { min: scale("min"), count: scale("") };
    return items.map(function (m) {
      var vals = series.map(function (s) { return shown(m, s); });
      var max = opts.percent ? 1 : scales[m.note === "min" ? "min" : "count"];
      return "<div class=\"ap-group\"><div class=\"ap-group-label\">" + esc(m.label) + "</div>" + vals.map(function (x) {
        var text = opts.percent ? Math.round(x.shown * 100) + "% <span class=\"muted\">(" + Math.round(x.raw) + ")</span>" : fmt(x.shown, m.note, opts.base && !opts.percent);
        return "<div class=\"ap-bar-line\"" + barAttrs(m.key, x.s.key, m.key) + "><span class=\"ap-track\"><span class=\"ap-fill\" style=\"width:" +
          (x.shown > 0 ? Math.max(1, x.shown / max * 100) : 0) + "%;background:" + seriesColor(res, x.s.key) + "\"></span></span><span class=\"ap-val\">" + text + "</span></div>";
      }).join("") + "</div>";
    }).join("");
  }

  // Stacked: a row per property / day / hour, one bar made of the series' parts.
  function stacked(res, xs, metric, opts) {
    var series = res.split === "none" ? [{ key: "all" }] : res.series;
    var totals = xs.map(function (x) { return series.reduce(function (n, s) { return n + value(res, x.key, s.key, metric.key); }, 0); });
    var max = Math.max.apply(null, totals.concat([0.0001]));
    return xs.map(function (x, i) {
      var label = opts.label ? opts.label(x) : esc(x.label);
      return "<div class=\"ap-stack-row\"><div class=\"ap-stack-label\">" + label + "</div><span class=\"ap-track\">" + series.map(function (s) {
        var v = value(res, x.key, s.key, metric.key);
        return v > 0 ? "<span class=\"ap-fill\"" + barAttrs(x.key, s.key, metric.key) + " style=\"width:" + (v / max * 100) + "%;background:" + seriesColor(res, s.key) +
          "\" title=\"" + esc((s.label || "") + (s.label ? ": " : "") + fmt(v, metric.note)) + "\"></span>" : "";
      }).join("") + "</span><span class=\"ap-val\">" + fmt(totals[i], metric.note) + "</span></div>";
    }).join("");
  }

  function render() {
    var res = lastChart, $chart = $main.find(".ap-chart");
    if (!res) { return; }
    var html = legend(res);
    var none = !res.rows.length;
    if (res.view === "metrics") {
      var visits = function (s) { return state.rates ? value(res, "visits", s, "visits") : null; };
      html += grouped(res, res.metrics.filter(function (m) { return !state.rates || (m.key !== "visits" && m.key !== "devices"); }), { base: state.rates ? visits : null });
    } else if (res.view === "path") {
      html += "<p class=\"muted\">Each step as a share of the visits.</p>" +
        grouped(res, res.metrics.filter(function (m) { return m.key !== "visits"; }), { percent: true, base: function (s) { return value(res, "visits", s, "visits"); } });
    } else if (res.view === "assets") {
      var metric = res.metrics[0];
      var devices = function (x) { return res.rows.filter(function (r) { return r.x === x && r.series === "all" && r.metric === "asset_devices"; }).reduce(function (n, r) { return n + r.value; }, 0); };
      var xs = state.showAll ? res.xs : res.xs.slice(0, 30);
      html += "<p class=\"muted\">Sorted by " + esc(metric.label.toLowerCase()) + ". 🔥 = opened on " + state.hot + " or more devices.</p>" +
        stacked(res, xs, metric, { label: function (x) {
          return (devices(x.key) >= state.hot ? "<span title=\"Opened on " + devices(x.key) + " devices\">🔥</span> " : "") + esc(x.label) +
            (x.parcelId ? " <a class=\"ap-map-link\" href=\"/?parcel=" + x.parcelId + "\" target=\"_blank\" rel=\"noopener\" title=\"Open it on the map (new tab)\">map ↗</a>" : "");
        } }) +
        (!state.showAll && res.xs.length > 30 ? "<button type=\"button\" class=\"btn btn-small\" data-ap=\"show-all\">Show all " + res.xs.length + "</button>" : "");
      none = !res.xs.length;
    } else if (res.view === "time") {
      html += res.metrics.map(function (m) {
        return "<h4>" + esc(m.label) + " <span class=\"muted\">per " + esc(res.bucket) + "</span></h4>" + stacked(res, res.xs, m, {});
      }).join("");
    } else {
      html += "<p class=\"muted\">Visits by the hour they were on the page (Athens time).</p>" + stacked(res, res.xs, res.metrics[0], {});
    }

    $chart.html(none ? legend(res) + "<div class=\"muted ap-empty\">No visits on these links between " + esc(res.fromDay) + " and " + esc(res.toDay) + ".</div>" : html);
  }

  // ---- who: places of one bar ------------------------------------------------------------------------------------

  function whoOf(el) {
    var $b = $(el), x = String($b.data("x")), series = String($b.data("series")), metric = String($b.data("metric"));
    var body = $.extend(request(), { metric: metric, series: state.split === "none" ? null : series, x: /^(assets|time|hours)$/.test(state.view) ? x : null });
    var key = JSON.stringify(body);
    if (!whoCache[key]) {
      whoCache[key] = Nadlan.api.post("/api/analytics/who", body);
      whoCache[key].catch(function () { delete whoCache[key]; });
    }
    return whoCache[key];
  }

  function barTitle(el) {
    var res = lastChart, $b = $(el), x = String($b.data("x")), series = String($b.data("series"));
    var xItem = res.xs.filter(function (i) { return i.key === x; })[0] || res.metrics.filter(function (i) { return i.key === x; })[0];
    var metric = res.metrics.filter(function (m) { return m.key === String($b.data("metric")); })[0];
    var s = res.split === "none" ? null : res.series.filter(function (i) { return i.key === series; })[0];
    return [metric && xItem && metric.key !== xItem.key ? metric.label + " · " + xItem.label : (xItem || metric || {}).label, s ? s.label : null].filter(Boolean).join(" — ");
  }

  function placeLine(p) {
    return esc(p.label) + " — " + p.visits + " visit" + (p.visits === 1 ? "" : "s") + ", " + p.devices + " device" + (p.devices === 1 ? "" : "s");
  }

  function tooltip(el, e) {
    var $tip = $main.find(".ap-tip");
    $tip.html("<strong>" + esc(barTitle(el)) + "</strong><div class=\"muted\">Loading places…</div>").prop("hidden", false);
    var r = el.getBoundingClientRect();
    $tip.css({ left: Math.min(window.innerWidth - 320, Math.max(8, (e && e.clientX ? e.clientX : r.left) + 12)), top: r.bottom + 6 });
    whoOf(el).then(function (who) {
      if ($tip.prop("hidden")) { return; }
      $tip.html("<strong>" + esc(barTitle(el)) + "</strong>" + (who.places.length
        ? "<ul>" + who.places.slice(0, 5).map(function (p) { return "<li>" + placeLine(p) + "</li>"; }).join("") + "</ul>" +
          (who.places.length > 5 ? "<div class=\"muted\">+ " + (who.places.length - 5) + " more places - click the bar</div>" : "<div class=\"muted\">Click for the visits</div>")
        : "<div class=\"muted\">No visits.</div>"));
    }, function (err) { $tip.html("<span class=\"error\">" + esc(err.message) + "</span>"); });
  }

  function side(el) {
    var $side = $main.find(".ap-side").prop("hidden", false);
    $main.find(".ap-layout").addClass("with-side");
    $side.find(".ap-side-title").text(barTitle(el));
    var $body = $side.find(".ap-side-body").html("<div class=\"muted\">Loading…</div>");
    whoOf(el).then(function (who) {
      if (!who.places.length) { $body.html("<div class=\"muted\">No visits.</div>"); return; }
      $body.html("<div class=\"muted\">" + who.places.length + " place" + (who.places.length === 1 ? "" : "s") + ". Click a place for its visits, a visit for its timeline.</div>" +
        who.places.map(function (p) {
          return "<div class=\"ap-place\"><button type=\"button\" class=\"ap-place-head\"><strong>" + placeLine(p) + "</strong><br><span class=\"muted\">" +
            [p.org, "last " + dayText(p.lastUtc)].filter(Boolean).map(esc).join(" · ") + "</span></button><div class=\"ap-place-visits\">" +
            p.visitList.map(function (v) {
              return "<button type=\"button\" class=\"ap-visit\" data-visit=\"" + v.contextId + "\">" + esc(when(v.firstUtc)) + " · " +
                esc(Nadlan.analyticsReport.duration(v.seconds)) + " on screen" + (v.device ? " · " + esc(v.device) : "") + "<br><span class=\"muted\">on " + esc(v.linkName) +
                (v.customer ? " - a link sent to " + esc(v.customer) : "") + "</span></button>";
            }).join("") + "</div></div>";
        }).join(""));
    }, function (err) { $body.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  // ---- start ---------------------------------------------------------------------------------------------------

  $(function () {
    $main = $("#ap-main");
    $status = $("#ap-status");
    Nadlan.session.ready.then(function () {
      if (!Nadlan.session.can("VIEW_ANALYTICS_PAGE")) {
        $main.html("<div class=\"muted\">The analytics page is for Admins. " + (Nadlan.session.can("USE_MAP") ? "<a href=\"/\">Back to the map</a>" : "") + "</div>");
        return;
      }

      var ids = readAddress();
      var restore = state.filters.contactId
        ? Nadlan.api.get("/api/analytics/links/search", { contactId: state.filters.contactId }).then(function (r) {
          state.filters.contactName = r.rows.length ? r.rows[0].contactName : "#" + state.filters.contactId;
        }, function () { })
        : Promise.resolve();
      restore.then(function () {
        layout();
        search();
        if (!ids.length) { chips(); chart(); return; }
        // A bookmarked view: its links back as chips, in the address' order.
        Nadlan.api.get("/api/analytics/links/search", { ids: ids }).then(function (r) {
          state.chosen = ids.map(function (id) { return r.rows.filter(function (x) { return x.id === id; })[0]; }).filter(Boolean);
          chosenChanged(); // the chips, the ticks in the finder, the bars
        }, function (err) { status(err.message, true); });
      });
    }, function (err) { $main.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  });
})(window, jQuery);
