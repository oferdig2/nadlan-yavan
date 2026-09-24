// ContactQuickEditor: create/edit a Contact in a dialog without leaving the current workflow.
// Resolves with a contact summary ({ contactId, displayName, email, phone, isActive }) or null when cancelled.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function formHtml(c, roles) {
    var esc = Nadlan.format.escapeHtml;
    var v = function (x) { return esc(x || ""); };
    var roleIds = c.roleIds || [];
    return "<form class=\"form\">" +
      "<label>Type<select class=\"input\" name=\"contactType\">" +
        "<option value=\"Person\"" + (c.contactType === "Organization" ? "" : " selected") + ">Person</option>" +
        "<option value=\"Organization\"" + (c.contactType === "Organization" ? " selected" : "") + ">Organization</option></select></label>" +
      "<label>Display name<input class=\"input\" name=\"displayName\" value=\"" + v(c.displayName) + "\" placeholder=\"Leave empty to use first + last name\"></label>" +
      "<div class=\"form-row\">" +
        "<label>First name<input class=\"input\" name=\"firstName\" value=\"" + v(c.firstName) + "\"></label>" +
        "<label>Last name<input class=\"input\" name=\"lastName\" value=\"" + v(c.lastName) + "\"></label>" +
      "</div>" +
      "<label>Company<input class=\"input\" name=\"companyName\" value=\"" + v(c.companyName) + "\"></label>" +
      "<div class=\"form-row\">" +
        "<label>Mobile<input class=\"input\" name=\"cellPhone\" value=\"" + v(c.cellPhone) + "\"></label>" +
        "<label>Phone<input class=\"input\" name=\"phone\" value=\"" + v(c.phone) + "\"></label>" +
      "</div>" +
      "<label>Email<input class=\"input\" name=\"email\" type=\"email\" value=\"" + v(c.email) + "\"></label>" +
      "<fieldset class=\"checks\"><legend>Roles</legend>" +
        roles.map(function (r) {
          return "<label class=\"check\"><input type=\"checkbox\" data-role-id=\"" + r.id + "\"" +
            (roleIds.indexOf(r.id) >= 0 ? " checked" : "") + "> " + esc(r.name) + (r.isActive ? "" : " (inactive)") + "</label>";
        }).join("") +
      "</fieldset>" +
      "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"2\">" + v(c.notes) + "</textarea></label>" +
      "<label class=\"check\"><input type=\"checkbox\" name=\"isActive\"" + (c.isActive === false ? "" : " checked") + "> Active</label>" +
      "</form>";
  }

  /** @param {number|null} contactId  null = create */
  function open(contactId, defaults) {
    return Promise.all([
      Nadlan.reference.load(),
      contactId ? Nadlan.api.get("/api/contacts/" + contactId) : Promise.resolve($.extend({ isActive: true }, defaults))
    ]).then(function (loaded) {
      var contact = loaded[1];
      var assigned = contact.roleIds || [];
      var roles = (loaded[0].contactRoles || []).filter(function (r) { return r.isActive || assigned.indexOf(r.id) >= 0; });
      return new Promise(function (resolve) {
        var $form = $(formHtml(contact, roles));
        var done = false;
        var d = Nadlan.dialog.open({
          title: contactId ? "Edit contact" : "New contact",
          content: $form,
          width: 480,
          buttons: [
            { text: "Save", primary: true, click: save },
            { text: "Cancel", click: function () { d.close(); } }
          ],
          onClose: function () { if (!done) { resolve(null); } }
        });
        $form.on("submit", function (e) { e.preventDefault(); save(); });

        function save() {
          var data = Nadlan.dialog.readForm($form);
          data.roleIds = $form.find("[data-role-id]:checked").map(function () { return $(this).data("roleId"); }).get();
          d.busy(true);
          var call = contactId ? Nadlan.api.put("/api/contacts/" + contactId, data) : Nadlan.api.post("/api/contacts", data);
          call.then(function (saved) {
            done = true;
            d.close();
            resolve({
              contactId: saved.contactId, displayName: saved.displayName, email: saved.email,
              phone: saved.cellPhone || saved.phone, isActive: saved.isActive
            });
          }, function (err) {
            d.busy(false);
            d.showError(err.message);
          });
        }
      });
    });
  }

  function summaryHtml(c) {
    var esc = Nadlan.format.escapeHtml;
    return "<div class=\"summary-title\">" + esc(c.displayName) + "</div>" +
      [c.phone, c.email].filter(Boolean).map(function (x) { return "<div class=\"muted\">" + esc(x) + "</div>"; }).join("");
  }

  Nadlan.contactEditor = { open: open, summaryHtml: summaryHtml };
})(window, jQuery);
