// Legal Owners of a Parcel: list, add (existing or new Contact + optional %), remove.
// Legal ownership belongs to the Parcel and never changes an Asset's Managing Contact.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function openAddDialog(parcelId) {
    return Nadlan.reference.load().then(function (ref) {
      var legalOwnerRole = (ref.contactRoles || []).filter(function (r) { return r.code === "LEGAL_OWNER"; })[0];
      return new Promise(function (resolve) {
        var done = false;
        var $form = $(
          "<form class=\"form\">" +
            "<div class=\"relation-slot\"></div>" +
            "<div class=\"form-row\">" +
              "<label>Ownership %<input class=\"input\" name=\"ownershipPercent\" data-type=\"number\" placeholder=\"optional\"></label>" +
            "</div>" +
            "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"2\"></textarea></label>" +
          "</form>");
        var owner = Nadlan.initializeEntityRelationEditor($form.find(".relation-slot"), {
          label: "Legal owner",
          emptyText: "Select or create the owner's Contact",
          source: Nadlan.entitySources.contact,
          renderSummary: Nadlan.contactEditor.summaryHtml,
          edit: function (current) {
            return Nadlan.contactEditor.open(current ? current.contactId : null, legalOwnerRole ? { roleIds: [legalOwnerRole.id] } : undefined);
          }
        });

        var d = Nadlan.dialog.open({
          title: "Add legal owner",
          content: $form,
          buttons: [
            { text: "Save", primary: true, click: save },
            { text: "Cancel", click: function () { d.close(); } }
          ],
          onClose: function () { resolve(done); }
        });

        function save() {
          var contact = owner.getValue();
          if (!contact) { d.showError("Select the owner's Contact."); return; }
          var data = Nadlan.dialog.readForm($form);
          if (data.ownershipPercent !== null && isNaN(data.ownershipPercent)) { d.showError("Ownership % must be a number."); return; }
          d.busy(true);
          Nadlan.api.put("/api/parcels/" + parcelId + "/legal-owners/" + contact.contactId,
            { ownershipPercent: data.ownershipPercent, notes: data.notes }).then(function () {
            done = true;
            d.close();
          }, function (err) { d.busy(false); d.showError(err.message); });
        }
      });
    });
  }

  /** Renders the owners of a Parcel into $slot and keeps it up to date after add/remove. canEdit=false hides Remove. */
  function render($slot, parcelId, canEdit) {
    var esc = Nadlan.format.escapeHtml;

    function load() {
      Nadlan.api.get("/api/parcels/" + parcelId + "/legal-owners").then(function (owners) {
        $slot.html(owners.length ? "<ul class=\"card-assets\">" + owners.map(function (o) {
          return "<li><div class=\"row-body\"><div>" + esc(o.displayName) +
            (o.ownershipPercent !== null ? " <span class=\"muted\">" + esc(o.ownershipPercent) + "%</span>" : "") + "</div>" +
            (o.notes ? "<div class=\"muted\">" + esc(o.notes) + "</div>" : "") + "</div>" +
            (canEdit === false ? "" : "<button type=\"button\" class=\"btn btn-small\" data-remove-owner=\"" + o.contactId + "\">Remove</button>") + "</li>";
        }).join("") + "</ul>" : "<div class=\"muted\">No legal owner recorded.</div>");
      }, function (err) { $slot.html("<span class=\"error\">" + esc(err.message) + "</span>"); });
    }

    $slot.off("click.owners").on("click.owners", "[data-remove-owner]", function () {
      var contactId = Number($(this).data("removeOwner"));
      Nadlan.dialog.confirm("Remove legal owner", "Remove this legal owner from the Parcel?", "Remove").then(function (ok) {
        if (ok) { Nadlan.api.del("/api/parcels/" + parcelId + "/legal-owners/" + contactId).then(load, function (err) { Nadlan.dialog.showError("Could not remove the legal owner", err); }); }
      });
    });

    load();
    return {
      reload: load,
      add: function () { return openAddDialog(parcelId).then(function (added) { if (added) { load(); } }); }
    };
  }

  Nadlan.legalOwners = { render: render };
})(window, jQuery);
