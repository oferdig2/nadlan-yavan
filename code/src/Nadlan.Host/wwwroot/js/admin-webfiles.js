// Admin page, Web files tab: browse the site's files and hot-patch them (edit, upload, replace, revert). A patch is served in
// front of the release's own file, with .br and .gz made on the server; it lasts until the next deploy (see WebFiles.cs).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var M = function () { return Nadlan.machine; };
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };
  var API = "/api/admin/machine/files";

  Nadlan.initializeAdminWebFiles = function ($panel) {
    var info = null, root = "web", path = "", listing = null;
    var $toolbar = $("<div class=\"meta-toolbar\"><h3>Web files</h3>" +
      "<select class=\"input\" data-act=\"root\" aria-label=\"Folder\"></select>" +
      "<button type=\"button\" class=\"btn\" data-act=\"up\">↑ Up</button>" +
      "<span class=\"crumbs\"></span>" +
      "<button type=\"button\" class=\"btn\" data-act=\"refresh\">Refresh</button></div>");
    var $note = $("<div class=\"muted small webfiles-note\"></div>");
    var $status = $("<div class=\"done-note\" aria-live=\"polite\"></div>");
    var $drop = $("<div class=\"drop-zone\" tabindex=\"0\" role=\"button\">" +
      "<strong>Hot patch:</strong> drop files here, or <u>choose files</u>, to put them in this folder. " +
      "An existing file is replaced; .html, .css, .js and other text files get their .br and .gz on the server." +
      "<input type=\"file\" multiple hidden></div>");
    var $table = $("<table class=\"meta-table webfiles-table\"><thead><tr><th>Name</th><th class=\"num\">Size</th><th>Modified</th>" +
      "<th>Served from</th><th></th></tr></thead><tbody></tbody></table>");
    var $patches = $("<div class=\"patches-box\"></div>");
    $panel.append($toolbar, $note, $status, $drop, $table, "<h4 class=\"server-h\">Hot patches in this release</h4>", $patches);

    function report(text) { $status.text(text); }
    function fail(err) { Nadlan.dialog.showError("Web files", err); }
    function filePath(name) { return path ? path + "/" + name : name; }

    Nadlan.api.get(API + "/roots").then(function (d) {
      info = d;
      d.roots.forEach(function (r) { $toolbar.find("[data-act=root]").append($("<option></option>").val(r.key).text(r.name)); });
      $note.text("Patches go in front of release " + d.release + " and last until the next deploy: commit the same change to git. " +
        "Web files up to " + M().bytes(d.maxUploadBytes) + ": " + d.extensions.join(" "));
      load();
    }, fail);

    function load() {
      Nadlan.api.get(API, { root: root, path: path }).then(function (l) { listing = l; render(); }, fail);
      loadPatches();
    }

    function render() {
      var writable = listing.writable;
      $drop.prop("hidden", !writable);
      $toolbar.find("[data-act=up]").prop("disabled", listing.parent === null);
      var $crumbs = $toolbar.find(".crumbs").empty().append($("<a href=\"#\" data-dir=\"\"></a>").text("/"));
      var acc = [];
      (listing.path ? listing.path.split("/") : []).forEach(function (part) {
        acc.push(part);
        $crumbs.append($("<a href=\"#\"></a>").attr("data-dir", acc.join("/")).text(part), " / ");
      });

      var $body = $table.find("tbody").empty();
      if (!listing.entries.length) {
        $body.append("<tr><td colspan=\"5\" class=\"muted\">Empty folder.</td></tr>");
      }
      listing.entries.forEach(function (e) {
        var $tr = $("<tr></tr>").toggleClass("patched", e.patched);
        if (e.kind === "dir") {
          $tr.append($("<td colspan=\"5\"></td>").append($("<a href=\"#\" class=\"dir-link\"></a>").attr("data-dir", filePath(e.name)).text("📁 " + e.name)));
          $body.append($tr);
          return;
        }
        var from = !writable ? "release" : e.patched ? (e.inRelease ? "Hot patch" : "Hot patch (new file)") : "Release";
        var $from = $("<td></td>").append($("<span></span>").toggleClass("status-pill status-warning", e.patched).text(e.patched ? "✎ " + from : from));
        if (e.encodings.length) { $from.append($("<span class=\"muted small\"></span>").text(" · " + e.encodings.join(", "))); }
        var $actions = $("<td class=\"row-buttons\"></td>");
        if (e.editable && writable) { $actions.append($("<button type=\"button\" class=\"btn btn-small\" data-act=\"edit\">Edit</button>")); }
        if (writable) { $actions.append($("<button type=\"button\" class=\"btn btn-small\" data-act=\"replace\">Replace…</button>")); }
        $actions.append($("<a class=\"btn btn-small\">Download</a>").attr("href", API + "/download?" + $.param({ root: root, path: filePath(e.name) })));
        if (e.patched) { $actions.append($("<button type=\"button\" class=\"btn btn-small\" data-act=\"revert\"></button>").text(e.inRelease ? "Revert" : "Delete")); }
        $tr.attr("data-name", e.name).append(
          $("<td></td>").text(e.name), $("<td class=\"num\"></td>").text(M().bytes(e.size)),
          $("<td></td>").text(M().time(e.modifiedUtcMs)), $from, $actions);
        $body.append($tr);
      });
    }

    function loadPatches() {
      Nadlan.api.get(API + "/patches").then(function (list) {
        $patches.empty();
        if (!list.length) { $patches.append("<div class=\"muted\">None: every web file is the release's own.</div>"); return; }
        var $t = $("<table class=\"meta-table\"><thead><tr><th>File</th><th class=\"num\">Size</th><th>Patched</th><th>By</th><th></th></tr></thead><tbody></tbody></table>");
        list.forEach(function (p) {
          $t.find("tbody").append($("<tr></tr>").attr("data-path", p.path).append(
            $("<td></td>").append($("<a href=\"#\" class=\"dir-link\"></a>").attr("data-dir", p.path.split("/").slice(0, -1).join("/")).text(p.path)),
            $("<td class=\"num\"></td>").text(M().bytes(p.size)), $("<td></td>").text(M().time(p.modifiedUtcMs)),
            $("<td></td>").text(p.by || "—"),
            $("<td class=\"row-buttons\"></td>").append($("<button type=\"button\" class=\"btn btn-small\" data-act=\"revert-path\"></button>").text(p.inRelease ? "Revert" : "Delete"))));
        });
        $patches.append($t);
      }, function (err) { $patches.text(err.message); });
    }

    // -------------------------------------------------------------- navigation
    $toolbar.on("change", "[data-act=root]", function () { root = $(this).val(); path = ""; load(); });
    $toolbar.on("click", "[data-act=up]", function () { path = listing.parent || ""; load(); });
    $toolbar.on("click", "[data-act=refresh]", load);
    $panel.on("click", "a[data-dir]", function (e) {
      e.preventDefault();
      if (root !== "web" && $(this).closest(".patches-box").length) { root = "web"; $toolbar.find("[data-act=root]").val("web"); }
      path = String($(this).attr("data-dir"));
      load();
    });

    // -------------------------------------------------------------- uploads
    function uploadFiles(fileList, targetName) {
      var files = Array.prototype.slice.call(fileList || []);
      if (!files.length) { return; }
      var existing = files.map(function (f) { return targetName || f.name; }).filter(function (n) {
        return listing.entries.some(function (e) { return e.kind === "file" && e.name.toLowerCase() === n.toLowerCase(); });
      });
      var where = "/" + (path ? path + "/" : "");
      var msg = "<p>Put " + files.length + " file(s) live in <b>" + esc(where) + "</b>?</p>" +
        (existing.length ? "<p>Replaces: " + existing.map(esc).join(", ") + "</p>" : "") +
        (targetName && targetName !== files[0].name ? "<p>" + esc(files[0].name) + " is saved as <b>" + esc(targetName) + "</b>.</p>" : "") +
        "<p class=\"muted\">Browsers get it on their next page load.</p>";
      Nadlan.dialog.confirm("Hot patch", msg, "Put live").then(function (ok) {
        if (!ok) { return; }
        var form = new FormData();
        form.append("path", path);
        if (targetName) { form.append("name", targetName); }
        files.forEach(function (f) { form.append("files", f, f.name); });
        report("Uploading…");
        M().upload(API + "/upload", form).then(function (results) {
          report(results.map(describe).join(" · "));
          load();
        }, function (err) { report(""); fail(err); });
      });
    }

    function describe(r) {
      return r.path + " live (" + M().bytes(r.size) + (r.brotliSize ? ", br " + M().bytes(r.brotliSize) + ", gz " + M().bytes(r.gzipSize) : "") + ")";
    }

    var $input = $drop.find("input[type=file]");
    $drop.on("click keydown", function (e) {
      if (e.type === "keydown" && e.key !== "Enter" && e.key !== " ") { return; }
      if (e.target !== $input[0]) { e.preventDefault(); $input.removeData("target").val("").trigger("click"); }
    });
    $input.on("change", function () { uploadFiles(this.files, $input.data("target")); });
    $drop.on("dragover", function (e) { e.preventDefault(); $drop.addClass("drag"); })
      .on("dragleave", function () { $drop.removeClass("drag"); })
      .on("drop", function (e) { e.preventDefault(); $drop.removeClass("drag"); uploadFiles(e.originalEvent.dataTransfer.files); });

    $table.on("click", "[data-act=replace]", function () {
      var name = $(this).closest("tr").data("name");
      $input.val("").data("target", name).trigger("click");
    });

    // -------------------------------------------------------------- revert
    function revert(filePathToRevert) {
      Nadlan.dialog.confirm("Remove hot patch", "<p>Remove the patch of <b>" + esc(filePathToRevert) + "</b>? " +
        "The release's own file is served again (a file that only the patch added is gone).</p>", "Remove patch").then(function (ok) {
        if (!ok) { return; }
        Nadlan.api.del(API + "?" + $.param({ path: filePathToRevert })).then(function (r) {
          report(filePathToRevert + (r.releaseFileServed ? ": the release's file is served again." : ": removed."));
          load();
        }, fail);
      });
    }
    $table.on("click", "[data-act=revert]", function () { revert(filePath($(this).closest("tr").data("name"))); });
    $patches.on("click", "[data-act=revert-path]", function () { revert($(this).closest("tr").data("path")); });

    // -------------------------------------------------------------- editor
    $table.on("click", "[data-act=edit]", function () {
      var p = filePath($(this).closest("tr").data("name"));
      Nadlan.api.get(API + "/text", { path: p }).then(function (f) { openEditor(p, f); }, fail);
    });

    function openEditor(p, file) {
      var $content = $("<div class=\"file-editor\"></div>").append(
        $("<div class=\"muted small\"></div>").text((file.patched ? "Currently a hot patch" : "Currently the release's file") +
          " · " + M().time(file.modifiedUtcMs) + ". Save puts your version live at once, with its .br and .gz."),
        $("<textarea class=\"input code-area\" spellcheck=\"false\" wrap=\"off\"></textarea>").val(file.text));
      var original = file.text;
      var d = Nadlan.dialog.open({
        title: "Edit " + p,
        content: $content,
        width: Math.min(1100, $(window).width() - 40),
        buttons: [
          { text: "Save and put live", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ]
      });
      var $area = $content.find("textarea");
      $area.css("height", Math.max(240, $(window).height() - 260) + "px").trigger("focus");
      // Tab inserts a tab instead of leaving the field.
      $area.on("keydown", function (e) {
        if (e.key !== "Tab" || e.shiftKey) { return; }
        e.preventDefault();
        var el = this, s = el.selectionStart;
        el.value = el.value.slice(0, s) + "\t" + el.value.slice(el.selectionEnd);
        el.selectionStart = el.selectionEnd = s + 1;
      });
      function save() {
        if ($area.val() === original) { d.showError("Nothing changed."); return; }
        d.busy(true);
        Nadlan.api.put(API + "/text", { path: p, text: $area.val() }).then(function (r) {
          d.close();
          report(describe(r));
          load();
        }, function (err) { d.busy(false); d.showError(err.message); });
      }
    }
  };
})(window, jQuery);
