// The contractor's location (piece 3, owner 2026-11-01): only after the contractor agreed on this phone, only while the
// page is open and visible (a web page can't track in the background). Each fix goes to the page (the blue dot, "Where
// am I", the 100 m warning, "Work on parcel"). Nothing goes to the server from here: the track is recorded only while
// "Work on parcel" is On (work-link-track.js, owner 2026-11-03).
//   Nadlan.workLocation.consent()           -> "yes" | "no" | null (not asked yet on this phone, for this link)
//   Nadlan.workLocation.setConsent(answer)
//   Nadlan.workLocation.start(onFix, onError), stop(), last() -> { lat, lng, accuracy, time } | null
//   Nadlan.workLocation.storageKey() -> Promise of this link's key prefix on this phone (null without hashing)
(function (window, document) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var watchId = null, wanted = false, handlers = null;
  var last = null, keyPromise = null;

  // The answer is kept per link on this phone: named after the token's hash, never the token itself.
  function storageKey() {
    if (!keyPromise) {
      var token = (window.location.hash || "").replace(/^#/, "");
      keyPromise = window.crypto && window.crypto.subtle && window.TextEncoder
        ? window.crypto.subtle.digest("SHA-256", new TextEncoder().encode(token)).then(function (buf) {
            return "nadlan.workLink.location." + Array.prototype.map.call(new Uint8Array(buf).slice(0, 8), function (b) { return ("0" + b.toString(16)).slice(-2); }).join("");
          })
        : Promise.resolve(null); // no hashing here (plain http): asked each time
    }
    return keyPromise;
  }

  function consent() {
    return storageKey().then(function (key) {
      if (!key) { return null; }
      try { return window.localStorage.getItem(key); } catch (e) { return null; }
    });
  }

  function setConsent(answer) {
    return storageKey().then(function (key) {
      if (key) { try { window.localStorage.setItem(key, answer); } catch (e) { /* private mode: asked again next time */ } }
    });
  }

  function watch() {
    if (watchId !== null || !navigator.geolocation) { return; }
    watchId = navigator.geolocation.watchPosition(function (pos) {
      last = { lat: pos.coords.latitude, lng: pos.coords.longitude, accuracy: pos.coords.accuracy || 9999, time: Date.now() };
      if (handlers) { handlers.onFix(last); }
    }, function (err) {
      if (handlers && handlers.onError) { handlers.onError(err); }
    }, { enableHighAccuracy: true, maximumAge: 10000, timeout: 30000 });
  }

  function unwatch() {
    if (watchId !== null && navigator.geolocation) { navigator.geolocation.clearWatch(watchId); }
    watchId = null;
  }

  // Hidden (another app, screen off): stop; back: go on.
  document.addEventListener("visibilitychange", function () {
    if (!wanted) { return; }
    if (document.visibilityState === "visible") { watch(); } else { unwatch(); }
  });

  Nadlan.workLocation = {
    supported: function () { return !!navigator.geolocation; },
    consent: consent,
    storageKey: storageKey,
    setConsent: setConsent,
    start: function (onFix, onError) {
      handlers = { onFix: onFix, onError: onError };
      wanted = true;
      if (document.visibilityState !== "hidden") { watch(); }
    },
    stop: function () { wanted = false; unwatch(); },
    active: function () { return wanted; },
    last: function () { return last; }
  };
})(window, document);
