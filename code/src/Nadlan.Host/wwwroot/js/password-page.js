// Password page, two modes:
//  - ?token=...  a reset / invitation link: choose a password without signing in (then you are signed in);
//  - no token    the signed-in user changes their password (forced after an Admin-set password), or - signed in with
//                Google and without one - sets a first password. Google sign-in keeps working either way.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var params = new URLSearchParams(window.location.search);
  var token = params.get("token");
  var returnUrl = Nadlan.safeReturnUrl(params.get("returnUrl"));
  var $form = $("[data-role=form]");
  var $error = $("[data-role=error]");
  var needsCurrent = false;

  function showError(message) { $error.text(message || "").prop("hidden", !message); }
  function header(title, sub) { $("[data-role=title]").text(title); $("[data-role=sub]").text(sub); }

  if (token) {
    $("[data-role=current]").remove();
    $("[data-role=cancel]").attr("href", "/login.html");
    Nadlan.api.get("/api/auth/reset", { token: token }).then(function (r) {
      header(r.purpose === "Invite" ? "Welcome, " + r.displayName : "Reset password", "Choose a password for " + r.email + ".");
      $form.find("[name=username]").val(r.email);
      $form.prop("hidden", false).find("[name=password]").trigger("focus");
    }, function (err) {
      header("Link not valid", "");
      showError(err.message);
      $("[data-role=to-login]").prop("hidden", false);
    });
  } else {
    $("[data-role=cancel]").attr("href", returnUrl);
    Nadlan.api.get("/api/auth/me").then(function (me) {
      needsCurrent = me.hasPassword;
      $("[data-role=current]").prop("hidden", !needsCurrent);
      $form.find("[name=username]").val(me.email);
      if (me.mustChangePassword) {
        header("Choose a new password", "Your administrator set a temporary password. Choose your own to continue.");
        $("[data-role=cancel]").prop("hidden", true);
      } else {
        header(me.hasPassword ? "Change password" : "Set a password",
          me.hasPassword ? me.email : "You sign in with Google. A password lets you sign in with email too; Google keeps working.");
      }
      $form.prop("hidden", false).find(needsCurrent ? "[name=current]" : "[name=password]").trigger("focus");
    }, function (err) {
      if (err.status === 401) { window.location.href = "/login.html?returnUrl=" + encodeURIComponent("/password.html"); return; }
      showError(err.message);
    });
  }

  $form.on("submit", function (e) {
    e.preventDefault();
    var password = $form.find("[name=password]").val();
    if (password !== $form.find("[name=repeat]").val()) { showError("The two new passwords differ."); return; }
    showError("");
    var $btn = $form.find("button[type=submit]").prop("disabled", true);
    var call = token
      ? Nadlan.api.post("/api/auth/reset", { token: token, newPassword: password })
      : Nadlan.api.post("/api/auth/change-password", { currentPassword: needsCurrent ? $form.find("[name=current]").val() : null, newPassword: password });
    call.then(function () {
      window.location.href = token ? "/" : returnUrl;
    }, function (err) {
      $btn.prop("disabled", false);
      showError(err.message);
    });
  });
})(window, jQuery);
