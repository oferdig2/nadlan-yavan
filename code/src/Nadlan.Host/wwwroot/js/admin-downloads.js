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

  // How to install and use the importer, per system: the same text as the packages' INSTALL.txt (code/installer).
  var GUIDES = {
    windows: {
      needs: "Windows 10 or 11, 64-bit. No administrator rights; it installs for your Windows user only. It opens the Cadastre map in Microsoft Edge.",
      install: [
        "Right-click the downloaded zip, choose Extract All…, then open the extracted folder.",
        "Double-click Install.cmd. If Windows says \"Windows protected your PC\": click More info, then Run anyway (only once - the program isn't code-signed yet).",
        "Start GreekPlot KAEK Importer from the Start Menu or the desktop shortcut."
      ],
      reports: "Documents\\Nadlan\\KAEK imports",
      settings: "%LOCALAPPDATA%\\Nadlan (importer.json, logs)",
      uninstall: "Settings › Apps › Installed apps › GreekPlot KAEK Importer › Uninstall.",
      check: function (file) { return "certutil -hashfile \"" + file + "\" SHA256"; },
      checkWhere: "Command Prompt, in the Downloads folder"
    },
    mac: {
      needs: "macOS 13 (Ventura) or newer. It uses Google Chrome if installed; otherwise it downloads its own browser the first time (about 150 MB).",
      install: [
        "Double-click the downloaded .tar.gz; it unpacks to GreekPlot KAEK Importer.app.",
        "Drag GreekPlot KAEK Importer.app into the Applications folder.",
        "Open it from Applications. The first time macOS says it can't verify the developer (only once): " +
          "on macOS 15 Sequoia or newer click Done, then System Settings › Privacy & Security › \"GreekPlot KAEK Importer was blocked\" › Open Anyway, and confirm; " +
          "on macOS 13-14 right-click (Control-click) the app › Open › Open."
      ],
      reports: "Documents/Nadlan/KAEK imports",
      settings: "~/Library/Application Support/Nadlan (importer.json, logs)",
      uninstall: "Drag GreekPlot KAEK Importer from Applications to the Bin.",
      check: function (file) { return "shasum -a 256 \"" + file + "\""; },
      checkWhere: "Terminal, in the Downloads folder"
    }
  };

  function guide(p, version) {
    var g = GUIDES[p.platform.indexOf("mac") === 0 ? "mac" : "windows"];
    function steps(tag, list) { var $l = $("<" + tag + "></" + tag + ">"); list.forEach(function (s) { $l.append($("<li></li>").text(s)); }); return $l; }
    var $g = $("<div></div>").append(
      $("<h4></h4>").text("How to install and use - " + p.title + " (version " + version + ")"),
      $("<p class=\"muted small\"></p>").text(g.needs));
    if (p.platform.indexOf("mac") === 0) {
      $g.append($("<p class=\"small\"></p>").text("Which Mac? Apple menu › About This Mac: \"Chip Apple M…\" = Mac with Apple chip; \"Processor Intel…\" = Mac with Intel chip."));
    }
    $g.append(
      $("<h5></h5>").text("1. Install"), steps("ol", g.install),
      $("<h5></h5>").text("2. First start: connect to GreekPlot"),
      steps("ul", [
        "The importer opens the Greek Cadastre map with a GreekPlot panel. Click Connect to GreekPlot: a GreekPlot tab opens.",
        "Sign in there with email and password (Google sign-in is usually refused inside automated browsers), then click Connect. The tab closes by itself and the importer remembers the connection.",
        "The person needs a GreekPlot user that may create parcels."
      ]),
      $("<h5></h5>").text("3. Import parcels"),
      steps("ul", [
        "Zoom in on the map until the yellow parcel lines show.",
        "Choose the geographic area in the GreekPlot panel, then click Acquire polygons - or tick \"Import each parcel I click on the map\" and click parcels yourself.",
        "Parcels already in GreekPlot are drawn on the map, so you see what is missing.",
        "Close the browser window to quit. A report of every run is saved in " + g.reports + "."
      ]),
      $("<h5></h5>").text("Good to know"),
      steps("ul", [
        "Offline (demo) mode: if GreekPlot can't be reached, the importer still runs and shows the parcels it finds in purple, with \"Offline - nothing will be saved\". The report still lists them.",
        "The GreekPlot address comes with the package. Settings and logs: " + g.settings + ".",
        "Uninstall: " + g.uninstall
      ]));
    if (p.sha256) {
      $g.append($("<h5></h5>").text("Check the download (optional)"),
        $("<p class=\"small\"></p>").text("In " + g.checkWhere + ", run the command below; it must print the SHA-256 shown on the card."),
        $("<pre class=\"download-cmd\"></pre>").text(g.check(p.file)));
    }
    return $g;
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
        ". Each package already points at this server. Pick a system to see how to install and use it."));

      // The guide below the cards shows this computer's system; hovering or focusing a card shows that one, clicking keeps it.
      var $cards = $("<div class=\"download-grid\" role=\"radiogroup\" aria-label=\"Choose a system to see its instructions\"></div>").appendTo($body);
      var $guide = $("<section class=\"download-guide\" aria-live=\"polite\"></section>");
      var selected = latest.packages.filter(function (p) { return isMac ? p.platform === "mac-apple-silicon" : p.platform === "windows"; })[0] || latest.packages[0];
      latest.packages.forEach(function (p) {
        var mine = isMac ? p.platform.indexOf("mac") === 0 : p.platform === "windows";
        var $card = $("<section class=\"download-card\" tabindex=\"0\" role=\"radio\"></section>").data("pkg", p).toggleClass("this-computer", mine).append(
          $("<header></header>").append($("<h5></h5>").text(p.title), mine ? $("<span class=\"status-pill status-good\">✓ This computer</span>") : $()),
          $("<p class=\"muted small\"></p>").text(p.hint),
          $("<a class=\"btn btn-primary download-btn\"></a>").attr("href", link(latest.version, p.file)).text("Download " + M().bytes(p.size)),
          $("<div class=\"muted small download-file\"></div>").text(p.file));
        if (p.sha256) {
          $card.append($("<div class=\"small download-sum\"></div>").append(
            $("<span class=\"muted\">SHA-256 </span>"), $("<code></code>").text(p.sha256)));
        }
        $card.append($("<div class=\"small download-guide-hint\"></div>").text("Instructions below ↓"));
        $cards.append($card);
      });
      $body.append($guide);

      function show(p) {
        $guide.empty().append(guide(p, latest.version));
        $cards.children().each(function () {
          var $c = $(this), isShown = $c.data("pkg") === p;
          $c.toggleClass("previewed", isShown).attr("aria-checked", String($c.data("pkg") === selected));
          $c.toggleClass("selected", $c.data("pkg") === selected);
        });
      }
      $cards.on("mouseenter focusin", ".download-card", function () { show($(this).data("pkg")); })
        .on("mouseleave", function () { show(selected); })
        .on("focusout", function (e) { if (!e.relatedTarget || !$.contains($cards[0], e.relatedTarget)) { show(selected); } })
        .on("click keydown", ".download-card", function (e) {
          if (e.type === "keydown" && e.key !== "Enter" && e.key !== " ") { return; }
          if ($(e.target).closest("a").length) { return; } // the Download button just downloads
          e.preventDefault();
          selected = $(this).data("pkg");
          show(selected);
        });
      show(selected);

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
