// Admin page, Costs tab: the video worker's runs (migration 028) - ffmpeg on AWS Fargate, started only while a video is converted.
// When each ran, how big, how long, what it cost, how it ended; this month and in total (owner, 2026-11-15: "so Alon can know").
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;

  function money(v) { return "$" + Number(v || 0).toFixed(v >= 1 ? 2 : 4); }

  function length(s) {
    if (s === null || s === undefined) { return "running…"; }
    var m = Math.floor(s / 60);
    return m ? m + " min " + Math.round(s % 60) + " s" : Math.round(s) + " s";
  }

  function when(utc) { return utc ? new Date(utc).toLocaleString() : ""; }

  Nadlan.initializeVideoRuns = function ($panel) {
    var $d = $("<details class=\"costs-section video-runs\" open></details>")
      .append($("<summary></summary>").text("Video conversions (AWS Fargate worker)"))
      .appendTo($panel);
    var $body = $("<div><span class=\"muted\">Loading…</span></div>").appendTo($d);
    Nadlan.api.get("/api/video/runs").then(function (r) {
      var esc = Nadlan.format.escapeHtml;
      var head = "<p class=\"muted small\">Each video is converted by a worker that starts for it and stops when done (" + (r.cpu / 1024) + " vCPU, " +
        (r.memoryMb / 1024) + " GB, ARM). Costs are at the list prices in Admin › Settings › Videos; AWS bills to the second, at least a minute. " +
        "A checker stops any worker left running." + (r.active ? "" : " <strong>Video optimization is off.</strong>") + "</p>" +
        "<p><strong>This month: " + esc(money(r.thisMonthUsd)) + "</strong> · in total " + esc(money(r.totalUsd)) + " · " + r.runs.length + " run" +
        (r.runs.length === 1 ? "" : "s") + (r.running ? " · <span class=\"file-optimizing\">" + r.running + " running now</span>" : "") + "</p>";
      if (!r.runs.length) { $body.html(head + "<p class=\"muted\">No runs yet.</p>"); return; }
      $body.html(head + "<div class=\"table-wrap\"><table class=\"data-table\"><thead><tr><th>Started</th><th>Video</th><th>Ran</th><th>Size</th><th>Ended</th><th>Cost</th></tr></thead><tbody>" +
        r.runs.map(function (x) {
          return "<tr><td>" + esc(when(x.startedUtc || x.submittedUtc)) + "</td><td>" + esc(x.fileName || (x.fileAttachmentId ? "#" + x.fileAttachmentId : "(deleted)")) +
            "</td><td>" + esc(length(x.seconds)) + "</td><td>" + (x.cpu / 1024) + " vCPU · " + (x.memoryMb / 1024) + " GB</td><td>" +
            esc(x.stoppedBy || "running") + "</td><td>" + (x.costUsd === null || x.costUsd === undefined ? "" : esc(money(x.costUsd))) + "</td></tr>";
        }).join("") + "</tbody></table></div>");
    }, function (err) { $body.html("<span class=\"error\">" + Nadlan.format.escapeHtml(err.message) + "</span>"); });
  };
})(window, jQuery);
