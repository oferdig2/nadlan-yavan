// Reads what is inside a media file, in the browser, from small slices of the local file (a 4 GB video is never read whole):
//   inspect(file)   -> Promise<metadata>  JPEG: EXIF (camera, time, GPS) + DJI XMP (position, height, gimbal, GPS status)
//                                         MP4/MOV: length, size, codec, bitrate, where the index is, phone GPS (©xyz / ISO 6709)
//   parseSrt(text)  -> { takenLocal, points: [[ms, lat, lng, relAlt], ...] }  DJI flight track, 2 points a second
// Field names match the server's MediaMetadata (Map drop, migration 016). Nothing is guessed: unknown = left out.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var JPEG_HEAD = 256 * 1024;       // EXIF + XMP sit in the first segments
  var MAX_MOOV = 64 * 1048576;      // an MP4 index this big is not a camera file
  var TRACK_STEP_MS = 500;          // 2 points a second

  function slice(file, start, end) {
    var blob = file.slice(start, end);
    if (blob.arrayBuffer) { return blob.arrayBuffer(); }
    return new Promise(function (resolve, reject) {
      var r = new FileReader();
      r.onload = function () { resolve(r.result); };
      r.onerror = function () { reject(r.error); };
      r.readAsArrayBuffer(blob);
    });
  }

  function ascii(view, start, length) {
    var s = "";
    for (var i = 0; i < length && start + i < view.byteLength; i++) {
      var c = view.getUint8(start + i);
      if (c === 0) { break; }
      s += String.fromCharCode(c);
    }
    return s;
  }

  function utf8(view, start, length) {
    try { return new TextDecoder("utf-8").decode(new Uint8Array(view.buffer, view.byteOffset + start, Math.min(length, view.byteLength - start))); }
    catch (e) { return ascii(view, start, length); }
  }

  // "2026:08:04 14:28:01" / "2026-08-04 14:28:51.951" -> "2026-08-04 14:28:01"
  function normalizeTime(text) {
    var m = /(\d{4})[:\-](\d\d)[:\-](\d\d)[ T](\d\d):(\d\d):(\d\d)/.exec(text || "");
    if (!m || m[1] === "0000") { return undefined; }
    return m[1] + "-" + m[2] + "-" + m[3] + " " + m[4] + ":" + m[5] + ":" + m[6];
  }

  function num(v) { return typeof v === "number" && isFinite(v) ? v : undefined; }
  function clean(o) { Object.keys(o).forEach(function (k) { if (o[k] === undefined || o[k] === null || o[k] === "") { delete o[k]; } }); return o; }

  // ---------------------------------------------------------------- JPEG: EXIF + XMP

  function readExif(view, tiff, out) {
    var le = ascii(view, tiff, 2) === "II";
    function u16(o) { return view.getUint16(tiff + o, le); }
    function u32(o) { return view.getUint32(tiff + o, le); }
    function rational(o) { var d = u32(o + 4); return d ? u32(o) / d : undefined; }
    var SIZES = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];
    function ifd(offset, wanted) {
      var r = {};
      if (offset <= 0 || tiff + offset + 2 > view.byteLength) { return r; }
      var n = u16(offset);
      for (var i = 0; i < n; i++) {
        var e = offset + 2 + i * 12;
        if (tiff + e + 12 > view.byteLength) { break; }
        var tag = u16(e), type = u16(e + 2), count = u32(e + 4);
        var name = wanted[tag];
        if (!name) { continue; }
        var vo = count * (SIZES[type] || 1) > 4 ? u32(e + 8) : e + 8;
        if (tiff + vo >= view.byteLength) { continue; }
        if (type === 2) { r[name] = ascii(view, tiff + vo, count); }
        else if (type === 5 || type === 10) { r[name] = []; for (var k = 0; k < count; k++) { r[name].push(rational(vo + 8 * k)); } }
        else if (type === 3) { r[name] = u16(vo); }
        else if (type === 4) { r[name] = u32(vo); }
        else if (type === 1 || type === 7) { r[name] = view.getUint8(tiff + vo); }
      }
      return r;
    }
    var ifd0 = ifd(u32(4), { 0x10f: "make", 0x110: "model", 0x132: "dateTime", 0x8769: "exif", 0x8825: "gps" });
    out.cameraMake = (ifd0.make || "").trim() || undefined;
    out.cameraModel = (ifd0.model || "").trim() || undefined;
    var exif = ifd0.exif ? ifd(ifd0.exif, { 0x9003: "taken", 0xa002: "width", 0xa003: "height" }) : {};
    out.takenLocal = normalizeTime(exif.taken || ifd0.dateTime);
    out.width = num(exif.width);
    out.height = num(exif.height);
    if (ifd0.gps) {
      var g = ifd(ifd0.gps, { 1: "latRef", 2: "lat", 3: "lngRef", 4: "lng", 5: "altRef", 6: "alt" });
      var dms = function (a) { return a && a.length === 3 && a.every(isFinite) ? a[0] + a[1] / 60 + a[2] / 3600 : undefined; };
      var lat = dms(g.lat), lng = dms(g.lng);
      if (lat !== undefined && lng !== undefined && !(lat === 0 && lng === 0)) {
        out.latitude = /S/i.test(g.latRef || "") ? -lat : lat;
        out.longitude = /W/i.test(g.lngRef || "") ? -lng : lng;
        if (g.alt && isFinite(g.alt[0])) { out.absAltitudeM = Math.round((g.altRef === 1 ? -g.alt[0] : g.alt[0]) * 100) / 100; }
      }
    }
  }

  function readXmp(text, out) {
    function attr(name) {
      var m = new RegExp("drone-dji:" + name + "\\s*=\\s*\"([^\"]*)\"").exec(text) ||
              new RegExp("<drone-dji:" + name + ">([^<]*)<").exec(text);
      return m ? m[1].trim() : undefined;
    }
    function f(name) { var v = parseFloat(attr(name)); return isFinite(v) ? v : undefined; }
    var lat = f("GpsLatitude") !== undefined ? f("GpsLatitude") : f("Latitude");
    var lng = f("GpsLongitude") !== undefined ? f("GpsLongitude") : f("GpsLongtitude") !== undefined ? f("GpsLongtitude") : f("Longitude");
    if (lat !== undefined && lng !== undefined && !(lat === 0 && lng === 0)) {
      // DJI's own reading wins over EXIF (the Mini 3 Pro leaves the EXIF GPS block empty).
      out.latitude = lat;
      out.longitude = lng;
    }
    out.relAltitudeM = f("RelativeAltitude");
    if (f("AbsoluteAltitude") !== undefined) { out.absAltitudeM = f("AbsoluteAltitude"); }
    out.gimbalYaw = f("GimbalYawDegree");
    out.gimbalPitch = f("GimbalPitchDegree");
    out.gpsStatus = attr("GpsStatus");
    var model = attr("DroneModel");
    if (model && !out.cameraModel) { out.cameraModel = model; }
    if (/drone-dji:/.test(text) && !out.cameraMake) { out.cameraMake = "DJI"; }
  }

  function inspectJpeg(file) {
    return slice(file, 0, Math.min(file.size, JPEG_HEAD)).then(function (buf) {
      var view = new DataView(buf), out = {};
      if (view.byteLength < 4 || view.getUint16(0) !== 0xFFD8) { return out; }
      var off = 2;
      while (off + 4 <= view.byteLength) {
        if (view.getUint8(off) !== 0xFF) { break; }
        var marker = view.getUint8(off + 1);
        if (marker === 0xDA || marker === 0xD9) { break; }   // image data starts: no more metadata
        var len = view.getUint16(off + 2);
        var body = off + 4;
        if (marker === 0xE1) {
          if (ascii(view, body, 4) === "Exif") {
            try { readExif(view, body + 6, out); } catch (e) { /* damaged EXIF: keep what was read */ }
          } else if (ascii(view, body, 28) === "http://ns.adobe.com/xap/1.0/") {
            readXmp(utf8(view, body + 29, len - 31), out);
          }
        }
        off += 2 + len;
      }
      return out;
    });
  }

  // ---------------------------------------------------------------- MP4 / MOV

  function boxes(view, start, end) {
    var list = [], o = start;
    while (o + 8 <= end) {
      var size = view.getUint32(o), type = ascii(view, o + 4, 4), header = 8;
      if (size === 1) { size = Number(view.getBigUint64 ? view.getBigUint64(o + 8) : 0); header = 16; }
      if (size === 0) { size = end - o; }
      if (size < 8 || o + size > end) { break; }
      list.push({ type: type, start: o, body: o + header, end: o + size });
      o += size;
    }
    return list;
  }

  function child(view, box, path) {
    var cur = [box];
    path.forEach(function (t) {
      var next = [];
      cur.forEach(function (b) { boxes(view, b.body, b.end).forEach(function (c) { if (c.type === t) { next.push(c); } }); });
      cur = next;
    });
    return cur;
  }

  // ISO 6709 "+38.5074+023.3691+012.3/" (phones) -> lat, lng
  function iso6709(text, out) {
    var m = /([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)/.exec(text || "");
    if (!m) { return; }
    var lat = parseFloat(m[1]), lng = parseFloat(m[2]);
    if (Math.abs(lat) <= 90 && Math.abs(lng) <= 180 && !(lat === 0 && lng === 0)) { out.latitude = lat; out.longitude = lng; }
  }

  function readMoov(view, out, fileSize) {
    var root = { body: 0, end: view.byteLength };
    var mvhd = child(view, root, ["moov", "mvhd"])[0];
    if (mvhd) {
      var v = view.getUint8(mvhd.body);
      var scale = view.getUint32(mvhd.body + (v ? 20 : 12));
      var dur = v ? Number(view.getBigUint64(mvhd.body + 24)) : view.getUint32(mvhd.body + 16);
      var created = v ? Number(view.getBigUint64(mvhd.body + 4)) : view.getUint32(mvhd.body + 4);
      if (scale) {
        out.durationS = Math.round(dur / scale * 1000) / 1000;
        if (out.durationS > 0) { out.bitrateKbps = Math.round(fileSize * 8 / out.durationS / 1000); }
      }
      if (created > 2082844800) { // seconds since 1904; MP4 creation times are UTC
        out.takenLocal = normalizeTime(new Date((created - 2082844800) * 1000).toISOString());
      }
    }
    child(view, root, ["moov", "trak"]).forEach(function (trak) {
      var hdlr = child(view, trak, ["mdia", "hdlr"])[0];
      if (!hdlr || ascii(view, hdlr.body + 8, 4) !== "vide" || out.videoCodec) { return; }
      var stsd = child(view, trak, ["mdia", "minf", "stbl", "stsd"])[0];
      if (stsd) {
        out.videoCodec = ascii(view, stsd.body + 12, 4);
        out.width = view.getUint16(stsd.body + 8 + 32);
        out.height = view.getUint16(stsd.body + 8 + 34);
      }
    });
    // Phones: udta/©xyz (Android, older iOS) or meta keys "com.apple.quicktime.location.ISO6709".
    var udta = child(view, root, ["moov", "udta"])[0];
    if (udta) {
      boxes(view, udta.body, udta.end).forEach(function (b) {
        if (view.getUint8(b.start + 4) === 0xA9 && ascii(view, b.start + 5, 3) === "xyz") { iso6709(ascii(view, b.body + 4, b.end - b.body - 4), out); }
        // Samsung phones: "auth" = the model name ("Galaxy Z Flip6"), "smta" = Samsung's own tags.
        if (b.type === "auth" && !out.cameraModel) {
          var model = utf8(view, b.body + 6, b.end - b.body - 6).replace(/[\u0000-\u001f]/g, "").trim();
          if (model) { out.cameraModel = model; }
        }
        if (b.type === "smta" && !out.cameraMake) { out.cameraMake = "samsung"; }
      });
    }
    var meta = child(view, root, ["moov", "meta"])[0];
    if (meta && out.latitude === undefined) {
      var raw = utf8(view, meta.body, meta.end - meta.body);
      var at = raw.indexOf("location.ISO6709");
      if (at >= 0) { iso6709(raw.slice(at), out); }
      var make = /com\.apple\.quicktime\.make[\s\S]{0,40}?data[\s\S]{8}([A-Za-z][\w ]{1,30})/.exec(raw);
      if (make && !out.cameraMake) { out.cameraMake = make[1].trim(); }
    }

  }

  function inspectMp4(file) {
    var out = {};
    var mdatAt = -1;
    function walk(offset, steps) {
      if (offset + 8 > file.size || steps > 50) { return Promise.resolve(null); }
      return slice(file, offset, Math.min(file.size, offset + 16)).then(function (buf) {
        var view = new DataView(buf);
        if (view.byteLength < 8) { return null; }
        var size = view.getUint32(0), type = ascii(view, 4, 4);
        if (size === 1 && view.byteLength >= 16) { size = Number(view.getBigUint64(8)); }
        if (size === 0) { size = file.size - offset; }
        if (size < 8) { return null; }
        if (type === "mdat" && mdatAt < 0) { mdatAt = offset; }
        if (type === "moov") {
          if (size > MAX_MOOV) { return null; }
          out.indexAtEnd = mdatAt >= 0 && mdatAt < offset;
          return slice(file, offset, offset + size).then(function (moov) { return new DataView(moov); });
        }
        return walk(offset + size, steps + 1);
      });
    }
    return walk(0, 0).then(function (moov) {
      if (moov) { try { readMoov(moov, out, file.size); } catch (e) { /* damaged index: keep what was read */ } }
      return out;
    });
  }

  // ---------------------------------------------------------------- DJI .srt flight track

  function parseSrt(text) {
    var points = [], takenLocal, next = 0;
    // Each block: index, "00:00:01,033 --> 00:00:01,066", then text with the position.
    var blocks = String(text || "").replace(/\r/g, "").split(/\n\s*\n/);
    blocks.forEach(function (block) {
      var t = /(\d+):(\d\d):(\d\d)[,.](\d{1,3})\s*-->/.exec(block);
      if (!t) { return; }
      var ms = ((+t[1] * 60 + +t[2]) * 60 + +t[3]) * 1000 + +(t[4] + "00").slice(0, 3);
      if (!takenLocal) { takenLocal = normalizeTime(block); }
      if (ms < next) { return; }
      var lat, lng, alt;
      // "longtitude": older DJI drones (Mavic Air 2, Mavic 2, Mini SE) spell it so; the Mini 3 Pro writes "longitude".
      var a = /latitude\s*:\s*(-?[\d.]+)\]?[\s\S]*?longt?itude\s*:\s*(-?[\d.]+)/i.exec(block);
      if (a) { lat = parseFloat(a[1]); lng = parseFloat(a[2]); }
      else {
        // Older DJI: "GPS(23.3691,38.5074,19)" = longitude, latitude, satellites/altitude
        var g = /GPS\s*\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)/i.exec(block);
        if (g) { lng = parseFloat(g[1]); lat = parseFloat(g[2]); }
      }
      var h = /rel_alt\s*:\s*(-?[\d.]+)/i.exec(block) || /\bH\s*[:(]?\s*(-?[\d.]+)\s*m/i.exec(block) || /\[altitude\s*:\s*(-?[\d.]+)/i.exec(block);
      if (h) { alt = parseFloat(h[1]); }
      if (!isFinite(lat) || !isFinite(lng) || (lat === 0 && lng === 0) || Math.abs(lat) > 90 || Math.abs(lng) > 180) { return; }
      points.push([ms, lat, lng, isFinite(alt) ? alt : null]);
      next = ms + TRACK_STEP_MS;
    });
    return { takenLocal: takenLocal, points: points };
  }

  // ---------------------------------------------------------------- entry point

  function kindOf(file) {
    var name = (file.name || "").toLowerCase();
    if (/\.srt$/.test(name)) { return "track"; }
    if (/^image\/jpe?g$/.test(file.type) || /\.jpe?g$/.test(name)) { return "jpeg"; }
    if (/^video\//.test(file.type) || /\.(mp4|mov|m4v)$/.test(name)) { return "video"; }
    if (/^image\//.test(file.type)) { return "image"; }
    return "other";
  }

  function inspect(file) {
    var kind = kindOf(file);
    var base = {};
    if (file.lastModified) { base.sourceModifiedUtc = new Date(file.lastModified).toISOString(); }
    var read = kind === "jpeg" ? inspectJpeg(file) : kind === "video" ? inspectMp4(file) : Promise.resolve({});
    return read.catch(function () { return {}; }).then(function (m) {
      Object.keys(m).forEach(function (k) { base[k] = m[k]; });
      ["latitude", "longitude", "relAltitudeM", "absAltitudeM", "gimbalYaw", "gimbalPitch", "durationS", "width", "height", "bitrateKbps"]
        .forEach(function (k) { base[k] = num(base[k]); });
      return clean(base);
    });
  }

  /** The name without extension, lower case: DJI_0898.MP4 and DJI_0898.SRT pair up. */
  function baseName(name) { return String(name || "").replace(/\.[^.]*$/, "").toLowerCase(); }

  Nadlan.mediaInspect = { inspect: inspect, parseSrt: parseSrt, kindOf: kindOf, baseName: baseName };
})(window);
