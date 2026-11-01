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
      showOnly("app");
      $("#wl-request").text(view.name);
      render();
      startMap();
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
          "<span class=\"wl-parcel-top\"><span class=\"wl-kaek\">" + esc(p.kaek || "—") + "</span>" + badge(t("parcel." + p.status), PARCEL_BADGE[p.status]) + "</span>" +
          "<span class=\"wl-parcel-sub\">" + esc([p.area, otPlot(p), p.sizeSqm !== null && p.sizeSqm !== undefined ? sqm(p.sizeSqm) : null].filter(Boolean).join(" · ")) + "</span>" +
          "<span class=\"wl-parcel-works\">" + p.works.map(function (w) {
            return "<span class=\"wl-work-tag\" style=\"border-color:" + BADGE[w.status] + "\">" + esc(workName(w)) + ": " + esc(t("work." + w.status)) + "</span>";
          }).join("") + "</span>" +
        "</button></li>";
      }).join("") + "</ul>" : "<div class=\"wl-empty\">" + esc(t("noParcels")) + "</div>");
  }

  function otPlot(p) {
    return p.ot || p.plot ? t("otPlot") + " " + (p.ot || "—") + " / " + (p.plot || "—") : null;
  }

  function sqm(value) { return Number(value).toLocaleString(I.locale(), { maximumFractionDigits: 0 }) + " m²"; }

  function parcelHtml(p) {
    return "<div class=\"wl-detail\">" +
      "<button type=\"button\" class=\"btn wl-back\" data-act=\"back\">" + esc(t("back")) + "</button>" +
      "<h2 class=\"wl-kaek-big\">" + esc(p.kaek || "—") + " " + badge(t("parcel." + p.status), PARCEL_BADGE[p.status]) + "</h2>" +
      "<dl class=\"wl-facts\">" +
        (p.area ? "<div><dt>" + esc(t("area")) + "</dt><dd>" + esc(p.area) + "</dd></div>" : "") +
        (p.ot || p.plot ? "<div><dt>" + esc(t("otPlot")) + "</dt><dd>" + esc((p.ot || "—") + " / " + (p.plot || "—")) + "</dd></div>" : "") +
        (p.sizeSqm !== null && p.sizeSqm !== undefined ? "<div><dt>" + esc(t("size")) + "</dt><dd>" + esc(sqm(p.sizeSqm)) + "</dd></div>" : "") +
      "</dl>" +
      "<h3>" + esc(t("works")) + "</h3>" +
      p.works.map(workHtml).join("") +
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
    return "<section class=\"wl-work\" data-status=\"" + esc(w.status) + "\">" + head + body + "</section>";
  }

  // ---- actions ------------------------------------------------------------------------------------------------

  function openParcel(id, fromMap) {
    current = id;
    render();
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
    if (!window.confirm(t(which === "start" ? "confirmStart" : "confirmDone", { work: workName(w), kaek: p.kaek || "" }))) { return; }
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
      mapApi.select(current);
    }, function () {
      $("#wl-map").html("<div class=\"wl-map-off\">" + esc(t("mapUnavailable")) + "</div>");
      $(".wl-app").addClass("no-map");
    });
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
      .on("click", "[data-step]", step);
    $("[data-act=map-size]").on("click", function () {
      bigMap = !bigMap;
      $(".wl-app").toggleClass("big-map", bigMap);
      applyTexts();
      if (mapApi && window.google) { window.google.maps.event.trigger(mapApi.map, "resize"); }
    });
    start();
  });
})(window, document, jQuery);
