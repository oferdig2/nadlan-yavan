// FileCard: thumbnail + title for an uploaded file. Images show the picture, videos their first frame,
// documents an icon. Clicking the card opens the metadata editor (wired by the caller).
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function kind(file) {
    if (/^image\//.test(file.mimeType)) { return "image"; }
    if (/^video\//.test(file.mimeType)) { return "video"; }
    if (file.mimeType === "application/pdf") { return "pdf"; }
    return "doc";
  }

  function thumbHtml(file) {
    var esc = Nadlan.format.escapeHtml;
    switch (kind(file)) {
      case "image":
        return "<img class=\"file-thumb\" loading=\"lazy\" src=\"" + esc(file.url) + "\" alt=\"" + esc(file.caption || file.originalFileName) + "\">";
      case "video":
        // #t=0.5 makes browsers render a frame as the poster without downloading the whole video.
        return "<video class=\"file-thumb\" preload=\"metadata\" muted playsinline src=\"" + esc(file.url) + "#t=0.5\"></video>" +
          "<span class=\"file-badge\">▶</span>";
      case "pdf":
        return "<div class=\"file-thumb file-icon\">PDF</div>";
      default:
        var ext = (file.originalFileName.split(".").pop() || "FILE").slice(0, 4).toUpperCase();
        return "<div class=\"file-thumb file-icon\">" + esc(ext) + "</div>";
    }
  }

  /** @returns {string} HTML for one card; data-file-id carries the id for click handling. */
  function renderFileCard(file) {
    var esc = Nadlan.format.escapeHtml;
    return "<div class=\"file-card\" data-file-id=\"" + file.fileAttachmentId + "\" title=\"Click to edit details\">" +
      "<div class=\"file-thumb-wrap\">" + thumbHtml(file) + "</div>" +
      "<div class=\"file-title\">" + esc(file.caption || file.originalFileName) + "</div>" +
      "<div class=\"file-sub muted\">" + esc(file.fileTypeName) + " · " + Nadlan.formatFileSize(file.fileSize) + "</div>" +
      "</div>";
  }

  Nadlan.renderFileCard = renderFileCard;
  Nadlan.fileKind = kind;
})(window);
