// The customer a public link was sent to (migration 027, owner 2026-11-10): an optional Contact, for the Admin's reports
// only - "no one except Admin sees it". Shown and set only with VIEW_ANALYTICS_PAGE; the server leaves it out of every
// answer to anyone else and refuses (403) a customer sent by anyone else. Never shown to the visitor, never in the title.
// Used by the Present dialog (present-link.js) and the analytics page (analytics-page.js).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s === null || s === undefined ? "" : String(s)); };

  function canSet() { return !!(Nadlan.session && Nadlan.session.can && Nadlan.session.can("VIEW_ANALYTICS_PAGE")); }

  var customerRole = null;
  function customerRoleId() {
    if (customerRole !== null) { return Promise.resolve(customerRole); }
    return Nadlan.reference.load().then(function (ref) {
      var r = (ref.contactRoles || []).filter(function (x) { return x.code === "CUSTOMER"; })[0];
      customerRole = r ? r.id : 0;
      return customerRole;
    }, function () { return 0; });
  }

  // Contacts matching the text: those with the role Customer first, then any other active Contact.
  function search(text) {
    return customerRoleId().then(function (roleId) {
      return Promise.all([
        roleId ? Nadlan.api.get("/api/contacts", { q: text, roleId: roleId, limit: 10 }) : Promise.resolve([]),
        Nadlan.api.get("/api/contacts", { q: text, limit: 20 })
      ]);
    }).then(function (lists) {
      var seen = {}, out = [];
      lists[0].concat(lists[1]).forEach(function (c) { if (!seen[c.contactId]) { seen[c.contactId] = true; out.push(c); } });
      return out.slice(0, 20);
    });
  }

  function describe(c) { return c.displayName + (c.phone ? " · " + c.phone : "") + (c.email ? " · " + c.email : ""); }

  /**
   * A customer field: a search box (name, email or phone), "+ New" (the contact dialog, role Customer) and × to clear.
   * opts: { contactId, contactName, onChange(contact|null) }. Returns { $el, value() } - value() = contactId or null.
   */
  function field(opts) {
    opts = opts || {};
    var chosen = opts.contactId ? { contactId: opts.contactId, displayName: opts.contactName || "#" + opts.contactId } : null;
    var $el = $("<span class=\"link-customer\"><input class=\"input\" placeholder=\"Search a contact: name, email or phone\" autocomplete=\"off\" aria-label=\"Customer\">" +
      "<button type=\"button\" class=\"btn btn-small\" data-lc=\"clear\" title=\"No customer\">×</button>" +
      "<button type=\"button\" class=\"btn btn-small\" data-lc=\"new\">+ New</button></span>");
    var $input = $el.find("input");

    function set(c, quiet) {
      chosen = c;
      $input.val(c ? c.displayName : "");
      $el.find("[data-lc=clear]").prop("hidden", !c);
      if (!quiet && opts.onChange) { opts.onChange(c); }
    }

    $input.autocomplete({
      minLength: 1,
      delay: 200,
      appendTo: $el.closest(".ui-dialog").length ? $el.closest(".ui-dialog") : null,
      source: function (req, res) { search(req.term).then(function (list) { res(list.map(function (c) { return { label: describe(c), value: c.displayName, c: c }; })); }, function () { res([]); }); },
      select: function (e, ui) { set(ui.item.c); return false; }
    }).on("blur", function () { if (!chosen || $input.val() !== chosen.displayName) { $input.val(chosen ? chosen.displayName : ""); } });

    $el.on("click", "[data-lc=clear]", function () { set(null); });
    $el.on("click", "[data-lc=new]", function () {
      customerRoleId().then(function (roleId) {
        return Nadlan.contactEditor.open(null, { roleIds: roleId ? [roleId] : [] });
      }).then(function (c) { if (c) { set({ contactId: c.contactId, displayName: c.displayName }); } },
        function (err) { Nadlan.dialog.showError("New contact", err); });
    });
    if (!(Nadlan.session && Nadlan.session.can("MANAGE_CONTACTS"))) { $el.find("[data-lc=new]").remove(); }
    set(chosen, true);
    return { $el: $el, value: function () { return chosen ? chosen.contactId : null; } };
  }

  /** "Customer: Nira & Eyal" (greyed when the contact is deactivated) or "Customer: —", with "change". */
  function lineHtml(link) {
    return "<span class=\"link-customer-line\">Customer: " + (link.contactId
      ? "<span class=\"" + (link.contactIsActive === false ? "muted link-customer-inactive\" title=\"This contact is deactivated" : "") + "\">" + esc(link.contactName) + "</span>"
      : "—") + " <button type=\"button\" class=\"an-link\" data-lc-change=\"" + link.id + "\">change</button></span>";
  }

  /** Set, change or clear the customer of a link (PATCH .../contact). Resolves with the updated link, or null when cancelled. */
  function change(link) {
    return new Promise(function (resolve) {
      var $form = $("<form class=\"form\"><p class=\"muted\">For reports only: only Admins see it - never the customer, nor other staff.</p></form>");
      var f = field({ contactId: link.contactId, contactName: link.contactName });
      $("<label>Customer</label>").append(f.$el).appendTo($form);
      var done = false;
      var d = Nadlan.dialog.open({
        title: "Customer of “" + (link.title || link.name || "this link") + "”",
        content: $form,
        width: 520,
        buttons: [
          { text: "Save", primary: true, click: function () {
            d.busy(true);
            Nadlan.api.patch("/api/presentation/links/" + link.id + "/contact", { contactId: f.value() }).then(function (saved) {
              done = true; d.close(); resolve(saved);
            }, function (err) { d.busy(false); d.showError(err.message); });
          } },
          { text: "Cancel", click: function () { d.close(); } }
        ],
        onClose: function () { if (!done) { resolve(null); } }
      });
    });
  }

  Nadlan.linkCustomer = { canSet: canSet, field: field, lineHtml: lineHtml, change: change };
})(window, jQuery);
