// Admin page composition: one tab per section; each tab is rebuilt when opened so it shows fresh data.
// TODO(auth slice): Admin-only page.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;

  var LISTS = {
    "asset-statuses": { title: "Asset statuses", hasColor: true },
    "property-types": { title: "Property types" },
    "portfolio-types": { title: "Portfolio types" },
    "contact-roles": { title: "Contact roles" },
    "file-types": { title: "File / document types", hasCategory: true },
    "geographic-areas": { title: "Geographic areas" }
  };

  function show(tab) {
    $("[data-tab]").removeClass("active").filter("[data-tab=" + tab + "]").addClass("active");
    var $panel = $("<div></div>");
    $("#admin-panel").empty().append($panel);
    if (tab === "contacts") {
      Nadlan.initializeAdminContacts($panel);
    } else {
      Nadlan.initializeMetadataTableEditor($panel, $.extend({ table: tab }, LISTS[tab]));
    }
    try { window.localStorage.setItem("nadlan.adminTab", tab); } catch (e) { /* private mode: fine */ }
  }

  $("[data-tab]").on("click", function () { show($(this).data("tab")); });

  var last = null;
  try { last = window.localStorage.getItem("nadlan.adminTab"); } catch (e) { /* ignore */ }
  show(last && (last === "contacts" || LISTS[last]) ? last : "contacts");
})(window, jQuery);
