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
      (file.downloadUrl ? "\" data-download=\"" + esc(file.downloadUrl) : "") +
      "\" data-name=\"" + esc(file.caption || file.originalFileName) + "\" title=\"" + (mediaKind === "video" ? "Play" : "View") + "\">" +
      (mediaKind === "video" ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 7l9 5-9 5z\"/></svg>"
                             : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M5 10V5h5M19 14v5h-5M14 5h5v5M10 19H5v-5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>") +
      "</button>";
  }

  // Media viewer above every dialog; closes on ×, Esc or a click outside the media.
  // startMs/endMs (optional, videos): a time frame on a Parcel (Map drop) - play starts there and pauses once at its end;
  // pressing play again goes on.
  // set (optional): { items: [{ kind, url, name, download }], index } - several photos / videos (a presentation property, a Files
  // list): "‹" / "›" and the arrow keys step through them without closing (remark 6). One file alone has no arrows.
  function openViewer(mediaKind, url, name, startMs, endMs, set) {
    var esc = Nadlan.format.escapeHtml;
    var items = set && set.items && set.items.length ? set.items : [{ kind: mediaKind, url: url, name: name }];
    var index = items.length > 1 ? Math.max(0, Math.min(items.length - 1, set.index || 0)) : 0;
    var $viewer = $(
      "<div class=\"media-viewer\" role=\"dialog\">" +
        "<div class=\"media-viewer-bar\"><span class=\"media-viewer-name\"></span>" +
          (items.length > 1 ? "<span class=\"media-viewer-count\"></span>" : "") +
          "<a class=\"media-viewer-open\" target=\"_blank\" rel=\"noopener\">Open in new tab</a>" +
          "<button type=\"button\" class=\"media-viewer-close\" title=\"Close (Esc)\">×</button></div>" +
        "<div class=\"media-viewer-stage\"></div>" +
        (items.length > 1
          ? "<button type=\"button\" class=\"media-viewer-nav prev\" data-step=\"-1\" title=\"Previous (←)\" aria-label=\"Previous\">‹</button>" +
            "<button type=\"button\" class=\"media-viewer-nav next\" data-step=\"1\" title=\"Next (→)\" aria-label=\"Next\">›</button>"
          : "") +
      "</div>").appendTo(document.body);
    var $stage = $viewer.find(".media-viewer-stage");

    function stopVideo() {
      $stage.find("video").each(function () { this.pause(); this.removeAttribute("src"); this.load(); }); // stop downloading
    }

    // A video whose picture the browser can't decode (e.g. H.265 / HEVC from many phones in a browser without it) plays
    // its sound over a black picture: say so instead, with a download link (remark 7).
    function cantPlay(it) {
      stopVideo();
      $stage.html("<div class=\"media-viewer-cant\">" +
        "<p><strong>This video's picture format can't be played in this browser</strong> - probably H.265 (HEVC), which many " +
        "phones record. Download it to watch, or open it in another browser.</p>" +
        "<a class=\"media-viewer-download\" href=\"" + esc(it.download || it.url) + "\" download>Download the video</a></div>");
    }

    function show(i) {
      stopVideo();
      index = i;
      var it = items[i];
      var frame = it.kind === "video" && i === (set ? set.index : 0) && startMs >= 0
        ? " · " + fmtTime(startMs) + (endMs > startMs ? "–" + fmtTime(endMs) : "") : "";
      $viewer.attr("aria-label", it.name || "");
      $viewer.find(".media-viewer-name").text((it.name || "") + frame);
      $viewer.find(".media-viewer-count").text((i + 1) + " / " + items.length);
      $viewer.find(".media-viewer-open").attr("href", it.url);
      $stage.html(it.kind === "video"
        ? "<video class=\"media-viewer-media\" controls autoplay playsinline src=\"" + esc(it.url) + "\"></video>"
        : "<img class=\"media-viewer-media\" src=\"" + esc(it.url) + "\" alt=\"" + esc(it.name || "") + "\">");
      var video = $stage.find("video")[0];
      if (!video) { return; }
      video.addEventListener("loadedmetadata", function () {
        if (!video.videoWidth) { cantPlay(it); return; } // sound only: the picture can't be decoded
        if (frame && startMs > 0) { video.currentTime = startMs / 1000; }
      }, { once: true });
      video.addEventListener("error", function () { if (video.error && video.error.code !== 1) { cantPlay(it); } }, { once: true }); // 1 = aborted
      if (frame && endMs > 0) {
        var stopAt = endMs / 1000;
        video.addEventListener("timeupdate", function onTime() {
          if (video.currentTime >= stopAt) { video.pause(); video.removeEventListener("timeupdate", onTime); }
        });
      }
    }

    function step(by) { if (items.length > 1) { show((index + by + items.length) % items.length); } }

    function close() {
      stopVideo();
      $viewer.remove();
      document.removeEventListener("keydown", onKey, true);
    }

    $viewer.on("click", function (e) {
      if ($(e.target).closest("[data-step]").length) { step(Number($(e.target).closest("[data-step]").data("step"))); return; }
      if (e.target === this || e.target === $stage[0] || $(e.target).is(".media-viewer-close")) { close(); }
    });
    // Capture phase, so Esc closes the viewer and not the jQuery UI dialog underneath; arrows don't scroll the page.
    function onKey(e) {
      if (e.key === "Escape") { e.stopPropagation(); e.preventDefault(); close(); }
      else if (items.length > 1 && (e.key === "ArrowLeft" || e.key === "ArrowRight")) {
        e.stopPropagation(); e.preventDefault(); step(e.key === "ArrowLeft" ? -1 : 1);
      }
    }
    document.addEventListener("keydown", onKey, true);
    show(index);
  }

  // The set a view button belongs to: the buttons with the same data-view-group (a presentation property), or those in
  // the nearest [data-view-set] container (a Files list, a card's thumbnails). A time-frame button stays on its own.
  function viewItem(b) {
    return { kind: b.getAttribute("data-view-file"), url: b.getAttribute("data-url"), name: b.getAttribute("data-name"), download: b.getAttribute("data-download") };
  }

  function viewSet(b) {
    if (b.classList.contains("file-segment")) { return { items: [viewItem(b)], index: 0 }; }
    var group = b.getAttribute("data-view-group");
    var container = group ? null : b.closest("[data-view-set]");
    var buttons = group ? document.querySelectorAll("[data-view-group=\"" + group.replace(/"/g, "") + "\"]")
      : container ? container.querySelectorAll("[data-view-file]:not(.file-segment)") : [];
    var items = [], seen = {}, index = 0;
    Array.prototype.forEach.call(buttons, function (x) {
      var u = x.getAttribute("data-url");
      if (!u || seen[u]) { return; }
      seen[u] = true;
      if (u === b.getAttribute("data-url")) { index = items.length; }
      items.push(viewItem(x));
    });
    return items.length > 1 ? { items: items, index: index } : null;
  }

  // One handler for every card (files dialog, map card strip, metadata editor). Capture phase on the document, so it runs
  // before the cards' own (bubbling) click handlers and stops the click there: opening the viewer doesn't also open the
  // metadata editor or the files dialog underneath.
  document.addEventListener("click", function (e) {
    var b = e.target && e.target.closest ? e.target.closest("[data-view-file]") : null;
    if (!b) { return; }
    e.preventDefault();
    e.stopPropagation();
    openViewer(b.getAttribute("data-view-file"), b.getAttribute("data-url"), b.getAttribute("data-name"),
      b.hasAttribute("data-start") ? Number(b.getAttribute("data-start")) : undefined, b.hasAttribute("data-end") ? Number(b.getAttribute("data-end")) : undefined,
      viewSet(b) || { items: [viewItem(b)], index: 0 });
  }, true);

  function thumbHtml(file) {
    var esc = Nadlan.format.escapeHtml;
    switch (kind(file)) {
      case "image":
        // The small preview made at upload (thumbUrl); older files only have the original.
        return "<img class=\"file-thumb\" loading=\"lazy\" src=\"" + esc(file.thumbUrl || file.url) + "\" alt=\"" + esc(file.caption || file.originalFileName) + "\">" + viewButton(file, "image");
      case "video":
        if (file.thumbUrl) {
          return "<img class=\"file-thumb\" loading=\"lazy\" src=\"" + esc(file.thumbUrl) + "\" alt=\"" + esc(file.caption || file.originalFileName) + "\">" + viewButton(file, "video");
        }
        // #t=0.5 makes browsers render a frame as the poster without downloading the whole video.
        return "<video class=\"file-thumb\" preload=\"metadata\" muted playsinline src=\"" + esc(file.url) + "#t=0.5\"></video>" + viewButton(file, "video");
      case "pdf":
        return "<div class=\"file-thumb file-icon\">PDF</div>";
      default:
        var ext = (file.originalFileName.split(".").pop() || "FILE").slice(0, 4).toUpperCase();
        return "<div class=\"file-thumb file-icon\">" + esc(ext) + "</div>";
    }
  }

  function fmtTime(ms) {
    var s = Math.floor(ms / 1000);
    return s >= 3600 ? Math.floor(s / 3600) + ":" + ("0" + Math.floor(s / 60) % 60).slice(-2) + ":" + ("0" + s % 60).slice(-2)
      : Math.floor(s / 60) + ":" + ("0" + s % 60).slice(-2);
  }

  // A video's time frames on the Parcel being shown (Map drop): one play button each, starting there.
  function segmentsHtml(file) {
    var esc = Nadlan.format.escapeHtml;
    if (!file.segments || !file.segments.length || kind(file) !== "video") { return ""; }
    return "<div class=\"file-segments\">" + file.segments.map(function (s) {
      return "<button type=\"button\" class=\"file-segment\" data-view-file=\"video\" data-url=\"" + esc(file.url) + "\" data-name=\"" +
        esc(file.caption || file.originalFileName) + "\" data-start=\"" + s.startMs + "\" data-end=\"" + s.endMs + "\" title=\"Play this part\">&#9654; " +
        fmtTime(s.startMs) + "–" + fmtTime(s.endMs) + "</button>";
    }).join("") + "</div>";
  }

  /** @returns {string} HTML for one card; data-file-id carries the id for click handling. */
  function renderFileCard(file) {
    var esc = Nadlan.format.escapeHtml;
    var html = "<div class=\"file-card\" data-file-id=\"" + file.fileAttachmentId + "\" title=\"Click to edit details\">" +
      "<div class=\"file-thumb-wrap\">" + thumbHtml(file) + "</div>" +
      "<div class=\"file-title\">" + esc(file.caption || file.originalFileName) + "</div>" +
      "<div class=\"file-sub muted\">" + esc(file.fileTypeName) + " · " + Nadlan.formatFileSize(file.fileSize) + "</div>" +
      segmentsHtml(file) +
      "</div>";
    // On a Parcel with time frames, ▶ on the picture starts at the first one too.
    if (file.segments && file.segments.length && kind(file) === "video") {
      html = html.replace("data-view-file=\"video\" data-url=", "data-view-file=\"video\" data-start=\"" + file.segments[0].startMs + "\" data-end=\"" + file.segments[0].endMs + "\" data-url=");
    }
    return html;
  }

  Nadlan.renderFileCard = renderFileCard;
  Nadlan.mediaViewer = { open: openViewer, formatTime: fmtTime };
  Nadlan.fileKind = kind;
})(window, jQuery);
