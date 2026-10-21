// FileMetadataEditor: correct/enrich an uploaded file - type, caption, notes, order - or open/delete it.
// The attachment target is never asked for; it comes from where the file was dropped.
// Resolves with "saved", "deleted" or null (cancelled).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function typeOptionsHtml(fileTypes, selectedId) {
    var esc = Nadlan.format.escapeHtml;
    var groups = {};
    var order = [];
    fileTypes.filter(function (t) { return t.isActive || t.id === selectedId; }).forEach(function (t) {
      if (!groups[t.category]) { groups[t.category] = []; order.push(t.category); }
      groups[t.category].push(t);
    });
    return order.map(function (cat) {
      return "<optgroup label=\"" + esc(cat) + "\">" + groups[cat].map(function (t) {
        return "<option value=\"" + t.id + "\"" + (t.id === selectedId ? " selected" : "") + ">" +
          esc(t.name) + (t.isActive ? "" : " (inactive)") + "</option>";
      }).join("") + "</optgroup>";
    }).join("");
  }

  // Time frames: videos on a Parcel, corrected by Map drop users (Admin, Data person).
  function canEditFrames(file) {
    return /^video\//.test(file.mimeType) && file.attachedToType === "Parcel" && Nadlan.session.can("MAP_DROP");
  }

  // "1:30" / "1:02:05" / "90" -> ms; null if unreadable.
  function parseTime(text) {
    var t = String(text || "").trim();
    if (!/^\d+(:\d{1,2}){0,2}$/.test(t)) { return null; }
    return t.split(":").reduce(function (total, part) { return total * 60 + Number(part); }, 0) * 1000;
  }

  function open(file, fileTypes) {
    return new Promise(function (resolve) {
      var esc = Nadlan.format.escapeHtml;
      var result = null;
      var $form = $(
        "<form class=\"form\">" +
          "<div class=\"file-preview\">" + Nadlan.renderFileCard(file) + "</div>" +
          "<div class=\"muted\">" + esc(file.originalFileName) + " · " + Nadlan.formatFileSize(file.fileSize) + "</div>" +
          "<label>Document type<select class=\"input\" name=\"fileTypeId\" data-type=\"id\">" + typeOptionsHtml(fileTypes, file.fileTypeId) + "</select></label>" +
          "<label>Title / caption<input class=\"input\" name=\"caption\" value=\"" + esc(file.caption || "") + "\"></label>" +
          "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"3\">" + esc(file.notes || "") + "</textarea></label>" +
          "<label>Sort order<input class=\"input\" name=\"sortOrder\" data-type=\"number\" value=\"" + (file.sortOrder === null || file.sortOrder === undefined ? "" : file.sortOrder) + "\"></label>" +
          (canEditFrames(file) ? "<fieldset class=\"file-frames\"><legend>Time frames on Parcels</legend>" +
            "<div class=\"muted md-small\">Where this video shows on other Parcels too, and where play starts (m:ss).</div>" +
            "<div class=\"frame-rows\"><span class=\"muted\">Loading…</span></div>" +
            "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-small\" data-act=\"add-frame\">+ Time frame</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"save-frames\">Save time frames</button></div></fieldset>" : "") +
          "<div class=\"btn-row\">" +
            "<a class=\"btn\" target=\"_blank\" rel=\"noopener\" href=\"/api/files/" + file.fileAttachmentId + "/content\">Open</a>" +
            "<button type=\"button\" class=\"btn btn-danger\" data-act=\"delete\">Delete file</button>" +
          "</div>" +
        "</form>");

      var d = Nadlan.dialog.open({
        title: "File details",
        content: $form,
        width: 460,
        buttons: [
          { text: "Save", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ],
        onClose: function () { resolve(result); }
      });
      $form.on("submit", function (e) { e.preventDefault(); save(); });

      $form.on("click", "[data-act=delete]", function () {
        Nadlan.dialog.confirm("Delete file", "Delete <strong>" + esc(file.originalFileName) + "</strong>? This removes it from storage.", "Delete")
          .then(function (ok) {
            if (!ok) { return; }
            d.busy(true);
            Nadlan.api.del("/api/files/" + file.fileAttachmentId).then(function () {
              result = "deleted";
              d.close();
            }, function (err) { d.busy(false); d.showError(err.message); });
          });
      });

      // ---- time frames (Map drop; data entry = MAP_DROP) ----
      var $rows = $form.find(".frame-rows");
      function frameRow(f) {
        return "<div class=\"frame-row\">" +
          "<input class=\"input\" data-f=\"kaek\" placeholder=\"KAEK\" value=\"" + esc(f.registryId || "") + "\" data-parcel=\"" + (f.parcelId || "") + "\">" +
          "<input class=\"input\" data-f=\"start\" placeholder=\"0:00\" value=\"" + (f.startMs >= 0 ? Nadlan.mediaViewer.formatTime(f.startMs) : "") + "\">" +
          "<span>–</span><input class=\"input\" data-f=\"end\" placeholder=\"0:30\" value=\"" + (f.endMs > 0 ? Nadlan.mediaViewer.formatTime(f.endMs) : "") + "\">" +
          "<button type=\"button\" class=\"card-icon\" data-act=\"remove-frame\" title=\"Remove\">×</button></div>";
      }
      if ($rows.length) {
        Nadlan.api.get("/api/files/" + file.fileAttachmentId + "/segments").then(function (list) {
          $rows.html(list.length ? list.map(frameRow).join("") : "<span class=\"muted none\">None - shown only on its own Parcel.</span>");
        }, function (err) { $rows.html("<span class=\"error\">" + esc(err.message) + "</span>"); });
      }
      $form.on("click", "[data-act=add-frame]", function () { $rows.find(".none").remove(); $rows.append(frameRow({ startMs: -1 })); });
      $form.on("click", "[data-act=remove-frame]", function () { $(this).closest(".frame-row").remove(); });
      $form.on("input", "[data-f=kaek]", function () { $(this).attr("data-parcel", ""); });
      $form.on("click", "[data-act=save-frames]", function () {
        var rows = $rows.find(".frame-row").toArray().map(function (r) {
          var $r = $(r);
          return { $kaek: $r.find("[data-f=kaek]"), startMs: parseTime($r.find("[data-f=start]").val()), endMs: parseTime($r.find("[data-f=end]").val()) };
        });
        var bad = rows.filter(function (r) { return r.startMs === null || r.endMs === null || r.endMs <= r.startMs || !String(r.$kaek.val()).trim(); });
        if (bad.length) { d.showError("Each time frame needs a KAEK and a start before its end (m:ss, e.g. 1:30 and 1:55)."); return; }
        d.busy(true);
        // Typed KAEKs -> Parcel ids (exact match).
        Promise.all(rows.map(function (r) {
          if (r.$kaek.attr("data-parcel")) { return Promise.resolve(Number(r.$kaek.attr("data-parcel"))); }
          var kaek = String(r.$kaek.val()).trim();
          return Nadlan.api.get("/api/parcels", { registryId: kaek }).then(function (res) {
            var hit = (res.items || []).filter(function (x) { return (x.summary.registryId || "").toLowerCase() === kaek.toLowerCase(); })[0];
            if (!hit) { throw new Error("No Parcel with KAEK " + kaek + "."); }
            return hit.summary.parcelId;
          });
        })).then(function (ids) {
          return Nadlan.api.put("/api/files/" + file.fileAttachmentId + "/segments",
            rows.map(function (r, k) { return { parcelId: ids[k], startMs: r.startMs, endMs: r.endMs, source: "Manual" }; }));
        }).then(function (list) {
          d.busy(false);
          d.showError("");
          result = "saved";
          $rows.html(list.length ? list.map(frameRow).join("") : "<span class=\"muted none\">None - shown only on its own Parcel.</span>");
          var $b = $form.find("[data-act=save-frames]").text("Saved ✓");
          setTimeout(function () { $b.text("Save time frames"); }, 2000);
        }, function (err) { d.busy(false); d.showError(err.message); });
      });

      function save() {
        var data = Nadlan.dialog.readForm($form);
        if (data.sortOrder !== null && (isNaN(data.sortOrder) || data.sortOrder % 1 !== 0)) { d.showError("Sort order must be a whole number."); return; }
        d.busy(true);
        Nadlan.api.put("/api/files/" + file.fileAttachmentId, {
          fileTypeId: data.fileTypeId || 0, caption: data.caption, notes: data.notes, sortOrder: data.sortOrder
        }).then(function () {
          result = "saved";
          d.close();
        }, function (err) { d.busy(false); d.showError(err.message); });
      }
    });
  }

  Nadlan.fileMetadataEditor = { open: open };
})(window, jQuery);
