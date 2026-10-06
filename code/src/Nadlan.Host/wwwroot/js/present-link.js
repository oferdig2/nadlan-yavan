// "Present": asks for the title the customer will read (never the Portfolio's internal name), then opens the
// presentation (present.html) in a new tab or copies its link to send. The link works for anyone signed in with
// access to those properties.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /** @param {{ portfolioId?: number, assetIds?: number[] }} what */
  function open(what) {
    var $form = $(
      "<form class=\"form\">" +
        "<label>Title the customer sees<input class=\"input\" name=\"title\" maxlength=\"120\" placeholder=\"e.g. Sea-view plots for the Smiths\"></label>" +
        "<div class=\"muted\">Leave empty for “4 properties · Skroponeria”. Only marketing photos and videos are shown.</div>" +
        "<div class=\"muted\" data-role=\"copied\" hidden>Link copied.</div>" +
      "</form>");

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
      width: 460,
      buttons: [
        { text: "Open", primary: true, click: function () {
          // Opened before the request ends, so the browser doesn't block it as a pop-up; filled in once the link is back.
          var tab = window.open("", "_blank");
          link().then(function (url) { if (tab) { tab.opener = null; tab.location = url; } else { window.open(url, "_blank", "noopener"); } d.close(); },
            function () { if (tab) { tab.close(); } });
        } },
        { text: "Copy link", click: function () {
          link().then(function (url) {
            var done = function () { $form.find("[data-role=copied]").prop("hidden", false); };
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(url).then(done, function () { window.prompt("Copy this link:", url); });
            } else { window.prompt("Copy this link:", url); }
          }, function () { /* shown in the dialog */ });
        } },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    return d;
  }

  Nadlan.presentLink = { open: open };
})(window, jQuery);
