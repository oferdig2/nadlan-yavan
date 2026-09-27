// The signed-in user for the page: who, which permissions, and the header user menu (change password, sign out).
// UI hints only - the server checks every call. Elements with data-requires="PERM_A PERM_B" show if the user has any.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var user = null;

  function can(permission) {
    return !!user && (user.isAdmin || user.permissions.indexOf(permission) >= 0);
  }

  function canAny(list) {
    return list.some(can);
  }

  function renderMenu() {
    var esc = Nadlan.format ? Nadlan.format.escapeHtml : function (s) { return $("<div>").text(s).html(); };
    var $menu = $(
      "<div class=\"user-menu\">" +
        "<button type=\"button\" aria-haspopup=\"true\">" + esc(user.displayName) + "<span class=\"user-role\">" + esc(user.roleName) + "</span></button>" +
        "<ul hidden>" +
          "<li><span>" + esc(user.email) + (user.loginMethod ? " · via " + esc(user.loginMethod) : "") + "</span></li>" +
          "<li><button type=\"button\" data-act=\"password\">" + (user.hasPassword ? "Change password" : "Set a password") + "</button></li>" +
          "<li><button type=\"button\" data-act=\"logout\">Sign out</button></li>" +
        "</ul>" +
      "</div>");
    $(".app-header").append($menu);
    var $list = $menu.find("ul");
    $menu.children("button").on("click", function (e) { e.stopPropagation(); $list.prop("hidden", !$list.prop("hidden")); });
    $(document).on("click", function () { $list.prop("hidden", true); });
    $menu.on("click", "[data-act=password]", function () {
      window.location.href = "/password.html?returnUrl=" + encodeURIComponent(window.location.pathname + window.location.search);
    });
    $menu.on("click", "[data-act=logout]", function () {
      Nadlan.api.post("/api/auth/logout").then(done, done);
      function done() { window.location.href = "/login.html"; }
    });
  }

  function applyRequires() {
    $("[data-requires]").each(function () {
      $(this).prop("hidden", !canAny(String($(this).data("requires")).split(/\s+/)));
    });
  }

  var ready = Nadlan.api.get("/api/auth/me").then(function (me) {
    user = me;
    if (me.mustChangePassword) {
      window.location.href = "/password.html?returnUrl=" + encodeURIComponent(window.location.pathname + window.location.search);
      return new Promise(function () { /* leaving the page */ });
    }

    $(function () { applyRequires(); renderMenu(); });
    return me;
  });

  Nadlan.session = {
    /** Resolves with the user once known; pages start from here. */
    ready: ready,
    user: function () { return user; },
    can: can,
    canAny: canAny,
    seesFileCategory: function (category) { return !!user && (user.isAdmin || user.fileCategories.indexOf(category) >= 0); },
    applyRequires: applyRequires
  };
})(window, jQuery);
