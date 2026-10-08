// Admin page, Downloads tab: the KAEK importer (polygon acquisition app) for Windows and Mac, from the app's own S3 folder
// (put there by release/2-upload-importer-s3.ps1). Each download link asks the server for a fresh 10-minute S3 link.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var M = function () { return Nadlan.machine; };
  var API = "/api/admin/machine/downloads";

  function link(version, file) {
    return API + "/" + encodeURIComponent(version) + "/" + encodeURIComponent(file);
  }

  Nadlan.initializeAdminDownloads = function ($panel) {
    var isMac = /Mac/i.test(window.navigator.platform || window.navigator.userAgent);
    $panel.append($("<div class=\"meta-toolbar\"><h3>Downloads</h3><span class=\"muted\">KAEK importer: finds parcels on the Ktimatologio map and saves them into GreekPlot.</span></div>"));
    var $body = $("<div class=\"downloads-body\"><div class=\"muted\">Loading…</div></div>").appendTo($panel);

    Nadlan.api.get(API).then(render, function (err) {
      $body.empty().append($("<div class=\"form-error\"></div>").text(err.message));
    });

    function render(d) {
      $body.empty();
      if (!d.storageConfigured) {
        $body.append($("<div class=\"warn\"></div>").text("File storage (S3) is not set up on this server, so there is nowhere to keep the downloads. Set it in the Settings tab."));
        return;
      }
      if (!d.versions.length) {
        $body.append($("<div class=\"download-empty\"></div>").append(
          $("<p></p>").text("No importer has been uploaded yet. On your PC, in code\\release:"),
          $("<ol></ol>").append(
            $("<li></li>").text("1-build-importer.ps1 with this server's address (" + window.location.origin + ")."),
            $("<li></li>").text("2-upload-importer-s3.ps1 with this server's bucket; folder: <root folder>/" + d.prefix.replace(/\/$/, "") + ".")),
          $("<p class=\"muted\"></p>").text("The versions then show here.")));
        return;
      }

      var latest = d.versions[0];
      $body.append($("<p class=\"muted small\"></p>").text("Version " + latest.version + " · uploaded " + M().time(latest.uploadedUtcMs) +
        ". Each package already points at the server it was built for; the first time it runs it asks to Connect to GreekPlot " +
        "(sign in with email and password, then Connect). Users need the right to create parcels."));

      var $cards = $("<div class=\"download-grid\"></div>").appendTo($body);
      latest.packages.forEach(function (p) {
        var mine = isMac ? p.platform.indexOf("mac") === 0 : p.platform === "windows";
        var $card = $("<section class=\"download-card\"></section>").toggleClass("this-computer", mine).append(
          $("<header></header>").append($("<h5></h5>").text(p.title), mine ? $("<span class=\"status-pill status-good\">✓ This computer</span>") : $()),
          $("<p class=\"muted small\"></p>").text(p.hint),
          $("<a class=\"btn btn-primary download-btn\"></a>").attr("href", link(latest.version, p.file)).text("Download " + M().bytes(p.size)),
          $("<div class=\"muted small download-file\"></div>").text(p.file));
        if (p.sha256) {
          $card.append($("<div class=\"small download-sum\"></div>").append(
            $("<span class=\"muted\">SHA-256 </span>"), $("<code></code>").text(p.sha256)));
        }
        $cards.append($card);
      });
      if (latest.readme) {
        $body.append($("<p></p>").append($("<a class=\"btn\"></a>").attr("href", link(latest.version, latest.readme)).text("Admin README (what to send to whom)")));
      }

      if (d.versions.length > 1) {
        var $older = $("<details class=\"download-older\"><summary>Older versions</summary></details>").appendTo($body);
        var $t = $("<table class=\"meta-table\"><thead><tr><th>Version</th><th>Uploaded</th><th>Packages</th></tr></thead><tbody></tbody></table>").appendTo($older);
        d.versions.slice(1).forEach(function (v) {
          var $files = $("<td></td>");
          v.packages.forEach(function (p) {
            $files.append($("<a class=\"btn btn-small\"></a>").attr("href", link(v.version, p.file)).text(p.title + " · " + M().bytes(p.size)), " ");
          });
          $t.find("tbody").append($("<tr></tr>").append($("<td></td>").text(v.version), $("<td></td>").text(M().time(v.uploadedUtcMs)), $files));
        });
      }
    }
  };
})(window, jQuery);
