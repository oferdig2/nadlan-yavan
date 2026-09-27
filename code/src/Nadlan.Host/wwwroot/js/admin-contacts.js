// Admin Contact management (Appendix 1 §9.2): search all Contacts (incl. inactive), filter by role, create, edit,
// activate/deactivate, history. Contacts are deactivated, never deleted (Scenario 25).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function initializeAdminContacts(container) {
    var esc = Nadlan.format.escapeHtml;
    var $root = $(container).addClass("admin-contacts");

    Nadlan.reference.load().then(function (ref) {
      $root.html(
        "<div class=\"meta-toolbar\">" +
          "<h3>Contacts</h3>" +
          "<input type=\"text\" class=\"input\" data-f=\"q\" placeholder=\"Name, email or phone…\">" +
          "<select class=\"input\" data-f=\"role\"><option value=\"\">All roles</option>" +
            (ref.contactRoles || []).map(function (r) { return "<option value=\"" + r.id + "\">" + esc(r.name) + "</option>"; }).join("") +
          "</select>" +
          "<label class=\"check\"><input type=\"checkbox\" data-f=\"inactive\" checked> Include inactive</label>" +
          "<button type=\"button\" class=\"btn btn-primary\" data-act=\"new\">+ New contact</button>" +
        "</div>" +
        "<table class=\"meta-table\"><thead><tr><th>Name</th><th>Phone</th><th>Email</th><th>Active</th><th></th></tr></thead>" +
        "<tbody><tr><td colspan=\"5\" class=\"muted\">Loading…</td></tr></tbody></table>" +
        "<div class=\"muted meta-note\">Showing up to 200 matches - type to narrow down.</div>");

      var seq = 0;
      function load() {
        var mine = ++seq;
        Nadlan.api.get("/api/contacts", {
          q: $root.find("[data-f=q]").val(),
          roleId: $root.find("[data-f=role]").val(),
          includeInactive: $root.find("[data-f=inactive]").prop("checked"),
          limit: 200
        }).then(function (list) {
          if (mine !== seq) { return; }
          $root.find("tbody").html(list.map(function (c) {
            return "<tr class=\"" + (c.isActive ? "" : "inactive") + "\">" +
              "<td>" + esc(c.displayName) + "</td><td>" + esc(c.phone || "") + "</td><td>" + esc(c.email || "") + "</td>" +
              "<td>" + (c.isActive ? "Yes" : "<span class=\"muted\">No</span>") + "</td>" +
              "<td class=\"row-buttons\"><button type=\"button\" class=\"btn btn-small\" data-edit=\"" + c.contactId + "\">Edit</button>" +
              "<button type=\"button\" class=\"btn btn-small\" data-files=\"" + c.contactId + "\" data-name=\"" + esc(c.displayName) + "\">Files</button>" +
              "<button type=\"button\" class=\"btn btn-small\" data-history=\"" + c.contactId + "\" data-name=\"" + esc(c.displayName) + "\">History</button></td></tr>";
          }).join("") || "<tr><td colspan=\"5\" class=\"muted\">No contacts match.</td></tr>");
        });
      }

      var timer = null;
      $root.on("input", "[data-f=q]", function () { clearTimeout(timer); timer = setTimeout(load, 250); });
      $root.on("change", "[data-f=role], [data-f=inactive]", load);
      $root.on("click", "[data-act=new]", function () { Nadlan.contactEditor.open(null).then(function (c) { if (c) { load(); } }); });
      $root.on("click", "[data-edit]", function () {
        Nadlan.contactEditor.open(Number($(this).data("edit"))).then(function (c) { if (c) { load(); } });
      });
      $root.on("click", "[data-files]", function () {
        Nadlan.filesDialog.open({ attachedToType: "Contact", attachedToId: Number($(this).data("files")), title: $(this).data("name") + " - files" });
      });
      $root.on("click", "[data-history]", function () {
        Nadlan.historyDialog.open({ entityType: "Contact", entityId: Number($(this).data("history")), title: $(this).data("name") + " - history" });
      });
      load();
    });
  }

  Nadlan.initializeAdminContacts = initializeAdminContacts;
})(window, jQuery);
