// Admin > Users (Appendix 1 §9.3): every login account. No self-registration - users exist only when created here.
// Sign-in is Google (matched by email) and/or a password; revoking a login = deactivating the user.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function initializeAdminUsers(container) {
    var esc = Nadlan.format.escapeHtml;
    var $root = $(container).addClass("admin-users").html(
      "<div class=\"meta-toolbar\">" +
        "<h3>Users</h3>" +
        "<input type=\"text\" class=\"input\" data-f=\"q\" placeholder=\"Name, email or contact…\">" +
        "<label class=\"check\"><input type=\"checkbox\" data-f=\"inactive\" checked> Show deactivated</label>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"new\">+ New user</button>" +
      "</div>" +
      "<table class=\"meta-table\"><thead><tr>" +
        "<th>Name</th><th>Email</th><th>Role</th><th>Contact</th><th>Sign-in</th><th>Status</th><th>Last sign-in</th><th></th>" +
      "</tr></thead><tbody><tr><td colspan=\"8\" class=\"muted\">Loading…</td></tr></tbody></table>" +
      "<div class=\"muted meta-note\">Google sign-in works for any active user whose email is their Google account; " +
        "a password is optional. No one can sign up - add people here.</div>");

    function when(utc) {
      if (!utc) { return "<span class=\"muted\">never</span>"; }
      var d = new Date(utc.endsWith("Z") ? utc : utc + "Z");
      return esc(d.toLocaleString("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" }));
    }

    function status(u) {
      var parts = [u.isActive ? "<span class=\"badge\" style=\"background:#16a34a22;color:#15803d\">Active</span>"
                              : "<span class=\"badge badge-warn\">Login revoked</span>"];
      if (u.isLocked) { parts.push("<span class=\"badge badge-warn\">Locked</span>"); }
      if (u.mustChangePassword) { parts.push("<span class=\"badge\">Must change password</span>"); }
      return parts.join(" ");
    }

    var googleEnabled = false;
    function signIn(u) {
      var ways = (googleEnabled ? ["Google"] : []).concat(u.hasPassword ? ["password"] : []);
      return ways.length ? esc(ways.join(" · ")) : "<span class=\"error\">none yet</span>";
    }

    var seq = 0;
    function load() {
      var mine = ++seq;
      Nadlan.api.get("/api/admin/users", {
        q: $root.find("[data-f=q]").val(),
        includeInactive: $root.find("[data-f=inactive]").prop("checked")
      }).then(function (users) {
        if (mine !== seq) { return; }
        $root.find("tbody").html(users.map(function (u) {
          return "<tr class=\"" + (u.isActive ? "" : "inactive") + "\">" +
            "<td>" + esc(u.displayName) + "</td><td>" + esc(u.email) + "</td><td>" + esc(u.roleName) + "</td>" +
            "<td>" + (u.contactName ? esc(u.contactName) : "<span class=\"muted\">—</span>") + "</td>" +
            "<td>" + signIn(u) + "</td>" +
            "<td>" + status(u) + "</td>" +
            "<td>" + when(u.lastLoginUtc) + (u.lastLoginMethod ? " <span class=\"muted\">(" + esc(u.lastLoginMethod) + ")</span>" : "") + "</td>" +
            "<td class=\"row-buttons\">" +
              "<button type=\"button\" class=\"btn btn-small\" data-edit=\"" + u.userId + "\">Edit</button>" +
              "<button type=\"button\" class=\"btn btn-small\" data-access=\"" + u.userId + "\">Access</button>" +
              "<button type=\"button\" class=\"btn btn-small\" data-history=\"" + u.userId + "\" data-name=\"" + esc(u.email) + "\">History</button>" +
            "</td></tr>";
        }).join("") || "<tr><td colspan=\"8\" class=\"muted\">No users match.</td></tr>");
      }, function (err) {
        if (mine !== seq) { return; }
        $root.find("tbody").html("<tr><td colspan=\"8\" class=\"error\">" + esc(err.message) + "</td></tr>");
      });
    }

    function failed(err) { Nadlan.dialog.showError("Could not open", err); }

    var timer = null;
    $root.on("input", "[data-f=q]", function () { clearTimeout(timer); timer = setTimeout(load, 250); });
    $root.on("change", "[data-f=inactive]", load);
    $root.on("click", "[data-act=new]", function () {
      Nadlan.userEditor.open(null).then(function (changed) { if (changed) { load(); } }, failed);
    });
    $root.on("click", "[data-edit]", function () {
      Nadlan.userEditor.open(Number($(this).data("edit"))).then(function (changed) { if (changed) { load(); } }, failed);
    });
    $root.on("click", "[data-access]", function () {
      Nadlan.resourceAccessEditor.open(Number($(this).data("access"))).catch(failed);
    });
    $root.on("click", "[data-history]", function () {
      Nadlan.historyDialog.open({ entityType: "User", entityId: Number($(this).data("history")), title: $(this).attr("data-name") + " - history" });
    });

    Nadlan.api.get("/api/auth/options").then(function (o) { googleEnabled = o.googleEnabled; }, function () { /* keep false */ }).then(load);
  }

  Nadlan.initializeAdminUsers = initializeAdminUsers;
})(window, jQuery);
