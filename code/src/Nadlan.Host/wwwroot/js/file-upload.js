// Upload engine: browser → S3 directly, multipart, via presigned part URLs from our API.
// - every file is multipart (one code path from 50 KB to multi-GB video)
// - at most 3 FILES upload at once (page-wide); the others wait in line ("Waiting…"), so dropping 500 photos doesn't
//   open 2000 connections and fail them all
// - 4 parts in flight per file, each retried up to 3 times with backoff
// - a lost connection (Wi-Fi drop, server restart) is retried by itself: it waits until the browser is online again,
//   then resumes from the parts S3 already has - up to 8 times, with growing pauses (2 s ... 30 s)
// - cancel aborts in-flight parts and the S3 multipart upload
// - retry after a failure resumes: asks the server which parts S3 already has and sends only the rest
// - photos and videos get a small JPEG preview (thumbnail) made here and stored with the file
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var CONCURRENCY = 4;
  var PART_ATTEMPTS = 3;
  var URL_BATCH = 100; // = FileService.MaxPartUrlsPerCall
  var MAX_FILES_AT_ONCE = 3;
  var AUTO_RETRIES = 8;
  var THUMB_SIZE = 480;
  var MAX_THUMB_BYTES = 300 * 1024; // = FileService.MaxThumbnailBytes

  function delay(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  function whenOnline() {
    if (navigator.onLine !== false) { return Promise.resolve(); }
    return new Promise(function (r) {
      window.addEventListener("online", function on() { window.removeEventListener("online", on); r(); });
    });
  }

  // A failure worth retrying by itself: no answer at all (network), or a gateway error while the server restarts.
  // Answers from our API with an error code (validation, permission, ...) are not.
  function isTransient(err) {
    if (!err || err.fatal || err.aborted || err.cancelled || (err.code && !/^HTTP_/.test(err.code))) { return false; }
    return err.status === undefined || err.status === 0 || err.status === 502 || err.status === 503 || err.status === 504;
  }

  // Page-wide line of files: take() → ticket { ready: Promise, release(), cancel() }.
  var running = 0;
  var line = [];
  function next() {
    while (running < MAX_FILES_AT_ONCE && line.length) {
      var t = line.shift();
      t.started = true;
      running++;
      t.go();
    }
  }
  function take() {
    var ticket = { started: false, released: false };
    ticket.ready = new Promise(function (resolve) { ticket.go = resolve; });
    ticket.release = function () {
      if (!ticket.started || ticket.released) { return; }
      ticket.released = true;
      running--;
      next();
    };
    ticket.cancel = function () {
      line = line.filter(function (t) { return t !== ticket; });
      ticket.release();
    };
    line.push(ticket);
    next();
    return ticket;
  }

  // ~480 px JPEG of a photo (or of a video's frame at 1 s), or null when the browser can't decode it (HEIC, RAW, ...).
  function makeThumbnail(file) {
    function toJpeg(source, width, height) {
      var scale = Math.min(1, THUMB_SIZE / Math.max(width, height));
      var canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.round(width * scale));
      canvas.height = Math.max(1, Math.round(height * scale));
      canvas.getContext("2d").drawImage(source, 0, 0, canvas.width, canvas.height);
      return new Promise(function (r) { canvas.toBlob(r, "image/jpeg", 0.8); });
    }
    if (/^image\//.test(file.type) && window.createImageBitmap && file.size <= 60 * 1048576) {
      return createImageBitmap(file).then(function (bmp) {
        return toJpeg(bmp, bmp.width, bmp.height).then(function (blob) { if (bmp.close) { bmp.close(); } return blob; });
      }).catch(function () { return null; });
    }
    if (/^video\//.test(file.type)) {
      return new Promise(function (resolve) {
        var url = URL.createObjectURL(file);
        var video = document.createElement("video");
        var finished = false;
        function done(blob) {
          if (finished) { return; }
          finished = true;
          video.removeAttribute("src");
          video.load();
          URL.revokeObjectURL(url);
          resolve(blob || null);
        }
        setTimeout(function () { done(null); }, 15000);
        video.muted = true;
        video.preload = "auto";
        video.onloadeddata = function () { video.currentTime = Math.min(1, (video.duration || 2) / 2); };
        video.onseeked = function () {
          if (!video.videoWidth) { done(null); return; }
          toJpeg(video, video.videoWidth, video.videoHeight).then(done, function () { done(null); });
        };
        video.onerror = function () { done(null); };
        video.src = url;
      });
    }
    return Promise.resolve(null);
  }

  // Best effort: a file without a preview still works (its card falls back to the original).
  function storeThumbnail(fileAttachmentId, file) {
    return makeThumbnail(file).then(function (blob) {
      if (!blob || blob.size > MAX_THUMB_BYTES || !window.fetch) { return; }
      return fetch("/api/files/" + fileAttachmentId + "/thumbnail", {
        method: "POST",
        credentials: "same-origin",
        body: blob,
        headers: { "Content-Type": "image/jpeg", "X-Nadlan-Request": "1" }
      });
    }).catch(function () { /* no preview */ });
  }

  /**
   * @param {File} file
   * @param {{ attachedToType: string, attachedToId: number, fileTypeId: number }} target
   * @param {{ onProgress: function(number, number): void, onState: function(string, string=): void }} handlers
   *        onProgress(bytesSent, totalBytes);
   *        onState("queued"|"uploading"|"waiting"|"completing"|"done"|"failed"|"cancelled", message)
   * @returns {{ promise: Promise<number>, cancel: function(): void, retry: function(): void }}  promise → fileAttachmentId
   */
  function upload(file, target, handlers) {
    var session = null;           // { fileAttachmentId, partSizeBytes, partCount }
    var etags = {};               // partNumber → ETag (confirmed by S3)
    var urls = {};                // partNumber → presigned PUT URL
    var partBytes = {};           // partNumber → bytes sent so far (for progress)
    var inflight = [];            // XHRs to abort on cancel
    var cancelled = false;
    var halted = false;           // a part failed for good: stop starting new parts until retry()
    var autoRetries = 0;          // automatic retries since the connection last worked
    var ticket = null;            // this file's place in the page-wide line
    var resolveDone, rejectDone;
    var promise = new Promise(function (res, rej) { resolveDone = res; rejectDone = rej; });
    promise.catch(function () { /* the caller watches onState; avoid unhandled-rejection noise */ });

    function progress() {
      var sent = 0;
      Object.keys(partBytes).forEach(function (k) { sent += partBytes[k]; });
      handlers.onProgress(Math.min(sent, file.size), file.size);
    }

    function partRange(n) {
      var start = (n - 1) * session.partSizeBytes;
      return { start: start, end: Math.min(start + session.partSizeBytes, file.size) };
    }

    function fetchUrls(numbers) {
      return Nadlan.api.post("/api/files/uploads/" + session.fileAttachmentId + "/part-urls", { partNumbers: numbers })
        .then(function (list) { list.forEach(function (p) { urls[p.partNumber] = p.url; }); });
    }

    function putPart(n) {
      return new Promise(function (resolve, reject) {
        var range = partRange(n);
        var xhr = new XMLHttpRequest();
        inflight.push(xhr);
        xhr.open("PUT", urls[n]);
        xhr.upload.onprogress = function (e) { partBytes[n] = e.loaded; progress(); };
        xhr.onload = function () {
          inflight = inflight.filter(function (x) { return x !== xhr; });
          if (xhr.status >= 200 && xhr.status < 300) {
            var etag = xhr.getResponseHeader("ETag");
            if (!etag) {
              reject({ fatal: true, message: "S3 did not expose the ETag header - the bucket CORS rule must include ExposeHeaders: ETag." });
              return;
            }
            etags[n] = etag;
            autoRetries = 0; // getting through again: a later drop gets the full set of retries
            partBytes[n] = range.end - range.start;
            progress();
            resolve();
          } else {
            reject({ status: xhr.status, message: "Part " + n + " failed (HTTP " + xhr.status + ")." });
          }
        };
        xhr.onerror = function () {
          inflight = inflight.filter(function (x) { return x !== xhr; });
          reject({ message: "Network error on part " + n + " (check the bucket CORS rule if this happens on every file)." });
        };
        xhr.onabort = function () { reject({ aborted: true }); };
        xhr.send(file.slice(range.start, range.end));
      });
    }

    // URLs are fetched in batches of up to URL_BATCH pending parts (one API call serves many parts);
    // concurrent workers share the batch request that is already in flight.
    var pendingParts = [];
    var urlRequest = null;
    function ensureUrl(n) {
      if (urls[n]) { return Promise.resolve(); }
      if (!urlRequest) {
        var from = pendingParts.indexOf(n);
        var batch = pendingParts.slice(Math.max(0, from)).filter(function (p) { return !urls[p] && !etags[p]; }).slice(0, URL_BATCH);
        if (batch.indexOf(n) < 0) { batch.unshift(n); batch = batch.slice(0, URL_BATCH); }
        urlRequest = fetchUrls(batch).then(function () { urlRequest = null; }, function (err) { urlRequest = null; throw err; });
      }
      return urlRequest.then(function () { return urls[n] ? null : ensureUrl(n); });
    }

    function sendPart(n, attempt) {
      return ensureUrl(n).then(function () { return putPart(n); }).catch(function (err) {
        if (cancelled || halted || err.aborted || err.fatal || attempt >= PART_ATTEMPTS) { throw err; }
        partBytes[n] = 0;
        if (err.status === 403) { delete urls[n]; } // presigned URL expired → get a fresh one
        return delay(1000 * Math.pow(3, attempt - 1)).then(whenOnline).then(function () { return sendPart(n, attempt + 1); });
      });
    }

    function runParts() {
      pendingParts = [];
      for (var n = 1; n <= session.partCount; n++) { if (!etags[n]) { pendingParts.push(n); } }
      if (!pendingParts.length) { return Promise.resolve(); } // every part already in S3 (e.g. only /complete failed)

      var nextPart = 0;
      function worker() {
        if (cancelled || halted || nextPart >= pendingParts.length) { return Promise.resolve(); }
        var n = pendingParts[nextPart++];
        return sendPart(n, 1).then(worker);
      }
      var workers = [];
      for (var i = 0; i < Math.min(CONCURRENCY, pendingParts.length); i++) { workers.push(worker()); }
      return Promise.all(workers);
    }

    function complete() {
      handlers.onState("completing");
      var parts = Object.keys(etags).map(function (k) { return { partNumber: Number(k), eTag: etags[k] }; });
      return Nadlan.api.post("/api/files/uploads/" + session.fileAttachmentId + "/complete", { parts: parts });
    }

    function fail(err) {
      if (cancelled) { return; }
      halted = true;
      inflight.forEach(function (x) { x.abort(); });
      if (isTransient(err) && autoRetries < AUTO_RETRIES) {
        var wait = Math.min(30000, 2000 * Math.pow(2, autoRetries++));
        handlers.onState("waiting", navigator.onLine === false
          ? "Offline - continues when the connection is back…"
          : "Connection problem - trying again in " + Math.round(wait / 1000) + " s…");
        delay(wait).then(whenOnline).then(function () { if (!cancelled && halted) { retry(); } });
        return;
      }
      handlers.onState("failed", err.message || "Upload failed.");
    }

    var alreadyStored = false; // the server says this upload already completed (its reply had been lost)

    // One path for the first attempt and for retries: wait for a slot → prepare (new session / resync with S3) →
    // parts → complete → preview image.
    function run(prepare) {
      halted = false;
      handlers.onState("queued");
      var mine = ticket = take();
      mine.ready.then(function () {
        if (cancelled) { return null; }
        handlers.onState("uploading");
        return prepare().then(function () { return alreadyStored ? null : runParts(); }).then(function () {
          if (cancelled || halted) { return null; }
          if (alreadyStored) {
            handlers.onState("done");
            resolveDone(session.fileAttachmentId);
            return null;
          }
          return complete().then(function (r) {
            if (cancelled) { return null; } // the server removes a file whose upload was cancelled mid-complete
            return storeThumbnail(r.fileAttachmentId, file).then(function () {
              handlers.onState("done");
              resolveDone(r.fileAttachmentId);
            });
          });
        });
      }).catch(fail).then(function () { mine.release(); });
    }

    function retry() { run(session ? resumeOrRestart : createSession); }

    function createSession() {
      return Nadlan.api.post("/api/files/uploads", {
        attachedToType: target.attachedToType, attachedToId: target.attachedToId, fileTypeId: target.fileTypeId,
        fileName: file.name, mimeType: file.type || null, fileSize: file.size
      }).then(function (s) {
        session = s;
        if (cancelled) { // Cancel was pressed before the server answered: drop the session it just created
          Nadlan.api.del("/api/files/uploads/" + s.fileAttachmentId).catch(function () { /* sweeper cleans up */ });
        }
      });
    }

    // Resume: S3 is the source of truth for which parts arrived.
    function resyncParts() {
      return Nadlan.api.get("/api/files/uploads/" + session.fileAttachmentId + "/parts").then(function (parts) {
        etags = {};
        partBytes = {};
        parts.forEach(function (p) {
          etags[p.partNumber] = p.eTag;
          var r = partRange(p.partNumber);
          partBytes[p.partNumber] = r.end - r.start;
        });
        urls = {};
        progress();
      });
    }

    // Retry: resume when S3 still has the multipart upload; otherwise decide from the server's answer.
    function resumeOrRestart() {
      return resyncParts().catch(function (err) {
        if (err.code === "FILE_NOT_UPLOADING") { alreadyStored = true; return; } // completed; only the reply was lost
        // Session gone (rejected & removed, or the multipart upload expired): start a fresh upload of the same file.
        if (err.status === 404 || /NoSuchUpload/.test(err.message || "")) {
          session = null;
          etags = {};
          partBytes = {};
          urls = {};
          return createSession();
        }
        throw err;
      });
    }

    run(createSession);

    return {
      promise: promise,

      cancel: function () {
        cancelled = true;
        if (ticket) { ticket.cancel(); }
        inflight.forEach(function (x) { x.abort(); });
        handlers.onState("cancelled");
        rejectDone({ cancelled: true });
        if (session) {
          Nadlan.api.del("/api/files/uploads/" + session.fileAttachmentId).catch(function (err) {
            // Too late: the upload had already finished and the file was kept - report it, so the gallery shows it.
            if (err.code === "FILE_NOT_UPLOADING") { handlers.onState("done", err.message); }
            /* anything else: the sweeper / lifecycle rule cleans up */
          });
        }
      },

      retry: function () { autoRetries = 0; retry(); }
    };
  }

  Nadlan.fileUploads = { upload: upload };
})(window, jQuery);
