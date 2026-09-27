// History dialog: the audit trail of one entity (Parcel, Asset, Portfolio, Contact), newest first.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function when(utc) {
    var d = new Date(utc.endsWith("Z") ? utc : utc + "Z"); // server sends UTC without a zone marker
    return d.toLocaleString("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  /** @param {{ entityType: string, entityId: number, title: string }} options */
  function open(options) {
    var esc = Nadlan.format.escapeHtml;
    var $body = $("<div class=\"history\"><div class=\"muted\">Loading…</div></div>");
    var d = Nadlan.dialog.open({
      title: options.title,
      content: $body,
      width: 520,
      modal: false,
      buttons: [{ text: "Close", click: function () { d.close(); } }]
    });

    Nadlan.api.get("/api/activity", { entityType: options.entityType, entityId: options.entityId, limit: 100 }).then(function (items) {
      if (!items.length) { $body.html("<div class=\"muted\">No history yet.</div>"); return; }
      $body.html("<ul class=\"history-list\">" + items.map(function (a) {
        return "<li><div class=\"history-when muted\">" + esc(when(a.createdUtc)) + "</div>" +
          "<div class=\"history-what\">" + esc(a.summary) + "</div></li>";
      }).join("") + "</ul>");
    }, function (err) {
      $body.html("<div class=\"error\">" + esc(err.message) + "</div>");
    });
    return d;
  }

  Nadlan.historyDialog = { open: open };
})(window, jQuery);
