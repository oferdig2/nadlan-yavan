// EntitySelector: type-ahead over any entity. Backend-driven, so large lists are never loaded into the page.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * Standard entity sources. Each: search(term) -> Promise<items>, id(item), label(item), detail(item).
   */
  var sources = {
    contact: {
      search: function (term) { return Nadlan.api.get("/api/contacts", { q: term, limit: 20 }); },
      id: function (c) { return c.contactId; },
      label: function (c) { return c.displayName; },
      detail: function (c) { return [c.phone, c.email].filter(Boolean).join(" · "); }
    },
    portfolio: {
      search: function (term) { return Nadlan.api.get("/api/portfolios", { q: term, limit: 20 }); },
      id: function (p) { return p.portfolioId; },
      label: function (p) { return p.name; },
      detail: function (p) { return p.typeName + " · " + p.assetCount + " assets"; }
    }
  };

  /**
   * @param {jQuery|HTMLElement} input  a text input
   * @param {{ source: object, onSelect: function(object): void, placeholder?: string, clearOnSelect?: boolean }} options
   */
  function initializeEntitySelector(input, options) {
    var $input = $(input).attr({ placeholder: options.placeholder || "Type to search…", autocomplete: "off" });
    var src = options.source;
    var esc = Nadlan.format.escapeHtml;

    $input.autocomplete({
      minLength: 0,
      delay: 200,
      source: function (request, respond) {
        src.search(request.term).then(function (items) {
          respond(items.map(function (item) {
            return { label: src.label(item), value: src.label(item), item: item };
          }));
        }, function () { respond([]); });
      },
      select: function (event, ui) {
        options.onSelect(ui.item.item);
        if (options.clearOnSelect) {
          $input.val("");
          event.preventDefault();
        }
      }
    }).on("focus", function () {
      $input.autocomplete("search", $input.val()); // show first matches immediately
    });

    $input.autocomplete("instance")._renderItem = function (ul, row) {
      var detail = src.detail ? src.detail(row.item) : "";
      return $("<li>").append(
        "<div><span class=\"ac-label\">" + esc(row.label) + "</span>" +
        (detail ? "<span class=\"ac-detail\">" + esc(detail) + "</span>" : "") + "</div>"
      ).appendTo(ul);
    };

    return {
      focus: function () { $input.trigger("focus"); },
      destroy: function () { $input.autocomplete("destroy"); }
    };
  }

  Nadlan.entitySources = sources;
  Nadlan.initializeEntitySelector = initializeEntitySelector;
})(window, jQuery);
