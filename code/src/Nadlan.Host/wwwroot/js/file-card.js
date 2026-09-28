// FileCard: thumbnail + title for an uploaded file. Images show the picture, videos their first frame,
// documents an icon. Clicking the card opens the metadata editor (wired by the caller).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function kind(file) {
    if (/^image\//.test(file.mimeType)) { return "image"; }
    if (/^video\//.test(file.mimeType)) { return "video"; }
    if (file.mimeType === "application/pdf") { return "pdf"; }
    return "doc";
  }

  // Round button on media thumbnails: ▶ plays the video, ⤢ shows the image, in the full-screen viewer.
  function viewButton(file, mediaKind) {
    var esc = Nadlan.format.escapeHtml;
    return "<button type=\"button\" class=\"file-view-btn\" data-view-file=\"" + esc(mediaKind) + "\" data-url=\"" + esc(file.url) +
      "\" data-name=\"" + esc(file.caption || file.originalFileName) + "\" title=\"" + (mediaKind === "video" ? "Play" : "View") + "\">" +
      (mediaKind === "video" ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 7l9 5-9 5z\"/></svg>"
                             : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M5 10V5h5M19 14v5h-5M14 5h5v5M10 19H5v-5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>") +
      "</button>";
  }

  // Media viewer above every dialog; closes on ×, Esc or a click outside the media.
  function openViewer(mediaKind, url, name) {
    var esc = Nadlan.format.escapeHtml;
    var $viewer = $(
      "<div class=\"media-viewer\" role=\"dialog\" aria-label=\"" + esc(name) + "\">" +
        "<div class=\"media-viewer-bar\"><span>" + esc(name) + "</span>" +
          "<a class=\"media-viewer-open\" href=\"" + esc(url) + "\" target=\"_blank\" rel=\"noopener\">Open in new tab</a>" +
          "<button type=\"button\" class=\"media-viewer-close\" title=\"Close (Esc)\">×</button></div>" +
        (mediaKind === "video"
          ? "<video class=\"media-viewer-media\" controls autoplay playsinline src=\"" + esc(url) + "\"></video>"
          : "<img class=\"media-viewer-media\" src=\"" + esc(url) + "\" alt=\"" + esc(name) + "\">") +
      "</div>").appendTo(document.body);

    function close() {
      $viewer.find("video").each(function () { this.pause(); this.removeAttribute("src"); this.load(); }); // stop downloading
      $viewer.remove();
      document.removeEventListener("keydown", onKey, true);
    }

    $viewer.on("click", function (e) { if (e.target === this || $(e.target).is(".media-viewer-close")) { close(); } });
    // Capture phase, so Esc closes the viewer and not the jQuery UI dialog underneath.
    function onKey(e) { if (e.key === "Escape") { e.stopPropagation(); e.preventDefault(); close(); } }
    document.addEventListener("keydown", onKey, true);
  }

  // One handler for every card (files dialog, map card strip, metadata editor); the card's own click is not triggered.
  $(document).on("click", "[data-view-file]", function (e) {
    e.preventDefault();
    e.stopPropagation();
    var $b = $(this);
    openViewer($b.attr("data-view-file"), $b.attr("data-url"), $b.attr("data-name"));
  });

  function thumbHtml(file) {
    var esc = Nadlan.format.escapeHtml;
    switch (kind(file)) {
      case "image":
        return "<img class=\"file-thumb\" loading=\"lazy\" src=\"" + esc(file.url) + "\" alt=\"" + esc(file.caption || file.originalFileName) + "\">" + viewButton(file, "image");
      case "video":
        // #t=0.5 makes browsers render a frame as the poster without downloading the whole video.
        return "<video class=\"file-thumb\" preload=\"metadata\" muted playsinline src=\"" + esc(file.url) + "#t=0.5\"></video>" + viewButton(file, "video");
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
})(window, jQuery);
