// Small lookup lists (statuses, property types, portfolio types, contact roles, areas). Every editor asks for them when
// it opens; they are re-read when older than half a minute, so a list an Admin changed meanwhile (a new contact role,
// a new property type) is in the form - an edit form built from a stale list would drop or blank that value on save.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var MAX_AGE_MS = 30000;
  var promise = null;
  var loadedAt = 0;
  var last = null; // the last good answer: used if a refresh fails

  Nadlan.reference = {
    load: function () {
      if (promise && Date.now() - loadedAt < MAX_AGE_MS) { return promise; }
      loadedAt = Date.now();
      promise = Nadlan.api.get("/api/reference").then(function (ref) { last = ref; return ref; }, function (err) {
        loadedAt = 0; // try again next time
        if (last) { return last; }
        promise = null;
        throw err;
      });
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

    // Property types an Asset may be given: "Beach" only for users who may edit every Asset (Admin, Data person) - the
    // server refuses it from anyone else - unless the Asset already is a beach.
    propertyTypesFor: function (list, currentId) {
      var mayBeach = !!Nadlan.session && Nadlan.session.can("EDIT_ALL_ASSETS");
      return this.forPicker(list, currentId).filter(function (t) { return mayBeach || t.code !== "BEACH" || t.id === currentId; });
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
