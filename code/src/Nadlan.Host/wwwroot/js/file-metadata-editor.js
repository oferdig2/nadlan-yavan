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
