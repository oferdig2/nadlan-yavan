// Customer presentation page (present.html): ?portfolio=12 or ?assets=5,9,3 (that order).
// Loads what the signed-in viewer may see (GET /api/presentation), shows it on the satellite map seen from above - the
// tour too - with an info card, a strip of all items, and a "3D" button for Google's photorealistic 3D (Alon, 2026-11-05:
// "3D is for the advanced"). present-views.js draws both.
(function (window, document, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var esc = Nadlan.format.escapeHtml;
  var ORBIT_MS = 16000;

  var $title = $("[data-role=title]"), $sub = $("[data-role=sub]"), $card = $("[data-role=card]");
  var $strip = $("[data-role=strip]"), $notice = $("[data-role=notice]"), $tour = $("[data-act=tour]");

  function notice(text, isError, busy) {
    $notice.text(text || "").toggleClass("error", !!isError).toggleClass("busy", !!busy).prop("hidden", !text);
  }

  // ?portfolio=12 | ?assets=5,9,3
  // t = the title sealed by the server (POST /api/presentation/link); text in the address itself is never shown.
  function readQuery() {
    var q = new URLSearchParams(window.location.search);
    var sealed = q.get("t") || null;
    var portfolio = Number(q.get("portfolio"));
    if (portfolio > 0) { return { portfolioId: portfolio, t: sealed }; }
    var ids = (q.get("assets") || "").split(",").map(function (s) { return Number(s.trim()); }).filter(function (n) { return n > 0; });
    return ids.length ? { assetIds: ids, t: sealed } : null;
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

  // 3D needs WebGL (graphics acceleration); without it Google's element only shows black.
  function hasWebGl() {
    try {
      var canvas = document.createElement("canvas");
      return !!(window.WebGL2RenderingContext && canvas.getContext("webgl2")) || !!canvas.getContext("webgl");
    } catch (e) { return false; }
  }

  // How long (tab visible) the 3D map may take to draw its first view; until then the satellite map stays in front.
  var READY_3D_MS = 30000;

  function withTimeout(promise, ms, what) {
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(function () { reject(new Error(what + " took too long")); }, ms);
      promise.then(function (v) { clearTimeout(timer); resolve(v); }, function (e) { clearTimeout(timer); reject(e); });
    });
  }

  function satelliteView(items, onPick) {
    return Promise.all([google.maps.importLibrary("maps"), google.maps.importLibrary("marker")]).then(function () {
      return Nadlan.presentViews.create2dView(document.getElementById("stage"), items, { onPick: onPick, titleOf: pinTitle,
        setStatus: function (text) { notice(text); }, onMeasuring: function (on) { $("body").toggleClass("measuring", on); },
        measureButton: document.querySelector("[data-act=measure]"), covered: coveredArea });
    });
  }

  // The 3D view, made behind the satellite map and shown only once it has really drawn. Loading alone proves nothing:
  // without graphics acceleration, with the Map Tiles API off on the key, or its quota used up, it stays black.
  // Rejects with what to tell the presenter; whatever 3D left behind is removed first.
  function threeDView(items, onPick) {
    if (!hasWebGl()) { return Promise.reject(new Error("3D needs graphics acceleration, which is off in this browser.")); }
    var view = null;
    return withTimeout(google.maps.importLibrary("maps3d"), READY_3D_MS, "The 3D library").then(function (lib) {
      if (!lib || !lib.Map3DElement) { throw new Error("no 3D"); }
      view = Nadlan.presentViews.create3dView(lib, document.getElementById("stage"), items, { onPick: onPick });
      view.element.classList.add("loading");
      return view.ready(READY_3D_MS).then(function (ok) {
        if (!ok) { throw new Error("3D didn't draw"); }
        view.element.classList.remove("loading");
        return view;
      });
    }).catch(function (err) {
      try { if (view) { view.destroy(); } } catch (e) { /* already gone */ }
      throw new Error(/graphics/.test(err.message) ? err.message : "The 3D view isn't available right now - staying on the map from above.");
    });
  }

  function price(it) {
    if (it.askPrice === null || it.askPrice === undefined) { return "Price on request"; }
    try {
      return new Intl.NumberFormat("en-US", { style: "currency", currency: it.currencyCode || "EUR", maximumFractionDigits: 0 }).format(it.askPrice);
    } catch (e) { return Number(it.askPrice).toLocaleString("en-US") + " " + esc(it.currencyCode || ""); }
  }

  function sqm(v) { return v === null || v === undefined ? null : Number(v).toLocaleString("en-US", { maximumFractionDigits: 0 }) + " m²"; }

  // What the panels cover of the map, in px from each edge: the top bar, the bottom bar, and the card - at the bottom (phone
  // upright) or on the right (computer, phone on its side). The map centres a property in the rest (2026-11-08).
  function coveredArea() {
    var w = window.innerWidth, h = window.innerHeight, c = { top: 0, right: 0, bottom: 0, left: 0 };
    var bar = document.querySelector(".pres-controls"), bottom = document.querySelector(".pres-bottom"), card = document.querySelector("[data-role=card]");
    if (bar) { c.top = Math.max(0, bar.getBoundingClientRect().bottom); }
    if (bottom && bottom.offsetParent) { c.bottom = Math.max(0, h - bottom.getBoundingClientRect().top); }
    if (card && !card.hidden) {
      var r = card.getBoundingClientRect();
      if (r.width > w * 0.6) { c.bottom = Math.max(c.bottom, h - r.top); } else { c.right = Math.max(0, w - r.left); }
    }
    return c;
  }

  function heading(it) { return (it.propertyType || "Property") + (it.area ? " · " + it.area : ""); }

  // The pin's hover text: OT / plot first when entered - how people know a plot (Alon, 2026-11-05).
  function pinTitle(it, i) { return (i + 1) + ". " + (it.otPlot ? it.otPlot + " · " : "") + heading(it) + " · " + price(it); }

  function thumbOf(m) { return m.thumbUrl || (m.kind === "image" ? m.url : null); }

  // One round button per photo/video, opening file-card.js's full-screen viewer.
  // group: one per property, so the viewer's "‹" / "›" step through that property's photos and videos (remark 6).
  function mediaButton(m, cls, group) {
    var thumb = thumbOf(m);
    return "<button type=\"button\" class=\"" + cls + "\" data-view-file=\"" + esc(m.kind) + "\" data-url=\"" + esc(m.url) + "\" data-name=\"" + esc(m.caption || "") + "\"" +
      " data-view-group=\"" + esc(group) + "\"" + (m.downloadUrl ? " data-download=\"" + esc(m.downloadUrl) + "\"" : "") +
      (thumb ? " style=\"background-image:url('" + esc(thumb).replace(/'/g, "%27") + "')\"" : "") + " title=\"" + (m.kind === "video" ? "Play" : "View") + "\">" +
      (m.kind === "video" ? "<span class=\"pres-play\">▶</span>" : "") + "</button>";
  }

  function factsHtml(it) {
    var facts = [
      ["Numbers", it.otPlot ? esc(it.otPlot) : null], // "OT 9 / Plot 83" first: how people know a plot (Alon, 2026-11-05)
      ["Asking", price(it)],
      ["Plot size", sqm(it.plotSqm)],
      ["House size", sqm(it.houseSqm)],
      ["Sea", it.seaMetres === null || it.seaMetres === undefined ? null : esc(Nadlan.format.seaDistance(it.seaMetres))],
      ["KAEK", it.kaek ? esc(it.kaek) : null]
    ].filter(function (f) { return f[1]; });
    return "<h2>" + esc(heading(it)) + "</h2>" +
      "<span class=\"pres-status\" style=\"--c:" + (/^#[0-9a-fA-F]{6}$/.test(it.statusColor || "") ? it.statusColor : "#64748b") + "\">" + esc(it.statusName || "") + "</span>" +
      "<dl class=\"pres-facts\">" + facts.map(function (f) { return "<dt>" + f[0] + "</dt><dd>" + f[1] + "</dd>"; }).join("") + "</dl>";
  }

  // The card (2026-11-08): its photos and videos as a carousel - ‹ ›, swipe, "3 / 12" - a click opens the big viewer
  // there; no arrows to other properties (the strip, the map and the keyboard do that).
  function cardHtml(it, index, count) {
    var media = it.media || [];
    var carousel = !media.length ? "" : "<div class=\"pres-carousel\" data-at=\"0\">" +
      media.map(function (m, i) { return mediaButton(m, "pres-hero" + (i === 0 ? " on" : ""), "item-" + index); }).join("") +
      (media.length > 1
        ? "<button type=\"button\" class=\"pres-car-nav prev\" data-car=\"-1\" aria-label=\"Previous photo\">‹</button>" +
          "<button type=\"button\" class=\"pres-car-nav next\" data-car=\"1\" aria-label=\"Next photo\">›</button>" +
          "<span class=\"pres-car-count\">1 / " + media.length + "</span>"
        : "") + "</div>";
    return "<div class=\"pres-card-head\"><span class=\"pres-num\">" + (index + 1) + "</span><span class=\"muted\">" + (index + 1) + " of " + count + "</span>" +
        "<span class=\"pres-head-gap\"></span>" +
        "<button type=\"button\" class=\"pres-close\" data-act=\"close-card\" title=\"Hide\">×</button></div>" +
      // No photos: no empty picture box - the card is only its text and covers less of the map (Alon, 2026-11-05).
      carousel + factsHtml(it);
  }

  function carouselStep($car, by) {
    var $slides = $car.find(".pres-hero"), n = $slides.length;
    var at = (Number($car.attr("data-at")) + by + n) % n;
    $slides.removeClass("on").eq(at).addClass("on");
    $car.attr("data-at", at).find(".pres-car-count").text((at + 1) + " / " + n);
  }

  function stripHtml(items) {
    return items.map(function (it, i) {
      var thumb = it.media && it.media.length ? thumbOf(it.media[0]) : null;
      return "<button type=\"button\" class=\"pres-chip\" data-index=\"" + i + "\">" +
        "<span class=\"pres-chip-img" + (thumb ? "" : " no-photo") + "\"" + (thumb ? " style=\"background-image:url('" + esc(thumb).replace(/'/g, "%27") + "')\"" : "") + "><span class=\"pres-num\">" + (i + 1) + "</span></span>" +
        "<span class=\"pres-chip-text\"><strong>" + esc(heading(it)) + "</strong><span>" + price(it) + (it.otPlot ? " · " + esc(it.otPlot) : "") + "</span></span></button>";
    }).join("");
  }

  // What the customer reads at the top: the presenter's title (sealed in the link, checked by the server), never the
  // Portfolio's internal name or notes; else a plain "4 properties · Skroponeria, Kokkinis".
  function titles(items, sealedTitle) {
    var chosen = $.trim(sealedTitle || "").slice(0, 120);
    var areas = [];
    items.forEach(function (it) { if (it.area && areas.indexOf(it.area) < 0) { areas.push(it.area); } });
    var count = items.length === 1 ? heading(items[0]) : items.length + " properties";
    return { title: chosen || count, sub: chosen ? count + (areas.length ? " · " + areas.slice(0, 3).join(", ") : "") : areas.slice(0, 3).join(", ") };
  }

  function start(data, config) {
    var items = data.items || [];
    var t = titles(items, data.title);
    $title.text(t.title);
    $sub.text(t.sub);
    document.title = t.title + " — GreekPlot";
    if (!items.length) {
      notice("Nothing to show: these properties don't exist, or they were not shared with you.", true);
      return;
    }
    if (data.truncated) {
      notice("Showing the first " + items.length + " of " + data.total + " properties. Present a smaller selection to see the rest.");
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
      var found = Nadlan.myLocation.standingOn(p, items, function (it) { return it.polygons; });
      if (found.rough) {
        notice("Your location is ± " + Math.round(p.accuracy) + " m - too rough to tell which property you're on.");
      } else if (found.hits.length === 1) {
        select(items.indexOf(found.hits[0]), false);
        notice("You're standing on " + heading(found.hits[0]) + ".");
      } else if (found.hits.length > 1) {
        notice("You're where " + found.hits.length + " of these properties meet: " + found.hits.map(function (it) { return (items.indexOf(it) + 1) + ". " + heading(it); }).join(", ") + ".");
      } else {
        notice("");
      }
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
    }, function () { $me.removeClass("on"); }); // tracking ended by itself (battery); "Me" starts it again

    function showMe() {
      if (!view) { return; }
      stopTour();
      if (!locator.start()) { return; }
      var known = locator.last();
      if (known) { arrive(known); } else { flyToMe = true; $me.addClass("busy"); }
    }
    $me.on("click", showMe);

    // Touching the map (finger, mouse, wheel, pinch) means the presenter wants to look around: the tour stops there
    // instead of flying off under their hand.
    ["pointerdown", "wheel", "touchstart"].forEach(function (type) {
      document.getElementById("stage").addEventListener(type, function () { if (touring) { stopTour(); } }, { passive: true, capture: true });
    });

    $strip.on("click", ".pres-chip", function () { pick(Number($(this).data("index"))); });
    $card.on("click", "[data-car]", function (e) { e.stopPropagation(); carouselStep($(this).closest(".pres-carousel"), Number($(this).data("car"))); });
    // Swipe on a phone: left = next photo, right = previous (a tap still opens the viewer).
    var swipeX = null;
    $card.on("touchstart", ".pres-carousel", function (e) { swipeX = e.originalEvent.touches[0].clientX; });
    $card.on("touchend", ".pres-carousel", function (e) {
      if (swipeX === null) { return; }
      var dx = e.originalEvent.changedTouches[0].clientX - swipeX;
      swipeX = null;
      if (Math.abs(dx) > 40) { e.preventDefault(); carouselStep($(this), dx < 0 ? 1 : -1); }
    });
    // The big viewer shows this property's details in a rail beside its photos.
    Nadlan.mediaViewer.infoFor = function (b) {
      var m = /^item-(\d+)$/.exec(b.getAttribute("data-view-group") || "");
      var it = m ? items[Number(m[1])] : null;
      return it ? "<div class=\"pres-rail\"><div class=\"pres-card-head\"><span class=\"pres-num\">" + (Number(m[1]) + 1) + "</span></div>" + factsHtml(it) + "</div>" : "";
    };
    $("[data-act=overview]").on("click", function () { stopTour(); if (view) { view.highlight(-1); view.showAll(); } $card.prop("hidden", true); });
    $tour.on("click", function () { if (touring) { stopTour(); } else if (view) { tour(); } });
    $card.on("click", "[data-act=close-card]", function () { $card.prop("hidden", true); });
    $(document).on("keydown", function (e) {
      if ($(".media-viewer").length || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) { return; }
      if (e.ctrlKey || e.metaKey || e.altKey) { return; } // Ctrl+M, Cmd+O... belong to the browser
      if (e.key === "ArrowRight") { pick(current + 1); }
      else if (e.key === "ArrowLeft") { pick(current < 0 ? items.length - 1 : current - 1); }
      else if (e.key === " ") { e.preventDefault(); $tour.trigger("click"); }
      else if (e.key === "o" || e.key === "O") { $("[data-act=overview]").trigger("click"); }
      else if (e.key === "m" || e.key === "M") { showMe(); }
    });

    // The "3D" button: Google's 3D for whoever wants it; pressed again (it then says "2D"), back to the map from above.
    var $view3d = $("[data-act=view3d]");
    var switching = false;
    function useView(v) {
      view = v;
      $("body").removeClass("view-3d view-2d").addClass("view-" + v.kind);
      $view3d.text(v.kind === "3d" ? "⬒ 2D" : "⛰ 3D").attr("title", v.kind === "3d" ? "Back to the map from above (D)" : "Google's 3D view (D)")
        .attr("aria-pressed", v.kind === "3d" ? "true" : "false").prop("hidden", false);
      if (v.kind === "3d") {
        // Quota used up, tiles refused mid-meeting: don't leave the customer in front of a black globe.
        v.onError(function () { to2d("3D stopped working - back to the map from above."); });
      }
      if (current >= 0) { v.highlight(current); }
    }

    function to2d(message) {
      if (switching || !view || view.kind !== "3d") { return; }
      switching = true;
      stopTour();
      var old = view;
      satelliteView(items, pick).then(function (v) {
        try { old.destroy(); } catch (e) { /* already gone */ }
        useView(v);
        if (current >= 0) { v.fly(current); } else { v.showAll(); }
        if (message) { notice(message); setTimeout(function () { notice(""); }, 6000); }
      }, function (err) { notice(err.message, true); }).then(function () { switching = false; });
    }

    function to3d() {
      if (switching || !view || view.kind === "3d") { return; }
      switching = true;
      stopTour();
      $view3d.addClass("busy");
      notice("Loading the 3D view…", false, true);
      threeDView(items, pick).then(function (v) {
        try { view.destroy(); } catch (e) { /* already gone */ }
        notice("");
        useView(v);
        if (current >= 0) { v.fly(current); }
      }, function (err) {
        notice(err.message, true);
        setTimeout(function () { notice(""); }, 8000);
      }).then(function () { switching = false; $view3d.removeClass("busy"); });
    }
    $view3d.on("click", function () { if (view && view.kind === "3d") { to2d(); } else { to3d(); } });
    $(document).on("keydown", function (e) {
      if (e.ctrlKey || e.metaKey || e.altKey || $(".media-viewer").length || /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) { return; }
      if (e.key === "d" || e.key === "D") { $view3d.trigger("click"); }
    });

    withTimeout(loadGoogle(config.googleMapsApiKey, config.maps3dChannel), 30000, "Google Maps").then(function () {
      return satelliteView(items, pick);
    }).then(function (v) {
      useView(v);
      // No card on arrival (Alon, 2026-11-05): the map alone; a card opens on a click (pin, plot, chip), arrows or the tour.
    }).catch(function (err) { notice(err.message + " Check the connection and reload the page.", true); });
  }

  // ---- show.html: a public link (owner, 2026-11-05) - no sign-in; the token after "#" never reaches a server log -----

  if ($("body").hasClass("present-public")) {
    var token = (window.location.hash || "").replace(/^#/, "");
    var $lock = $("[data-role=lock]");

    var call = function (method, path, body) {
      return window.fetch("/api/present-link" + path, {
        method: method, credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-Nadlan-Request": "1", "X-Present-Link": token },
        body: body === undefined ? undefined : JSON.stringify(body)
      }).then(function (res) {
        return res.json().catch(function () { return {}; }).then(function (json) {
          if (res.ok) { return json; }
          var err = new Error(json.message || "Something went wrong.");
          err.status = res.status; err.code = json.error;
          throw err;
        });
      });
    };

    var stop = function (err) {
      $lock.prop("hidden", true);
      $("body").addClass("present-locked");
      $title.text("Presentation");
      notice(err.status === 404 || !token ? "This link isn't active. Ask the person who sent it for a new one." : err.message + " Check the connection and reload the page.", true);
    };

    var show = function () {
      call("GET", "/data").then(function (data) {
        $lock.prop("hidden", true);
        $("body").removeClass("present-locked");
        start(data, data);
      }, function (err) { if (err.status === 401) { askPassword(""); } else { stop(err); } });
    };

    var askPassword = function (message) {
      $title.text("Presentation");
      $("body").addClass("present-locked");
      $lock.prop("hidden", false).find(".pres-lock-error").text(message || "").prop("hidden", !message);
      $lock.find("[name=password]").val("").trigger("focus");
    };

    $lock.on("submit", function (e) {
      e.preventDefault();
      var $btn = $lock.find("button").prop("disabled", true);
      call("POST", "/unlock", { password: $lock.find("[name=password]").val() }).then(show, function (err) {
        askPassword(err.status === 423 ? "Too many wrong passwords. Try again in 15 minutes." : err.status === 401 ? "Wrong password." : err.message);
        if (err.status === 404) { stop(err); }
      }).then(function () { $btn.prop("disabled", false); });
    });

    if (!token) { stop({ status: 404 }); return; }
    call("GET", "/state").then(function (st) {
      if (st.locked) { askPassword("Too many wrong passwords. Try again in 15 minutes."); } else if (st.needsPassword) { askPassword(""); } else { show(); }
    }, stop);
    return;
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
