// In-house analytics, the page side (migration 026, owner 2026-11-10): a generic event tracker. For now only show.html
// (a public presentation link) starts it; anywhere else every call does nothing.
//   Nadlan.events.start({ source: "present_public", endpoint: "/api/present-link/events", headers: { "X-Present-Link": token }, scope: token });
//   Nadlan.events.track("asset_viewed", ["Asset", 97], { via: "chip" });
// visitorId: random, kept in localStorage (one browser over time); sessionId: random per tab and link, renewed after 30
// minutes idle (sessionStorage). Events wait in a queue in localStorage (a closed tab sends them at the next visit), go
// every 10 s and when the page is hidden or closed (fetch keepalive - sendBeacon can't send our headers). Each batch has
// its own id and is sent again until the server answers; a 4xx it will never take is dropped, never blocking the rest.
// No cookies of its own, no third-party scripts. It must never break the page: every entry point swallows its errors.
(function (window, document) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var SEND_EVERY_MS = 10000;
  var IDLE_MS = 30 * 60 * 1000;
  var MAX_BATCH_EVENTS = 100; // the server takes 200
  var MAX_BATCHES = 50;       // a browser offline for long: the oldest go first
  var VISITOR_KEY = "nadlan.visitor";

  function storage(name) {
    try {
      var s = window[name], k = "nadlan.probe";
      s.setItem(k, "1");
      s.removeItem(k);
      return s;
    } catch (e) { return null; } // blocked (private mode, settings): the tracker works for this page only
  }

  var local = storage("localStorage"), tab = storage("sessionStorage");
  var cfg = null, scopeKey = "", sending = false;
  var memory = { visitor: null, session: null, queue: null };
  var leaveHooks = [];

  function now() { return Date.now(); }

  function randomId(prefix) {
    var hex = "";
    try {
      var bytes = window.crypto.getRandomValues(new Uint8Array(12));
      for (var i = 0; i < bytes.length; i++) { hex += ("0" + bytes[i].toString(16)).slice(-2); }
    } catch (e) {
      for (var j = 0; j < 24; j++) { hex += Math.floor(Math.random() * 16).toString(16); }
    }
    return prefix + "-" + hex;
  }

  // A short name for the link (FNV-1a) so the queue and the session are per link without keeping its token.
  function hash(text) {
    var h = 0x811c9dc5;
    for (var i = 0; i < text.length; i++) { h ^= text.charCodeAt(i); h = (h * 0x01000193) >>> 0; }
    return h.toString(36);
  }

  function read(store, key) {
    if (!store) { return null; }
    try { return JSON.parse(store.getItem(key) || "null"); } catch (e) { return null; }
  }

  function write(store, key, value) {
    if (!store) { return; }
    try { store.setItem(key, JSON.stringify(value)); } catch (e) { /* full or blocked: this page's memory only */ }
  }

  function visitorId() {
    if (memory.visitor) { return memory.visitor; }
    var v = null;
    if (local) { try { v = local.getItem(VISITOR_KEY); } catch (e) { v = null; } }
    if (!v || !/^[A-Za-z0-9-]{8,40}$/.test(v)) {
      v = randomId("v");
      if (local) { try { local.setItem(VISITOR_KEY, v); } catch (e) { /* this page only */ } }
    }
    memory.visitor = v;
    return v;
  }

  // The tab's visit of this link: { id, last, contextSent }; a new one after 30 minutes without an event.
  function session() {
    var key = "nadlan.session." + scopeKey;
    var s = read(tab, key) || memory.session;
    if (!s || !s.id || now() - (s.last || 0) > IDLE_MS) {
      s = { id: randomId("s"), last: now(), contextSent: false };
    }
    s.last = now();
    memory.session = s;
    write(tab, key, s);
    return s;
  }

  // { open: [events not in a batch yet, each with its session], batches: [{ id, sessionId, visitorId, context, events }] }
  function queue() {
    if (!memory.queue) {
      var q = read(local, "nadlan.events." + scopeKey);
      memory.queue = q && q.open && q.batches ? q : { open: [], batches: [] };
    }
    return memory.queue;
  }

  function save() { write(local, "nadlan.events." + scopeKey, queue()); }

  function pageContext() {
    var c = {};
    try {
      c.screen = [window.screen.width, window.screen.height];
      c.viewport = [window.innerWidth, window.innerHeight];
      c.dpr = window.devicePixelRatio || 1;
      var o = window.screen.orientation && window.screen.orientation.type;
      c.orientation = o ? (o.indexOf("portrait") === 0 ? "portrait" : "landscape") : (window.innerHeight >= window.innerWidth ? "portrait" : "landscape");
      c.touch = (navigator.maxTouchPoints || 0) > 0 || "ontouchstart" in window;
      c.lang = navigator.language || null;
      c.langs = (navigator.languages || []).slice(0, 10);
      try { c.tz = Intl.DateTimeFormat().resolvedOptions().timeZone || null; } catch (e) { c.tz = null; }
      c.referrer = document.referrer || null;
      var q = new URLSearchParams(window.location.search), utm = {};
      ["source", "medium", "campaign"].forEach(function (k) { if (q.get("utm_" + k)) { utm[k] = q.get("utm_" + k); } });
      if (utm.source || utm.medium || utm.campaign) { c.utm = utm; }
    } catch (e) { /* whatever was read */ }
    return c;
  }

  // The open events into batches: one per session, at most 100 events; the session's first carries the page's context.
  function seal() {
    var q = queue();
    if (!q.open.length) { return; }
    var current = memory.session;
    var bySession = {};
    q.open.forEach(function (e) { (bySession[e.sid] = bySession[e.sid] || []).push({ e: e.e, t: e.t, s: e.s, p: e.p }); });
    Object.keys(bySession).forEach(function (sid) {
      var list = bySession[sid];
      for (var i = 0; i < list.length; i += MAX_BATCH_EVENTS) {
        var b = { id: randomId("b"), sessionId: sid, visitorId: visitorId(), events: list.slice(i, i + MAX_BATCH_EVENTS) };
        if (current && current.id === sid && !current.contextSent) {
          b.context = pageContext();
          current.contextSent = true;
          write(tab, "nadlan.session." + scopeKey, current);
        }
        q.batches.push(b);
      }
    });
    q.open = [];
    if (q.batches.length > MAX_BATCHES) { q.batches = q.batches.slice(q.batches.length - MAX_BATCHES); }
  }

  function body(b) {
    var out = { batchId: b.id, sentAt: now(), source: cfg.source, sessionId: b.sessionId, visitorId: b.visitorId, events: b.events };
    if (b.context) { out.context = b.context; }
    return JSON.stringify(out);
  }

  function post(b, keepalive) {
    var headers = { "Content-Type": "application/json", "X-Nadlan-Request": "1" };
    Object.keys(cfg.headers || {}).forEach(function (k) { headers[k] = cfg.headers[k]; });
    return window.fetch(cfg.endpoint, { method: "POST", credentials: "same-origin", keepalive: !!keepalive, headers: headers, body: body(b) });
  }

  // 2xx: stored (or a repeat). 4xx the server will never take (bad batch, inactive link, too big): dropped. 401 (the
  // password again), 429 (slow down), 5xx and no network: kept for the next round.
  function finished(status) { return (status >= 200 && status < 300) || (status >= 400 && status < 500 && status !== 401 && status !== 429); }

  function done(id) {
    var q = queue();
    q.batches = q.batches.filter(function (b) { return b.id !== id; });
    save();
  }

  // One batch at a time, oldest first; stops at the first failure and tries again on the next round.
  function send() {
    if (!cfg || sending || !queue().batches.length || !window.fetch) { return; }
    var b = queue().batches[0];
    sending = true;
    post(b, false).then(function (res) {
      sending = false;
      if (finished(res.status)) { done(b.id); send(); }
    }, function () { sending = false; });
  }

  function flush() {
    try {
      seal();
      save();
      send();
    } catch (e) { /* never break the page */ }
  }

  // The page goes away (another app, screen off, closed): what it adds now (time on screen) first, then everything is
  // kept and sent with keepalive (the browser finishes it after the page is gone).
  function leave() {
    if (!cfg) { return; }
    leaveHooks.forEach(function (fn) { try { fn(); } catch (e) { /* the next hook */ } });
    try {
      seal();
      save();
      if (!window.fetch) { return; }
      queue().batches.slice(0, 10).forEach(function (b) { // keepalive bodies share 64 KB
        post(b, true).then(function (res) { if (finished(res.status)) { done(b.id); } }, function () { /* next time */ });
      });
    } catch (e) { /* never break the page */ }
  }

  // ---- time on screen ------------------------------------------------------------------------------------------

  var visibleTotal = 0, visibleSince = document.visibilityState === "hidden" ? null : now();

  function visibleMs() { return visibleTotal + (visibleSince !== null ? now() - visibleSince : 0); }

  // Registered first (this script loads before the page's), so the page's own handlers already read the right time.
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "hidden") {
      if (visibleSince !== null) { visibleTotal += now() - visibleSince; visibleSince = null; }
      leave();
    } else if (visibleSince === null) {
      visibleSince = now();
    }
  });
  // Closed or navigated away while still visible (no visibilitychange first).
  window.addEventListener("pagehide", function () { if (document.visibilityState !== "hidden") { leave(); } });

  /** Seconds on screen since it was made (or last reset): { seconds(), reset() }. */
  function stopwatch() {
    var at = visibleMs();
    return {
      seconds: function () { return Math.round((visibleMs() - at) / 100) / 10; },
      reset: function () { at = visibleMs(); }
    };
  }

  // ---- the API -------------------------------------------------------------------------------------------------

  Nadlan.events = {
    /** @param {{ source: string, endpoint: string, headers?: Object, scope?: string }} options */
    start: function (options) {
      try {
        if (cfg || !options || !options.source || !options.endpoint) { return; }
        cfg = { source: options.source, endpoint: options.endpoint, headers: options.headers || {} };
        scopeKey = hash(options.source + "|" + (options.scope || options.endpoint));
        session();
        save();
        send(); // what an earlier visit left unsent
        window.setInterval(flush, SEND_EVERY_MS);
      } catch (e) { cfg = null; }
    },
    /** name: snake_case; subject: ["Asset", 97] or null; props: a small object or null. */
    track: function (name, subject, props) {
      try {
        if (!cfg) { return; }
        var s = session();
        queue().open.push({ sid: s.id, e: name, t: now(), s: subject || null, p: props || null });
        save();
      } catch (e) { /* never break the page */ }
    },
    /** Runs just before the page is hidden or closed (and before the events are sent): the page adds its time there. */
    beforeLeave: function (fn) { if (typeof fn === "function") { leaveHooks.push(fn); } },
    stopwatch: stopwatch,
    flush: flush,
    started: function () { return !!cfg; },
    pending: function () {
      try { var q = queue(); return q.open.length + q.batches.reduce(function (n, b) { return n + b.events.length; }, 0); } catch (e) { return 0; }
    }
  };
})(window, document);
