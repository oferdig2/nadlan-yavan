// Shared by the server tabs of the admin page (Server, Web files, Settings): number formats, sparklines, meters, restarts.
// Only for the Admins in MachineAdminAccess (the server answers 404 to everyone else).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  function num(value, digits) {
    return value === null || value === undefined || isNaN(value) ? "—"
      : Number(value).toLocaleString("en-US", { maximumFractionDigits: digits === undefined ? 0 : digits, minimumFractionDigits: 0 });
  }

  function mb(value) {
    if (value === null || value === undefined) { return "—"; }
    return value >= 1024 ? num(value / 1024, 2) + " GB" : num(value, value < 10 ? 1 : 0) + " MB";
  }

  function bytes(value) {
    if (value === null || value === undefined) { return "—"; }
    if (value < 1024) { return num(value) + " B"; }
    if (value < 1024 * 1024) { return num(value / 1024, 1) + " KB"; }
    return num(value / 1024 / 1024, 1) + " MB";
  }

  function duration(seconds) {
    if (seconds === null || seconds === undefined) { return "—"; }
    var d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
    if (d > 0) { return d + " d " + h + " h"; }
    if (h > 0) { return h + " h " + m + " min"; }
    return m > 0 ? m + " min" : Math.max(0, Math.round(seconds)) + " s";
  }

  function time(ms) {
    return ms ? new Date(ms).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit" }) : "—";
  }

  function clock(ms) {
    return new Date(ms).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
  }

  /**
   * One-series trend line over the last hour, with a hover crosshair and the exact value. Gaps (null) break the line.
   * @param {number[]} times  epoch ms
   * @param {Array<number|null>} values
   * @param {{ max?: number, format: function(number): string, label: string }} options  max: fixed top (e.g. 100 for %)
   */
  function sparkline(times, values, options) {
    var w = 240, h = 44, pad = 3;
    var present = values.filter(function (v) { return v !== null && v !== undefined; });
    var $box = $("<div class=\"spark\" tabindex=\"0\"></div>").attr("aria-label", options.label + ", last hour");
    if (present.length < 2) {
      return $box.addClass("spark-empty").text("Trend appears after a few samples");
    }

    var top = Math.max(options.max || 0, Math.max.apply(null, present) * 1.1) || 1;
    var t0 = times[0], t1 = times[times.length - 1] || t0 + 1;
    function x(i) { return pad + (times[i] - t0) / Math.max(1, t1 - t0) * (w - 2 * pad); }
    function y(v) { return h - pad - v / top * (h - 2 * pad); }

    var d = "", pen = false;
    values.forEach(function (v, i) {
      if (v === null || v === undefined) { pen = false; return; }
      d += (pen ? "L" : "M") + x(i).toFixed(1) + " " + y(v).toFixed(1);
      pen = true;
    });

    var svg = "<svg viewBox=\"0 0 " + w + " " + h + "\" preserveAspectRatio=\"none\" aria-hidden=\"true\">" +
      "<line class=\"spark-base\" x1=\"0\" x2=\"" + w + "\" y1=\"" + (h - pad) + "\" y2=\"" + (h - pad) + "\"/>" +
      "<path class=\"spark-line\" d=\"" + d + "\"/>" +
      "<line class=\"spark-cross\" y1=\"0\" y2=\"" + h + "\" hidden/>" +
      "</svg><div class=\"spark-dot\" hidden></div><div class=\"spark-tip\" hidden></div>";
    $box.html(svg);

    var $cross = $box.find(".spark-cross"), $tip = $box.find(".spark-tip"), $dot = $box.find(".spark-dot");
    function showAt(i) {
      var v = values[i];
      var px = x(i) / w * 100;
      $cross.attr({ x1: x(i), x2: x(i) }).prop("hidden", false);
      $tip.text(clock(times[i]) + "  " + (v === null || v === undefined ? "no data" : options.format(v))).prop("hidden", false)
        .css({ left: Math.min(Math.max(px, 15), 85) + "%" });
      $dot.prop("hidden", v === null || v === undefined).css({ left: px + "%", top: (v === null || v === undefined ? 0 : y(v) / h * 100) + "%" });
    }
    function hide() { $cross.prop("hidden", true); $tip.prop("hidden", true); $dot.prop("hidden", true); }
    function nearest(clientX) {
      var r = $box[0].getBoundingClientRect();
      var tAt = t0 + (clientX - r.left) / r.width * (t1 - t0), best = 0;
      times.forEach(function (t, i) { if (Math.abs(t - tAt) < Math.abs(times[best] - tAt)) { best = i; } });
      return best;
    }
    var focusIndex = values.length - 1;
    $box.on("mousemove", function (e) { showAt(nearest(e.clientX)); })
      .on("mouseleave blur", hide)
      .on("focus", function () { focusIndex = values.length - 1; showAt(focusIndex); })
      .on("keydown", function (e) {
        if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") { return; }
        e.preventDefault();
        focusIndex = Math.min(values.length - 1, Math.max(0, focusIndex + (e.key === "ArrowLeft" ? -1 : 1)));
        showAt(focusIndex);
      });
    return $box;
  }

  /** Used against a limit; the fill turns warning/critical as it nears the limit (with the numbers beside it, never colour alone). */
  function meter(used, limit, label) {
    if (used === null || used === undefined || !limit) { return $(); }
    var pct = Math.min(100, used / limit * 100);
    var level = pct >= 90 ? "critical" : pct >= 75 ? "warning" : "ok";
    return $("<div class=\"meter\" role=\"meter\" aria-valuemin=\"0\" aria-valuemax=\"100\"></div>")
      .attr({ "aria-valuenow": Math.round(pct), "aria-label": label })
      .addClass("meter-" + level)
      .append($("<span></span>").css("width", pct + "%"));
  }

  /** Stat tile: label, value, sub line, optional meter and sparkline. */
  function tile(label, value, sub, extras) {
    var $t = $("<div class=\"stat-tile\"></div>")
      .append($("<div class=\"stat-label\"></div>").text(label))
      .append($("<div class=\"stat-value\"></div>").text(value));
    if (sub) { $t.append($("<div class=\"stat-sub\"></div>").text(sub)); }
    (extras || []).forEach(function ($e) { $t.append($e); });
    return $t;
  }

  var RESTART_WARNINGS = {
    app: "GreekPlot stops for about 10–20 seconds and starts again. Pages stay open; requests made meanwhile fail. Unsaved dialogs elsewhere are not lost, but their Save fails until it's back.",
    mysql: "The database stops for about 10–30 seconds. The app answers \"database unavailable\" meanwhile and reconnects by itself.",
    nginx: "The website is unreachable for a few seconds, this page included. It comes back on its own."
  };

  /**
   * Asks the server to restart a service, then follows it. Resolves when it is back (or after the waiting time).
   * @param {string} key app | mysql | nginx
   * @param {string} title
   * @param {function(string): void} report  progress text for the page
   */
  function restartService(key, title, report) {
    return Nadlan.dialog.confirm("Restart " + title + "?", "<p>" + esc(RESTART_WARNINGS[key] || "") + "</p>", "Restart").then(function (ok) {
      if (!ok) { return false; }
      var before = null;
      return Nadlan.api.get("/api/admin/machine/status").then(function (s) { before = s; }, function () { /* fine */ })
        .then(function () { return Nadlan.api.post("/api/admin/machine/services/" + encodeURIComponent(key) + "/restart"); })
        .then(function () {
          report(title + ": restart requested…");
          return waitForRestart(key, before, report, title);
        });
    });
  }

  function waitForRestart(key, before, report, title) {
    var action = { app: "restart-nadlan", mysql: "restart-mysqld", nginx: "restart-nginx" }[key];
    var asked = Date.now(), deadline = asked + 120000;
    function previousFinish() {
      var r = before && before.control.lastResults.filter(function (x) { return x.action === action; })[0];
      return r ? r.finishedUtc : null;
    }
    return new Promise(function (resolve) {
      (function poll() {
        if (Date.now() > deadline) { report(title + ": no answer after 2 minutes. Refresh this page and check the server."); resolve(true); return; }
        Nadlan.api.get("/api/admin/machine/status").then(function (s) {
          var result = s.control.lastResults.filter(function (x) { return x.action === action; })[0];
          var appBack = key === "app" && before && s.appStartedUtcMs !== before.appStartedUtcMs;
          if (result && result.finishedUtc !== previousFinish()) {
            report(title + ": " + (result.ok ? "restarted" : "restart failed") + " — " + result.message);
            resolve(true);
          } else if (appBack) {
            report(title + ": restarted.");
            resolve(true);
          } else {
            report(title + ": restarting… (" + Math.round((Date.now() - asked) / 1000) + " s)");
            setTimeout(poll, 3000);
          }
        }, function () {
          report(title + ": restarting… (" + Math.round((Date.now() - asked) / 1000) + " s, server not answering yet)");
          setTimeout(poll, 3000);
        });
      })();
    });
  }

  /** multipart upload with the CSRF header; resolves with the JSON body or rejects like Nadlan.api. */
  function upload(url, formData) {
    return new Promise(function (resolve, reject) {
      $.ajax({ method: "POST", url: url, data: formData, processData: false, contentType: false, dataType: "json",
        headers: { "X-Nadlan-Request": "1" } })
        .done(resolve)
        .fail(function (xhr) {
          var p = xhr.responseJSON || {};
          reject({ status: xhr.status, code: p.error || "HTTP_" + xhr.status, message: p.message || xhr.statusText || "Upload failed" });
        });
    });
  }

  Nadlan.machine = {
    num: num, mb: mb, bytes: bytes, duration: duration, time: time,
    sparkline: sparkline, meter: meter, tile: tile,
    restartService: restartService, upload: upload
  };
})(window, jQuery);
