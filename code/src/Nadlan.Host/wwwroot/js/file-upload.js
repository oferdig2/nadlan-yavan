// Upload engine: browser → S3 directly, multipart, via presigned part URLs from our API.
// - every file is multipart (one code path from 50 KB to multi-GB video)
// - 4 parts in flight, each retried up to 3 times with backoff
// - cancel aborts in-flight parts and the S3 multipart upload
// - retry after a failure resumes: asks the server which parts S3 already has and sends only the rest
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var CONCURRENCY = 4;
  var PART_ATTEMPTS = 3;
  var URL_BATCH = 50;

  function delay(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  /**
   * @param {File} file
   * @param {{ attachedToType: string, attachedToId: number, fileTypeId: number }} target
   * @param {{ onProgress: function(number, number): void, onState: function(string, string=): void }} handlers
   *        onProgress(bytesSent, totalBytes); onState("uploading"|"completing"|"done"|"failed"|"cancelled", message)
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

    function sendPart(n, attempt) {
      var ready = urls[n] ? Promise.resolve() : fetchUrls([n]);
      return ready.then(function () { return putPart(n); }).catch(function (err) {
        if (cancelled || halted || err.aborted || err.fatal || attempt >= PART_ATTEMPTS) { throw err; }
        partBytes[n] = 0;
        if (err.status === 403) { delete urls[n]; } // presigned URL expired → get a fresh one
        return delay(1000 * Math.pow(3, attempt - 1)).then(function () { return sendPart(n, attempt + 1); });
      });
    }

    function runParts() {
      var pending = [];
      for (var n = 1; n <= session.partCount; n++) { if (!etags[n]) { pending.push(n); } }

      // Fetch the first batch of URLs up front; later ones on demand.
      return fetchUrls(pending.slice(0, URL_BATCH)).then(function () {
        var next = 0;
        function worker() {
          if (cancelled || halted || next >= pending.length) { return Promise.resolve(); }
          var n = pending[next++];
          return sendPart(n, 1).then(worker);
        }
        var workers = [];
        for (var i = 0; i < Math.min(CONCURRENCY, pending.length); i++) { workers.push(worker()); }
        return Promise.all(workers);
      });
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
      handlers.onState("failed", err.message || "Upload failed.");
    }

    function start() {
      handlers.onState("uploading");
      Nadlan.api.post("/api/files/uploads", {
        attachedToType: target.attachedToType, attachedToId: target.attachedToId, fileTypeId: target.fileTypeId,
        fileName: file.name, mimeType: file.type || null, fileSize: file.size
      }).then(function (s) {
        session = s;
        return runParts();
      }).then(function () {
        if (cancelled) { return; }
        return complete().then(function (r) {
          handlers.onState("done");
          resolveDone(r.fileAttachmentId);
        });
      }).catch(fail);
    }

    start();

    return {
      promise: promise,

      cancel: function () {
        cancelled = true;
        inflight.forEach(function (x) { x.abort(); });
        handlers.onState("cancelled");
        rejectDone({ cancelled: true });
        if (session) { Nadlan.api.del("/api/files/uploads/" + session.fileAttachmentId).catch(function () { /* lifecycle rule cleans up */ }); }
      },

      // Resume: S3 is the source of truth for which parts arrived.
      retry: function () {
        halted = false;
        if (!session) { start(); return; }
        handlers.onState("uploading");
        Nadlan.api.get("/api/files/uploads/" + session.fileAttachmentId + "/parts").then(function (parts) {
          etags = {};
          partBytes = {};
          parts.forEach(function (p) {
            etags[p.partNumber] = p.eTag;
            var r = partRange(p.partNumber);
            partBytes[p.partNumber] = r.end - r.start;
          });
          urls = {};
          progress();
          return runParts();
        }).then(function () {
          if (cancelled) { return; }
          return complete().then(function (r) { handlers.onState("done"); resolveDone(r.fileAttachmentId); });
        }).catch(fail);
      }
    };
  }

  Nadlan.fileUploads = { upload: upload };
})(window, jQuery);
