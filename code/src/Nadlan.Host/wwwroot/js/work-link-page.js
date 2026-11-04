// The contractor's page (work-link.html): the password if the link has one, then the request's Parcels on a 2D map and
// in a list with filters; on a Parcel, a price (and an estimated start) per work, then Start and Done. The server checks
// every step; this page only shows the buttons that apply. Texts: work-i18n.js.
(function (window, document, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var api = Nadlan.workLinkApi;
  var I = Nadlan.workI18n;
  var t = I.t;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  var view = null;       // the request, as GET /request answers it
  var filter = null;     // null until the first load decides it (Approved once something is approved)
  var current = null;    // the Parcel open in the sheet, or null = the list
  var mapApi = null;     // Nadlan.workMap instance, once Google Maps loaded
  var bigMap = false;

  var FILTERS = [
    { key: "all", statuses: null },
    { key: "open", statuses: ["REQUESTED", "OFFERED"] },
    { key: "approved", statuses: ["APPROVED"] },
    { key: "inwork", statuses: ["IN_WORK"] },
    { key: "done", statuses: ["DONE"] },
    { key: "paid", statuses: ["PAID"] }
  ];

  var BADGE = { REQUESTED: "#64748b", OFFERED: "#b45309", APPROVED: "#1d4ed8", IN_WORK: "#7c3aed", DONE: "#15803d", PAID: "#0f766e" };
  var PARCEL_BADGE = { OPEN_OFFERS: "#b45309", APPROVED: "#1d4ed8", IN_WORK: "#7c3aed", DONE: "#15803d", PAID: "#0f766e" };

  // ---- small helpers ------------------------------------------------------------------------------------------

  function badge(text, color) { return Nadlan.format.statusBadge(text, color); }

  function money(amount) {
    var whole = Number(amount) % 1 === 0; // 320 → "€ 320", 1250.5 → "€ 1.250,50"
    return "€ " + Number(amount).toLocaleString(I.locale(), { minimumFractionDigits: whole ? 0 : 2, maximumFractionDigits: 2 });
  }

  function day(value) {
    if (!value) { return ""; }
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    var d = m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : new Date(value);
    return isNaN(d.getTime()) ? "" : d.toLocaleDateString(I.locale(), { day: "numeric", month: "short", year: "numeric" });
  }

  function dayTime(value) {
    var d = new Date(value);
    return isNaN(d.getTime()) ? "" : d.toLocaleString(I.locale(), { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
  }

  function workName(w) { return I.lang() === "el" && w.nameEl ? w.nameEl : w.name; }

  var toastTimer = null;
  function toast(message, isError) {
    var $t = $("#wl-toast").text(message).toggleClass("is-error", !!isError).prop("hidden", false);
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () { $t.prop("hidden", true); }, isError ? 6000 : 3000);
  }

  function showOnly(which) {
    $("#wl-password").prop("hidden", which !== "password");
    $("#wl-app").prop("hidden", which !== "app");
    $("#wl-consent").prop("hidden", which !== "consent");
    if (which !== "message") { $("#wl-message").text(""); }
  }

  function message(text) {
    showOnly("message");
    $("#wl-message").text(text);
  }

  // A refusal from the server: the link is gone, or the password is needed again (changed by staff, 30 days over).
  function handleFatal(err) {
    if (err.code === "WORK_LINK_INACTIVE") { message(t("inactive")); return true; }
    if (err.code === "WORK_LINK_PASSWORD" || err.code === "WORK_LINK_LOCKED") { start(); return true; }
    return false;
  }

  function errorText(err) {
    switch (err.code) {
      case "WORK_PRICE_REQUIRED": return t("priceRequired");
      case "WORK_PRICE_INVALID": return t("priceInvalid");
      case "WORK_START_INVALID": return t("startInvalid");
      default: return t("failed");
    }
  }

  // ---- texts and language -------------------------------------------------------------------------------------

  function applyTexts() {
    document.documentElement.lang = I.lang();
    document.title = "GreekPlot - " + t("pageTitle");
    $("[data-t]").each(function () { $(this).text(t($(this).data("t"))); });
    $(".wl-lang button").each(function () {
      var on = $(this).data("lang") === I.lang();
      $(this).toggleClass("active", on).attr("aria-pressed", on ? "true" : "false");
    });
    $("[data-act=map-size]").text(bigMap ? t("smallerMap") : t("biggerMap"));
    $(".wl-work-on-state").text(t(workOn ? "workOnOn" : "workOnOff"));
    if (typeof checkWork === "function" && view) { checkWork(); }
  }

  // ---- the password -------------------------------------------------------------------------------------------

  function showPassword(state) {
    showOnly("password");
    var $form = $("#wl-password");
    var locked = state && state.locked && state.lockedUntilUtc;
    $form.find(".wl-error").text(locked ? t("passwordLocked", { time: new Date(state.lockedUntilUtc).toLocaleTimeString(I.locale(), { hour: "2-digit", minute: "2-digit" }) }) : "");
    $form.find("[name=password]").val("").trigger("focus");
  }

  function unlock(e) {
    e.preventDefault();
    var $form = $("#wl-password");
    var $btn = $form.find("button").prop("disabled", true);
    api.post("/unlock", { password: $form.find("[name=password]").val() || "" }).then(function () {
      $btn.prop("disabled", false);
      loadRequest();
    }, function (err) {
      $btn.prop("disabled", false);
      if (err.code === "WORK_LINK_WRONG_PASSWORD") {
        $form.find(".wl-error").text(t("passwordWrong"));
        $form.find("[name=password]").val("").trigger("focus");
      } else if (err.code === "WORK_LINK_LOCKED") {
        showPassword({ locked: true, lockedUntilUtc: err.lockedUntilUtc });
      } else if (!handleFatal(err)) {
        $form.find(".wl-error").text(t("failed"));
      }
    });
  }

  // ---- the request --------------------------------------------------------------------------------------------

  function loadRequest() {
    return api.get("/request").then(function (res) {
      view = res;
      if (filter === null) {
        // Owner: opens on "Approved" once something is approved - a default, not a lock.
        filter = allWorks().some(function (w) { return w.status === "APPROVED"; }) ? "approved" : "all";
      }
      $("#wl-request").text(view.name);
      Nadlan.workTrack.init(); // sends what an earlier visit left unsent
      afterLoad();
    }, function (err) {
      if (!handleFatal(err)) { message(t("failed")); }
    });
  }

  function allWorks() {
    return view.parcels.reduce(function (all, p) { return all.concat(p.works); }, []);
  }

  function parcelById(id) {
    return view.parcels.filter(function (p) { return p.parcelId === id; })[0] || null;
  }

  function inFilter(p, key) {
    var f = FILTERS.filter(function (x) { return x.key === key; })[0];
    return !f.statuses || p.works.some(function (w) { return f.statuses.indexOf(w.status) >= 0; });
  }

  function render() {
    applyTexts();
    if (current && !parcelById(current)) { current = null; }
    var $sheet = $("#wl-sheet");
    $sheet.html(current ? parcelHtml(parcelById(current)) : listHtml());
    fillDetail();
    if (mapApi) { mapApi.select(current); }
  }

  function listHtml() {
    var chips = FILTERS.map(function (f) {
      var n = view.parcels.filter(function (p) { return inFilter(p, f.key); }).length;
      return "<button type=\"button\" class=\"wl-chip" + (f.key === filter ? " active" : "") + "\" data-filter=\"" + f.key + "\" aria-pressed=\"" +
        (f.key === filter) + "\">" + esc(t("filters." + f.key)) + " <span class=\"wl-chip-count\">" + n + "</span></button>";
    }).join("");
    var shown = view.parcels.filter(function (p) { return inFilter(p, filter); });
    return "<div class=\"wl-chips\" role=\"group\">" + chips + "</div>" +
      (shown.length ? "<ul class=\"wl-list\">" + shown.map(function (p) {
        return "<li><button type=\"button\" class=\"wl-parcel\" data-parcel=\"" + p.parcelId + "\">" +
          "<span class=\"wl-parcel-top\"><span class=\"wl-kaek\">" + esc(parcelName(p)) + "</span>" + badge(t("parcel." + p.status), PARCEL_BADGE[p.status]) + "</span>" +
          "<span class=\"wl-parcel-sub\">" + esc([p.area, kaekLine(p), requestLine(p), p.sizeSqm !== null && p.sizeSqm !== undefined ? sqm(p.sizeSqm) : null].filter(Boolean).join(" · ")) + "</span>" +
          "<span class=\"wl-parcel-works\">" + p.works.map(function (w) {
            return "<span class=\"wl-work-tag\" style=\"border-color:" + BADGE[w.status] + "\">" + esc(workName(w)) + ": " + esc(t("work." + w.status)) + "</span>";
          }).join("") + "</span>" +
        "</button></li>";
      }).join("") + "</ul>" : "<div class=\"wl-empty\">" + esc(t("noParcels")) + "</div>");
  }

  // The parcel as the contractor knows it (Alon, 2026-11-03): "OT7 Plot60" in bold; the KAEK beside it, small. No OT or
  // plot: the KAEK.
  function parcelName(p) {
    if (!p.ot && !p.plot) { return p.kaek || "—"; }
    return [p.ot ? t("otName", { ot: p.ot }) : null, p.plot ? t("plotName", { plot: p.plot }) : null].filter(Boolean).join(" ");
  }

  // With the contractor's link (all his requests): which request the parcel is in, when he has more than one.
  function requestLine(p) {
    return view.requests && view.requests.length > 1 && p.requests ? p.requests.join(", ") : null;
  }

  function kaekLine(p) {
    return (p.ot || p.plot) && p.kaek ? t("kaek") + " " + p.kaek : null;
  }

  function sqm(value) { return Number(value).toLocaleString(I.locale(), { maximumFractionDigits: 0 }) + " m²"; }

  function parcelHtml(p) {
    return "<div class=\"wl-detail\">" +
      "<button type=\"button\" class=\"btn wl-back\" data-act=\"back\">" + esc(t("back")) + "</button>" +
      "<h2 class=\"wl-kaek-big\">" + esc(parcelName(p)) + " " + badge(t("parcel." + p.status), PARCEL_BADGE[p.status]) + "</h2>" +
      "<dl class=\"wl-facts\">" +
        (p.area ? "<div><dt>" + esc(t("area")) + "</dt><dd>" + esc(p.area) + "</dd></div>" : "") +
        ((p.ot || p.plot) && p.kaek ? "<div><dt>" + esc(t("kaek")) + "</dt><dd>" + esc(p.kaek) + "</dd></div>" : "") +
        (p.sizeSqm !== null && p.sizeSqm !== undefined ? "<div><dt>" + esc(t("size")) + "</dt><dd>" + esc(sqm(p.sizeSqm)) + "</dd></div>" : "") +
      "</dl>" +
      "<h3>" + esc(t("works")) + "</h3>" +
      "<div class=\"wl-warning\" role=\"status\" hidden></div>" +
      p.works.map(workHtml).join("") +
      "<div class=\"wl-parcel-media\"></div>" +
      "<h3>" + esc(t("messages")) + "</h3>" +
      "<div class=\"wl-messages\" aria-live=\"polite\"></div>" +
      "<form class=\"wl-message-form\" data-message-parcel=\"" + p.parcelId + "\">" +
        "<textarea class=\"input\" name=\"body\" rows=\"3\" maxlength=\"4000\" placeholder=\"" + esc(t("messagePlaceholder")) + "\"></textarea>" +
        "<button type=\"submit\" class=\"btn btn-primary wl-big\">" + esc(t("send")) + "</button>" +
      "</form>" +
    "</div>";
  }

  function workHtml(w) {
    var head = "<div class=\"wl-work-head\"><span class=\"wl-work-name\">" + esc(workName(w)) + "</span>" + badge(t("work." + w.status), BADGE[w.status]) + "</div>";
    var body = "";
    if (w.status === "REQUESTED" || w.status === "OFFERED") {
      var price = w.price === null || w.price === undefined ? "" : String(w.price).replace(".", I.lang() === "el" ? "," : ".");
      body = "<form class=\"wl-offer\" data-work=\"" + w.workItemId + "\">" +
        "<label class=\"wl-field\"><span>" + esc(t("price")) + "</span><span class=\"wl-money\"><span class=\"wl-euro\" aria-hidden=\"true\">€</span>" +
          "<input class=\"input\" name=\"price\" inputmode=\"decimal\" autocomplete=\"off\" value=\"" + esc(price) + "\" aria-label=\"" + esc(t("price")) + " (€)\"></span></label>" +
        "<label class=\"wl-field\"><span>" + esc(t("estStart")) + "</span><input class=\"input\" type=\"date\" name=\"estStart\" value=\"" + esc(w.estStartDate || "") + "\"></label>" +
        "<button type=\"submit\" class=\"btn btn-primary wl-big\">" + esc(w.status === "OFFERED" ? t("changeOffer") : t("sendOffer")) + "</button>" +
        (w.status === "OFFERED" ? "<div class=\"wl-note\">" + esc(t("offerSentAt", { date: dayTime(w.offeredUtc) })) + "</div>" : "") +
      "</form>";
    } else if (w.status === "APPROVED") {
      body = "<div class=\"wl-note\">" + esc(t("approvedPrice", { price: money(w.price) })) + (w.estStartDate ? " · " + esc(day(w.estStartDate)) : "") + "</div>" +
        "<div class=\"wl-note muted\">" + esc(t("startHint")) + "</div>" +
        "<button type=\"button\" class=\"btn btn-primary wl-big\" data-step=\"start\" data-work=\"" + w.workItemId + "\">" + esc(t("start")) + "</button>";
    } else if (w.status === "IN_WORK") {
      body = "<div class=\"wl-note\">" + esc(t("approvedPrice", { price: money(w.price) })) + "</div>" +
        "<div class=\"wl-note\">" + esc(t("inWorkSince", { date: dayTime(w.startedUtc) })) + "</div>" +
        "<button type=\"button\" class=\"btn btn-primary wl-big\" data-step=\"done\" data-work=\"" + w.workItemId + "\">" + esc(t("done")) + "</button>";
    } else if (w.status === "DONE") {
      body = "<div class=\"wl-note\">" + esc(t("approvedPrice", { price: money(w.price) })) + "</div>" +
        "<div class=\"wl-note\">" + esc(t("doneWaiting", { date: dayTime(w.doneUtc) })) + "</div>";
    } else if (w.status === "PAID") {
      body = "<div class=\"wl-note\">" + esc(t("approvedPrice", { price: money(w.price) })) + "</div>" +
        "<div class=\"wl-note\">" + esc(t("paidOn", { date: dayTime(w.paidUtc) })) + "</div>";
    }
    // Staff's remark on this work (an instruction for the job): as text, in its own lines.
    var remark = w.remarks ? "<div class=\"wl-remark\"><div class=\"wl-remark-label\">" + esc(t("remark")) + "</div>" + esc(w.remarks) + "</div>" : "";
    return "<section class=\"wl-work\" data-status=\"" + esc(w.status) + "\">" + head + remark + body + picturesHtml(w) + "</section>";
  }

  // ---- actions ------------------------------------------------------------------------------------------------

  function openParcel(id, fromMap) {
    current = id;
    render();
    loadMessages(id);
    $("#wl-sheet").scrollTop(0);
    if (mapApi && !fromMap) { mapApi.fit(id); }
  }

  function after(promise, okText) {
    return promise.then(function (res) {
      view = res;
      render();
      if (mapApi) { mapApi.setParcels(shapes()); mapApi.select(current); }
      toast(okText);
    }, function (err) {
      if (handleFatal(err)) { return; }
      if (err.status === 409) {
        // Changed meanwhile (e.g. approved while he typed): show it as it is now.
        loadRequest().then(function () { toast(t("changed"), true); });
        return;
      }
      toast(errorText(err), true);
      throw err;
    });
  }

  function sendOffer(e) {
    e.preventDefault();
    var $form = $(this);
    var raw = $.trim($form.find("[name=price]").val() || "");
    var price = Nadlan.format.parseNumber(raw);
    if (price === null) { toast(t("priceRequired"), true); return; }
    if (isNaN(price) || price <= 0) { toast(isNaN(price) ? t("priceInvalid") : t("priceRequired"), true); return; }
    var $btn = $form.find("button").prop("disabled", true);
    after(api.post("/offer", { workItemId: Number($form.data("work")), price: price, estStartDate: $form.find("[name=estStart]").val() || null }), t("offerSaved"))
      .catch(function () { $btn.prop("disabled", false); });
  }

  function step() {
    var $btn = $(this);
    var which = $btn.data("step");
    var p = parcelById(current);
    var w = p.works.filter(function (x) { return x.workItemId === Number($btn.data("work")); })[0];
    if (!window.confirm(t(which === "start" ? "confirmStart" : "confirmDone", { work: workName(w), kaek: parcelName(p) }))) { return; }
    $btn.prop("disabled", true);
    after(api.post("/" + which, { workItemId: w.workItemId }), t(which === "start" ? "started" : "finished"))
      .catch(function () { $btn.prop("disabled", false); });
  }

  // ---- the map ------------------------------------------------------------------------------------------------

  function shapes() {
    return view.parcels.map(function (p) { return { parcelId: p.parcelId, status: p.status, geometry: p.geometry }; });
  }

  function startMap() {
    if (mapApi) { mapApi.setParcels(shapes()); mapApi.select(current); return; }
    Nadlan.workMap.load(view.mapsKey).then(function () {
      if (mapApi) { return; }
      mapApi = Nadlan.workMap.create(document.getElementById("wl-map"), {
        center: view.defaultCenter, zoom: 13,
        onParcelClick: function (id) { openParcel(id, true); }
      });
      mapApi.setParcels(shapes());
      mapApi.fit();
      if (Nadlan.workLocation.last()) { mapApi.setPosition(Nadlan.workLocation.last()); } // a fix that came before the map
      mapApi.select(current);
    }, function () {
      $("#wl-map").html("<div class=\"wl-map-off\">" + esc(t("mapUnavailable")) + "</div>");
      $(".wl-app").addClass("no-map");
    });
  }

  // ---- after the request loaded: the location question once, then the map ---------------------------------------

  var consentAsked = false;

  function afterLoad() {
    var L = Nadlan.workLocation;
    if (consentAsked || !L.supported()) { showApp(); return; }
    consentAsked = true;
    L.consent().then(function (answer) {
      if (answer === "yes") { startTracking(); showApp(); } else if (answer === "no") { showApp(); } else { showOnly("consent"); applyTexts(); }
    });
  }

  function showApp() {
    showOnly("app");
    render();
    startMap();
    updateLocationButtons();
  }

  function answerConsent(yes) {
    Nadlan.workLocation.setConsent(yes ? "yes" : "no");
    api.post("/consent", { shared: yes }).catch(function () { /* the history line only */ });
    if (yes) { startTracking(); }
    showApp();
  }

  // ---- location (piece 3) ---------------------------------------------------------------------------------------

  // "Work on parcel" (owner, 2026-11-03): the contractor works on one parcel; the map follows them, and while they are
  // outside it their dot turns red and blinks, with a beep every 5 s.
  var workOn = false;
  var workParcel = null;   // the parcel being worked on
  var beepTimer = null;
  var audio = null;        // made on the tap that turns it on (browsers allow sound only after a tap)
  var BEEP_EVERY_MS = 5000;

  function beep() {
    if (!audio) { return; }
    try {
      var osc = audio.createOscillator(), gain = audio.createGain();
      osc.frequency.value = 880;
      gain.gain.setValueAtTime(0.25, audio.currentTime);
      gain.gain.exponentialRampToValueAtTime(0.001, audio.currentTime + 0.35);
      osc.connect(gain);
      gain.connect(audio.destination);
      osc.start();
      osc.stop(audio.currentTime + 0.35);
    } catch (e) { /* no sound: the red dot still shows it */ }
  }

  // A parcel with work to do now: a work Approved or In work.
  function workable(p) {
    return (p.works || []).some(function (w) { return w.status === "APPROVED" || w.status === "IN_WORK"; });
  }

  // Inside a Parcel of the request; else the nearest within 100 m; else null. onlyWorkable: only parcels with work to do.
  function parcelHere(pos, onlyWorkable) {
    var best = null;
    view.parcels.forEach(function (p) {
      if (onlyWorkable && !workable(p)) { return; }
      var d = Nadlan.workMap.distanceM(p.geometry, pos.lat, pos.lng);
      if (!best || d < best.d) { best = { p: p, d: d }; }
    });
    return best && best.d <= 100 ? best.p : null;
  }

  // Outside = farther from the parcel than the phone's own uncertainty (at least 5 m, at most 25 m): GPS jitter at the
  // border doesn't set it off.
  function whereFrom(p, pos) {
    var d = p && pos ? Nadlan.workMap.distanceM(p.geometry, pos.lat, pos.lng) : 0;
    return { d: d, outside: !!(p && pos && d !== Infinity && d > Math.min(25, Math.max(5, pos.accuracy || 0))) };
  }

  // Farther than this from the parcel: the track ends and "Work on parcel" turns Off (owner, 2026-11-03).
  var FAR_M = 1000;
  var wasOutside = false;

  // The track (Admins see it): out / in once each time, a point otherwise (the recorder's 5 s / 2 m rules); 1 km = the end.
  function recordWork(p, pos, w) {
    if (!pos) { return true; }
    if (w.d !== Infinity && w.d > FAR_M) {
      Nadlan.workTrack.event("far", p.parcelId, pos, true);
      setWorkOn(false);
      toast(t("farStopped", { kaek: parcelName(p) }), true);
      return false;
    }
    if (w.outside !== wasOutside) { Nadlan.workTrack.event(w.outside ? "out" : "in", p.parcelId, pos, w.outside); }
    else { Nadlan.workTrack.point(p.parcelId, pos, w.outside); }
    wasOutside = w.outside;
    return true;
  }

  function checkWork() {
    var pos = Nadlan.workLocation.last();
    var p = workOn && workParcel && parcelById(workParcel);
    var w = whereFrom(p, pos);
    if (p && !recordWork(p, pos, w)) { return; }
    var d = w.d, outside = w.outside;
    if (mapApi) { mapApi.setAlert(outside); }
    if (outside && !beepTimer) { beep(); beepTimer = setInterval(beep, BEEP_EVERY_MS); }
    if (!outside && beepTimer) { clearInterval(beepTimer); beepTimer = null; }
    var $s = $(".wl-work-status");
    $s.prop("hidden", !workOn).toggleClass("is-out", outside);
    if (workOn && p) {
      $s.text(outside ? t("outsideParcel", { kaek: parcelName(p), m: (Math.round(d / 5) * 5).toLocaleString(I.locale()) }) : t("workingOn", { kaek: parcelName(p) }));
    }
  }

  function setWorkOn(on) {
    workOn = on;
    if (!on) { workParcel = null; }
    $("[data-act=work-on]").toggleClass("active", on).attr("aria-pressed", on ? "true" : "false");
    $(".wl-work-on-state").text(t(on ? "workOnOn" : "workOnOff"));
    checkWork();
  }

  // Start on a parcel: only within 1 km of it.
  function startWork(id) {
    var p = parcelById(id), pos = Nadlan.workLocation.last(), w = whereFrom(p, pos);
    if (pos && w.d !== Infinity && w.d > FAR_M) {
      toast(t("tooFarToStart", { kaek: parcelName(p), km: (Math.round(w.d / 100) / 10).toLocaleString(I.locale()) }), true);
      return false;
    }
    workParcel = id;
    wasOutside = w.outside;
    Nadlan.workTrack.event("start", id, pos, w.outside);
    return true;
  }

  function stopWork() {
    var p = parcelById(workParcel), pos = Nadlan.workLocation.last();
    Nadlan.workTrack.event("stop", workParcel, pos, whereFrom(p, pos).outside);
  }

  // The button (owner, 2026-11-03). While On the work stays on its parcel - looking at another parcel doesn't move it.
  // On: first where he is - inside a parcel with work to do, or within 100 m of one: that one, opened; else the parcel
  // he has open (his choice); else he is asked to choose one, in the list or on the map.
  function toggleWorkOn() {
    if (workOn) { stopWork(); setWorkOn(false); return; }
    if (!Nadlan.workLocation.active()) { toast(t("needLocation"), true); showOnly("consent"); applyTexts(); return; }
    if (!audio && (window.AudioContext || window.webkitAudioContext)) {
      try { audio = new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { audio = null; }
    }
    if (audio && audio.state === "suspended") { audio.resume(); }
    var pos = Nadlan.workLocation.last();
    var here = pos ? parcelHere(pos, true) : null;
    var target = here ? here.parcelId : current;
    if (!target) { toast(t("chooseParcelToWork"), true); return; }
    if (target !== current) { openParcel(target, false); }
    if (!startWork(target)) { return; }
    setWorkOn(true);
    if (pos && mapApi) { mapApi.panTo(pos); }
  }

  function startTracking() {
    Nadlan.workLocation.start(function (pos) {
      if (mapApi) {
        mapApi.setPosition(pos);
        if (workOn) { mapApi.panTo(pos); }
      }
      updateWarning();
      checkWork();
    }, function (err) {
      if (err && err.code === 1) { // blocked: stop asking, say how to allow it
        Nadlan.workLocation.stop();
        Nadlan.workLocation.setConsent("no");
        updateLocationButtons();
        toast(t("locationBlocked"), true);
      }
    });
    updateLocationButtons();
  }

  function updateLocationButtons() {
    var on = Nadlan.workLocation.active();
    $("#wl-sharing").prop("hidden", !on);
    $("[data-act=share]").prop("hidden", on || !Nadlan.workLocation.supported());
    $(".wl-body").toggleClass("is-sharing", on);
  }

  // Inside a Parcel of the request; else the nearest within 100 m; else none.
  function whereAmI() {
    var pos = Nadlan.workLocation.last();
    if (!pos) {
      if (Nadlan.workLocation.active()) { toast(t("noFix"), true); } else { showOnly("consent"); applyTexts(); }
      return;
    }
    var best = null;
    view.parcels.forEach(function (p) {
      var d = Nadlan.workMap.distanceM(p.geometry, pos.lat, pos.lng);
      if (!best || d < best.d) { best = { p: p, d: d }; }
    });
    if (best && best.d === 0) {
      toast(t("standingOn", { kaek: parcelName(best.p) }));
      openParcel(best.p.parcelId, false);
    } else if (best && best.d <= 100) {
      toast(t("nearest", { kaek: parcelName(best.p), m: Math.round(best.d) }));
      openParcel(best.p.parcelId, false);
    } else {
      toast(t("noneNear"), true);
    }
    if (mapApi) { mapApi.panTo(pos); }
  }

  // "You're not in the right place": a chosen Parcel and more than 100 m outside it. A warning, never a block (owner).
  function updateWarning() {
    var $w = $("#wl-sheet .wl-warning");
    if (!$w.length) { return; }
    var p = current && parcelById(current), pos = Nadlan.workLocation.last();
    var d = p && pos ? Nadlan.workMap.distanceM(p.geometry, pos.lat, pos.lng) : 0;
    var away = d > 100 && d !== Infinity;
    $w.prop("hidden", !away).text(away ? t("notHere", { m: (Math.round(d / 10) * 10).toLocaleString(I.locale()), kaek: parcelName(p) }) : "");
  }

  // ---- pictures of a work (piece 3) -----------------------------------------------------------------------------

  var uploads = []; // { workItemId, name, pct, failed, job }

  function picturesHtml(w) {
    var mine = uploads.filter(function (u) { return u.workItemId === w.workItemId; });
    if (!w.files.length && !w.pictures && !mine.length) { return ""; }
    return "<div class=\"wl-pictures\" data-pictures=\"" + w.workItemId + "\">" +
      "<div class=\"wl-pictures-title\">" + esc(t("pictures")) + "</div>" +
      (w.files.length ? "<div class=\"wl-thumbs\">" + w.files.map(function (f, i) {
        return "<button type=\"button\" class=\"wl-thumb\" data-view-work=\"" + w.workItemId + "\" data-index=\"" + i + "\" aria-label=\"" + esc(t("pictures")) + " " + (i + 1) + "\">" +
          (f.thumbUrl ? "<img src=\"" + esc(f.thumbUrl) + "\" alt=\"\" loading=\"lazy\">" : "<span class=\"wl-thumb-kind\">" + (f.kind === "video" ? "▶" : "🖼") + "</span>") +
          (f.kind === "video" && f.thumbUrl ? "<span class=\"wl-thumb-play\" aria-hidden=\"true\">▶</span>" : "") + "</button>";
      }).join("") + "</div>" : "") +
      mine.map(function (u) {
        return "<div class=\"wl-upload" + (u.failed ? " is-failed" : "") + "\"><span class=\"wl-upload-name\"></span>" +
          "<span class=\"wl-upload-state\">" + esc(u.failed ? t("uploadFailed") : t("uploading", { p: u.pct })) + "</span>" +
          "<span class=\"wl-upload-bar\"><span style=\"width:" + (u.failed ? 100 : u.pct) + "%\"></span></span></div>";
      }).join("") +
      (w.pictures && view.uploads ? "<label class=\"btn wl-big wl-add-pictures\"><input type=\"file\" accept=\"image/*,video/*\" multiple data-upload=\"" +
        w.workItemId + "\" hidden>" + esc(t("addPictures")) + "</label>" : "") +
    "</div>";
  }

  // The file names go in as text (a phone's file name is whatever the phone made it).
  function namesInto($box, workItemId) {
    var mine = uploads.filter(function (u) { return u.workItemId === workItemId; });
    $box.find(".wl-upload-name").each(function (i) { $(this).text(mine[i] ? mine[i].name : ""); });
  }

  function startUploads(workItemId, files) {
    Array.prototype.forEach.call(files, function (file) {
      var u = { workItemId: workItemId, name: file.name, pct: 0, failed: false };
      uploads.push(u);
      var pos = Nadlan.workLocation.active() ? Nadlan.workLocation.last() : null;
      u.job = Nadlan.workUpload.upload(file, workItemId, pos, function (sent, total) {
        var pct = total ? Math.floor(sent * 100 / total) : 0;
        if (pct !== u.pct) { u.pct = pct; refreshPictures(workItemId); }
      });
      u.job.promise.then(function () {
        uploads = uploads.filter(function (x) { return x !== u; });
        toast(t("uploaded"));
        loadRequest();
      }, function () {
        u.failed = true;
        refreshPictures(workItemId);
        toast(t("uploadFailed"), true);
        setTimeout(function () { uploads = uploads.filter(function (x) { return x !== u; }); refreshPictures(workItemId); }, 8000);
      });
    });
    refreshPictures(workItemId);
  }

  // Only that work's pictures box is drawn again: a typed price or message elsewhere stays as it is.
  function refreshPictures(workItemId) {
    var p = current && parcelById(current);
    var w = p && p.works.filter(function (x) { return x.workItemId === workItemId; })[0];
    var $box = $("#wl-sheet [data-pictures=" + workItemId + "]");
    if (!w || !$box.length) { return; }
    var $new = $(picturesHtml(w));
    $box.replaceWith($new);
    namesInto($new, workItemId);
  }

  // ---- the Parcel's own photos, and the viewer ------------------------------------------------------------------

  var parcelMedia = null; // all the request's Parcels' customer photos, loaded once

  function fillParcelMedia() {
    var $box = $("#wl-sheet .wl-parcel-media");
    if (!$box.length) { return; }
    if (parcelMedia === null) {
      parcelMedia = [];
      api.get("/media").then(function (list) { parcelMedia = list; fillParcelMedia(); }, function () { /* none shown */ });
      return;
    }
    var mine = parcelMedia.filter(function (m) { return m.parcelId === current; });
    $box.html(mine.length ? "<h3>" + esc(t("parcelPhotos")) + "</h3><div class=\"wl-thumbs\">" + mine.map(function (m, i) {
      return "<button type=\"button\" class=\"wl-thumb\" data-view-parcel=\"" + i + "\">" +
        (m.thumbUrl ? "<img src=\"" + esc(m.thumbUrl) + "\" alt=\"\" loading=\"lazy\">" : "<span class=\"wl-thumb-kind\">" + (m.kind === "video" ? "▶" : "🖼") + "</span>") + "</button>";
    }).join("") + "</div>" : "");
  }

  function openViewer(items, index) {
    var i = index;
    var $v = $("<div class=\"wl-viewer\" role=\"dialog\" aria-modal=\"true\">" +
      "<button type=\"button\" class=\"wl-viewer-close\" aria-label=\"" + esc(t("close")) + "\">×</button>" +
      "<div class=\"wl-viewer-stage\"></div>" +
      (items.length > 1 ? "<button type=\"button\" class=\"wl-viewer-nav prev\" data-step-view=\"-1\" aria-label=\"‹\">‹</button>" +
        "<button type=\"button\" class=\"wl-viewer-nav next\" data-step-view=\"1\" aria-label=\"›\">›</button>" : "") +
      "</div>").appendTo(document.body);
    function show() {
      var it = items[i];
      $v.find("video").each(function () { this.pause(); });
      $v.find(".wl-viewer-stage").html(it.kind === "video"
        ? "<video controls autoplay playsinline src=\"" + esc(it.url) + "\"></video>"
        : "<img src=\"" + esc(it.url) + "\" alt=\"\">");
    }
    function close() { $v.find("video").each(function () { this.pause(); }); $v.remove(); $(document).off("keydown.wlviewer"); }
    $v.on("click", ".wl-viewer-close", close);
    $v.on("click", "[data-step-view]", function () { i = (i + Number($(this).data("stepView")) + items.length) % items.length; show(); });
    $v.on("click", function (e) { if (e.target === $v.get(0)) { close(); } });
    $(document).on("keydown.wlviewer", function (e) { if (e.key === "Escape") { close(); } });
    show();
  }

  // ---- the conversation -----------------------------------------------------------------------------------------

  var messages = {}; // parcelId -> the last list loaded

  function fillMessages() {
    var $box = $("#wl-sheet .wl-messages");
    var list = messages[current];
    if (!$box.length || !list) { return; }
    $box.html(list.length ? list.map(function (m) {
      return "<div class=\"wl-msg" + (m.mine ? " mine" : "") + "\"><div class=\"wl-msg-head\"></div><div class=\"wl-msg-body\"></div></div>";
    }).join("") : "<div class=\"wl-empty\">" + esc(t("noMessages")) + "</div>");
    // As text: whatever anyone typed is never HTML.
    $box.find(".wl-msg").each(function (i) {
      $(this).find(".wl-msg-head").text(list[i].author + " · " + dayTime(list[i].createdUtc));
      $(this).find(".wl-msg-body").text(list[i].body);
    });
  }

  function loadMessages(parcelId) {
    api.get("/messages?parcelId=" + encodeURIComponent(parcelId)).then(function (list) {
      messages[parcelId] = list;
      if (current === parcelId) { fillMessages(); }
    }, function (err) { handleFatal(err); });
  }

  function sendMessage(e) {
    e.preventDefault();
    var $form = $(this), parcelId = Number($form.data("messageParcel"));
    var body = $.trim($form.find("[name=body]").val() || "");
    if (!body) { return; }
    var $btn = $form.find("button").prop("disabled", true);
    api.post("/messages", { parcelId: parcelId, body: body }).then(function (list) {
      $btn.prop("disabled", false);
      $form.find("[name=body]").val("");
      messages[parcelId] = list;
      fillMessages();
      toast(t("messageSent"));
    }, function (err) {
      $btn.prop("disabled", false);
      if (!handleFatal(err)) { toast(err.code === "WORK_MESSAGE_TOO_LONG" ? t("messageTooLong") : t("failed"), true); }
    });
  }

  // After the sheet is drawn: the parts that come from elsewhere (warning, photos, messages, upload names).
  function fillDetail() {
    if (!current) { return; }
    updateWarning();
    fillParcelMedia();
    fillMessages();
    var p = parcelById(current);
    p.works.forEach(function (w) { namesInto($("#wl-sheet [data-pictures=" + w.workItemId + "]"), w.workItemId); });
  }

  // ---- start --------------------------------------------------------------------------------------------------

  function start() {
    showOnly("message");
    $("#wl-message").text(t("loading"));
    if (!api.hasToken()) { message(t("inactive")); return; }
    api.get("/state").then(function (state) {
      if (state.needsPassword) { showPassword(state); } else { loadRequest(); }
    }, function (err) {
      if (!handleFatal(err)) { message(t("failed")); }
    });
  }

  // Another link opened in this tab changes only the part after "#": the browser keeps the page, so start it afresh.
  $(window).on("hashchange", function () { window.location.reload(); });

  $(function () {
    applyTexts();
    $(".wl-lang").on("click", "button", function () {
      I.setLang($(this).data("lang"));
      if (view && !$("#wl-app").prop("hidden")) { render(); } else { applyTexts(); }
    });
    $("#wl-password").on("submit", unlock);
    $("#wl-sheet")
      .on("click", "[data-filter]", function () { filter = $(this).data("filter"); render(); })
      .on("click", "[data-parcel]", function () { openParcel(Number($(this).data("parcel")), false); })
      .on("click", "[data-act=back]", function () { current = null; render(); if (mapApi) { mapApi.fit(); } })
      .on("submit", ".wl-offer", sendOffer)
      .on("click", "[data-step]", step)
      .on("change", "[data-upload]", function () { if (this.files && this.files.length) { startUploads(Number($(this).data("upload")), this.files); } })
      .on("click", "[data-view-work]", function () {
        var p = parcelById(current), id = Number($(this).data("viewWork"));
        var w = p.works.filter(function (x) { return x.workItemId === id; })[0];
        openViewer(w.files, Number($(this).data("index")));
      })
      .on("click", "[data-view-parcel]", function () {
        openViewer(parcelMedia.filter(function (m) { return m.parcelId === current; }), Number($(this).data("viewParcel")));
      })
      .on("submit", ".wl-message-form", sendMessage);
    $("[data-act=consent-yes]").on("click", function () { answerConsent(true); });
    $("[data-act=consent-no]").on("click", function () { answerConsent(false); });
    $("[data-act=where]").on("click", whereAmI);
    $("[data-act=work-on]").on("click", toggleWorkOn);
    $("[data-act=share]").on("click", function () { showOnly("consent"); applyTexts(); });
    $("[data-act=map-size]").on("click", function () {
      bigMap = !bigMap;
      $(".wl-app").toggleClass("big-map", bigMap);
      applyTexts();
      if (mapApi && window.google) { window.google.maps.event.trigger(mapApi.map, "resize"); }
    });
    start();
  });
})(window, document, jQuery);
