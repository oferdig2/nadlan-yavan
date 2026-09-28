// "Connect the KAEK importer": opened by the importer in its own browser. The page is protected (signed-out visitors
// go through the login page and come back here). Connect issues a token for the signed-in user and puts it in
// #importer-token, where the importer, which drives this tab, picks it up and closes the tab.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var device = new URLSearchParams(window.location.search).get("device") || "";

  function show(role, text) { $("[data-role=" + role + "]").text(text || "").prop("hidden", !text); }

  Nadlan.api.get("/api/auth/me").then(function (me) {
    if (me.mustChangePassword) {
      window.location.href = "/password.html?returnUrl=" + encodeURIComponent(window.location.pathname + window.location.search);
      return;
    }

    $("[data-role=who]").text("Signed in as " + me.displayName + " (" + me.email + ")" + (device ? " · computer " + device : ""));
    var allowed = me.isAdmin || me.permissions.indexOf("EDIT_ALL_PARCELS") >= 0;
    if (!allowed) {
      show("error", "Your account may not create Parcels, so the importer could not save any. Ask an administrator for a role with \"Create and edit Parcels\".");
      return;
    }

    $("[data-role=ask]").prop("hidden", false);
    $("[data-role=connect]").trigger("focus");
  });

  $("[data-role=connect]").on("click", function () {
    var $btn = $(this).prop("disabled", true);
    show("error", "");
    Nadlan.api.post("/api/importer/token", { device: device }).then(function (r) {
      $("[data-role=ask]").prop("hidden", true);
      show("ok", "Connected. The importer takes it from here - this tab closes by itself.");
      $("#importer-token").attr("data-token", r.token);
    }, function (err) {
      $btn.prop("disabled", false);
      show("error", err.message);
    });
  });

  $("[data-role=switch]").on("click", function (e) {
    e.preventDefault();
    var back = window.location.pathname + window.location.search;
    Nadlan.api.post("/api/auth/logout").then(go, go);
    function go() { window.location.href = "/login.html?returnUrl=" + encodeURIComponent(back); }
  });
})(window, jQuery);
