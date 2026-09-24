// AssetEditor: create an Asset on a Parcel, or edit an existing Asset's business fields.
// Resolves with { assetId } when saved, null when cancelled.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function formHtml(a, ref, parcel) {
    var esc = Nadlan.format.escapeHtml;
    var num = function (x) { return x === null || x === undefined ? "" : esc(x); };
    var exclusive = a.isExclusive === true ? "yes" : a.isExclusive === false ? "no" : "";
    return "<form class=\"form\">" +
      "<div class=\"form-static\"><span class=\"card-label\">Parcel</span> " +
        Nadlan.format.registryId(parcel.registryId, parcel.registryIdIsProvisional) +
        (parcel.geographicArea ? " <span class=\"muted\">· " + esc(parcel.geographicArea) + "</span>" : "") + "</div>" +
      "<div class=\"relation-slot\"></div>" +
      "<div class=\"form-row\">" +
        "<label>Status<select class=\"input\" name=\"assetStatusId\" data-type=\"id\">" +
          Nadlan.reference.optionsHtml(Nadlan.reference.forPicker(ref.assetStatuses, a.assetStatusId), a.assetStatusId, "Select…") + "</select></label>" +
        "<label>Property type<select class=\"input\" name=\"propertyTypeId\" data-type=\"id\">" +
          Nadlan.reference.optionsHtml(Nadlan.reference.forPicker(ref.propertyTypes, a.propertyTypeId), a.propertyTypeId, "—") + "</select></label>" +
      "</div>" +
      "<div class=\"form-row\">" +
        "<label>Ask price<input class=\"input\" name=\"askPrice\" data-type=\"number\" inputmode=\"decimal\" value=\"" + num(a.askPrice) + "\"></label>" +
        "<label class=\"narrow\">Currency<input class=\"input\" name=\"currencyCode\" maxlength=\"3\" value=\"" + esc(a.currencyCode || "EUR") + "\"></label>" +
      "</div>" +
      "<div class=\"form-row\">" +
        "<label>House m²<input class=\"input\" name=\"houseSqm\" data-type=\"number\" value=\"" + num(a.houseSqm) + "\"></label>" +
        "<label>Exclusive<select class=\"input\" name=\"exclusive\">" +
          "<option value=\"\"" + (exclusive === "" ? " selected" : "") + ">Unknown</option>" +
          "<option value=\"yes\"" + (exclusive === "yes" ? " selected" : "") + ">Yes</option>" +
          "<option value=\"no\"" + (exclusive === "no" ? " selected" : "") + ">No</option></select></label>" +
      "</div>" +
      "<label>Special conditions<textarea class=\"input\" name=\"specialConditions\" rows=\"2\">" + esc(a.specialConditions || "") + "</textarea></label>" +
      "<label>Remarks<textarea class=\"input\" name=\"remarks\" rows=\"2\">" + esc(a.remarks || "") + "</textarea></label>" +
      "</form>";
  }

  /**
   * @param {{ parcel: object, assetId?: number }} options  parcel = Parcel summary; assetId set = edit mode
   */
  function open(options) {
    var loads = [Nadlan.reference.load()];
    if (options.assetId) { loads.push(Nadlan.api.get("/api/assets/" + options.assetId)); }

    return Promise.all(loads).then(function (loaded) {
      var ref = loaded[0];
      var detail = loaded[1] || null;
      var asset = detail ? detail.asset : { assetStatusId: defaultStatusId(ref) };
      var contact = detail && detail.managingContact
        ? { contactId: detail.managingContact.contactId, displayName: detail.managingContact.displayName,
            email: detail.managingContact.email, phone: detail.managingContact.phone }
        : null;

      return new Promise(function (resolve) {
        var $form = $(formHtml(asset, ref, options.parcel));
        var done = false;
        var relation = Nadlan.initializeEntityRelationEditor($form.find(".relation-slot"), {
          label: "Managing contact",
          emptyText: "No contact assigned",
          source: Nadlan.entitySources.contact,
          value: contact,
          renderSummary: Nadlan.contactEditor.summaryHtml,
          edit: function (current) { return Nadlan.contactEditor.open(current ? current.contactId : null); }
        });

        var d = Nadlan.dialog.open({
          title: options.assetId ? "Edit asset #" + options.assetId : "Create asset",
          content: $form,
          width: 520,
          buttons: [
            { text: "Save", primary: true, click: save },
            { text: "Cancel", click: function () { d.close(); } }
          ],
          onClose: function () { if (!done) { resolve(null); } }
        });
        $form.on("submit", function (e) { e.preventDefault(); save(); });

        function save() {
          var data = Nadlan.dialog.readForm($form);
          var managing = relation.getValue();
          var body = {
            parcelId: options.parcel.parcelId,
            managingContactId: managing ? managing.contactId : 0,
            propertyTypeId: data.propertyTypeId,
            assetStatusId: data.assetStatusId || 0,
            askPrice: data.askPrice,
            currencyCode: data.currencyCode,
            houseSqm: data.houseSqm,
            specialConditions: data.specialConditions,
            remarks: data.remarks,
            isExclusive: data.exclusive === "yes" ? true : data.exclusive === "no" ? false : null
          };
          if (body.askPrice !== null && isNaN(body.askPrice)) { d.showError("Ask price must be a number."); return; }
          if (body.houseSqm !== null && isNaN(body.houseSqm)) { d.showError("House m² must be a number."); return; }

          d.busy(true);
          var call = options.assetId
            ? Nadlan.api.put("/api/assets/" + options.assetId, body)
            : Nadlan.api.post("/api/assets", body);
          call.then(function (saved) {
            done = true;
            d.close();
            resolve({ assetId: saved.assetId });
          }, function (err) {
            d.busy(false);
            d.showError(err.message);
          });
        }
      });
    });
  }

  function defaultStatusId(ref) {
    var forSale = (ref.assetStatuses || []).filter(function (s) { return s.code === "FOR_SALE"; })[0];
    return forSale ? forSale.id : null;
  }

  Nadlan.assetEditor = { open: open };
})(window, jQuery);
