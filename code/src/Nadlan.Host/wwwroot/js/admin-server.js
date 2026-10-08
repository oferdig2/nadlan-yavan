// Admin page, Server tab: machine and service workload (last hour), MySQL, and Restart buttons.
// Data: GET /api/admin/machine/status (sampled every 10 s on the server), /mysql/tables on demand.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var M = function () { return Nadlan.machine; };
  var REFRESH_MS = 10000;

  Nadlan.initializeAdminServer = function ($panel) {
    var timer = null, busy = false, last = null;
    var $toolbar = $("<div class=\"meta-toolbar\"><h3>Server</h3><span class=\"muted server-ident\"></span>" +
      "<label class=\"check\"><input type=\"checkbox\" data-act=\"auto\" checked> Refresh every 10 s</label>" +
      "<button type=\"button\" class=\"btn\" data-act=\"refresh\">Refresh</button></div>");
    var $progress = $("<div class=\"done-note\" aria-live=\"polite\"></div>");
    var $body = $("<div class=\"server-body\"><div class=\"muted\">Loading…</div></div>");
    $panel.append($toolbar, $progress, $body);

    function load() {
      if (busy) { return; }
      busy = true;
      Nadlan.api.get("/api/admin/machine/status").then(function (s) { last = s; render(s); }, function (err) {
        $body.find(".load-error").remove();
        $body.prepend($("<div class=\"form-error load-error\"></div>").text("Status not loaded: " + err.message));
      }).then(function () { busy = false; });
    }

    function schedule() {
      clearInterval(timer);
      // Stops by itself once the tab is closed (the panel is removed from the page).
      timer = setInterval(function () {
        if (!$.contains(document, $panel[0])) { clearInterval(timer); return; }
        if ($toolbar.find("[data-act=auto]").prop("checked") && !document.hidden) { load(); }
      }, REFRESH_MS);
    }

    function report(text) { $progress.text(text); }

    $toolbar.on("click", "[data-act=refresh]", load);
    $body.on("click", "[data-restart]", function () {
      var key = $(this).data("restart");
      var title = $(this).data("title");
      M().restartService(key, title, report).then(function (done) { if (done) { load(); } }, function (err) { report(title + ": " + err.message); });
    });
    $body.on("click", "[data-act=tables]", function () { loadTables($(this).closest(".tables-box")); });

    load();
    schedule();

    function render(s) {
      var L = s.latest, h = s.history, m = L.machine;
      $toolbar.find(".server-ident").text(s.host + " · " + s.os + " · " + s.cores + " CPU · release " + s.release +
        " · sampled " + M().time(L.capturedUtcMs));
      $body.empty();

      if (!s.control.available) {
        $body.append($("<div class=\"warn server-note\"></div>").text("Restart buttons: " + s.control.reason));
      }

      // Machine
      var memUsed = m.memTotalMb !== null && m.memAvailableMb !== null ? m.memTotalMb - m.memAvailableMb : null;
      var swapUsed = m.swapTotalMb !== null && m.swapFreeMb !== null ? m.swapTotalMb - m.swapFreeMb : null;
      var pct = function (v) { return v === null || v === undefined ? "—" : M().num(v, 1) + " %"; };
      var $machine = $("<div class=\"tile-grid\"></div>").append(
        M().tile("CPU", pct(m.cpuPercent),
          m.stealPercent === null ? null : "steal " + M().num(m.stealPercent, 1) + " % · I/O wait " + M().num(m.ioWaitPercent, 1) + " %",
          [M().sparkline(h.t, h.cpu, { max: 100, format: pct, label: "CPU" })]),
        M().tile("Memory used", M().mb(memUsed), "of " + M().mb(m.memTotalMb) + " · " + M().mb(m.memAvailableMb) + " available",
          [M().meter(memUsed, m.memTotalMb, "Memory used"), M().sparkline(h.t, h.memUsedMb, { max: m.memTotalMb, format: M().mb, label: "Memory used" })]),
        m.swapTotalMb === null ? $() : M().tile("Swap used", M().mb(swapUsed), "of " + M().mb(m.swapTotalMb),
          [M().meter(swapUsed, m.swapTotalMb, "Swap used"), M().sparkline(h.t, h.swapUsedMb, { max: m.swapTotalMb, format: M().mb, label: "Swap used" })]),
        M().tile("Disk free", m.diskFreeGb === null ? "—" : M().num(m.diskFreeGb, 1) + " GB", "of " + M().num(m.diskTotalGb, 1) + " GB",
          [M().meter(m.diskTotalGb - m.diskFreeGb, m.diskTotalGb, "Disk used")]),
        M().tile("Load (1 / 5 / 15 min)", m.loadAverage ? m.loadAverage.map(function (v) { return M().num(v, 2); }).join(" / ") : "—",
          "machine up " + M().duration(m.uptimeSeconds))
      );
      $body.append("<h4 class=\"server-h\">Machine</h4>", $machine);
      if (m.stealPercent !== null && m.stealPercent >= 10) {
        $machine.after($("<div class=\"warn server-note\"></div>").text(
          "High CPU steal: AWS is throttling this instance (t3 CPU credits used up). Pages get slow until credits build up again."));
      }

      // Services
      var $services = $("<div class=\"service-grid\"></div>");
      L.services.forEach(function (svc) { $services.append(serviceCard(s, svc)); });
      $body.append("<h4 class=\"server-h\">Services</h4>", $services);

      // Recent restarts
      if (s.control.lastResults.length || s.control.pending.length) {
        var $list = $("<ul class=\"restart-log\"></ul>");
        s.control.pending.forEach(function (p) { $list.append($("<li></li>").text("Waiting: " + p)); });
        s.control.lastResults.forEach(function (r) {
          $list.append($("<li></li>").append(
            $("<span class=\"status-pill\"></span>").addClass(r.ok ? "status-good" : "status-critical").text(r.ok ? "✓ OK" : "✕ Failed"),
            document.createTextNode(" " + r.action + " · " + (r.finishedUtc ? M().time(Date.parse(r.finishedUtc)) : "") + " · " + (r.message || ""))));
        });
        $body.append("<h4 class=\"server-h\">Last restarts</h4>", $list);
      }

      $body.append("<h4 class=\"server-h\">Database tables</h4>",
        "<div class=\"tables-box\"><button type=\"button\" class=\"btn\" data-act=\"tables\">Show table sizes</button></div>");
    }

    function serviceCard(s, svc) {
      var h = s.history, series = h.services[svc.key] || { cpu: [], memMb: [] };
      var limit = svc.memoryMaxMb || svc.memoryHighMb;
      var mem = svc.cgroupMemoryMb !== null ? svc.cgroupMemoryMb : svc.memoryMb;
      var $card = $("<section class=\"service-card\"></section>");
      var $head = $("<header></header>").append(
        $("<h5></h5>").text(svc.title),
        $("<span class=\"status-pill\"></span>").addClass(svc.running ? "status-good" : "status-critical").text(svc.running ? "● Running" : "✕ Not running"));
      $card.append($head);
      $card.append($("<div class=\"muted small\"></div>").text(svc.running
        ? "since " + M().time(svc.startedUtcMs) + (svc.processCount > 1 ? " · " + svc.processCount + " processes" : "")
        : (svc.key === "mysql" ? "No MySQL on this machine (or not visible to the app)." : "Not found on this machine.")));

      var pct = function (v) { return v === null || v === undefined ? "—" : M().num(v, 1) + " %"; };
      var limits = [svc.memoryHighMb ? "soft " + M().mb(svc.memoryHighMb) : null, svc.memoryMaxMb ? "hard " + M().mb(svc.memoryMaxMb) : null]
        .filter(Boolean).join(", ");
      var memSub = (svc.cgroupMemoryMb !== null ? "incl. file cache" : "resident") + (limits ? " · limit " + limits : "");
      var $tiles = $("<div class=\"tile-grid tile-grid-2\"></div>").append(
        M().tile("CPU", pct(svc.cpuPercent), "share of the whole machine", [M().sparkline(h.t, series.cpu, { max: 100, format: pct, label: svc.title + " CPU" })]),
        M().tile("Memory", M().mb(mem), memSub, [M().meter(mem, limit, svc.title + " memory against its limit"),
          M().sparkline(h.t, series.memMb, { max: limit || 0, format: M().mb, label: svc.title + " memory" })]));
      $card.append($tiles);

      var facts = [];
      if (svc.key === "app") {
        var a = s.latest.app;
        facts.push([".NET heap", M().mb(a.gcHeapMb) + (a.gcHeapLimitMb ? " of " + M().mb(a.gcHeapLimitMb) : "")]);
        facts.push(["Requests / min", M().num(a.requestsPerMinute, 0)]);
        facts.push(["Server errors / min", M().num(a.serverErrorsPerMinute, 1) + " (" + M().num(a.serverErrorsSinceStart) + " since start)"]);
        facts.push(["Threads", M().num(a.threads)]);
        facts.push(["Release", s.release + " (" + s.environment + ")"]);
      } else if (svc.key === "mysql") {
        var q = s.latest.mySql;
        if (!q.reachable) {
          facts.push(["Database", "not reachable: " + (q.error || "")]);
        } else {
          facts.push(["Version", q.version]);
          facts.push(["Connections", M().num(q.threadsConnected) + " of " + M().num(q.maxConnections) + " (most ever " + M().num(q.maxUsedConnections) + ")"]);
          facts.push(["Queries / s", M().num(q.queriesPerSecond, 1) + " · " + M().num(q.threadsRunning) + " running now"]);
          facts.push(["Buffer pool", M().mb(q.bufferPoolMb) + " · " + M().num(q.bufferPoolUsedPercent, 0) + " % used · " + M().num(q.bufferPoolHitPercent, 2) + " % from memory"]);
          facts.push(["Slow queries", M().num(q.slowQueries) + " since start"]);
          facts.push(["Up", M().duration(q.uptimeSeconds) + " · answers in " + M().num(q.pingMs, 1) + " ms"]);
        }
      }
      if (facts.length) {
        var $dl = $("<dl class=\"facts\"></dl>");
        facts.forEach(function (f) { $dl.append($("<dt></dt>").text(f[0]), $("<dd></dd>").text(f[1])); });
        $card.append($dl);
      }
      if (svc.key === "mysql" && s.latest.mySql.reachable) {
        $card.append($("<div class=\"spark-row\"></div>").append($("<span class=\"muted small\"></span>").text("Queries / s"),
          M().sparkline(h.t, h.qps, { format: function (v) { return M().num(v, 1) + " /s"; }, label: "MySQL queries per second" })));
      }

      $card.append($("<div class=\"btn-row\"></div>").append(
        $("<button type=\"button\" class=\"btn\"></button>").text("Restart " + svc.title)
          .attr({ "data-restart": svc.key, "data-title": svc.title })
          .prop("disabled", !s.control.available)
          .attr("title", s.control.available ? "" : s.control.reason)));
      return $card;
    }

    function loadTables($box) {
      $box.html("<div class=\"muted\">Loading…</div>");
      Nadlan.api.get("/api/admin/machine/mysql/tables").then(function (rows) {
        var total = rows.reduce(function (sum, r) { return sum + r.dataMb + r.indexMb; }, 0);
        var $t = $("<table class=\"meta-table\"><thead><tr><th>Table</th><th class=\"num\">Rows (approx.)</th><th class=\"num\">Data</th><th class=\"num\">Indexes</th></tr></thead><tbody></tbody></table>");
        rows.forEach(function (r) {
          $t.find("tbody").append($("<tr></tr>").append(
            $("<td></td>").text(r.name), $("<td class=\"num\"></td>").text(M().num(r.rows)),
            $("<td class=\"num\"></td>").text(M().mb(r.dataMb)), $("<td class=\"num\"></td>").text(M().mb(r.indexMb))));
        });
        $box.empty().append($("<div class=\"muted small\"></div>").text("Total " + M().mb(total) + " in " + rows.length +
          " tables. MySQL refreshes these figures now and then, so they are approximate."), $t);
      }, function (err) { $box.empty().append($("<div class=\"form-error\"></div>").text(err.message)); });
    }

    return { reload: function () { if (last) { render(last); } } };
  };
})(window, jQuery);
