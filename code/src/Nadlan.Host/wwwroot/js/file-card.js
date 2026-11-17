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

  // A converted video's extras on its play button (migration 028): the 720p for small screens, the heights (subtitle cues) and
  // whether they show at first. Used by the Files cards and the presentation. hiRes (staff lists only, migration 029): the file id
  // while its original is kept - the viewer offers Hi-res and Catch image to an Admin / Data person.
  function videoAttrs(video) {
    var esc = Nadlan.format.escapeHtml;
    if (!video) { return ""; }
    var hiRes = video.hiRes && Nadlan.session && Nadlan.session.can && Nadlan.session.can("MAP_DROP");
    return (video.urlSmall ? " data-url-small=\"" + esc(video.urlSmall) + "\"" : "") +
      (video.heights ? " data-heights=\"" + esc(JSON.stringify(video.heights)) + "\" data-heights-on=\"" + (video.heightsOn ? "1" : "0") + "\"" : "") +
      (hiRes ? " data-hires=\"" + esc(String(video.hiRes)) + "\"" : "");
  }

  // Round button on media thumbnails: ▶ plays the video, ⤢ shows the image, in the full-screen viewer.
  function viewButton(file, mediaKind) {
    var esc = Nadlan.format.escapeHtml;
    return "<button type=\"button\" class=\"file-view-btn\"" + videoAttrs(file.video) + " data-view-file=\"" + esc(mediaKind) + "\" data-url=\"" + esc(file.url) +
      (file.downloadUrl ? "\" data-download=\"" + esc(file.downloadUrl) : "") +
      "\" data-name=\"" + esc(file.caption || file.originalFileName) + "\" title=\"" + (mediaKind === "video" ? "Play" : "View") + "\">" +
      (mediaKind === "video" ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 7l9 5-9 5z\"/></svg>"
                             : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M5 10V5h5M19 14v5h-5M14 5h5v5M10 19H5v-5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>") +
      "</button>";
  }

  // Media viewer above every dialog; closes on ×, Esc or a click outside the media.
  // startMs/endMs (optional, videos): a time frame on a Parcel (Map drop) - play starts there and pauses once at its end;
  // pressing play again goes on.
  // set (optional): { items: [{ kind, url, name, download }], index, info } - several photos / videos (a presentation property, a Files
  // list): "‹" / "›" and the arrow keys step through them without closing (remark 6). One file alone has no arrows.
  // info: HTML the page built (escaped there) shown in a rail at the top right - the presentation's property details (2026-11-08).
  // onShow (optional): onShow(i, item, via, video) each time a photo / video is shown - via "open" / "arrow" / "key"; video = its
  // element, or null (the presentation counts what its visitors watch; the viewer itself records nothing).
  function openViewer(mediaKind, url, name, startMs, endMs, set) {
    var esc = Nadlan.format.escapeHtml;
    var items = set && set.items && set.items.length ? set.items : [{ kind: mediaKind, url: url, name: name }];
    var index = items.length > 1 ? Math.max(0, Math.min(items.length - 1, set.index || 0)) : 0;
    var info = set && set.info ? set.info : "";
    var $viewer = $(
      "<div class=\"media-viewer" + (info ? " has-info" : "") + "\" role=\"dialog\">" +
        "<div class=\"media-viewer-bar\"><span class=\"media-viewer-name\"></span>" +
          (items.length > 1 ? "<span class=\"media-viewer-count\"></span>" : "") +
          "<span class=\"media-viewer-note\" hidden></span>" +
          "<button type=\"button\" class=\"media-viewer-extend\" hidden title=\"Keeping original big videos is charged - cents per GB per day. Keeps this original 7 days longer.\">Extend 7 days</button>" +
          "<button type=\"button\" class=\"media-viewer-catch\" hidden title=\"Add this moment to the tray; Get images makes full-size photos of them. Frame grabbing runs on a separate server and has a cost - usually a fraction of a cent, but please don't overuse it.\">Grab frame</button>" +
          "<button type=\"button\" class=\"media-viewer-hires\" hidden title=\"Play the original upload at full resolution (kept for a few weeks)\">Hi-res</button>" +
          "<button type=\"button\" class=\"media-viewer-heights\" hidden title=\"Show or hide the height above take-off\">Heights</button>" +
          "<a class=\"media-viewer-open\" target=\"_blank\" rel=\"noopener\">Open in new tab</a>" +
          "<button type=\"button\" class=\"media-viewer-close\" title=\"Close (Esc)\">×</button></div>" +
        "<div class=\"media-viewer-tray\" hidden><div class=\"media-viewer-tray-items\"></div>" +
          "<button type=\"button\" class=\"media-viewer-get\" title=\"Full-size photos of these moments, added to the files. Frame grabbing runs on a separate server and has a cost - usually a fraction of a cent, but please don't overuse it.\">Get images</button></div>" +
        "<div class=\"media-viewer-stage\"></div>" +
        (info ? "<aside class=\"media-viewer-info\">" + info + "</aside>" : "") +
        (items.length > 1
          ? "<button type=\"button\" class=\"media-viewer-nav prev\" data-step=\"-1\" title=\"Previous (←)\" aria-label=\"Previous\">‹</button>" +
            "<button type=\"button\" class=\"media-viewer-nav next\" data-step=\"1\" title=\"Next (→)\" aria-label=\"Next\">›</button>"
          : "") +
      "</div>").appendTo(document.body);
    var $stage = $viewer.find(".media-viewer-stage");
    var heightsShown = null; // the viewer's choice once the Heights button was used (else each video's default)
    var trackUrl = null;
    var shown = 0; // which show() a late answer belongs to (Hi-res, grabbed frames)
    var current = null; // the shown video's Hi-res controls (frames grabbed but not sent)

    function stopVideo() {
      $stage.find("video").each(function () { this.pause(); this.removeAttribute("src"); this.load(); }); // stop downloading
      if (trackUrl) { URL.revokeObjectURL(trackUrl); trackUrl = null; }
    }

    // Phones and small windows get the 720p copy; a big screen the 1080p (already in url once converted).
    function playUrl(it) {
      return it.urlSmall && (window.innerWidth < 900 || (window.matchMedia && window.matchMedia("(pointer: coarse)").matches)) ? it.urlSmall : it.url;
    }

    // Heights as a subtitle track (also in a phone's full screen, where an overlay wouldn't show): cues from the server.
    function addHeights(video, it) {
      var $btn = $viewer.find(".media-viewer-heights");
      if (!it.heights || !it.heights.length || !window.Blob) { $btn.prop("hidden", true); return; }
      var two = function (n) { return ("0" + n).slice(-2); };
      var ts = function (ms) { var s = Math.floor(ms / 1000); return two(Math.floor(s / 3600)) + ":" + two(Math.floor(s / 60) % 60) + ":" + two(s % 60) + "." + ("00" + (ms % 1000)).slice(-3); };
      var nl = String.fromCharCode(10);
      var vtt = "WEBVTT" + nl + nl + it.heights.map(function (c) { return ts(c[0]) + " --> " + ts(c[1]) + " line:8%" + nl + c[2] + nl; }).join(nl);
      trackUrl = URL.createObjectURL(new Blob([vtt], { type: "text/vtt" }));
      var track = document.createElement("track");
      track.kind = "subtitles"; track.label = "Heights"; track.srclang = "en"; track.src = trackUrl;
      video.appendChild(track);
      var on = heightsShown === null ? !!it.heightsOn : heightsShown;
      var apply = function () { if (video.textTracks[0]) { video.textTracks[0].mode = on ? "showing" : "hidden"; } $btn.toggleClass("active", on).attr("aria-pressed", on ? "true" : "false"); };
      track.addEventListener("load", apply);
      apply();
      $btn.prop("hidden", false).off("click").on("click", function () { on = !on; heightsShown = on; apply(); });
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

    // Hi-res and frame grabbing (owner, 2026-11-16/17; Admin / Data person, while the original is kept). The original plays straight
    // from storage over a signed link (our server never handles video bytes). Grab frame only notes the moment in a tray (a small
    // preview, its time, x to drop it); Get images sends them all: one worker run cuts each exact frame from the original at full
    // size, and they become photos of the video's parcel / asset in about a minute. The original is deleted some days after the
    // conversion: the time left shows, and Extend keeps it 7 days more.
    function hiResFor(video, it) {
      var $hi = $viewer.find(".media-viewer-hires"), $catch = $viewer.find(".media-viewer-catch"), $note = $viewer.find(".media-viewer-note");
      var $extend = $viewer.find(".media-viewer-extend"), $tray = $viewer.find(".media-viewer-tray"), $get = $tray.find(".media-viewer-get");
      var mine = shown, fileId = it.hiRes, light = playUrl(it), on = false, tray = [], original = null;
      $hi.removeClass("active").prop("disabled", false).text("Hi-res").prop("hidden", !fileId).off("click");
      $catch.prop("hidden", true).prop("disabled", false).off("click");
      $extend.prop("hidden", true).prop("disabled", false).off("click");
      $get.prop("disabled", false).off("click");
      $tray.prop("hidden", true).find(".media-viewer-tray-items").empty().off("click");
      $note.text("").prop("hidden", true);
      var none = { failed: function () { return false; }, pending: function () { return 0; } };
      if (!fileId) { return none; }
      var url = "/api/files/" + encodeURIComponent(fileId) + "/video";
      var note = function (text) { if (mine === shown) { $note.text(text).prop("hidden", !text); } };
      var at = function (ms) { return fmtTime(ms) + "." + Math.floor((ms % 1000) / 100); };
      // "5 days left" / "18 hours left": until the original is deleted (none: it is the kept file).
      var left = function (iso) {
        var h = (new Date(iso).getTime() - Date.now()) / 3600000;
        if (h >= 48) { return Math.floor(h / 24) + " days left"; }
        if (h >= 24) { return "1 day left"; }
        return h >= 1 ? Math.floor(h) + " hours left" : "less than an hour left";
      };
      var showOriginal = function () {
        if (!original) { return; }
        note("Original" + (original.size ? " · " + Math.round(original.size / 1048576) + " MB" : "") + (original.keptUntilUtc ? " · " + left(original.keptUntilUtc) : ""));
        $extend.prop("hidden", !original.keptUntilUtc);
      };
      // The moment and play state are taken when Hi-res is pressed: a source that fails resets the player's time to 0.
      var moment = 0, playing = false, pending = null;
      var swap = function (src, onBad) {
        if (pending) { video.removeEventListener("loadedmetadata", pending); }
        pending = function () {
          pending = null;
          if (onBad && !video.videoWidth) { onBad(); return; } // sound only: the picture can't be decoded
          try { video.currentTime = moment; } catch (e) { /* not seekable yet */ }
          if (playing) { var p = video.play(); if (p && p.catch) { p.catch(function () { /* the user presses play */ }); } }
        };
        video.addEventListener("loadedmetadata", pending, { once: true });
        video.autoplay = playing; // paused on a frame stays paused (to grab it)
        video.src = src;
      };
      var keep = function () { moment = video.currentTime; playing = !video.paused; };
      // The original's picture can't be played here (e.g. H.265): the light copy again at the same moment; the grabbed frames are
      // still cut from the original.
      var fallBack = function () {
        on = false;
        $hi.removeClass("active");
        swap(light);
        note("The original can't play in this browser (probably H.265): you see the light copy. Grabbed frames still come from the original, full size.");
      };
      $hi.on("click", function () {
        if (on) { on = false; keep(); $hi.removeClass("active"); $catch.prop("hidden", true); $extend.prop("hidden", true); note(""); swap(light); return; }
        $hi.prop("disabled", true).text("Loading…");
        Nadlan.api.get(url + "/original").then(function (o) {
          if (mine !== shown) { return; }
          $hi.prop("disabled", false).text("Hi-res").addClass("active");
          on = true;
          original = o;
          keep();
          swap(o.url, fallBack);
          $catch.prop("hidden", !o.canCatch);
          showOriginal();
        }, function (err) {
          $hi.prop("disabled", false).text("Hi-res");
          note(err && err.message ? err.message : "The original could not be opened.");
        });
      });
      $extend.on("click", function () {
        $extend.prop("disabled", true);
        Nadlan.api.post(url + "/original/extend", {}).then(function (res) {
          $extend.prop("disabled", false);
          if (original) { original.keptUntilUtc = res.keptUntilUtc; }
          showOriginal();
        }, function (err) {
          $extend.prop("disabled", false);
          note(err && err.message ? err.message : "Could not keep it longer.");
        });
      });

      // ---- the tray: grabbed moments, nothing sent until Get images ----
      var renderTray = function () {
        var $items = $tray.find(".media-viewer-tray-items").empty();
        tray.sort(function (a, b) { return a.ms - b.ms; }).forEach(function (t) {
          var $item = $("<div class=\"media-viewer-tray-item\" title=\"Show this moment\"></div>").attr("data-ms", t.ms);
          $item.append(t.canvas).append($("<span class=\"media-viewer-tray-time\"></span>").text(at(t.ms)))
            .append("<button type=\"button\" class=\"media-viewer-tray-drop\" title=\"Remove\" aria-label=\"Remove\">×</button>");
          $items.append($item);
        });
        $get.text("Get images (" + tray.length + ")");
        $tray.prop("hidden", !tray.length);
      };
      $tray.find(".media-viewer-tray-items").on("click", function (e) {
        var $item = $(e.target).closest(".media-viewer-tray-item");
        if (!$item.length) { return; }
        var ms = Number($item.attr("data-ms"));
        if ($(e.target).closest(".media-viewer-tray-drop").length) {
          tray = tray.filter(function (t) { return t.ms !== ms; });
          renderTray();
          return;
        }
        video.pause();
        try { video.currentTime = ms / 1000; } catch (err) { /* not seekable yet */ }
      });
      $catch.on("click", function () {
        var ms = Math.round(video.currentTime * 1000);
        video.pause();
        if (tray.some(function (t) { return Math.abs(t.ms - ms) < 40; })) { note("Frame " + at(ms) + " is already in the tray."); return; }
        if (tray.length >= 40) { note("At most 40 frames at a time: press Get images first."); return; }
        // The preview is drawn from the player (any picture it shows); the photo itself is cut from the original.
        var canvas = document.createElement("canvas");
        canvas.width = 112;
        canvas.height = Math.max(1, Math.round(112 * (video.videoHeight || 9) / (video.videoWidth || 16)));
        try { canvas.getContext("2d").drawImage(video, 0, 0, canvas.width, canvas.height); } catch (err) { /* no preview */ }
        tray.push({ ms: ms, canvas: canvas });
        renderTray();
        note("Frame " + at(ms) + " grabbed. Grab more, then Get images.");
      });
      $get.on("click", function () {
        var batch = tray.map(function (t) { return t.ms; });
        if (!batch.length) { return; }
        $get.prop("disabled", true);
        Nadlan.api.post(url + "/capture", { atMs: batch }).then(function (res) {
          $get.prop("disabled", false);
          tray = [];
          renderTray();
          var ids = res.captureIds || [], n = ids.length;
          note("Getting " + n + (n === 1 ? " image" : " images") + " - added to the files in about a minute.");
          var tries = 0;
          (function poll() {
            if (mine !== shown || !document.body.contains($viewer[0]) || ++tries > 60) { return; }
            setTimeout(function () {
              Nadlan.api.get(url + "/captures").then(function (list) {
                var mineNow = (list || []).filter(function (x) { return ids.indexOf(x.captureId) >= 0; });
                var done = mineNow.filter(function (x) { return x.status === "Done"; }).length;
                var failed = mineNow.filter(function (x) { return x.status === "Failed"; });
                if (done + failed.length < n) { poll(); return; }
                note(failed.length
                  ? done + " of " + n + " added to the files; " + failed.length + " failed: " + (failed[0].error || "unknown error")
                  : (n === 1 ? "The image was" : "All " + n + " images were") + " added to the files.");
              }, poll);
            }, 5000);
          })();
        }, function (err) {
          $get.prop("disabled", false);
          note(err && err.message ? err.message : "Could not get the images.");
        });
      });
      return { failed: function () { if (on) { fallBack(); return true; } return false; }, pending: function () { return tray.length; } };
    }

    // Grabbed frames not sent yet: leaving this video drops them - ask first.
    function leaveOk() {
      var n = current ? current.pending() : 0;
      return !n || window.confirm("Drop the " + n + " grabbed frame" + (n === 1 ? "" : "s") + " not sent yet? (Get images keeps them.)");
    }

    function show(i, via) {
      stopVideo();
      shown++;
      index = i;
      var it = items[i];
      var frame = it.kind === "video" && i === (set ? set.index : 0) && startMs >= 0
        ? " · " + fmtTime(startMs) + (endMs > startMs ? "–" + fmtTime(endMs) : "") : "";
      $viewer.attr("aria-label", it.name || "");
      $viewer.find(".media-viewer-name").text((it.name || "") + frame);
      $viewer.find(".media-viewer-count").text((i + 1) + " / " + items.length);
      $viewer.find(".media-viewer-open").attr("href", it.kind === "video" ? playUrl(it) : it.url);
      $stage.html(it.kind === "video"
        ? "<video class=\"media-viewer-media\" controls autoplay playsinline src=\"" + esc(playUrl(it)) + "\"></video>"
        : "<img class=\"media-viewer-media\" src=\"" + esc(it.url) + "\" alt=\"" + esc(it.name || "") + "\">");
      var video = $stage.find("video")[0];
      if (set && set.onShow) { try { set.onShow(i, it, via || "open", video || null); } catch (e) { /* the page's hook never breaks the viewer */ } }
      current = null;
      if (!video) { $viewer.find(".media-viewer-heights, .media-viewer-hires, .media-viewer-catch, .media-viewer-extend, .media-viewer-tray, .media-viewer-note").prop("hidden", true); return; }
      addHeights(video, it);
      var hi = current = hiResFor(video, it);
      video.addEventListener("loadedmetadata", function () {
        if (!video.videoWidth) { cantPlay(it); return; } // sound only: the picture can't be decoded
        if (frame && startMs > 0) { video.currentTime = startMs / 1000; }
      }, { once: true });
      video.addEventListener("error", function onError() {
        if (!video.error || video.error.code === 1 || hi.failed()) { return; } // 1 = aborted; the original failed: back to the copy
        video.removeEventListener("error", onError);
        cantPlay(it);
      });
      if (frame && endMs > 0) {
        var stopAt = endMs / 1000;
        video.addEventListener("timeupdate", function onTime() {
          if (video.currentTime >= stopAt) { video.pause(); video.removeEventListener("timeupdate", onTime); }
        });
      }
    }

    function step(by, via) { if (items.length > 1 && leaveOk()) { show((index + by + items.length) % items.length, via); } }

    function close() {
      if (!leaveOk()) { return; }
      stopVideo();
      $viewer.remove();
      document.removeEventListener("keydown", onKey, true);
    }

    $viewer.on("click", function (e) {
      if ($(e.target).closest("[data-step]").length) { step(Number($(e.target).closest("[data-step]").data("step")), "arrow"); return; }
      if ($(e.target).closest(".media-viewer-info").length) { return; }
      if (e.target === this || e.target === $stage[0] || $(e.target).is(".media-viewer-close")) { close(); }
    });
    // Capture phase, so Esc closes the viewer and not the jQuery UI dialog underneath; arrows don't scroll the page.
    function onKey(e) {
      if (e.key === "Escape") { e.stopPropagation(); e.preventDefault(); close(); }
      else if (items.length > 1 && (e.key === "ArrowLeft" || e.key === "ArrowRight")) {
        e.stopPropagation(); e.preventDefault(); step(e.key === "ArrowLeft" ? -1 : 1, "key");
      }
    }
    document.addEventListener("keydown", onKey, true);
    show(index);
  }

  // The set a view button belongs to: the buttons with the same data-view-group (a presentation property), or those in
  // the nearest [data-view-set] container (a Files list, a card's thumbnails). A time-frame button stays on its own.
  function viewItem(b) {
    var heights = null;
    try { heights = b.getAttribute("data-heights") ? JSON.parse(b.getAttribute("data-heights")) : null; } catch (e) { heights = null; }
    return { kind: b.getAttribute("data-view-file"), url: b.getAttribute("data-url"), name: b.getAttribute("data-name"), download: b.getAttribute("data-download"),
      urlSmall: b.getAttribute("data-url-small"), heights: heights, heightsOn: b.getAttribute("data-heights-on") === "1", hiRes: b.getAttribute("data-hires") };
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
    var set = viewSet(b) || { items: [viewItem(b)], index: 0 };
    // A page may add details beside the photos (the presentation: the property's card) - Nadlan.mediaViewer.infoFor(button).
    set.info = Nadlan.mediaViewer.infoFor ? Nadlan.mediaViewer.infoFor(b) : "";
    // ... and follow what is shown - Nadlan.mediaViewer.onShowFor(button) returns an onShow (above), or nothing.
    set.onShow = Nadlan.mediaViewer.onShowFor ? Nadlan.mediaViewer.onShowFor(b) : null;
    openViewer(b.getAttribute("data-view-file"), b.getAttribute("data-url"), b.getAttribute("data-name"),
      b.hasAttribute("data-start") ? Number(b.getAttribute("data-start")) : undefined, b.hasAttribute("data-end") ? Number(b.getAttribute("data-end")) : undefined,
      set);
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
      return "<button type=\"button\" class=\"file-segment\"" + videoAttrs(file.video) + " data-view-file=\"video\" data-url=\"" + esc(file.url) + "\" data-name=\"" +
        esc(file.caption || file.originalFileName) + "\" data-start=\"" + s.startMs + "\" data-end=\"" + s.endMs + "\" title=\"Play this part\">&#9654; " +
        fmtTime(s.startMs) + "–" + fmtTime(s.endMs) + "</button>";
    }).join("") + "</div>";
  }

  /** @returns {string} HTML for one card; data-file-id carries the id for click handling. */
  // "2026-10-19 14:28:01" (the camera's own clock, no time zone) -> "19 Oct 2026"; with time -> "19 Oct 2026, 14:28".
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  function takenDate(text, withTime) {
    var m = /^(\d{4})-(\d\d)-(\d\d) (\d\d):(\d\d)/.exec(text || "");
    if (!m) { return text || ""; }
    return Number(m[3]) + " " + MONTHS[Number(m[2]) - 1] + " " + m[1] + (withTime ? ", " + m[4] + ":" + m[5] : "");
  }

  function renderFileCard(file) {
    var esc = Nadlan.format.escapeHtml;
    var html = "<div class=\"file-card\" data-file-id=\"" + file.fileAttachmentId + "\" title=\"Click to edit details\">" +
      "<div class=\"file-thumb-wrap\">" + thumbHtml(file) + "</div>" +
      "<div class=\"file-title\">" + esc(file.caption || file.originalFileName) + "</div>" +
      "<div class=\"file-sub muted\">" + esc(file.fileTypeName) + " · " + Nadlan.formatFileSize(file.fileSize) + "</div>" +
      (file.takenLocal ? "<div class=\"file-sub file-taken muted\">taken " + esc(takenDate(file.takenLocal)) + "</div>" : "") +
      (file.video && (file.video.status === "Queued" || file.video.status === "Submitted") ? "<div class=\"file-sub file-optimizing\">Optimizing for playing…</div>" : "") +
      segmentsHtml(file) +
      "</div>";
    // On a Parcel with time frames, ▶ on the picture starts at the first one too.
    if (file.segments && file.segments.length && kind(file) === "video") {
      html = html.replace("data-view-file=\"video\" data-url=", "data-view-file=\"video\" data-start=\"" + file.segments[0].startMs + "\" data-end=\"" + file.segments[0].endMs + "\" data-url=");
    }
    return html;
  }

  Nadlan.renderFileCard = renderFileCard;
  Nadlan.mediaViewer = { open: openViewer, formatTime: fmtTime, videoAttrs: videoAttrs };
  Nadlan.fileKind = kind;
  Nadlan.takenDate = takenDate;
})(window, jQuery);
