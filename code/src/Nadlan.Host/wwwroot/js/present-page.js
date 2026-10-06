// Customer presentation page (present.html): ?portfolio=12 or ?assets=5,9,3 (that order).
// Loads what the signed-in viewer may see (GET /api/presentation), shows it in Google's photorealistic 3D
// (present-views.js; satellite map if 3D isn't available) with an info card, a strip of all items and a guided tour.
(function (window, document, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var esc = Nadlan.format.escapeHtml;
  var ORBIT_MS = 16000;

  var $title = $("[data-role=title]"), $sub = $("[data-role=sub]"), $card = $("[data-role=card]");
  var $strip = $("[data-role=strip]"), $notice = $("[data-role=notice]"), $tour = $("[data-act=tour]");

  function notice(text, isError) {
    $notice.text(text || "").toggleClass("error", !!isError).prop("hidden", !text);
  }

  // ?portfolio=12 | ?assets=5,9,3
  function readQuery() {
    var q = new URLSearchParams(window.location.search);
    var portfolio = Number(q.get("portfolio"));
    if (portfolio > 0) { return { portfolioId: portfolio }; }
    var ids = (q.get("assets") || "").split(",").map(function (s) { return Number(s.trim()); }).filter(function (n) { return n > 0; });
    return ids.length ? { assetIds: ids } : null;
  }

  function loadGoogle(apiKey, channel) {
    return new Promise(function (resolve, reject) {
      if (!apiKey) { reject(new Error("The Google Maps key is not configured (Nadlan:Maps:GoogleApiKey).")); return; }
      window.__nadlanPresentReady = resolve;
      window.gm_authFailure = function () { notice("Google rejected the Maps key for " + window.location.origin + ".", true); };
      var v = /^[a-z0-9.]+$/.test(channel || "") ? channel : "beta";
      var script = document.createElement("script");
      script.src = "https://maps.googleapis.com/maps/api/js?key=" + encodeURIComponent(apiKey) + "&v=" + v + "&loading=async&callback=__nadlanPresentReady";
      script.async = true;
      script.onerror = function () { reject(new Error("Google Maps failed to load.")); };
      document.head.appendChild(script);
    });
  }

  // 3D when Google serves it for this key and browser; otherwise the satellite map, said once in a small notice.
  function createView(items, onPick) {
    var stage = document.getElementById("stage");
    return google.maps.importLibrary("maps3d").then(function (lib) {
      if (!lib || !lib.Map3DElement) { throw new Error("no 3D"); }
      return Nadlan.presentViews.create3dView(lib, stage, items, { onPick: onPick });
    }).catch(function () {
      return Promise.all([google.maps.importLibrary("maps"), google.maps.importLibrary("marker")]).then(function () {
        notice("3D view isn't available here - showing the satellite map.");
        setTimeout(function () { notice(""); }, 6000);
        return Nadlan.presentViews.create2dView(stage, items, { onPick: onPick });
      });
    });
  }

  function price(it) {
    if (it.askPrice === null || it.askPrice === undefined) { return "Price on request"; }
    try {
      return new Intl.NumberFormat("en-US", { style: "currency", currency: it.currencyCode || "EUR", maximumFractionDigits: 0 }).format(it.askPrice);
    } catch (e) { return Number(it.askPrice).toLocaleString("en-US") + " " + esc(it.currencyCode || ""); }
  }

  function sqm(v) { return v === null || v === undefined ? null : Number(v).toLocaleString("en-US", { maximumFractionDigits: 0 }) + " m²"; }

  function heading(it) { return (it.propertyType || "Property") + (it.area ? " · " + it.area : ""); }

  function thumbOf(m) { return m.thumbUrl || (m.kind === "image" ? m.url : null); }

  // One round button per photo/video, opening file-card.js's full-screen viewer.
  function mediaButton(m, cls) {
    var thumb = thumbOf(m);
    return "<button type=\"button\" class=\"" + cls + "\" data-view-file=\"" + esc(m.kind) + "\" data-url=\"" + esc(m.url) + "\" data-name=\"" + esc(m.caption || "") + "\"" +
      (thumb ? " style=\"background-image:url('" + esc(thumb).replace(/'/g, "%27") + "')\"" : "") + " title=\"" + (m.kind === "video" ? "Play" : "View") + "\">" +
      (m.kind === "video" ? "<span class=\"pres-play\">▶</span>" : "") + "</button>";
  }

  function cardHtml(it, index, count) {
    var facts = [
      ["Asking", price(it)],
      ["Plot", sqm(it.plotSqm)],
      ["House", sqm(it.houseSqm)],
      ["KAEK", it.kaek ? esc(it.kaek) : null]
    ].filter(function (f) { return f[1]; });
    var media = it.media || [];
    return "<div class=\"pres-card-head\"><span class=\"pres-num\">" + (index + 1) + "</span><span class=\"muted\">" + (index + 1) + " of " + count + "</span>" +
        "<button type=\"button\" class=\"pres-close\" data-act=\"close-card\" title=\"Hide\">×</button></div>" +
      (media.length ? mediaButton(media[0], "pres-hero") : "<div class=\"pres-hero pres-hero-empty\">No photos yet</div>") +
      "<h2>" + esc(heading(it)) + "</h2>" +
      "<span class=\"pres-status\" style=\"--c:" + (/^#[0-9a-fA-F]{6}$/.test(it.statusColor || "") ? it.statusColor : "#64748b") + "\">" + esc(it.statusName || "") + "</span>" +
      "<dl class=\"pres-facts\">" + facts.map(function (f) { return "<dt>" + f[0] + "</dt><dd>" + f[1] + "</dd>"; }).join("") + "</dl>" +
      (media.length > 1 ? "<div class=\"pres-gallery\">" + media.slice(1).map(function (m) { return mediaButton(m, "pres-thumb"); }).join("") + "</div>" : "");
  }

  function stripHtml(items) {
    return items.map(function (it, i) {
      var thumb = it.media && it.media.length ? thumbOf(it.media[0]) : null;
      return "<button type=\"button\" class=\"pres-chip\" data-index=\"" + i + "\">" +
        "<span class=\"pres-chip-img\"" + (thumb ? " style=\"background-image:url('" + esc(thumb).replace(/'/g, "%27") + "')\"" : "") + "><span class=\"pres-num\">" + (i + 1) + "</span></span>" +
        "<span class=\"pres-chip-text\"><strong>" + esc(heading(it)) + "</strong><span>" + price(it) + "</span></span></button>";
    }).join("");
  }

  function start(data, config) {
    var items = data.items || [];
    $title.text(data.title || (items.length === 1 ? heading(items[0]) : items.length + " properties"));
    $sub.text(data.description || (items.length + (items.length === 1 ? " property" : " properties")));
    document.title = (data.title || "Presentation") + " — GreekPlot";
    if (!items.length) {
      notice("Nothing to show: these properties don't exist, or they were not shared with you.", true);
      return;
    }

    $strip.html(stripHtml(items));
    var view = null, current = -1, touring = false, tourRun = 0;

    function select(i, fly) {
      current = (i + items.length) % items.length;
      $card.html(cardHtml(items[current], current, items.length)).prop("hidden", false);
      $strip.find(".pres-chip").removeClass("active").eq(current).addClass("active");
      var chip = $strip.find(".pres-chip")[current];
      if (chip && chip.scrollIntoView) { chip.scrollIntoView({ behavior: "smooth", inline: "center", block: "nearest" }); }
      if (view) { view.highlight(current); }
      return fly === false || !view ? Promise.resolve() : view.fly(current);
    }

    function stopTour() {
      touring = false;
      tourRun++;
      $tour.text("▶ Tour").removeClass("on");
      if (view) { view.stop(); }
    }

    // Fly to each in turn, circle it, go on to the next - until Pause, a click, or a key.
    function tour() {
      touring = true;
      var run = ++tourRun;
      $tour.text("❚❚ Pause").addClass("on");
      var i = current < 0 ? 0 : current;
      (function step() {
        if (!touring || run !== tourRun) { return; }
        select(i).then(function () {
          if (!touring || run !== tourRun) { return null; }
          return view.orbit(i, ORBIT_MS);
        }).then(function () {
          if (!touring || run !== tourRun) { return; }
          i = (i + 1) % items.length;
          step();
        });
      })();
    }

    function pick(i) { stopTour(); select(i); }

    // "Me": fly to where the viewer stands (a buyer walking the land); the pin then follows them. If they stand on one
    // of the properties, its card opens.
    var flyToMe = false;
    var $me = $("[data-act=me]");

    function arrive(p) {
      view.showMe(p, true);
      var on = p.accuracy <= 150 ? items.findIndex(function (it) {
        return it.polygons.some(function (g) { return Nadlan.myLocation.containsPoint(g, p.lat, p.lng); });
      }) : -1;
      if (on >= 0) { select(on, false); notice("You're standing on " + heading(items[on]) + "."); }
      else { notice(p.accuracy > 150 ? "Your location is rough (± " + Math.round(p.accuracy) + " m)." : ""); }
      setTimeout(function () { notice(""); }, 6000);
    }

    var locator = Nadlan.myLocation.createLocator(function (p) {
      if (!view) { return; }
      $me.removeClass("busy").addClass("on");
      if (flyToMe) { flyToMe = false; arrive(p); } else { view.showMe(p, false); } // later fixes only move the pin
    }, function (message) {
      flyToMe = false;
      $me.removeClass("busy on");
      notice(message, true);
      setTimeout(function () { notice(""); }, 8000);
    });

    function showMe() {
      if (!view) { return; }
      stopTour();
      if (!locator.start()) { return; }
      var known = locator.last();
      if (known) { arrive(known); } else { flyToMe = true; $me.addClass("busy"); }
    }
    $me.on("click", showMe);

    $strip.on("click", ".pres-chip", function () { pick(Number($(this).data("index"))); });
    $("[data-act=next]").on("click", function () { pick(current + 1); });
    $("[data-act=prev]").on("click", function () { pick(current < 0 ? items.length - 1 : current - 1); });
    $("[data-act=overview]").on("click", function () { stopTour(); if (view) { view.highlight(-1); view.showAll(); } $card.prop("hidden", true); });
    $tour.on("click", function () { if (touring) { stopTour(); } else if (view) { tour(); } });
    $card.on("click", "[data-act=close-card]", function () { $card.prop("hidden", true); });
    $(document).on("keydown", function (e) {
      if ($(".media-viewer").length || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) { return; }
      if (e.key === "ArrowRight") { pick(current + 1); }
      else if (e.key === "ArrowLeft") { pick(current < 0 ? items.length - 1 : current - 1); }
      else if (e.key === " ") { e.preventDefault(); $tour.trigger("click"); }
      else if (e.key === "o" || e.key === "O") { $("[data-act=overview]").trigger("click"); }
      else if (e.key === "m" || e.key === "M") { showMe(); }
    });

    loadGoogle(config.googleMapsApiKey, config.maps3dChannel).then(function () {
      return createView(items, pick);
    }).then(function (v) {
      view = v;
      $("body").addClass("view-" + v.kind);
      select(0, false); // first card open; the camera starts on the whole set
    }).catch(function (err) { notice(err.message, true); });
  }

  var query = readQuery();
  if (!query) {
    $title.text("Presentation");
    notice("Open this page with ?portfolio=<id> or ?assets=<id>,<id>,… (from a Portfolio's “Present” button).", true);
    return;
  }

  Promise.all([Nadlan.api.get("/api/config/client"), Nadlan.api.get("/api/presentation", query)]).then(function (loaded) {
    start(loaded[1], loaded[0]);
  }, function (err) {
    $title.text("Presentation");
    notice(err.status === 404 ? "This Portfolio doesn't exist, or it was not shared with you." : err.message, true);
  });
})(window, document, jQuery);
