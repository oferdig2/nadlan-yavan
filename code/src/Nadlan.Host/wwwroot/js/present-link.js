// "Present": asks for the title the customer will read (never the Portfolio's internal name), then opens the
// presentation (present.html) in a new tab or copies its link to send. That link works for anyone signed in with
// access to those properties.
// With SHARE_PRESENTATION (Admin, Data person - owner, 2026-11-05) the dialog also makes and manages PUBLIC links
// (show.html#token): no sign-in, an optional password, 1 week by default (up to a year), disable / new link / delete.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  function copyText(text, done) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, function () { window.prompt("Copy this link:", text); });
    } else { window.prompt("Copy this link:", text); }
  }

  function day(d) { var x = new Date(d); return x.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }); }

  function lastDayOf(expiresUtc) { return new Date(new Date(expiresUtc).getTime() - 1).toISOString().slice(0, 10); }

  function inDays(n) { var d = new Date(); d.setDate(d.getDate() + n); return d.toISOString().slice(0, 10); }

  // A password we make up for the customer: 6 digits (the staff member may type their own over it).
  function madeUpPassword() {
    var n = window.crypto && window.crypto.getRandomValues ? window.crypto.getRandomValues(new Uint32Array(1))[0] % 1000000 : Math.floor(Math.random() * 1000000);
    return ("00000" + n).slice(-6);
  }

  // The buttons under each link (+1 week, Disable, New password, New link, Delete ...) are hidden for now (owner,
  // 2026-11-05: "confusing"); the server still offers them.
  var SHOW_ROW_ACTIONS = false;

  var STATE = { active: "Active", disabled: "Disabled", expired: "Expired", locked: "Locked (wrong passwords)" };

  /** @param {{ portfolioId?: number, assetIds?: number[] }} what */
  function open(what) {
    var canShare = !!(Nadlan.session && Nadlan.session.can && Nadlan.session.can("SHARE_PRESENTATION"));
    var $form = $(
      "<form class=\"form present-dialog\">" +
        "<label>Title the customer sees<input class=\"input\" name=\"title\" maxlength=\"120\" placeholder=\"e.g. Sea-view plots for the Smiths\"></label>" +
        "<div class=\"muted\">Leave empty for “4 properties · Skroponeria”. Only marketing photos and videos are shown.</div>" +
        (canShare
          ? "<section class=\"public-links\"><h4>Public link <span class=\"muted\">- opens without signing in</span></h4>" +
              "<div class=\"muted\">Shows only properties For sale" + (what.portfolioId ? "; follows the Portfolio as it changes" : "") + ". <strong>Copy link</strong> below makes it and copies it.</div>" +
              "<div class=\"form-row public-new\">" +
                "<label>Works until<input class=\"input\" type=\"date\" name=\"lastDay\"></label>" +
              "</div>" +
              "<label class=\"check\"><input type=\"checkbox\" name=\"protect\"> Password</label>" +
              "<div class=\"public-password\" hidden><input class=\"input\" name=\"password\" maxlength=\"64\" autocomplete=\"off\" spellcheck=\"false\">" +
                "<span class=\"muted\">4-64 characters. Send it separately.</span></div>" +
              "<div class=\"public-made\" hidden></div>" +
              "<div class=\"public-list\"><div class=\"muted\">Loading…</div></div>" +
            "</section>"
          : "") +
      "</form>");
    $form.find("[name=lastDay]").val(inDays(7)).attr({ min: inDays(0), max: inDays(365) });
    $form.on("change", "[name=protect]", function () {
      var on = this.checked, $box = $form.find(".public-password").prop("hidden", !on);
      if (on && !$.trim($box.find("[name=password]").val())) { $box.find("[name=password]").val(madeUpPassword()); }
      if (on) { $box.find("[name=password]").trigger("focus").trigger("select"); }
    });

    // The server seals the title into the link (a link typed by hand can't carry text of its own onto the page).
    function link() {
      d.showError("");
      return Nadlan.api.post("/api/presentation/link", {
        portfolioId: what.portfolioId || null, assetIds: what.assetIds || null, title: $.trim($form.find("[name=title]").val()) || null
      }).then(function (r) { return window.location.origin + r.url; }, function (err) { d.showError(err.message); throw err; });
    }

    var d = Nadlan.dialog.open({
      title: "Present to a customer",
      content: $form,
      width: canShare ? 600 : 460,
      buttons: [
        { text: "Open", primary: true, click: function () {
          // Opened before the request ends, so the browser doesn't block it as a pop-up; filled in once the link is back.
          var tab = window.open("", "_blank");
          link().then(function (url) { if (tab) { tab.opener = null; tab.location = url; } else { window.open(url, "_blank", "noopener"); } d.close(); },
            function () { if (tab) { tab.close(); } });
        } },
        // Copy link = the PUBLIC link (owner, 2026-11-05: "Open" is for the signed-in one); only for those who may share.
      ].concat(canShare ? [{ text: "Copy link", click: function () { copyPublic(); } }] : []).concat([
        { text: "Cancel", click: function () { d.close(); } }
      ])
    });
    if (!canShare) { return d; }

    // ---- public links ---------------------------------------------------------------------------------------------

    var base = "/api/presentation/links";
    var target = what.portfolioId ? { portfolioId: what.portfolioId } : { assetIds: what.assetIds };
    var $list = $form.find(".public-list"), $made = $form.find(".public-made");
    var links = [];
    // The customer of a new link: Admins only (the server refuses it from anyone else).
    var customer = Nadlan.linkCustomer && Nadlan.linkCustomer.canSet() ? Nadlan.linkCustomer.field({}) : null;
    if (customer) {
      $("<div class=\"public-customer\"><div class=\"public-customer-head\">Customer <span class=\"muted\">(for reports, only Admins see it)</span></div></div>")
        .append(customer.$el).insertBefore($made);
    }

    function publicUrl(token) { return window.location.origin + "/show.html#" + token; }

    function showMade(text) { $made.html(text).prop("hidden", !text); }

    function render() {
      var analytics = !!(Nadlan.analyticsReport && Nadlan.analyticsReport.canSee());
      if (!links.length) { $list.html("<div class=\"muted\">No public links yet.</div>"); return; }
      $list.html("<ul class=\"public-rows\">" + links.map(function (l) {
        return "<li data-id=\"" + l.id + "\"><div class=\"public-row-head\"><strong class=\"public-title\"></strong>" +
            "<span class=\"badge public-state public-" + esc(l.state) + "\">" + esc(STATE[l.state] || l.state) + "</span>" +
            (l.hasPassword ? " <span title=\"Asks for a password\">🔒</span>" : "") + "</div>" +
          "<div class=\"muted\">Until " + esc(day(new Date(new Date(l.expiresUtc).getTime() - 1))) +
            // One count only (review 2026-11-10): with analytics the line below says how often; else the link's own counter.
            (analytics ? "" : " · opened " + l.openCount + " time" + (l.openCount === 1 ? "" : "s") + (l.lastOpenedUtc ? ", last " + esc(day(l.lastOpenedUtc)) : "")) +
            " · by " + esc(l.createdBy) + "</div>" +
          (analytics ? "<div class=\"public-stats muted\"></div>" : "") +
          // "contactId" is in the answer only for Admins (VIEW_ANALYTICS_PAGE).
          (customer && "contactId" in l ? "<div class=\"public-customer-line muted\">" + Nadlan.linkCustomer.lineHtml(l) + "</div>" : "") +
          (!SHOW_ROW_ACTIONS ? "" : "<div class=\"btn-row public-actions\">" +
            (l.enabled ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"copy\">Copy link</button>" : "") +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"extend\">+1 week</button>" +
            "<label class=\"public-day\">until <input type=\"date\" class=\"input input-small\" data-act=\"day\" value=\"" + lastDayOf(l.expiresUtc) + "\" min=\"" + inDays(0) +
              "\" max=\"" + inDays(365) + "\"></label>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"" + (l.enabled ? "disable" : "enable") + "\">" + (l.enabled ? "Disable" : "Enable") + "</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"password\">" + (l.hasPassword ? "New password" : "Add password") + "</button>" +
            (l.hasPassword ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"nopassword\">No password</button>" : "") +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"renew\" title=\"A new address; the current one stops at once\">New link</button>" +
            "<button type=\"button\" class=\"btn btn-small btn-danger\" data-act=\"delete\">Delete</button>" +
          "</div>") + "</li>";
      }).join("") + "</ul>");
      $list.find("li").each(function (i) { $(this).find(".public-title").text(links[i].title || "(no title)"); });
      stats();
    }

    // Who opened each link (in-house analytics, VIEW_ANALYTICS only - analytics-report.js): a line, and "Details".
    function stats() {
      var report = Nadlan.analyticsReport;
      if (!report || !report.canSee() || !links.length) { return; }
      report.summaries(links.map(function (l) { return l.id; })).then(function (byId) {
        $list.find("li").each(function () { $(this).find(".public-stats").html(report.lineHtml(byId[Number($(this).data("id"))])); });
      }, function () { $list.find(".public-stats").text(""); });
    }
    $form.on("click", "[data-lc-change]", function () {
      var id = idOf(this), l = links.filter(function (x) { return x.id === id; })[0];
      if (l) { Nadlan.linkCustomer.change(l).then(function (saved) { if (saved) { load(); } }); }
    });
    $form.on("click", "[data-an-details]", function () {
      var id = idOf(this), l = links.filter(function (x) { return x.id === id; })[0];
      if (l) { Nadlan.analyticsReport.open(l); }
    });

    function load() {
      return Nadlan.api.get(base, target).then(function (rows) { links = rows; render(); },
        function (err) { $list.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
    }

    function act(promise, message) {
      d.showError("");
      return promise.then(function (res) { if (message) { showMade(esc(message)); } load(); return res; },
        function (err) { d.showError(err.message); return null; });
    }

    function idOf(el) { return Number($(el).closest("li").data("id")); }

    // "Copy link": makes the public link with the settings above and copies it. Pressed again with the same settings, the
    // same link is copied again (no new one each time).
    var lastMade = null; // { settings, url, password }
    function copyPublic() {
      var body = $.extend({}, target, {
        title: $.trim($form.find("[name=title]").val()) || null, lastDay: $form.find("[name=lastDay]").val() || null,
        password: $form.find("[name=protect]").prop("checked") ? ($.trim($form.find("[name=password]").val()) || madeUpPassword()) : null
      });
      if (customer && customer.value()) { body.contactId = customer.value(); } // never sent by anyone else
      var settings = JSON.stringify(body);
      if (lastMade && lastMade.settings === settings) { copyText(lastMade.url, function () { }); shown(lastMade, "Copied again"); return; }
      d.busy(true);
      act(Nadlan.api.post(base, body)).then(function (res) {
        d.busy(false);
        if (!res) { return; }
        lastMade = { settings: settings, url: publicUrl(res.token), password: res.password };
        copyText(lastMade.url, function () { });
        shown(lastMade, "Public link made and copied");
      });
    }

    function shown(m, what) {
      showMade(esc(what) + ": <input class=\"input public-url\" readonly>" +
        (m.password ? "<div>Password: <strong class=\"public-pw\"></strong> <span class=\"muted\">- send it separately.</span></div>" : ""));
      $made.find(".public-url").val(m.url);
      $made.find(".public-pw").text(m.password || "");
    }

    $form.on("click", "[data-act=copy]", function () {
      var id = idOf(this);
      act(Nadlan.api.get(base + "/" + id + "/token")).then(function (res) {
        if (res) { copyText(publicUrl(res.token), function () { showMade("Link copied."); }); }
      });
    });
    $form.on("click", "[data-act=extend]", function () { act(Nadlan.api.post(base + "/" + idOf(this) + "/extend"), "Extended by a week."); });
    $form.on("change", "[data-act=day]", function () {
      var v = $(this).val();
      if (v) { act(Nadlan.api.put(base + "/" + idOf(this) + "/expiry", { lastDay: v }), "The link works until " + day(v) + "."); }
    });
    $form.on("click", "[data-act=disable]", function () { act(Nadlan.api.post(base + "/" + idOf(this) + "/disable"), "Disabled: the link opens nothing now."); });
    $form.on("click", "[data-act=enable]", function () { act(Nadlan.api.post(base + "/" + idOf(this) + "/enable"), "Enabled."); });
    $form.on("click", "[data-act=nopassword]", function () { act(Nadlan.api.del(base + "/" + idOf(this) + "/password"), "The link opens without a password now."); });
    $form.on("click", "[data-act=password]", function () {
      act(Nadlan.api.post(base + "/" + idOf(this) + "/password", { generate: true })).then(function (res) {
        if (!res) { return; }
        showMade("New password: <strong class=\"public-pw\"></strong> <span class=\"muted\">- shown only now; phones that had the old one must type it.</span>");
        $made.find(".public-pw").text(res.password);
      });
    });
    $form.on("click", "[data-act=renew]", function () {
      var id = idOf(this);
      Nadlan.dialog.confirm("A new link", "The current address stops at once. Send the customer the new one.", "New link").then(function (ok) {
        if (!ok) { return; }
        act(Nadlan.api.post(base + "/" + id + "/renew")).then(function (res) {
          if (!res) { return; }
          var url = publicUrl(res.token);
          copyText(url, function () { });
          showMade("New link made and copied: <input class=\"input public-url\" readonly>");
          $made.find(".public-url").val(url);
        });
      });
    });
    $form.on("click", "[data-act=delete]", function () {
      var id = idOf(this);
      Nadlan.dialog.confirm("Delete the public link", "It stops working at once and leaves the list.", "Delete").then(function (ok) {
        if (ok) { act(Nadlan.api.del(base + "/" + id), "Deleted."); }
      });
    });

    load();
    return d;
  }

  Nadlan.presentLink = { open: open };
})(window, jQuery);
