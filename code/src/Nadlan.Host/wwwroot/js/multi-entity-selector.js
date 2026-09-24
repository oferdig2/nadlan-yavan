// MultiEntitySelector: search, add to a chip list, keep searching. Values are the selected ids.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {jQuery|HTMLElement} container  empty element to render into
   * @param {{ source: object, placeholder?: string, onChange?: function(Array): void }} options
   */
  function initializeMultiEntitySelector(container, options) {
    var src = options.source;
    var esc = Nadlan.format.escapeHtml;
    var selected = []; // [{ id, label }]
    var $root = $(container).addClass("multi-select").empty();
    var $chips = $("<div class=\"chips\"></div>").appendTo($root);
    var $input = $("<input type=\"text\" class=\"input\">").appendTo($root);

    function render() {
      $chips.html(selected.map(function (s) {
        return "<span class=\"chip\" data-id=\"" + esc(s.id) + "\">" + esc(s.label) +
          "<button type=\"button\" aria-label=\"Remove\">×</button></span>";
      }).join(""));
    }

    function changed() {
      render();
      if (options.onChange) { options.onChange(api.getValue()); }
    }

    $chips.on("click", "button", function () {
      var id = String($(this).parent().data("id"));
      selected = selected.filter(function (s) { return String(s.id) !== id; });
      changed();
    });

    Nadlan.initializeEntitySelector($input, {
      source: src,
      placeholder: options.placeholder,
      clearOnSelect: true,
      onSelect: function (item) {
        var id = src.id(item);
        if (!selected.some(function (s) { return s.id === id; })) {
          selected.push({ id: id, label: src.label(item) });
          changed();
        }
      }
    });

    var api = {
      getValue: function () { return selected.map(function (s) { return s.id; }); },
      clear: function () { selected = []; changed(); }
    };
    return api;
  }

  Nadlan.initializeMultiEntitySelector = initializeMultiEntitySelector;
})(window, jQuery);
