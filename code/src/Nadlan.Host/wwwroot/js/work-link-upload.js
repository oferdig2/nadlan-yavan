// Pictures and videos of a work, from the contractor's phone: straight to S3 in parts (presigned URLs from the link API),
// as the app's own uploads do (file-upload.js - copied here, that file belongs to the Parcel / Asset files). Then a small
// JPEG preview made on the phone and what is inside the file (media-inspect.js: date taken, camera, place).
//   Nadlan.workUpload.upload(file, workItemId, position, onProgress(sent, total)) -> { promise, cancel() }
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var api = Nadlan.workLinkApi;

  var CONCURRENCY = 3;
  var PART_ATTEMPTS = 3;
  var URL_BATCH = 100;
  var THUMB_SIZE = 480;
  var MAX_THUMB_BYTES = 300 * 1024;

  function delay(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  // ~480 px JPEG of a photo (or of a video's frame at 1 s), or null when the phone can't decode it (HEIC, ...).
  // Copied from file-upload.js makeThumbnail.
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

  /**
   * @param {File} file
   * @param {number} workItemId
   * @param {{lat:number,lng:number,accuracy:number}|null} position  the phone's own position now (if shared)
   * @param {function(number, number)} onProgress
   */
  function upload(file, workItemId, position, onProgress) {
    var session = null, etags = {}, urls = {}, sent = {}, inflight = [], cancelled = false;

    function progress() {
      var total = 0;
      Object.keys(sent).forEach(function (k) { total += sent[k]; });
      onProgress(Math.min(total, file.size), file.size);
    }

    function range(n) {
      var start = (n - 1) * session.partSizeBytes;
      return { start: start, end: Math.min(start + session.partSizeBytes, file.size) };
    }

    function ensureUrls(numbers) {
      var missing = numbers.filter(function (n) { return !urls[n]; }).slice(0, URL_BATCH);
      if (!missing.length) { return Promise.resolve(); }
      return api.post("/files/" + session.fileAttachmentId + "/part-urls", { partNumbers: missing })
        .then(function (list) { list.forEach(function (p) { urls[p.partNumber] = p.url; }); });
    }

    function put(n) {
      return new Promise(function (resolve, reject) {
        var r = range(n);
        var xhr = new XMLHttpRequest();
        inflight.push(xhr);
        xhr.open("PUT", urls[n]);
        xhr.upload.onprogress = function (e) { sent[n] = e.loaded; progress(); };
        xhr.onload = function () {
          inflight = inflight.filter(function (x) { return x !== xhr; });
          var etag = xhr.getResponseHeader("ETag");
          if (xhr.status >= 200 && xhr.status < 300 && etag) {
            etags[n] = etag;
            sent[n] = r.end - r.start;
            progress();
            resolve();
          } else {
            reject({ status: xhr.status });
          }
        };
        xhr.onerror = function () { inflight = inflight.filter(function (x) { return x !== xhr; }); reject({ network: true }); };
        xhr.onabort = function () { reject({ aborted: true }); };
        xhr.send(file.slice(r.start, r.end));
      });
    }

    function sendPart(n, attempt) {
      return ensureUrls([n]).then(function () { return put(n); }).catch(function (err) {
        if (cancelled || err.aborted || attempt >= PART_ATTEMPTS) { throw err; }
        sent[n] = 0;
        if (err.status === 403) { delete urls[n]; } // the signed URL ran out: a new one
        return delay(1500 * attempt).then(function () { return sendPart(n, attempt + 1); });
      });
    }

    var promise = api.post("/files", {
      workItemId: workItemId, fileName: file.name, mimeType: file.type || null, fileSize: file.size,
      latitude: position ? position.lat : null, longitude: position ? position.lng : null, accuracyM: position ? Math.round(position.accuracy * 10) / 10 : null
    }).then(function (s) {
      session = s;
      var parts = [];
      for (var n = 1; n <= s.partCount; n++) { parts.push(n); }
      return ensureUrls(parts).then(function () {
        var next = 0;
        function worker() {
          if (cancelled || next >= parts.length) { return Promise.resolve(); }
          return sendPart(parts[next++], 1).then(worker);
        }
        var workers = [];
        for (var i = 0; i < Math.min(CONCURRENCY, parts.length); i++) { workers.push(worker()); }
        return Promise.all(workers);
      });
    }).then(function () {
      if (cancelled) { throw { cancelled: true }; }
      return api.post("/files/" + session.fileAttachmentId + "/complete", {
        parts: Object.keys(etags).map(function (k) { return { partNumber: Number(k), eTag: etags[k] }; })
      });
    }).then(function () {
      // Best effort: a picture without a preview or its data is still there.
      var id = session.fileAttachmentId;
      var thumb = makeThumbnail(file).then(function (blob) {
        if (blob && blob.size <= MAX_THUMB_BYTES && window.fetch) { return api.postBlob("/files/" + id + "/thumbnail", blob, "image/jpeg"); }
        return null;
      }).catch(function () { return null; });
      var data = Nadlan.mediaInspect && /^(jpeg|video)$/.test(Nadlan.mediaInspect.kindOf(file))
        ? Nadlan.mediaInspect.inspect(file).then(function (metadata) { return api.post("/files/" + id + "/media-data", { metadata: metadata }); })
            .catch(function () { return null; })
        : Promise.resolve();
      return Promise.all([thumb, data]).then(function () { return id; });
    }).catch(function (err) {
      if (session && !err.code) {
        // Stopped half-way (cancel, no network): free the upload in S3. The daily clean-up gets it otherwise.
        api.post("/files/" + session.fileAttachmentId + "/abort").catch(function () { /* fine */ });
      }
      throw err;
    });

    return {
      promise: promise,
      cancel: function () {
        cancelled = true;
        inflight.forEach(function (x) { x.abort(); });
      }
    };
  }

  Nadlan.workUpload = { upload: upload };
})(window, jQuery);
