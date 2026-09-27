// Professionals linked to an Asset (Engineer, Attorney, Topographer, customer contact, other): list, add, remove.
// The Managing Contact is separate (a field on the Asset).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var TYPES = [
    { code: "Engineer", label: "Engineer", role: "ENGINEER" },
    { code: "Attorney", label: "Attorney", role: "ATTORNEY" },
    { code: "Topographer", label: "Topographer", role: "TOPOGRAPHER" },
    { code: "CustomerContact", label: "Customer contact", role: "CUSTOMER" },
    { code: "Other", label: "Other", role: "OTHER" }
  ];

  function label(code) {
    var t = TYPES.filter(function (x) { return x.code === code; })[0];
    return t ? t.label : code;
  }

  function openAddDialog(assetId) {
    return Nadlan.reference.load().then(function (ref) {
      return new Promise(function (resolve) {
        var done = false;
        var $form = $(
          "<form class=\"form\">" +
            "<label>Role<select class=\"input\" name=\"relationshipType\">" +
              TYPES.map(function (t) { return "<option value=\"" + t.code + "\">" + t.label + "</option>"; }).join("") +
            "</select></label>" +
            "<div class=\"relation-slot\"></div>" +
            "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"2\"></textarea></label>" +
          "</form>");

        // A new Contact created here starts with the matching role (e.g. Engineer).
        function roleIdFor(code) {
          var t = TYPES.filter(function (x) { return x.code === code; })[0];
          var role = t && (ref.contactRoles || []).filter(function (r) { return r.code === t.role; })[0];
          return role ? [role.id] : [];
        }

        var person = Nadlan.initializeEntityRelationEditor($form.find(".relation-slot"), {
          label: "Contact",
          emptyText: "Select or create the Contact",
          source: Nadlan.entitySources.contact,
          renderSummary: Nadlan.contactEditor.summaryHtml,
          edit: function (current) {
            return Nadlan.contactEditor.open(current ? current.contactId : null, { roleIds: roleIdFor($form.find("[name=relationshipType]").val()) });
          }
        });

        var d = Nadlan.dialog.open({
          title: "Link professional",
          content: $form,
          buttons: [
            { text: "Link", primary: true, click: save },
            { text: "Cancel", click: function () { d.close(); } }
          ],
          onClose: function () { resolve(done); }
        });

        function save() {
          var contact = person.getValue();
          if (!contact) { d.showError("Select the Contact."); return; }
          var data = Nadlan.dialog.readForm($form);
          d.busy(true);
          Nadlan.api.post("/api/assets/" + assetId + "/contacts", {
            contactId: contact.contactId, relationshipType: data.relationshipType, notes: data.notes
          }).then(function () {
            done = true;
            d.close();
          }, function (err) { d.busy(false); d.showError(err.message); });
        }
      });
    });
  }

  /** Renders the Asset's professionals into $slot, with its own Add button (hidden, like Remove, when canEdit is false). */
  function render($slot, assetId, canEdit) {
    var esc = Nadlan.format.escapeHtml;
    $slot.html("<div class=\"card-section-head\"><span>Professionals</span>" +
      (canEdit === false ? "" : "<button type=\"button\" class=\"btn btn-small\" data-add-prof>+ Add</button>") + "</div><div class=\"prof-list muted\">Loading…</div>");
    var $list = $slot.find(".prof-list");

    function load() {
      Nadlan.api.get("/api/assets/" + assetId + "/contacts").then(function (links) {
        $list.removeClass("muted").html(links.length ? "<ul class=\"card-assets\">" + links.map(function (l) {
          return "<li><div class=\"row-body\"><div><strong>" + esc(label(l.relationshipType)) + "</strong> " + esc(l.displayName) + "</div>" +
            "<div class=\"muted\">" + esc([l.phone, l.email].filter(Boolean).join(" · ")) + (l.notes ? " · " + esc(l.notes) : "") + "</div></div>" +
            (canEdit === false ? "" : "<button type=\"button\" class=\"btn btn-small\" data-remove-prof=\"" + l.assetContactId + "\">Remove</button>") + "</li>";
        }).join("") + "</ul>" : "<span class=\"muted\">No professionals linked.</span>");
      }, function (err) { $list.html("<span class=\"error\">" + esc(err.message) + "</span>"); });
    }

    $slot.on("click", "[data-add-prof]", function () {
      openAddDialog(assetId).then(function (added) { if (added) { load(); } }, function (err) { Nadlan.dialog.showError("Could not open", err); });
    });
    $slot.on("click", "[data-remove-prof]", function () {
      var id = Number($(this).data("removeProf"));
      Nadlan.dialog.confirm("Unlink professional", "Unlink this professional from the Asset?", "Unlink").then(function (ok) {
        if (ok) { Nadlan.api.del("/api/assets/" + assetId + "/contacts/" + id).then(load, function (err) { Nadlan.dialog.showError("Could not unlink", err); }); }
      });
    });

    load();
  }

  Nadlan.assetContacts = { render: render };
})(window, jQuery);
