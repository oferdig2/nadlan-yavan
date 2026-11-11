// In-house analytics, the reports (migration 026, owner 2026-11-10) - for VIEW_ANALYTICS (Admin, Data person). Used by the
// Present dialog's public-link list (present-link.js): a line under each link, and "Details": its visits (device, place,
// network, where it came from), each property's numbers, and a visit's timeline. Times in this computer's local time; the
// IP only in a visit's own detail. Read only.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s === null || s === undefined ? "" : String(s)); };
  var ATTRIBUTION = "This product includes GeoLite2 data created by MaxMind, available from " +
    "<a href=\"https://www.maxmind.com\" target=\"_blank\" rel=\"noopener\">https://www.maxmind.com</a>.";

  function canSee() { return !!(Nadlan.session && Nadlan.session.can && Nadlan.session.can("VIEW_ANALYTICS")); }

  function day(d) { return new Date(d).toLocaleDateString(undefined, { day: "numeric", month: "short" }); }

  function when(d) { return new Date(d).toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" }); }

  function duration(seconds) {
    var s = Math.round(seconds || 0);
    if (s < 60) { return s + " s"; }
    if (s < 3600) { return Math.floor(s / 60) + " min" + (s < 600 && s % 60 >= 10 ? " " + (s % 60) + " s" : ""); }
    return Math.floor(s / 3600) + " h " + (Math.floor(s / 60) % 60) + " min";
  }

  function place(city, country, region) {
    var where = [city, region && region !== city ? region : null].filter(Boolean).join(", ");
    return where ? where + (country ? " (" + country + ")" : "") : country || "";
  }

  function version(name, v) { return name ? name + (v ? " " + v : "") : ""; }

  function device(v) {
    return [v.deviceType, v.deviceModel, version(v.browser, v.browserVersion), version(v.os, v.osVersion)].filter(Boolean).join(" · ");
  }

  /**
   * "3 visits from 2 devices · last Nov 9, Athens (GR) · phone" - or none yet. The one count of the link's line (review
   * 2026-11-10): a visit = a browser tab on the link; reloads within it, and returns within 30 minutes, are the same visit.
   */
  function lineHtml(s) {
    if (!s || !s.visits) { return "<span class=\"muted\">No visits yet.</span>"; }
    var last = [s.lastUtc ? "last " + esc(day(s.lastUtc)) + (s.lastCity || s.lastCountry ? ", " + esc(place(s.lastCity, s.lastCountry)) : "") : "", esc(s.lastDeviceType || "")]
      .filter(Boolean).join(" · ");
    return s.visits + " visit" + (s.visits === 1 ? "" : "s") + " from " + s.devices + " device" + (s.devices === 1 ? "" : "s") + (last ? " · " + last : "") +
      " · <button type=\"button\" class=\"an-link\" data-an-details>Details</button>";
  }

  /** The lines for these links: { linkId: summary }. */
  function summaries(ids) {
    if (!canSee() || !ids.length) { return Promise.resolve({}); }
    return Nadlan.api.get("/api/analytics/links", { ids: ids }).then(function (rows) {
      var byId = {};
      (rows || []).forEach(function (r) { byId[r.presentationLinkId] = r; });
      return byId;
    });
  }

  function propertiesHtml(props) {
    if (!props.length) { return "<p class=\"muted\">No property opened yet.</p>"; }
    return "<table class=\"an-table\"><thead><tr><th>Property</th><th class=\"num\">Views</th><th class=\"num\">Time</th><th class=\"num\">Photos</th>" +
      "<th class=\"num\" title=\"Opened (watched to 75 % or more)\">Videos</th><th class=\"num\">Measured</th><th class=\"num\">Me</th></tr></thead><tbody>" +
      props.map(function (p) {
        return "<tr><td>" + esc(p.label) + "</td><td class=\"num\" title=\"" + p.visitors + " device(s)\">" + p.views + "</td><td class=\"num\">" + esc(duration(p.seconds)) +
          "</td><td class=\"num\">" + p.photos + "</td><td class=\"num\">" + p.videos + (p.videos75 ? " (" + p.videos75 + ")" : "") +
          "</td><td class=\"num\">" + p.measured + "</td><td class=\"num\">" + p.me + "</td></tr>";
      }).join("") + "</tbody></table>";
  }

  function visitsHtml(visits) {
    if (!visits.length) { return "<p class=\"muted\">No visits yet.</p>"; }
    return "<table class=\"an-table an-visits\"><thead><tr><th>Start</th><th class=\"num\">On screen</th><th>Device</th><th>Place</th><th>Network</th><th>From</th></tr></thead><tbody>" +
      visits.map(function (v) {
        return "<tr class=\"an-visit\" data-visit=\"" + v.contextId + "\" title=\"Show this visit\"><td>" + esc(when(v.firstUtc)) + (v.returningDevice ? " <span class=\"an-badge\" title=\"This device came before\">again</span>" : "") +
          "</td><td class=\"num\">" + esc(duration(v.seconds)) + "</td><td>" + esc(device(v)) + "</td><td>" + esc(place(v.city, v.country, v.region)) +
          "</td><td>" + esc(v.org || "") + "</td><td>" + esc(v.referrerHost || "direct") + "</td></tr>";
      }).join("") + "</tbody></table>";
  }

  function visitDetailHtml(d) {
    var c = d.context;
    var facts = [
      ["IP", c.ip],
      ["Place", place(c.geoCity, c.geoCountry, c.geoRegion) + (c.geoAccuracyKm ? " (± " + c.geoAccuracyKm + " km)" : "")],
      ["Network", c.geoOrg ? c.geoOrg + (c.geoAsn ? " (AS" + c.geoAsn + ")" : "") : ""],
      ["Device", device(c)],
      ["Screen", c.screenW ? c.screenW + " × " + c.screenH + (c.pixelRatio ? " @" + c.pixelRatio + "x" : "") + (c.orientation ? ", " + c.orientation : "") + (c.touch ? ", touch" : "") : ""],
      ["Language", [c.language, c.timeZone].filter(Boolean).join(" · ")],
      ["Came from", c.referrer || "direct (no referrer)"],
      ["Campaign", [c.utmSource, c.utmMedium, c.utmCampaign].filter(Boolean).join(" / ")],
      ["Browser says", c.userAgent]
    ].filter(function (f) { return f[1]; });
    return "<div class=\"an-timeline\">" + (d.steps.length ? d.steps.map(function (s) { return "<span title=\"" + esc(when(s.atUtc)) + "\">" + esc(s.text) + "</span>"; }).join(" → ") : "<span class=\"muted\">No events.</span>") +
      "</div><dl class=\"an-facts\">" + facts.map(function (f) { return "<dt>" + esc(f[0]) + "</dt><dd>" + esc(f[1]) + "</dd>"; }).join("") + "</dl>";
  }

  /** The Details dialog of one public link. */
  function open(link) {
    var $body = $("<div class=\"an-report\"><div class=\"muted\">Loading…</div></div>");
    var d = Nadlan.dialog.open({ title: "Visits · " + (link.title || "(no title)"), content: $body, width: 900, buttons: [{ text: "Close", click: function () { d.close(); } }] });
    Nadlan.api.get("/api/analytics/links/" + link.id).then(function (r) {
      $body.html("<h4>Properties</h4>" + propertiesHtml(r.properties) +
        "<h4>Visits <span class=\"muted\">- click one for its timeline</span></h4>" + visitsHtml(r.visits) +
        (r.truncated ? "<p class=\"muted\">A very busy link: the newest events only.</p>" : "") +
        "<p class=\"an-attribution muted\">" + ATTRIBUTION + "</p>");
    }, function (err) { $body.html("<div class=\"error\">" + esc(err.message) + "</div>"); });

    $body.on("click", ".an-visit", function () {
      var $row = $(this), id = Number($row.data("visit"));
      var $next = $row.next(".an-visit-detail");
      if ($next.length) { $next.remove(); $row.removeClass("open"); return; }
      $row.addClass("open");
      var $detail = $("<tr class=\"an-visit-detail\"><td colspan=\"6\"><span class=\"muted\">Loading…</span></td></tr>").insertAfter($row);
      Nadlan.api.get("/api/analytics/visits/" + id).then(function (v) { $detail.find("td").html(visitDetailHtml(v)); },
        function (err) { $detail.find("td").html("<span class=\"error\">" + esc(err.message) + "</span>"); });
    });
    return d;
  }

  Nadlan.analyticsReport = { canSee: canSee, summaries: summaries, lineHtml: lineHtml, open: open };
})(window, jQuery);
