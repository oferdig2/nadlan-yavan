// Admin page composition: one tab per section; each tab is rebuilt when opened so it shows fresh data.
// Tabs show by permission (Users/Roles: MANAGE_USERS, Contacts: contact permissions, lists: MANAGE_METADATA);
// the server enforces the same rules on every call.
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
    if (tab === "users") {
      Nadlan.initializeAdminUsers($panel);
    } else if (tab === "roles") {
      Nadlan.initializeAdminRoles($panel);
    } else if (tab === "contacts") {
      Nadlan.initializeAdminContacts($panel);
    } else {
      Nadlan.initializeMetadataTableEditor($panel, $.extend({ table: tab }, LISTS[tab]));
    }
    try { window.localStorage.setItem("nadlan.adminTab", tab); } catch (e) { /* private mode: fine */ }
  }

  // The Files dialog reads storage on/off and the size limit from here (the map page sets it the same way).
  Nadlan.clientConfig = {};

  Nadlan.session.ready.then(function () {
    Nadlan.api.get("/api/config/client").then(function (config) { Nadlan.clientConfig = config; },
      function (err) { if (window.console) { console.warn("Client config not loaded; file uploads may be unavailable.", err); } });

    Nadlan.session.applyRequires();
    var allowed = $("[data-tab]").filter(function () { return !this.hidden; }).map(function () { return $(this).data("tab"); }).get();
    if (!allowed.length) {
      $("#admin-panel").html("<div class=\"muted\">You have no administration rights. <a href=\"/\">Back to the map</a></div>");
      return;
    }

    $("[data-tab]").on("click", function () { show($(this).data("tab")); });
    var last = null;
    try { last = window.localStorage.getItem("nadlan.adminTab"); } catch (e) { /* ignore */ }
    show(allowed.indexOf(last) >= 0 ? last : allowed[0]);
  }, function (err) {
    $("#admin-panel").html("<div class=\"error\">" + Nadlan.format.escapeHtml(err.message) + "</div>");
  });
})(window, jQuery);
