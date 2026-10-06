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

    function link() {
      var q = what.portfolioId ? "portfolio=" + what.portfolioId : "assets=" + what.assetIds.join(",");
      var title = $.trim($form.find("[name=title]").val());
      return window.location.origin + "/present.html?" + q + (title ? "&title=" + encodeURIComponent(title) : "");
    }

    var d = Nadlan.dialog.open({
      title: "Present to a customer",
      content: $form,
      width: 460,
      buttons: [
        { text: "Open", primary: true, click: function () { window.open(link(), "_blank", "noopener"); d.close(); } },
        { text: "Copy link", click: function () {
          var url = link();
          var done = function () { $form.find("[data-role=copied]").prop("hidden", false); };
          if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(url).then(done, function () { window.prompt("Copy this link:", url); });
          } else { window.prompt("Copy this link:", url); }
        } },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    return d;
  }

  Nadlan.presentLink = { open: open };
})(window, jQuery);
