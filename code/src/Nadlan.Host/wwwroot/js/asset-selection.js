// The set of Assets the user has picked (from cards, the results list, or "select all").
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function createAssetSelection() {
    var ids = new Set();
    var listeners = [];

    function changed() { listeners.forEach(function (fn) { fn(ids.size); }); }

    return {
      has: function (id) { return ids.has(id); },
      toggle: function (id, on) {
        if (on === undefined ? !ids.has(id) : on) { ids.add(id); } else { ids.delete(id); }
        changed();
      },
      addMany: function (list) { list.forEach(function (id) { ids.add(id); }); changed(); },
      clear: function () { ids.clear(); changed(); },
      values: function () { return Array.from(ids); },
      size: function () { return ids.size; },
      onChange: function (fn) { listeners.push(fn); }
    };
  }

  Nadlan.createAssetSelection = createAssetSelection;
})(window);
