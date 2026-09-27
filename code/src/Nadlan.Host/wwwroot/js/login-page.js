// Sign-in page: Google (when configured) or email + password; "forgot password" sends a reset link when email is set up.
// There is no sign-up: accounts are created by an Admin.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var params = new URLSearchParams(window.location.search);
  var returnUrl = safeReturnUrl(params.get("returnUrl"));
  var $error = $("[data-role=error]");
  var $ok = $("[data-role=ok]");

  // Same rule as the server: only paths on this site.
  function safeReturnUrl(url) {
    return url && url.charAt(0) === "/" && url.slice(0, 2) !== "//" && url.slice(0, 2) !== "/\\" ? url : "/";
  }

  function showError(message) { $ok.prop("hidden", true); $error.text(message || "").prop("hidden", !message); }
  function showOk(message) { $error.prop("hidden", true); $ok.text(message || "").prop("hidden", !message); }

  // Messages for errors the Google round trip brings back in the URL.
  var SSO_ERRORS = {
    SSO_NO_ACCOUNT: function (email) { return "There is no account for " + (email || "this Google account") + ". Ask your administrator to add you."; },
    SSO_EMAIL_UNVERIFIED: function () { return "Google did not confirm an email address for this account."; },
    LOGIN_DISABLED: function () { return "This account is disabled. Ask your administrator."; },
    SSO_FAILED: function () { return "Google sign-in did not complete. Try again."; },
    SSO_NOT_CONFIGURED: function () { return "Google sign-in is not set up on this server."; }
  };
  var ssoError = params.get("error");
  if (ssoError) { showError((SSO_ERRORS[ssoError] || SSO_ERRORS.SSO_FAILED)(params.get("email"))); }

  var passwordResetByEmail = false;
  Nadlan.api.get("/api/auth/options").then(function (o) {
    $("[data-role=google]").prop("hidden", !o.googleEnabled);
    $("[data-role=google-link]").attr("href", "/api/auth/google?returnUrl=" + encodeURIComponent(returnUrl));
    passwordResetByEmail = o.passwordResetByEmail;
  });

  $("[data-role=login]").on("submit", function (e) {
    e.preventDefault();
    var $form = $(this);
    var $btn = $form.find("button[type=submit]").prop("disabled", true);
    showError("");
    Nadlan.api.post("/api/auth/login", {
      email: $form.find("[name=email]").val(),
      password: $form.find("[name=password]").val()
    }).then(function (r) {
      window.location.href = r.mustChangePassword ? "/password.html?returnUrl=" + encodeURIComponent(returnUrl) : returnUrl;
    }, function (err) {
      $btn.prop("disabled", false);
      $form.find("[name=password]").val("").trigger("focus");
      showError(err.message);
    });
  });

  $("[data-role=show-forgot]").on("click", function (e) {
    e.preventDefault();
    if (!passwordResetByEmail) {
      showError("Password reset by email is not set up. Ask your administrator for a reset link.");
      return;
    }
    $("[data-role=forgot] [name=email]").val($("[data-role=login] [name=email]").val());
    $("[data-role=login]").prop("hidden", true);
    $("[data-role=forgot]").prop("hidden", false).find("[name=email]").trigger("focus");
    showError("");
  });

  $("[data-role=show-login]").on("click", function (e) {
    e.preventDefault();
    $("[data-role=forgot]").prop("hidden", true);
    $("[data-role=login]").prop("hidden", false);
  });

  $("[data-role=forgot]").on("submit", function (e) {
    e.preventDefault();
    var $btn = $(this).find("button[type=submit]").prop("disabled", true);
    Nadlan.api.post("/api/auth/forgot", { email: $(this).find("[name=email]").val() }).then(function (r) {
      showOk(r.message);
    }, function (err) {
      $btn.prop("disabled", false);
      showError(err.message);
    });
  });
})(window, jQuery);
