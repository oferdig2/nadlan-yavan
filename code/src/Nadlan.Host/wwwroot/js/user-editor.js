// UserEditor (Appendix 2 §26): create/edit a user, revoke/restore the login, passwords (set, one-time link, remove),
// unlock, sign out everywhere, API tokens, delete. Resolves with true when anything changed.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  function when(utc) {
    if (!utc) { return "never"; }
    var d = new Date(utc.endsWith("Z") ? utc : utc + "Z");
    return d.toLocaleString("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function roleOptions(roles, selectedId) {
    return roles.filter(function (r) { return r.isActive || r.securityRoleId === selectedId; }).map(function (r) {
      return "<option value=\"" + r.securityRoleId + "\"" + (r.securityRoleId === selectedId ? " selected" : "") + ">" +
        esc(r.name) + (r.isActive ? "" : " (inactive)") + "</option>";
    }).join("");
  }

  /** @param {number|null} userId  null = new user */
  function open(userId) {
    return Promise.all([
      Nadlan.api.get("/api/admin/roles"),
      userId ? Nadlan.api.get("/api/admin/users/" + userId) : Promise.resolve(null),
      Nadlan.api.get("/api/auth/options")
    ]).then(function (loaded) {
      var roles = loaded[0];
      var user = loaded[1];
      var options = loaded[2];
      var isNew = !user;
      var defaultRole = roles.filter(function (r) { return r.code === "VIEWER"; })[0] || roles[0];
      var changed = false;

      var $form = $(
        "<form class=\"form\">" +
          "<label>Email (also their Google account)<input class=\"input\" name=\"email\" type=\"email\" value=\"" + esc(user ? user.email : "") + "\" required></label>" +
          "<label>Name<input class=\"input\" name=\"displayName\" value=\"" + esc(user ? user.displayName : "") + "\" placeholder=\"From the email if empty\"></label>" +
          "<label>Role<select class=\"input\" name=\"securityRoleId\" data-type=\"id\">" + roleOptions(roles, user ? user.securityRoleId : defaultRole.securityRoleId) + "</select></label>" +
          "<div class=\"contact-slot\"></div>" +
          "<label class=\"check\"><input type=\"checkbox\" name=\"isActive\"" + (!user || user.isActive ? " checked" : "") + "> Can sign in (unticked = login revoked)</label>" +
          (isNew
            ? "<fieldset class=\"geometry-box\"><legend>Password (optional)</legend>" +
                "<div class=\"muted\">Leave empty for Google sign-in only, or to send a \"choose your password\" link afterwards.</div>" +
                "<div class=\"form-row\"><label>Initial password<input class=\"input\" name=\"password\" autocomplete=\"new-password\"></label>" +
                "<button type=\"button\" class=\"btn\" data-act=\"generate\" style=\"align-self:flex-end\">Generate</button></div>" +
                "<label class=\"check\"><input type=\"checkbox\" name=\"mustChangePassword\" checked> Must choose a new one at first sign-in</label>" +
              "</fieldset>"
            : "<fieldset class=\"geometry-box\"><legend>Sign-in</legend>" +
                "<div>" + (options.googleEnabled ? "Google: <strong>works</strong> for " + esc(user.email) : "Google sign-in is not configured on this server") + "</div>" +
                "<div>Password: " + (user.hasPassword ? "<strong>set</strong> <span class=\"muted\">(changed " + esc(when(user.passwordChangedUtc)) + ")</span>" : "<span class=\"muted\">none</span>") + "</div>" +
                (user.isLocked ? "<div class=\"warn\">Locked after failed attempts until " + esc(when(user.lockedUntilUtc)) + "</div>" : "") +
                (user.hasPassword ? "<label class=\"check\"><input type=\"checkbox\" name=\"mustChangePassword\"" + (user.mustChangePassword ? " checked" : "") + "> Must choose a new password at next sign-in</label>" : "") +
                "<div class=\"btn-row\">" +
                  "<button type=\"button\" class=\"btn\" data-act=\"set-password\">Set password…</button>" +
                  "<button type=\"button\" class=\"btn\" data-act=\"link\">Password link…</button>" +
                  (user.hasPassword ? "<button type=\"button\" class=\"btn\" data-act=\"remove-password\">Remove password</button>" : "") +
                  (user.isLocked ? "<button type=\"button\" class=\"btn\" data-act=\"unlock\">Unlock</button>" : "") +
                  "<button type=\"button\" class=\"btn\" data-act=\"sign-out\">Sign out everywhere</button>" +
                "</div>" +
                "<div class=\"btn-row\">" +
                  "<button type=\"button\" class=\"btn\" data-act=\"access\">Access to objects…</button>" +
                  "<button type=\"button\" class=\"btn\" data-act=\"tokens\">API tokens…</button>" +
                  "<button type=\"button\" class=\"btn\" data-act=\"delete\" style=\"margin-left:auto;color:var(--error)\">Delete user</button>" +
                "</div>" +
                "<div class=\"muted\">Created " + esc(when(user.createdUtc)) + " · last sign-in " + esc(when(user.lastLoginUtc)) +
                  (user.lastLoginMethod ? " (" + esc(user.lastLoginMethod) + ")" : "") + "</div>" +
              "</fieldset>") +
          "<div class=\"auth-ok done-note\"></div>" +
        "</form>");

      // Business identity: Assets whose Managing Contact is this Contact are the user's "own" Assets (spec §8.2).
      var contact = user && user.contactId ? { contactId: user.contactId, displayName: user.contactName } : null;
      var relation = Nadlan.initializeEntityRelationEditor($form.find(".contact-slot"), {
        label: "Linked contact (own Assets = Assets this Contact manages)",
        emptyText: "No contact - internal user",
        source: Nadlan.entitySources.contact,
        value: contact,
        renderSummary: function (c) {
          return "<span class=\"summary-title\">" + esc(c.displayName) + "</span> <a href=\"#\" data-act=\"unlink\">unlink</a>";
        },
        edit: function (current) { return Nadlan.contactEditor.open(current ? current.contactId : null); }
      });
      $form.on("click", "[data-act=unlink]", function (e) { e.preventDefault(); relation.setValue(null); });

      return new Promise(function (resolve) {
        var d = Nadlan.dialog.open({
          title: isNew ? "New user" : "User " + user.email,
          content: $form,
          width: 580,
          buttons: [
            { text: isNew ? "Create" : "Save", primary: true, click: save },
            { text: "Close", click: function () { d.close(); } }
          ],
          onClose: function () { resolve(changed); }
        });
        $form.on("submit", function (e) { e.preventDefault(); save(); });

        $form.on("click", "[data-act=generate]", function () { $form.find("[name=password]").val(randomPassword()); });

        function save() {
          var data = Nadlan.dialog.readForm($form);
          var linked = relation.getValue();
          var body = {
            email: data.email, displayName: data.displayName, contactId: linked ? linked.contactId : null,
            securityRoleId: data.securityRoleId || 0, isActive: data.isActive,
            mustChangePassword: data.mustChangePassword === true, password: isNew ? data.password : null
          };
          d.busy(true);
          var call = isNew ? Nadlan.api.post("/api/admin/users", body) : Nadlan.api.put("/api/admin/users/" + user.userId, body);
          call.then(function (saved) {
            changed = true;
            d.close();
            if (isNew) {
              // Straight into the new user, so the Admin can send a password link or grant access.
              open(saved.userId);
            }
          }, function (err) { d.busy(false); d.showError(err.message); });
        }

        function act(promise, message) {
          d.busy(true);
          return promise.then(function (r) {
            changed = true;
            d.busy(false);
            if (message) { d.showError(""); $form.find(".done-note").text(message); }
            return r;
          }, function (err) { d.busy(false); d.showError(err.message); throw err; });
        }

        function reopen() { d.close(); open(user.userId); }

        $form.on("click", "[data-act=set-password]", function () {
          setPasswordDialog(user).then(function (set) { if (set) { changed = true; reopen(); } });
        });
        $form.on("click", "[data-act=link]", function () {
          passwordLinkDialog(user, options).then(function (made) { if (made) { changed = true; } });
        });
        $form.on("click", "[data-act=remove-password]", function () {
          Nadlan.dialog.confirm("Remove password", "Remove the password of <strong>" + esc(user.email) +
            "</strong>? They can then sign in with Google only.", "Remove").then(function (ok) {
            if (ok) { act(Nadlan.api.del("/api/admin/users/" + user.userId + "/password")).then(reopen, function () { /* shown */ }); }
          });
        });
        $form.on("click", "[data-act=unlock]", function () {
          act(Nadlan.api.post("/api/admin/users/" + user.userId + "/unlock")).then(reopen, function () { /* shown */ });
        });
        $form.on("click", "[data-act=sign-out]", function () {
          act(Nadlan.api.post("/api/admin/users/" + user.userId + "/sign-out"), user.email + " is signed out on every device.")
            .catch(function () { /* shown */ });
        });
        $form.on("click", "[data-act=access]", function () {
          Nadlan.resourceAccessEditor.open(user.userId).catch(function (err) { d.showError(err.message); });
        });
        $form.on("click", "[data-act=tokens]", function () {
          apiTokensDialog(user).catch(function (err) { d.showError(err.message); });
        });
        $form.on("click", "[data-act=delete]", function () {
          Nadlan.dialog.confirm("Delete user", "Delete the user <strong>" + esc(user.email) + "</strong>? Their sign-in, grants and tokens go; " +
            "Contacts, Assets and history stay. To only stop them signing in, untick \"Can sign in\" instead.", "Delete").then(function (ok) {
            if (!ok) { return; }
            act(Nadlan.api.del("/api/admin/users/" + user.userId)).then(function () { d.close(); }, function () { /* shown */ });
          });
        });
      });
    });
  }

  function randomPassword() {
    var alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    var bytes = new Uint32Array(14);
    window.crypto.getRandomValues(bytes);
    return Array.prototype.map.call(bytes, function (b) { return alphabet.charAt(b % alphabet.length); }).join("");
  }

  // The Admin types (or generates) a password to hand over; by default the user must replace it at first sign-in.
  function setPasswordDialog(user) {
    return new Promise(function (resolve) {
      var done = false;
      var $form = $(
        "<form class=\"form\">" +
          "<div class=\"muted\">Give this password to " + esc(user.displayName) + " yourself (it is not emailed).</div>" +
          "<div class=\"form-row\"><label>Password<input class=\"input\" name=\"password\" autocomplete=\"new-password\"></label>" +
          "<button type=\"button\" class=\"btn\" data-act=\"generate\" style=\"align-self:flex-end\">Generate</button></div>" +
          "<label class=\"check\"><input type=\"checkbox\" name=\"mustChangePassword\" checked> Must choose a new one at next sign-in</label>" +
        "</form>");
      $form.find("[name=password]").val(randomPassword());
      $form.on("click", "[data-act=generate]", function () { $form.find("[name=password]").val(randomPassword()); });
      var d = Nadlan.dialog.open({
        title: "Set password - " + user.email,
        content: $form,
        width: 440,
        buttons: [
          { text: "Set password", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ],
        onClose: function () { resolve(done); }
      });
      $form.on("submit", function (e) { e.preventDefault(); save(); });

      function save() {
        var data = Nadlan.dialog.readForm($form);
        d.busy(true);
        Nadlan.api.post("/api/admin/users/" + user.userId + "/password", { password: data.password, mustChangePassword: data.mustChangePassword })
          .then(function () { done = true; d.close(); }, function (err) { d.busy(false); d.showError(err.message); });
      }
    });
  }

  // A one-time link where the user chooses their password (invitation or reset); copy it, or email it when SMTP is set up.
  function passwordLinkDialog(user, options) {
    return new Promise(function (resolve) {
      var made = false;
      var $body = $(
        "<div class=\"form\">" +
          "<div>Creates a one-time link where " + esc(user.displayName) + " chooses a password. Earlier links stop working.</div>" +
          (options.passwordResetByEmail ? "<label class=\"check\"><input type=\"checkbox\" name=\"send\" checked> Also email it to " + esc(user.email) + "</label>"
                                        : "<div class=\"muted\">Email is not set up on this server: copy the link and send it yourself.</div>") +
          "<div class=\"link-result\" hidden><input class=\"input\" readonly> <div class=\"muted link-note\"></div></div>" +
        "</div>");
      var d = Nadlan.dialog.open({
        title: "Password link - " + user.email,
        content: $body,
        width: 520,
        buttons: [
          { text: "Create link", primary: true, click: create },
          { text: "Close", click: function () { d.close(); } }
        ],
        onClose: function () { resolve(made); }
      });

      function create() {
        d.busy(true);
        Nadlan.api.post("/api/admin/users/" + user.userId + "/password-link", { sendEmail: $body.find("[name=send]").prop("checked") === true })
          .then(function (r) {
            made = true;
            d.busy(false);
            var $input = $body.find(".link-result").prop("hidden", false).find("input").val(r.link);
            $input.trigger("focus").trigger("select");
            $body.find(".link-note").text((r.emailed ? "Emailed. " : "") + "Works once, for " + r.expiresHours + " hours." +
              (navigator.clipboard ? "" : " Copy it with Ctrl+C."));
            if (navigator.clipboard) { navigator.clipboard.writeText(r.link).then(function () { $body.find(".link-note").append(" Copied to the clipboard."); }, function () { /* manual copy */ }); }
          }, function (err) { d.busy(false); d.showError(err.message); });
      }
    });
  }

  // Bearer tokens for tools (the KAEK importer's --token / importer.json "Token"). Shown once.
  function apiTokensDialog(user) {
    var $body = $(
      "<div class=\"form\">" +
        "<div class=\"muted\">A token acts as " + esc(user.displayName) + " with their role's permissions. It is shown once - store it in the tool's settings.</div>" +
        "<table class=\"meta-table\"><thead><tr><th>Name</th><th>Token</th><th>Created</th><th>Last used</th><th></th></tr></thead><tbody></tbody></table>" +
        "<div class=\"form-row\"><label>New token name<input class=\"input\" name=\"name\" placeholder=\"e.g. KAEK importer - office PC\"></label>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"create\" style=\"align-self:flex-end\">Create token</button></div>" +
        "<div class=\"token-result\" hidden><input class=\"input\" readonly><div class=\"muted\">Copy it now - it can't be shown again.</div></div>" +
      "</div>");
    var d = Nadlan.dialog.open({
      title: "API tokens - " + user.email,
      content: $body,
      width: 640,
      modal: false,
      buttons: [{ text: "Close", click: function () { d.close(); } }]
    });

    function load() {
      return Nadlan.api.get("/api/admin/users/" + user.userId + "/tokens").then(function (tokens) {
        $body.find("tbody").html(tokens.map(function (t) {
          return "<tr class=\"" + (t.revokedUtc ? "inactive" : "") + "\"><td>" + esc(t.name) + "</td><td class=\"kaek\">" + esc(t.tokenPrefix) + "…</td>" +
            "<td>" + esc(when(t.createdUtc)) + "</td><td>" + esc(when(t.lastUsedUtc)) + "</td>" +
            "<td>" + (t.revokedUtc ? "<span class=\"muted\">revoked</span>" : "<button type=\"button\" class=\"btn btn-small\" data-revoke=\"" + t.apiTokenId + "\">Revoke</button>") + "</td></tr>";
        }).join("") || "<tr><td colspan=\"5\" class=\"muted\">No tokens.</td></tr>");
      }, function (err) { d.showError(err.message); });
    }

    $body.on("click", "[data-act=create]", function () {
      d.busy(true);
      Nadlan.api.post("/api/admin/users/" + user.userId + "/tokens", { name: $body.find("[name=name]").val() }).then(function (r) {
        d.busy(false);
        d.showError("");
        $body.find("[name=name]").val("");
        $body.find(".token-result").prop("hidden", false).find("input").val(r.token).trigger("focus").trigger("select");
        load();
      }, function (err) { d.busy(false); d.showError(err.message); });
    });
    $body.on("click", "[data-revoke]", function () {
      var id = Number($(this).data("revoke"));
      Nadlan.dialog.confirm("Revoke token", "Revoke this token? Tools using it stop working at once.", "Revoke").then(function (ok) {
        if (ok) { Nadlan.api.del("/api/admin/users/" + user.userId + "/tokens/" + id).then(load, function (err) { d.showError(err.message); }); }
      });
    });

    return load();
  }

  Nadlan.userEditor = { open: open };
})(window, jQuery);
