// The contractor's track while "Work on parcel" is On (owner, 2026-11-03). Recorded here and kept on the phone until
// the server has it: about every minute what was recorded becomes a batch with its own id and is sent; a batch with no
// answer is sent again (the server adds it once), and one left by a closed page goes the next time the link is opened.
// What is recorded, per parcel: the events Start / Stop / out (stepped out of the parcel) / in (back) / far (1 km away -
// the track ends) with their times, and between them a point every 5 s at most, none within 2 m of the last one.
//   Nadlan.workTrack.init()                                 - after the link is open: sends what an earlier visit left
//   Nadlan.workTrack.event(name, parcelId, fix, outside)    - always kept (with the place when the fix is good enough)
//   Nadlan.workTrack.point(parcelId, fix, outside)          - kept when the rules above allow
//   Nadlan.workTrack.flush()                                - seal and send now
(function (window, document) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var api = Nadlan.workLinkApi;

  var EVERY_MS = 5000;
  var MIN_MOVE_M = 2;
  var SEND_EVERY_MS = 60 * 1000;
  var MAX_ACCURACY_M = 100;
  var BATCH_MAX = 500;          // a batch is about a minute (~12 entries); an offline day goes in slices

  var state = { open: [], batches: [] }; // not sent yet: what is being collected, and sealed batches { id, entries }
  var key = null, started = false, sending = false, lastKept = null;

  function save() {
    if (!key) { return; }
    try { window.localStorage.setItem(key, JSON.stringify(state)); } catch (e) { /* full or private: kept in memory */ }
  }

  function newId() {
    if (window.crypto && window.crypto.randomUUID) { return window.crypto.randomUUID(); }
    return Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 12);
  }

  function metres(a, b) {
    var kx = 111320 * Math.cos(a.lat * Math.PI / 180);
    var dx = (b.lng - a.lng) * kx, dy = (b.lat - a.lat) * 110574;
    return Math.sqrt(dx * dx + dy * dy);
  }

  function round(n, digits) { var f = Math.pow(10, digits); return Math.round(n * f) / f; }

  function entry(name, parcelId, fix, outside) {
    var e = { t: Date.now(), p: parcelId, out: outside ? 1 : 0 };
    if (name) { e.e = name; }
    if (fix && fix.accuracy <= MAX_ACCURACY_M) {
      e.lat = round(fix.lat, 7);
      e.lng = round(fix.lng, 7);
      e.acc = round(fix.accuracy, 1);
    }
    return e;
  }

  function add(e) {
    state.open.push(e);
    if (e.lat !== undefined) { lastKept = { lat: e.lat, lng: e.lng, t: e.t }; }
    if (state.open.length >= BATCH_MAX) { seal(); }
    save();
  }

  function seal() {
    if (!state.open.length) { return; }
    state.batches.push({ id: newId(), entries: state.open });
    state.open = [];
  }

  function done(id) {
    state.batches = state.batches.filter(function (b) { return b.id !== id; });
    save();
  }

  // One batch at a time, oldest first; stops at the first failure (no signal) and tries again on the next round.
  function send() {
    if (sending || !started || !state.batches.length) { return; }
    var b = state.batches[0];
    sending = true;
    api.post("/track", { batchId: b.id, sentAt: Date.now(), entries: b.entries }).then(function () {
      sending = false;
      done(b.id);
      send();
    }, function (err) {
      sending = false;
      if (err && err.status === 400) { done(b.id); send(); } // the server will never take it: don't block the rest
    });
  }

  function flush() {
    seal();
    save();
    send();
  }

  // The page goes away (another app, screen off, closed): keep everything on the phone and try to send it now.
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState !== "hidden") { return; }
    seal();
    save();
    if (!started || !api.postKeepalive) { return; }
    state.batches.forEach(function (b) {
      api.postKeepalive("/track", { batchId: b.id, sentAt: Date.now(), entries: b.entries }).then(function (res) {
        if (res.ok || res.status === 400) { done(b.id); }
      }, function () { /* next time */ });
    });
  });

  function init() {
    if (started) { return; }
    started = true;
    var keyed = Nadlan.workLocation && Nadlan.workLocation.storageKey ? Nadlan.workLocation.storageKey() : Promise.resolve(null);
    keyed.then(function (k) {
      key = k ? k.replace(".location.", ".track.") : null; // per link on this phone, like the location answer
      var left = null;
      if (key) { try { left = JSON.parse(window.localStorage.getItem(key) || "null"); } catch (e) { left = null; } }
      if (left && left.batches) {
        // An earlier visit's leftovers first; what this visit recorded meanwhile after them.
        state.batches = left.batches.concat(left.open && left.open.length ? [{ id: newId(), entries: left.open }] : [], state.batches);
      }
      save();
      send();
    });
    window.setInterval(flush, SEND_EVERY_MS);
  }

  Nadlan.workTrack = {
    init: init,
    event: function (name, parcelId, fix, outside) {
      add(entry(name, parcelId, fix, outside));
      if (name === "stop" || name === "far") { lastKept = null; flush(); }
    },
    point: function (parcelId, fix, outside) {
      if (!fix || fix.accuracy > MAX_ACCURACY_M) { return; }
      if (lastKept && (Date.now() - lastKept.t < EVERY_MS || metres(lastKept, fix) < MIN_MOVE_M)) { return; }
      add(entry(null, parcelId, fix, outside));
    },
    flush: flush,
    pending: function () { return state.open.length + state.batches.reduce(function (n, b) { return n + b.entries.length; }, 0); }
  };
})(window, document);
