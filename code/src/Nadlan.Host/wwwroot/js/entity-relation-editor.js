// EntityRelationEditor: compact summary of a linked entity + [Select] (search existing) + [✏] (create or edit).
// The parent form keeps its unsaved state; only this field's value changes.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ label: string, source: object, emptyText?: string,
   *           renderSummary: function(object): string,        // HTML for the linked entity
   *           edit: function(object|null): Promise<object>,   // quick dialog; resolves with the saved entity
   *           value?: object|null, onChange?: function(object|null): void }} options
   */
  function initializeEntityRelationEditor(container, options) {
    var esc = Nadlan.format.escapeHtml;
    var current = options.value || null;
    var $root = $(container).addClass("relation-editor").html(
      "<div class=\"relation-label\">" + esc(options.label) + "</div>" +
      "<div class=\"relation-body\">" +
        "<div class=\"relation-summary\"></div>" +
        "<div class=\"relation-actions\">" +
          "<button type=\"button\" class=\"btn\" data-action=\"select\">Select</button>" +
          "<button type=\"button\" class=\"btn\" data-action=\"edit\" title=\"Create new / edit selected\">✏</button>" +
        "</div>" +
      "</div>" +
      "<div class=\"relation-search\" hidden><input type=\"text\" class=\"input\"></div>");

    var $summary = $root.find(".relation-summary");
    var $search = $root.find(".relation-search");

    function render() {
      $summary.html(current ? options.renderSummary(current) : "<span class=\"muted\">" + esc(options.emptyText || "None") + "</span>");
    }

    function set(entity) {
      current = entity;
      render();
      if (options.onChange) { options.onChange(current); }
    }

    var selector = Nadlan.initializeEntitySelector($search.find("input"), {
      source: options.source,
      clearOnSelect: true,
      onSelect: function (item) {
        $search.prop("hidden", true);
        set(item);
      }
    });

    $root.on("click", "[data-action=select]", function () {
      $search.prop("hidden", false);
      selector.focus();
    });

    $root.on("click", "[data-action=edit]", function () {
      options.edit(current).then(function (saved) { if (saved) { set(saved); } });
    });

    render();
    return {
      getValue: function () { return current; },
      setValue: function (entity) { current = entity; render(); }
    };
  }

  Nadlan.initializeEntityRelationEditor = initializeEntityRelationEditor;
})(window, jQuery);
