// Admin > Roles: what each security role may do (RolePermission, spec §4). Admin always has everything.
// Grant-only permissions (VIEW_ASSET on one Asset, ...) are not here - they are given per object in a user's Access.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function initializeAdminRoles(container) {
    var esc = Nadlan.format.escapeHtml;
    var roles = [];
    var permissions = [];
    var selectedId = null;
    var $root = $(container).addClass("admin-roles").html(
      "<div class=\"meta-toolbar\"><h3>Roles</h3>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"new\">+ New role</button></div>" +
      "<div class=\"roles-layout\">" +
        "<ul class=\"roles-list\"></ul>" +
        "<div class=\"role-detail\"><div class=\"muted\">Loading…</div></div>" +
      "</div>" +
      "<div class=\"form-error\" hidden></div>");

    function showError(message) { $root.find(".form-error").text(message || "").prop("hidden", !message); }

    function renderList() {
      $root.find(".roles-list").html(roles.map(function (r) {
        return "<li data-role-id=\"" + r.securityRoleId + "\" class=\"" + (r.securityRoleId === selectedId ? "active" : "") + (r.isActive ? "" : " inactive") + "\">" +
          "<div>" + esc(r.name) + "</div><div class=\"muted\">" + esc(r.code) + " · " + r.userCount + " user(s)</div></li>";
      }).join(""));
    }

    function renderDetail() {
      var role = roles.filter(function (r) { return r.securityRoleId === selectedId; })[0];
      var $detail = $root.find(".role-detail");
      if (!role) { $detail.html("<div class=\"muted\">Choose a role.</div>"); return; }

      var has = {};
      role.permissionCodes.forEach(function (c) { has[c] = true; });
      var groups = [];
      var byGroup = {};
      // "Manage users" is Admin-only (the server refuses it in a role), so it isn't offered.
      permissions.filter(function (p) { return p.scope === "Role" && p.code !== "MANAGE_USERS"; }).forEach(function (p) {
        if (!byGroup[p.groupName]) { byGroup[p.groupName] = []; groups.push(p.groupName); }
        byGroup[p.groupName].push(p);
      });

      $detail.html(
        "<form class=\"form\">" +
          "<div class=\"form-row\"><label>Name<input class=\"input\" name=\"name\" value=\"" + esc(role.name) + "\"" + (role.isSystem ? " disabled" : "") + "></label>" +
          "<label class=\"check\" style=\"align-self:flex-end\"><input type=\"checkbox\" name=\"isActive\"" + (role.isActive ? " checked" : "") + (role.isSystem ? " disabled" : "") + "> Active</label></div>" +
          "<label>Description<input class=\"input\" name=\"description\" value=\"" + esc(role.description || "") + "\"" + (role.isSystem ? " disabled" : "") + "></label>" +
          (role.isSystem
            ? "<div class=\"auth-ok done-note\">Admin sees and does everything; its permissions can't be changed.</div>"
            : groups.map(function (g) {
                return "<fieldset class=\"checks role-perms\"><legend>" + esc(g) + "</legend>" + byGroup[g].map(function (p) {
                  return "<label class=\"check\" title=\"" + esc(p.description || "") + "\"><input type=\"checkbox\" data-perm=\"" + esc(p.code) + "\"" +
                    (has[p.code] ? " checked" : "") + "> " + esc(p.name) + "</label>";
                }).join("") + "</fieldset>";
              }).join("") +
              "<div class=\"muted\">Hover a permission for details. Changes apply to the role's users at their next click.</div>" +
              "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-primary\" data-act=\"save\">Save role</button>" +
              "<button type=\"button\" class=\"btn\" data-act=\"history\">History</button></div>") +
        "</form>");
    }

    function load() {
      return Promise.all([Nadlan.api.get("/api/admin/roles"), Nadlan.api.get("/api/admin/permissions")]).then(function (loaded) {
        roles = loaded[0];
        permissions = loaded[1];
        if (selectedId === null && roles.length) { selectedId = roles[0].securityRoleId; }
        showError("");
        renderList();
        renderDetail();
      }, function (err) { showError(err.message); });
    }

    $root.on("click", ".roles-list li", function () {
      selectedId = Number($(this).data("roleId"));
      renderList();
      renderDetail();
    });

    $root.on("click", "[data-act=save]", function () {
      var $form = $root.find(".role-detail form");
      var $btn = $(this).prop("disabled", true);
      Nadlan.api.put("/api/admin/roles/" + selectedId, {
        name: $form.find("[name=name]").val(),
        description: $form.find("[name=description]").val(),
        isActive: $form.find("[name=isActive]").prop("checked"),
        permissionCodes: $form.find("[data-perm]:checked").map(function () { return $(this).data("perm"); }).get()
      }).then(function () { $btn.prop("disabled", false); load(); }, function (err) { $btn.prop("disabled", false); showError(err.message); });
    });

    $root.on("click", "[data-act=history]", function () {
      var role = roles.filter(function (r) { return r.securityRoleId === selectedId; })[0];
      Nadlan.historyDialog.open({ entityType: "Role", entityId: selectedId, title: "Role " + (role ? role.name : "") + " - history" });
    });

    $root.on("click", "[data-act=new]", function () {
      var $form = $("<form class=\"form\"><label>Code<input class=\"input\" name=\"code\" placeholder=\"e.g. PARTNER\"></label>" +
        "<label>Name<input class=\"input\" name=\"name\"></label><label>Description<input class=\"input\" name=\"description\"></label></form>");
      var d = Nadlan.dialog.open({
        title: "New role",
        content: $form,
        buttons: [
          { text: "Create", primary: true, click: create },
          { text: "Cancel", click: function () { d.close(); } }
        ]
      });
      $form.on("submit", function (e) { e.preventDefault(); create(); });
      function create() {
        var data = Nadlan.dialog.readForm($form);
        d.busy(true);
        Nadlan.api.post("/api/admin/roles", data).then(function (r) {
          d.close();
          selectedId = r.securityRoleId;
          load();
        }, function (err) { d.busy(false); d.showError(err.message); });
      }
    });

    load();
  }

  Nadlan.initializeAdminRoles = initializeAdminRoles;
})(window, jQuery);
