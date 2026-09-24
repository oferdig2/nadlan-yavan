// Small lookup lists (statuses, property types, portfolio types, contact roles, areas), loaded once per page.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var promise = null;

  Nadlan.reference = {
    load: function () {
      promise = promise || Nadlan.api.get("/api/reference");
      return promise;
    },

    // Active items only, for pickers. Existing records may still point at inactive ones.
    active: function (list) {
      return (list || []).filter(function (x) { return x.isActive; });
    },

    // For edit forms: active items plus the record's current value even if it was deactivated since,
    // so opening and saving a record never silently drops a value.
    forPicker: function (list, currentId) {
      return (list || []).filter(function (x) { return x.isActive || x.id === currentId; }).map(function (x) {
        return x.isActive ? x : $.extend({}, x, { name: x.name + " (inactive)" });
      });
    },

    optionsHtml: function (list, selectedId, emptyLabel) {
      var esc = Nadlan.format.escapeHtml;
      var html = emptyLabel === undefined ? "" : "<option value=\"\">" + esc(emptyLabel) + "</option>";
      (list || []).forEach(function (x) {
        html += "<option value=\"" + x.id + "\"" + (x.id === selectedId ? " selected" : "") + ">" + esc(x.name) + "</option>";
      });
      return html;
    }
  };
})(window, jQuery);
