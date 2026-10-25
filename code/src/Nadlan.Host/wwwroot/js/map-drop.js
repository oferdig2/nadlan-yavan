// Map drop (owner, 2026-10-20): drop drone/phone media anywhere on the map page; they upload unassigned, are read in the
// browser (media-inspect.js), matched to Parcels by the server (position, or a DJI .srt flight track), and shown as cards
// in a full-width strip at the bottom - the map stays visible above, so hovering a KAEK shows that Parcel. OK assigns;
// Assign + a click on the map assigns what wasn't found; what isn't assigned is deleted after 5 days.
// Only a video with a flight track can cover several Parcels (time frames). Admin + Data person (MAP_DROP).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var DAY = 86400000;

  /**
   * @param {{ showParcel: function(number): void, onAssigned: function(number): void, setStatus: function(string, boolean=): void }} options
   *   showParcel(parcelId) zooms the map to that Parcel and outlines it; onAssigned(parcelId) refreshes what the page shows of it
   */
  function init(options) {
    var esc = Nadlan.format.escapeHtml;
    var me = Nadlan.session.user();
    var ref = null;                 // reference data (file types)
    var items = [];                 // from GET /api/map-drop
    var keepDays = 5;
    var uploads = [];               // local uploads not on the server list yet: { id, file, name, size, pct, state, message, controller, fileAttachmentId }
    var ui = {};                    // fileAttachmentId -> { typeId, chosen: parcelId, ticks: {index: bool}, manual: {parcelId, registryId}, busy }
    var tracks = {};                // lower-case base name -> { points, takenLocal, fileAttachmentId? } of a dropped .srt
    var localMeta = {};             // fileAttachmentId -> metadata read from the local file
    var filter = "all";
    var picking = null;             // fileAttachmentId waiting for a click on the map
    var uploadSeq = 0;

    var $button = $(
      "<button type=\"button\" class=\"btn map-drop-button\" title=\"Drop drone and phone photos, videos and .srt tracks; then assign them to Parcels\">" +
        "&#x1F4E5; Map drop <span class=\"map-drop-badge\" hidden></span></button>").insertBefore(".app-header [data-tool=portfolios]");
    var $panel = $(
      "<section class=\"map-drop-panel\" hidden aria-label=\"Map drop\">" +
        "<header class=\"map-drop-head\">" +
          "<strong>Map drop</strong> <span class=\"map-drop-counts muted\"></span>" +
          "<span class=\"map-drop-filters\">" +
            "<button type=\"button\" data-filter=\"all\" class=\"active\">All</button><button type=\"button\" data-filter=\"found\">Found</button>" +
            "<button type=\"button\" data-filter=\"notfound\">Not found</button></span>" +
          "<span class=\"map-drop-actions\">" +
            "<button type=\"button\" class=\"btn btn-small btn-primary\" data-act=\"ok-all\">OK all found</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"delete-all\" title=\"Deletes the unassigned files you uploaded\">Delete my unassigned…</button>" +
            "<button type=\"button\" class=\"card-icon\" data-act=\"close\" title=\"Close (uploads continue)\">×</button>" +
          "</span>" +
        "</header>" +
        "<div class=\"map-drop-strip\">" +
          "<div class=\"drop-area map-drop-zone\" tabindex=\"0\"><div class=\"drop-label\">Drop photos, videos and .srt tracks here</div>" +
            "<div class=\"drop-hint muted\">or <u>browse</u> · DJI_0123.MP4 + DJI_0123.SRT together = video split over Parcels</div>" +
            "<input type=\"file\" multiple hidden></div>" +
        "</div>" +
        "<div class=\"map-drop-pick\" hidden></div>" +
      "</section>").appendTo(document.body);
    var $overlay = $("<div class=\"drop-area map-drop-overlay\" hidden><div>Drop to add to Map drop</div></div>").appendTo(document.body);
    var $strip = $panel.find(".map-drop-strip");
    var $zone = $panel.find(".map-drop-zone");
    var $input = $zone.find("input");

    // ------------------------------------------------------------------ open / close, badge

    function open() { $panel.prop("hidden", false); $("body").addClass("map-drop-open"); render(); }
    function close() { $panel.prop("hidden", true); $("body").removeClass("map-drop-open"); stopPicking(); if (options.clearPoint) { options.clearPoint(); } }

    $button.on("click", function () { if ($panel.prop("hidden")) { open(); } else { close(); } });
    $panel.on("click", "[data-act=close]", close);

    function badge() {
      var waiting = items.length + uploads.filter(function (u) { return u.state !== "done"; }).length;
      var oldest = items.reduce(function (t, i) { return Math.min(t, Date.parse(i.expiresUtc)); }, Infinity);
      var days = isFinite(oldest) ? Math.max(0, Math.ceil((oldest - Date.now()) / DAY)) : null;
      $button.find(".map-drop-badge").prop("hidden", !waiting)
        .text(waiting + (days !== null ? " · " + (days === 0 ? "today" : days + (days === 1 ? " day" : " days")) : ""))
        .attr("title", days !== null ? "Unassigned files are deleted " + keepDays + " days after upload - the oldest in " + days + " day(s)." : "");
    }

    // ------------------------------------------------------------------ data

    var loadTimer = null;
    function loadSoon() { clearTimeout(loadTimer); loadTimer = setTimeout(load, 600); }
    function load() {
      return Nadlan.api.get("/api/map-drop").then(function (res) {
        items = res.items;
        keepDays = res.keepDays;
        // Uploads that the list now shows are done with.
        uploads = uploads.filter(function (u) { return !(u.fileAttachmentId && items.some(function (i) { return i.file.fileAttachmentId === u.fileAttachmentId; })); });
        items.forEach(function (i) { pairTrack(i); });
        render();
        badge();
      }, function (err) { options.setStatus("Map drop: " + err.message, true); });
    }

    function typesFor(item) {
      return ref.fileTypes.filter(function (t) { return t.isActive !== false && Nadlan.session.seesFileCategory(t.category); });
    }

    function typeByCode(code) { return ref.fileTypes.filter(function (t) { return t.code === code && t.isActive !== false; })[0]; }

    // Initial document type from what the file is: DJI (camera make, a DJI_ file name, or a flight track beside the
    // video) -> drone types, .srt -> flight track. DJI videos carry no maker tag inside (the DJI app writes them with FFmpeg).
    function defaultType(kind, make, name, hasTrack) {
      var dji = /dji/i.test(make || "") || /^dji_/i.test(name || "") || !!hasTrack;
      var code = kind === "track" ? "DRONE_TRACK" : kind === "video" ? (dji ? "DRONE_VIDEO" : "VIDEO")
        : kind === "jpeg" || kind === "image" ? (dji ? "DRONE_PHOTO" : "PHOTO") : "OTHER";
      return (typeByCode(code) || typeByCode("OTHER") || ref.fileTypes[0]).id;
    }

    function kindOfItem(i) {
      if (/\.srt$/i.test(i.file.originalFileName)) { return "track"; }
      return /^video\//.test(i.file.mimeType) ? "video" : /^image\//.test(i.file.mimeType) ? "image" : "other";
    }

    function state(id) { return (ui[id] = ui[id] || { ticks: {} }); }

    // A video in the list and its .srt (dropped together): send the track once both are uploaded.
    function pairTrack(item) {
      if (kindOfItem(item) !== "video" || (item.metadata && item.metadata.hasTrack)) { return; }
      var t = tracks[Nadlan.mediaInspect.baseName(item.file.originalFileName)];
      if (!t || !t.fileAttachmentId || t.sentTo === item.file.fileAttachmentId) { return; }
      t.sentTo = item.file.fileAttachmentId;
      var metadata = $.extend({}, localMeta[item.file.fileAttachmentId] || item.metadata || {}, { trackFileId: t.fileAttachmentId });
      if (!metadata.takenLocal && t.takenLocal) { metadata.takenLocal = t.takenLocal; }
      delete metadata.hasTrack;
      Nadlan.api.put("/api/map-drop/" + item.file.fileAttachmentId + "/inspection", { metadata: metadata, track: t.points })
        .then(loadSoon, function (err) { t.sentTo = null; options.setStatus("Flight track of " + item.file.originalFileName + ": " + err.message, true); });
    }

    // ------------------------------------------------------------------ dropping files

    function addFiles(list) {
      var files = Array.prototype.slice.call(list || []).filter(function (f) { return f.size > 0 && !/^\./.test(f.name); }); // .DS_Store & co
      if (!files.length) { return; }
      open();
      var maxBytes = (Nadlan.clientConfig || {}).maxFileSizeBytes || Infinity;
      var tooBig = files.filter(function (f) { return f.size > maxBytes; });
      files = files.filter(function (f) { return f.size <= maxBytes; });
      if (tooBig.length) {
        Nadlan.dialog.showError("Too large", tooBig.length + " file(s) are over " + Nadlan.formatFileSize(maxBytes) + ": " + tooBig.map(function (f) { return f.name; }).join(", "));
      }
      Nadlan.api.post("/api/map-drop/duplicates", { files: files.map(function (f) { return { name: f.name, size: f.size }; }) }).then(function (hits) {
        if (!hits.length) { return files; }
        var key = function (n, s) { return n.toLowerCase() + "|" + s; };
        var dup = {};
        hits.forEach(function (h) { (dup[key(h.fileName, h.fileSize)] = dup[key(h.fileName, h.fileSize)] || []).push(h); });
        var rows = files.filter(function (f) { return dup[key(f.name, f.size)]; }).map(function (f) {
          return "<li><strong>" + esc(f.name) + "</strong> (" + Nadlan.formatFileSize(f.size) + ") - already " + dup[key(f.name, f.size)].map(function (h) {
            return h.attachedToType === "Parcel" ? "on Parcel " + esc(h.registryId || "#" + h.attachedToId) : h.attachedToType === "MapDrop" ? "in Map drop" : "on a " + esc(h.attachedToType);
          }).join(", ") + "</li>";
        });
        return Nadlan.dialog.confirm("Already uploaded?",
          "<p>These files were uploaded before (same name and size):</p><ul class=\"map-drop-dups\">" + rows.join("") + "</ul>" +
          "<p>Upload them again (e.g. if the stored copy may be damaged), or skip them?</p>", "Upload again").then(function (again) {
          return again ? files : files.filter(function (f) { return !dup[key(f.name, f.size)]; });
        });
      }).then(function (toSend) { (toSend || []).forEach(startUpload); },
        function (err) { options.setStatus("Map drop: " + err.message, true); });
    }

    function startUpload(file) {
      var u = { id: ++uploadSeq, file: file, name: file.name, size: file.size, pct: 0, state: "reading", message: "" };
      uploads.push(u);
      render();
      badge();
      var kind = Nadlan.mediaInspect.kindOf(file);
      var reading = kind === "track"
        ? (file.text ? file.text() : Promise.resolve("")).then(function (text) {
            var t = Nadlan.mediaInspect.parseSrt(text);
            tracks[Nadlan.mediaInspect.baseName(file.name)] = { points: t.points, takenLocal: t.takenLocal };
            // Its first point goes on its own card (where the flight starts), also when its video never comes.
            var first = t.points[0];
            return first ? { takenLocal: t.takenLocal, latitude: first[1], longitude: first[2] } : { takenLocal: t.takenLocal };
          })
        : Nadlan.mediaInspect.inspect(file);
      reading.catch(function () { return {}; }).then(function (metadata) {
        if (u.cancelled) { return; } // Cancel pressed while the file was being read: never upload it
        u.state = "queued";
        render();
        u.controller = Nadlan.fileUploads.upload(file,
          { attachedToType: "MapDrop", attachedToId: me.userId, fileTypeId: defaultType(kind, metadata.cameraMake, file.name, kind === "video" && !!tracks[Nadlan.mediaInspect.baseName(file.name)]) },
          {
            onProgress: function (sent, total) { u.pct = total ? Math.floor(sent * 100 / total) : 0; u.state = "uploading"; renderUpload(u); },
            onState: function (s, message) {
              u.state = s;
              u.message = message || "";
              if (s === "cancelled") { uploads = uploads.filter(function (x) { return x !== u; }); render(); badge(); return; }
              renderUpload(u);
            }
          });
        return u.controller.promise.then(function (fileAttachmentId) {
          u.fileAttachmentId = fileAttachmentId;
          localMeta[fileAttachmentId] = metadata;
          if (kind === "track") { tracks[Nadlan.mediaInspect.baseName(file.name)].fileAttachmentId = fileAttachmentId; }
          u.state = "checking";
          renderUpload(u);
          // A .srt keeps its own flight on the server, so it can be linked to its video later (also from another page).
          var ownTrack = kind === "track" ? (tracks[Nadlan.mediaInspect.baseName(file.name)] || {}).points || null : null;
          return Nadlan.api.put("/api/map-drop/" + fileAttachmentId + "/inspection", { metadata: metadata, track: ownTrack && ownTrack.length ? ownTrack : null });
        }).then(loadSoon, function (err) {
          if (err && err.cancelled) { return; }
          if (u.fileAttachmentId) { loadSoon(); return; } // uploaded; the reading couldn't be stored - the card shows it as not found
          u.state = "failed";
          u.message = err && err.message;
          renderUpload(u);
        });
      });
    }

    $zone.on("click keydown", function (e) { if (e.type === "click" || e.key === "Enter" || e.key === " ") { $input.trigger("click"); } });
    $input.on("click", function (e) { e.stopPropagation(); });
    $input.on("change", function () { addFiles(this.files); this.value = ""; });
    $zone.on("dragover dragenter", function (e) { e.preventDefault(); $zone.addClass("drag-over"); });
    $zone.on("dragleave dragend", function () { $zone.removeClass("drag-over"); });
    $zone.on("drop", function (e) { e.preventDefault(); e.stopPropagation(); $zone.removeClass("drag-over"); addFiles(e.originalEvent.dataTransfer && e.originalEvent.dataTransfer.files); });

    // Files dragged anywhere over the page: a full-window drop target (outside the Files dialog's own zones).
    function isFileDrag(e) {
      var types = e.originalEvent && e.originalEvent.dataTransfer && e.originalEvent.dataTransfer.types;
      return !!types && Array.prototype.indexOf.call(types, "Files") >= 0;
    }
    // The overlay covers the window once shown (its text ignores the pointer), so leaving it = leaving the window.
    $(document).on("dragenter", function (e) {
      if (!isFileDrag(e) || $(e.target).closest(".ui-dialog, .map-drop-panel, .map-drop-overlay").length) { return; }
      $overlay.prop("hidden", false);
    });
    $overlay.on("dragover", function (e) { e.preventDefault(); e.originalEvent.dataTransfer.dropEffect = "copy"; });
    $overlay.on("dragleave", function (e) { if (e.target === $overlay[0]) { $overlay.prop("hidden", true); } });
    $overlay.on("drop", function (e) {
      e.preventDefault();
      e.stopPropagation();
      $overlay.prop("hidden", true);
      addFiles(e.originalEvent.dataTransfer && e.originalEvent.dataTransfer.files);
    });
    $(document).on("dragend drop", function () { $overlay.prop("hidden", true); });

    // ------------------------------------------------------------------ rendering

    function fmtTime(ms) {
      var s = Math.floor(ms / 1000);
      return s >= 3600 ? Math.floor(s / 3600) + ":" + ("0" + Math.floor(s / 60) % 60).slice(-2) + ":" + ("0" + s % 60).slice(-2)
        : Math.floor(s / 60) + ":" + ("0" + s % 60).slice(-2);
    }

    function found(i) { return i.suggestions.length > 0; }

    function visibleItems() {
      // A .srt that belongs to a video in the list is shown on the video's card, not as its own.
      var videoIds = {};
      items.forEach(function (i) { if (i.metadata && i.metadata.trackFileId) { videoIds[i.metadata.trackFileId] = true; } });
      return items.filter(function (i) {
        if (videoIds[i.file.fileAttachmentId]) { return false; }
        return filter === "all" || (filter === "found") === found(i);
      });
    }

    function uploadHtml(u) {
      var text = u.state === "reading" ? "Reading…" : u.state === "queued" ? "Waiting…" : u.state === "uploading" ? "Uploading " + u.pct + "%"
        : u.state === "waiting" ? u.message : u.state === "completing" ? "Finishing…" : u.state === "checking" ? "Checking…"
        : u.state === "failed" ? u.message || "Failed" : "";
      return "<article class=\"map-drop-card uploading\" data-upload=\"" + u.id + "\">" +
        "<div class=\"md-thumb md-icon\">" + esc((u.name.split(".").pop() || "").slice(0, 4).toUpperCase()) + "</div>" +
        "<div class=\"md-name\" title=\"" + esc(u.name) + "\">" + esc(u.name) + "</div>" +
        "<div class=\"md-meta muted\">" + Nadlan.formatFileSize(u.size) + "</div>" +
        "<progress max=\"100\" value=\"" + u.pct + "\"></progress>" +
        "<div class=\"md-status" + (u.state === "failed" ? " error" : "") + "\">" + esc(text) + "</div>" +
        "<div class=\"md-buttons\">" +
          (u.state === "failed" ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"retry\">Retry</button>" : "") +
          (u.state !== "completing" && u.state !== "checking" ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"cancel\">Cancel</button>" : "") +
        "</div></article>";
    }

    function renderUpload(u) {
      var $card = $strip.find("[data-upload=" + u.id + "]");
      if ($card.length) { $card.replaceWith(uploadHtml(u)); } else { render(); }
    }

    function metaLine(i) {
      var m = i.metadata || {};
      var parts = [];
      if (m.takenLocal) { parts.push(m.takenLocal.slice(0, 16)); }
      if (m.cameraMake || m.cameraModel) { parts.push(esc([m.cameraMake, m.cameraModel].filter(Boolean).join(" "))); }
      if (m.durationS) { parts.push(fmtTime(m.durationS * 1000)); }
      if (m.bitrateKbps && m.bitrateKbps > 12000) { parts.push("<span class=\"md-warn\" title=\"Plays poorly on slower connections\">" + Math.round(m.bitrateKbps / 1000) + " Mbit/s</span>"); }
      return parts.join(" · ");
    }

    function chip(parcelId, registryId, extra, cls) {
      return "<span class=\"md-chip " + (cls || "") + "\" data-parcel=\"" + parcelId + "\" title=\"Show on the map\">" +
        esc(registryId || "#" + parcelId) + (extra ? " <span class=\"muted\">" + extra + "</span>" : "") + "</span>";
    }

    // A card's choices before the user touches it: document type, ticked time frames (long looks), the Parcel picked
    // among overlapping ones. Also for cards not drawn (hidden by a filter), so "OK all found" sees the same choices.
    function defaults(i) {
      var st = state(i.file.fileAttachmentId);
      var kind = kindOfItem(i);
      var m = i.metadata || {};
      if (st.typeId === undefined) {
        st.typeId = i.file.fileTypeId || defaultType(kind, m.cameraMake, i.file.originalFileName, m.hasTrack);
      }
      // A plain "Video" that turned out to have a flight track (paired after upload) is a drone video - unless the user chose.
      var plainVideo = typeByCode("VIDEO"), droneVideo = typeByCode("DRONE_VIDEO");
      if (!st.typeTouched && kind === "video" && m.hasTrack && plainVideo && droneVideo && st.typeId === plainVideo.id) { st.typeId = droneVideo.id; }
      if (kind === "video" && m.hasTrack) {
        i.suggestions.forEach(function (s, k) { if (st.ticks[k] === undefined) { st.ticks[k] = s.longLook; } });
      } else if (i.suggestions.length && st.chosen === undefined) {
        st.chosen = i.suggestions[0].parcelId;
      }
      return st;
    }

    // Where a file is when no Parcel was found there: the coordinate (a track: its first point), a click shows it on the map;
    // "+ Parcel here" starts drawing a new Parcel at that spot (for users who may create Parcels).
    function pointHtml(m, isTrack) {
      var text = m.latitude.toFixed(6) + ", " + m.longitude.toFixed(6);
      return "<div class=\"md-point\"><button type=\"button\" class=\"link\" data-point=\"" + m.latitude + "," + m.longitude + "\" title=\"Show this spot on the map\">&#x1F4CD; " +
        text + "</button>" + (isTrack ? " <span class=\"muted md-small\">start of flight</span>" : "") +
        (Nadlan.session.can("EDIT_ALL_PARCELS") ? " <button type=\"button\" class=\"btn btn-small\" data-act=\"parcel-here\" data-point=\"" + m.latitude + "," + m.longitude + "\" title=\"Draw a new Parcel around this spot\">+ Parcel here</button>" : "") +
        "</div>";
    }

    // An .srt waiting for its video: still uploading in this page (they join when it's done), already in GreekPlot (found by
    // name: link it), or not there at all.
    function trackStatus(i) {
      var base = Nadlan.mediaInspect.baseName(i.file.originalFileName);
      var video = uploads.filter(function (u) { return Nadlan.mediaInspect.baseName(u.name) === base && /.(mp4|mov|m4v)$/i.test(u.name); })[0];
      if (video) { return "Flight track of " + esc(video.name) + " - joins its card when that upload is done"; }
      var st = state(i.file.fileAttachmentId);
      if (st.link) {
        return "Flight track of " + esc(st.link.fileName) + " on Parcel " + esc(st.link.registryId || "#" + st.link.attachedToId) +
          (st.link.suggestions.length ? " - tick its time frames, then OK" : " - the flight crosses no Parcel you can see");
      }
      if (st.videos === undefined) { loadVideos(i); }
      if (!st.videos) { return "Flight track - looking for its video…"; }
      if (!st.videos.length) { return "Flight track - drop its video (" + esc(base.toUpperCase()) + ".MP4) to use it"; }
      return "Flight track of:" + st.videos.map(function (v) {
        return "<div class=\"md-link-video\">" + esc(v.fileName) + " " +
          (v.attachedToType === "Parcel" ? "on Parcel " + esc(v.registryId || "#" + v.attachedToId) : "in Map drop") +
          " <button type=\"button\" class=\"btn btn-small\" data-link-video=\"" + v.fileAttachmentId + "\"" + (st.busy ? " disabled" : "") + ">Link</button></div>";
      }).join("");
    }

    // The videos an .srt may belong to (same name), asked once per card.
    function loadVideos(i) {
      var st = state(i.file.fileAttachmentId);
      st.videos = null;
      Nadlan.api.get("/api/map-drop/" + i.file.fileAttachmentId + "/videos").then(function (list) { st.videos = list; render(); },
        function () { st.videos = []; render(); });
    }

    // The ticked frames of a .srt linked to a video on a Parcel - or null when there's nothing to apply.
    function linkedFrames(st) {
      var ticked = st.link.suggestions.filter(function (s, k) { return st.linkTicks[k]; });
      return ticked.length ? ticked.map(function (s) { return { parcelId: s.parcelId, startMs: s.startMs, endMs: s.endMs, source: "Track" }; }) : null;
    }

    function itemHtml(i) {
      var id = i.file.fileAttachmentId;
      var st = state(id);
      var kind = kindOfItem(i);
      var m = i.metadata || {};
      defaults(i);
      var thumb = i.file.thumbUrl
        ? "<img class=\"md-thumb\" src=\"" + esc(i.file.thumbUrl) + "\" alt=\"\">"
        : "<div class=\"md-thumb md-icon\">" + esc((i.file.originalFileName.split(".").pop() || "").slice(0, 4).toUpperCase()) + "</div>";
      var view = kind === "video" || kind === "image"
        ? "<button type=\"button\" class=\"file-view-btn\" data-view-file=\"" + kind + "\" data-url=\"" + esc(i.file.url) + "\" data-download=\"" + esc(i.file.downloadUrl || "") + "\" data-name=\"" + esc(i.file.originalFileName) + "\" title=\"" + (kind === "video" ? "Play" : "View") + "\">" +
          (kind === "video" ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 7l9 5-9 5z\"/></svg>" : "&#x2922;") + "</button>"
        : "";

      var where;
      var segs = kind === "video" && m.hasTrack ? i.suggestions : null;
      if (st.manual) {
        where = "<div class=\"md-found\">" + chip(st.manual.parcelId, st.manual.registryId, "chosen", "chosen") +
          " <button type=\"button\" class=\"link\" data-act=\"pick\">change</button></div>";
      } else if (kind === "track" && st.link && st.link.suggestions.length) {
        // A .srt linked to its video on a Parcel: the video's frames, ticked like on a video's own card.
        var lsegs = st.link.suggestions;
        var lpassed = lsegs.filter(function (s) { return !s.longLook; }).length;
        where = "<div class=\"md-notfound muted\">" + trackStatus(i) + "</div><div class=\"md-segments\">" + lsegs.map(function (s, k) {
          return "<label class=\"md-seg" + (s.longLook ? "" : " passed") + "\"><input type=\"checkbox\" data-link-tick=\"" + k + "\"" + (st.linkTicks[k] ? " checked" : "") + "> " +
            chip(s.parcelId, s.registryId) + " <button type=\"button\" class=\"link md-play\" data-link-play=\"" + k + "\" title=\"Play this part\">" + fmtTime(s.startMs) + "–" + fmtTime(s.endMs) + "</button></label>";
        }).join("") + "</div>" +
          (lpassed ? "<div class=\"muted md-small\">" + lpassed + " passed over (under 5 s) - not ticked</div>" : "");
      } else if (segs && segs.length) {
        var passed = segs.filter(function (s) { return !s.longLook; }).length;
        where = "<div class=\"md-segments\">" + segs.map(function (s, k) {
          return "<label class=\"md-seg" + (s.longLook ? "" : " passed") + "\"><input type=\"checkbox\" data-tick=\"" + k + "\"" + (st.ticks[k] ? " checked" : "") + "> " +
            chip(s.parcelId, s.registryId) + " <button type=\"button\" class=\"link md-play\" data-play=\"" + k + "\" title=\"Play this part\">" + fmtTime(s.startMs) + "–" + fmtTime(s.endMs) + "</button></label>";
        }).join("") + "</div>" +
          (passed ? "<div class=\"muted md-small\">" + passed + " passed over (under 5 s) - not ticked</div>" : "");
      } else if (found(i)) {
        where = "<div class=\"md-found\">" + i.suggestions.map(function (s) {
          return i.suggestions.length > 1
            ? "<label><input type=\"radio\" name=\"md-choose-" + id + "\" data-choose=\"" + s.parcelId + "\"" + (st.chosen === s.parcelId ? " checked" : "") + "> " + chip(s.parcelId, s.registryId) + "</label>"
            : chip(s.parcelId, s.registryId);
        }).join(" ") + (i.suggestions.length > 1 ? "<div class=\"muted md-small\">Parcels overlap here - pick one</div>" : "") + "</div>";
      } else {
        var hasPoint = m.latitude !== undefined && m.latitude !== null && m.longitude !== undefined && m.longitude !== null;
        where = "<div class=\"md-notfound muted\">" + (!i.inspected ? "Checking…"
          : kind === "track" ? trackStatus(i)
          : hasPoint ? (kind === "video" && m.hasTrack ? "The flight crosses no Parcel you can see" : "Not on a Parcel you can see")
          : kind === "video" ? "No location (no .srt flight track)" : "No location in the file") + "</div>" +
          (i.inspected && hasPoint ? pointHtml(m, kind === "track" || m.hasTrack) : "");
      }
      var warn = m.gpsStatus && /invalid/i.test(m.gpsStatus) && found(i)
        ? "<div class=\"md-warn md-small\" title=\"DJI marks the GPS reading invalid: it was right on some of his photos and 24 km off on one\">GPS marked invalid by the drone - check the Parcel</div>" : "";
      var types = typesFor(i).map(function (t) { return "<option value=\"" + t.id + "\"" + (t.id === st.typeId ? " selected" : "") + ">" + esc(t.name) + "</option>"; }).join("");
      var canOk = !!(st.manual || (segs && segs.some(function (s, k) { return st.ticks[k]; })) || (found(i) && !segs) ||
        (kind === "track" && st.link && linkedFrames(st)));
      var expires = new Date(i.expiresUtc);
      return "<article class=\"map-drop-card" + (picking === id ? " picking" : "") + "\" data-id=\"" + id + "\">" +
        "<div class=\"md-thumb-wrap\">" + thumb + view + "</div>" +
        "<div class=\"md-name\" title=\"" + esc(i.file.originalFileName) + "\">" + esc(i.file.originalFileName) + "</div>" +
        "<div class=\"md-meta muted\">" + Nadlan.formatFileSize(i.file.fileSize) + (metaLine(i) ? " · " + metaLine(i) : "") + "</div>" +
        where + warn +
        (picking === id ? "<div class=\"md-picking\">Click a Parcel on the map, or KAEK: <input class=\"input\" data-kaek placeholder=\"KAEK\"> " +
          "<button type=\"button\" class=\"btn btn-small\" data-act=\"find\">Find</button> <button type=\"button\" class=\"link\" data-act=\"unpick\">cancel</button></div>" : "") +
        "<div class=\"md-foot\"><select class=\"input\" data-type title=\"Document type\">" + types + "</select>" +
          "<div class=\"md-buttons\">" +
            (canOk ? "<button type=\"button\" class=\"btn btn-small btn-primary\" data-act=\"ok\"" + (st.busy ? " disabled" : "") + ">OK</button>" : "") +
            (picking === id ? "" : "<button type=\"button\" class=\"btn btn-small\" data-act=\"pick\">Assign</button>") +
            "<button type=\"button\" class=\"card-icon\" data-act=\"delete\" title=\"Delete\">&#x1F5D1;</button>" +
          "</div></div>" +
        "<div class=\"md-small muted\" title=\"Uploaded by " + esc(i.uploadedByName || "?") + "\">" + (i.uploadedByUserId !== me.userId ? esc(i.uploadedByName || "?") + " · " : "") +
          "deleted " + expires.toLocaleDateString("en-GB", { day: "numeric", month: "short" }) + " if not assigned</div>" +
      "</article>";
    }

    function render() {
      if ($panel.prop("hidden")) { return; }
      var list = visibleItems();
      $strip.children(".map-drop-card").remove();
      $strip.append(uploads.map(uploadHtml).join("") + list.map(itemHtml).join(""));
      var foundCount = items.filter(found).length;
      $panel.find(".map-drop-counts").text(items.length + uploads.length === 0 ? "nothing waiting"
        : items.length + " file(s) · " + foundCount + " found · " + (items.length - foundCount) + " not found" + (uploads.length ? " · " + uploads.length + " uploading" : ""));
      $panel.find("[data-act=ok-all]").prop("disabled", foundCount === 0);
      $panel.find("[data-act=delete-all]").prop("disabled", items.length === 0);
      $zone.toggleClass("compact", items.length + uploads.length > 0);
    }

    // ------------------------------------------------------------------ card actions

    function item(id) { return items.filter(function (i) { return i.file.fileAttachmentId === id; })[0]; }
    function cardId(el) { return Number($(el).closest(".map-drop-card").data("id")); }

    $panel.on("click", "[data-filter]", function () {
      filter = $(this).data("filter");
      $panel.find("[data-filter]").removeClass("active");
      $(this).addClass("active");
      render();
    });
    $strip.on("change", "[data-type]", function () { var st = state(cardId(this)); st.typeId = Number(this.value); st.typeTouched = true; });
    // A .srt linked to its video on a Parcel: the ticked frames go on the video, the .srt to the video's Parcel.
    function applyLink(i, st) {
      var frames = linkedFrames(st);
      if (!frames) { return Promise.resolve(false); }
      st.busy = true;
      return Nadlan.api.post("/api/map-drop/" + i.file.fileAttachmentId + "/apply", { videoFileId: st.link.fileAttachmentId, segments: frames }).then(function () {
        items = items.filter(function (x) { return x !== i; });
        delete ui[i.file.fileAttachmentId];
        var shown = {};
        [st.link.attachedToId].concat(frames.map(function (f) { return f.parcelId; })).forEach(function (p) {
          if (!shown[p]) { shown[p] = true; options.onAssigned(p); }
        });
        return true;
      }, function (err) { st.busy = false; throw err; });
    }

    // Link a .srt to one of its videos: a video in Map drop gets the frames on its own card; one on a Parcel shows them here.
    $strip.on("click", "[data-link-video]", function () {
      var i = item(cardId(this));
      if (!i) { return; }
      var st = state(i.file.fileAttachmentId);
      var video = (st.videos || []).filter(function (v) { return v.fileAttachmentId === Number($(this).data("linkVideo")); }, this)[0];
      if (!video) { return; }
      st.busy = true;
      render();
      Nadlan.api.post("/api/map-drop/" + i.file.fileAttachmentId + "/link", { videoFileId: video.fileAttachmentId }).then(function (r) {
        st.busy = false;
        if (video.attachedToType !== "Parcel") {
          options.setStatus(i.file.originalFileName + " linked to " + video.fileName + " - assign the video from its card.");
          loadSoon();
          return;
        }
        st.link = $.extend({}, video, { suggestions: r.suggestions || [] });
        st.linkTicks = {};
        st.link.suggestions.forEach(function (s, k) { st.linkTicks[k] = s.longLook; });
        options.setStatus(i.file.originalFileName + " linked to " + video.fileName + ".");
        render();
      }, function (err) { st.busy = false; render(); options.setStatus(err.message, true); });
    });
    $strip.on("change", "[data-link-tick]", function () { state(cardId(this)).linkTicks[$(this).data("linkTick")] = this.checked; render(); });
    $strip.on("click", "[data-link-play]", function (e) {
      e.preventDefault();
      var st = state(cardId(this));
      var s = st.link && st.link.suggestions[Number($(this).data("linkPlay"))];
      if (s) { Nadlan.mediaViewer.open("video", st.link.url, st.link.fileName, s.startMs, s.endMs); options.showParcel(s.parcelId); }
    });

    $strip.on("change", "[data-tick]", function () { state(cardId(this)).ticks[$(this).data("tick")] = this.checked; render(); });
    $strip.on("change", "[data-choose]", function () { state(cardId(this)).chosen = Number($(this).data("choose")); });
    $strip.on("mouseenter click", ".md-chip", function () { options.showParcel(Number($(this).data("parcel"))); });
    function pointOf(el) { var p = String($(el).attr("data-point")).split(","); return { lat: Number(p[0]), lng: Number(p[1]) }; }
    $strip.on("click", "button.link[data-point]", function () {
      var p = pointOf(this);
      options.showPoint(p.lat, p.lng, $(this).closest(".map-drop-card").find(".md-name").text());
    });
    $strip.on("click", "[data-act=parcel-here]", function () { var p = pointOf(this); options.newParcelAt(p.lat, p.lng); });
    $strip.on("click", "[data-play]", function (e) {
      e.preventDefault();
      var i = item(cardId(this));
      var s = i && i.suggestions[Number($(this).data("play"))];
      if (s) { Nadlan.mediaViewer.open("video", i.file.url, i.file.originalFileName, s.startMs, s.endMs); options.showParcel(s.parcelId); }
    });
    $strip.on("click", "[data-upload] [data-act=cancel]", function () {
      var u = uploads.filter(function (x) { return x.id === Number($(this).closest("[data-upload]").data("upload")); }, this)[0];
      if (!u) { return; }
      u.cancelled = true;
      if (u.controller) { u.controller.cancel(); } else { uploads = uploads.filter(function (x) { return x !== u; }); render(); badge(); }
    });
    $strip.on("click", "[data-upload] [data-act=retry]", function () {
      var u = uploads.filter(function (x) { return x.id === Number($(this).closest("[data-upload]").data("upload")); }, this)[0];
      if (u && u.controller) { u.controller.retry(); }
    });

    // The assignment a card stands for: home Parcel + (videos with a track) the ticked time frames.
    function assignment(i) {
      var st = defaults(i);
      var m = i.metadata || {};
      if (st.manual) { return { parcelId: st.manual.parcelId, segments: [] }; }
      if (kindOfItem(i) === "video" && m.hasTrack) {
        var ticked = i.suggestions.filter(function (s, k) { return st.ticks[k]; });
        if (!ticked.length) { return null; }
        var total = {};
        ticked.forEach(function (s) { total[s.parcelId] = (total[s.parcelId] || 0) + (s.endMs - s.startMs); });
        var home = Object.keys(total).sort(function (a, b) { return total[b] - total[a]; })[0];
        return { parcelId: Number(home), segments: ticked.map(function (s) { return { parcelId: s.parcelId, startMs: s.startMs, endMs: s.endMs, source: "Track" }; }) };
      }
      if (!found(i)) { return null; }
      return { parcelId: st.chosen || i.suggestions[0].parcelId, segments: [] };
    }

    function assign(i) {
      var lst = state(i.file.fileAttachmentId);
      if (kindOfItem(i) === "track" && lst.link && !lst.manual) { return applyLink(i, lst); }
      var a = assignment(i);
      if (!a) { return Promise.resolve(false); }
      var st = state(i.file.fileAttachmentId);
      st.busy = true;
      return Nadlan.api.post("/api/map-drop/" + i.file.fileAttachmentId + "/assign", { parcelId: a.parcelId, fileTypeId: st.typeId, segments: a.segments }).then(function () {
        items = items.filter(function (x) { return x !== i; });
        if (i.metadata && i.metadata.trackFileId) { items = items.filter(function (x) { return x.file.fileAttachmentId !== i.metadata.trackFileId; }); }
        delete ui[i.file.fileAttachmentId];
        options.onAssigned(a.parcelId);
        a.segments.forEach(function (s) { if (s.parcelId !== a.parcelId) { options.onAssigned(s.parcelId); } });
        return true;
      }, function (err) { st.busy = false; throw err; });
    }

    $strip.on("click", "[data-act=ok]", function () {
      var i = item(cardId(this));
      if (!i) { return; }
      assign(i).then(function (done) {
        if (done) { options.setStatus(i.file.originalFileName + " assigned."); render(); badge(); }
      }, function (err) { render(); Nadlan.dialog.showError("Could not assign " + i.file.originalFileName, err); });
      render();
    });

    $panel.on("click", "[data-act=ok-all]", function () {
      var ready = items.filter(function (i) { return assignment(i); });
      if (!ready.length) { return; }
      Nadlan.dialog.confirm("Assign all found", "Assign <strong>" + ready.length + "</strong> file(s) to the Parcels shown on their cards?", "Assign").then(function (yes) {
        if (!yes) { return; }
        var failed = [];
        return ready.reduce(function (p, i) {
          return p.then(function () { return assign(i).catch(function (err) { failed.push(i.file.originalFileName + ": " + err.message); }); });
        }, Promise.resolve()).then(function () {
          render();
          badge();
          options.setStatus((ready.length - failed.length) + " file(s) assigned." + (failed.length ? " " + failed.length + " failed." : ""), failed.length > 0);
          if (failed.length) { Nadlan.dialog.showError("Some files were not assigned", failed.join("\n")); }
        });
      });
    });

    function deleteFiles(list) {
      return list.reduce(function (p, i) {
        return p.then(function () { return Nadlan.api.del("/api/files/" + i.file.fileAttachmentId).catch(function () { /* already gone */ }); });
      }, Promise.resolve()).then(load);
    }

    $strip.on("click", "[data-act=delete]", function () {
      var i = item(cardId(this));
      if (!i) { return; }
      var track = i.metadata && i.metadata.trackFileId ? item(i.metadata.trackFileId) : null;
      Nadlan.dialog.confirm("Delete file", "Delete <strong>" + esc(i.file.originalFileName) + "</strong>" + (track ? " and its flight track" : "") + "? This removes it from storage.", "Delete")
        .then(function (yes) { if (yes) { return deleteFiles(track ? [i, track] : [i]); } });
    });

    // Only the caller's own uploads: someone else's files may be waiting for them to assign (🗑 on a card still deletes one).
    $panel.on("click", "[data-act=delete-all]", function () {
      var mine = items.filter(function (i) { return i.uploadedByUserId === me.userId; });
      var others = items.length - mine.length;
      var keep = others ? " <strong>" + others + "</strong> file(s) uploaded by others stay." : "";
      if (!mine.length) { Nadlan.dialog.confirm("Delete unassigned", "None of the unassigned files are yours." + keep, "OK"); return; }
      var bytes = mine.reduce(function (t, i) { return t + i.file.fileSize; }, 0);
      Nadlan.dialog.confirm("Delete unassigned", "Delete your <strong>" + mine.length + "</strong> unassigned file(s) (" + Nadlan.formatFileSize(bytes) + ")? This removes them from storage." + keep, "Delete")
        .then(function (yes) { if (yes) { return deleteFiles(mine); } });
    });

    // Manual assignment: a click on a Parcel on the map (map-page calls picked()), or a typed KAEK.
    function startPicking(id) {
      picking = id;
      $("body").addClass("map-drop-picking");
      options.setStatus("Click the Parcel for this file on the map (Esc cancels).");
      render();
      $strip.find(".map-drop-card[data-id=" + id + "] [data-kaek]").trigger("focus");
    }
    function stopPicking() {
      if (picking === null) { return; }
      picking = null;
      $("body").removeClass("map-drop-picking");
      options.setStatus("");
      render();
    }
    function picked(parcel) {
      if (picking === null) { return; }
      state(picking).manual = { parcelId: parcel.parcelId, registryId: parcel.registryId };
      options.showParcel(parcel.parcelId);
      stopPicking();
    }

    $strip.on("click", "[data-act=pick]", function () { var id = cardId(this); delete state(id).manual; startPicking(id); });
    $strip.on("click", "[data-act=unpick]", stopPicking);
    $strip.on("keydown", "[data-kaek]", function (e) { if (e.key === "Enter") { e.preventDefault(); $(this).siblings("[data-act=find]").trigger("click"); } });
    $strip.on("click", "[data-act=find]", function () {
      var kaek = String($(this).siblings("[data-kaek]").val() || "").trim();
      if (!kaek) { return; }
      Nadlan.api.get("/api/parcels", { registryId: kaek }).then(function (res) {
        var exact = (res.items || []).filter(function (x) { return (x.summary.registryId || "").toLowerCase() === kaek.toLowerCase(); });
        var hits = exact.length ? exact : res.items || [];
        if (hits.length !== 1) { options.setStatus(hits.length ? hits.length + " Parcels match \"" + kaek + "\" - type more of the KAEK." : "No Parcel with KAEK \"" + kaek + "\".", true); return; }
        picked(hits[0].summary);
      }, function (err) { options.setStatus(err.message, true); });
    });
    $(document).on("keydown", function (e) { if (e.key === "Escape" && picking !== null) { stopPicking(); } });

    // ------------------------------------------------------------------ start

    var ready = Nadlan.reference.load().then(function (r) { ref = r; return load(); });

    return {
      ready: ready,
      open: open,
      /** True while a card waits for a Parcel click (map-page sends the click here instead of opening the card). */
      isPicking: function () { return picking !== null; },
      picked: picked
    };
  }

  Nadlan.mapDrop = { init: init };
})(window, jQuery);
