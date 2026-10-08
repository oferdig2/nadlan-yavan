// ParcelEditor: new-Parcel and edit-Parcel form. Geometry comes from ParcelDrawTool (draw on the map, paste
// coordinates, or - when editing - drag the corners of the current polygon).
// The dialog is non-modal so the polygon stays editable on the map while the form is open.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var openDialog = null;

  function formHtml(ref, p) {
    var esc = Nadlan.format.escapeHtml;
    var v = function (x) { return x === null || x === undefined ? "" : esc(x); };
    var editing = !!p.parcelId;
    var kaekValue = editing && !p.registryIdIsProvisional ? p.registryId : "";
    var kaekHint = editing && p.registryIdIsProvisional
      ? "Provisional: " + p.registryId + " - type the real KAEK to replace it"
      : editing ? "" : "Leave empty if unknown - a provisional id is generated";
    return "<form class=\"form\">" +
      "<fieldset class=\"geometry-box\"><legend>Polygon</legend>" +
        "<div class=\"geometry-state muted\"></div>" +
        "<div class=\"btn-row\">" +
          "<button type=\"button\" class=\"btn\" data-geo=\"draw\">" + (editing ? "Redraw" : "Draw again") + "</button>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"undo\">Undo corner</button>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"finish\">Finish</button>" +
        "</div>" +
        "<details><summary>…or paste coordinates</summary>" +
          "<textarea class=\"input paste\" rows=\"3\" placeholder=\"23.3669,38.5089 23.3667,38.5086 ...  (lon,lat - KML order) or GeoJSON\"></textarea>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"paste\">Show on map</button>" +
        "</details>" +
      "</fieldset>" +
      "<label>KAEK<input class=\"input\" name=\"registryId\" value=\"" + v(kaekValue) + "\" placeholder=\"" + esc(kaekHint) + "\"></label>" +
      "<label>Geographic area<select class=\"input\" name=\"geographicAreaId\" data-type=\"id\">" +
        Nadlan.reference.optionsHtml(Nadlan.reference.forPicker(ref.geographicAreas, p.geographicAreaId), p.geographicAreaId, "—") + "</select></label>" +
      "<div class=\"form-row\">" +
        "<label>OT<input class=\"input\" name=\"ot\" value=\"" + v(p.ot) + "\"></label><label class=\"narrow\">Ext<input class=\"input\" name=\"otExt\" value=\"" + v(p.otExt) + "\"></label>" +
        "<label>Plot<input class=\"input\" name=\"plotNumber\" value=\"" + v(p.plotNumber) + "\"></label><label class=\"narrow\">Ext<input class=\"input\" name=\"plotExt\" value=\"" + v(p.plotExt) + "\"></label>" +
      "</div>" +
      // Divided / united: the other OT / plot numbers as free text (V1: shown, not searched).
      "<label>Parcel<select class=\"input\" name=\"divisionStatus\">" +
        [["regular", "Regular"], ["divided", "Divided"], ["united", "United"]].map(function (o) {
          return "<option value=\"" + o[0] + "\"" + ((p.divisionStatus || "regular") === o[0] ? " selected" : "") + ">" + o[1] + "</option>";
        }).join("") + "</select></label>" +
      "<label data-related" + ((p.divisionStatus || "regular") === "regular" ? " hidden" : "") + ">Other OT / plot numbers" +
        "<textarea class=\"input\" name=\"relatedNumbers\" rows=\"2\" maxlength=\"500\" placeholder=\"Free text, e.g. 171a/3 and 171a/4\">" + v(p.relatedNumbers) + "</textarea></label>" +
      "<div class=\"form-row\">" +
        "<label>Official area m²<input class=\"input\" name=\"officialAreaSqm\" data-type=\"number\" value=\"" + v(p.officialAreaSqm) + "\"></label>" +
        "<label>Build factor<input class=\"input\" name=\"buildFactor\" data-type=\"number\" data-number=\"ratio\" value=\"" + v(p.buildFactor) + "\"></label>" +
        "<label>Inclination %<input class=\"input\" name=\"inclination\" data-type=\"number\" data-number=\"ratio\" value=\"" + v(p.inclination) + "\"></label>" +
      "</div>" +
      "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"2\">" + v(p.notes) + "</textarea></label>" +
      "<div class=\"overlap-box\" hidden></div>" +
      "</form>";
  }

  /**
   * @param {{ drawTool: object, parcelId?: number, onSaved: function(number): void, onShowParcel: function(number): void,
   *           onDeleted?: function(number, string): void }} options
   *        parcelId set = edit that Parcel; otherwise create a new one.
   */
  function open(options) {
    if (openDialog) { openDialog.close(); }
    var draw = options.drawTool;
    var editing = !!options.parcelId;

    return Promise.all([
      Nadlan.reference.load(),
      editing ? Nadlan.api.get("/api/parcels/" + options.parcelId) : Promise.resolve(null)
    ]).then(function (loaded) {
      var ref = loaded[0];
      var detail = loaded[1];
      var values = detail ? $.extend({ parcelId: options.parcelId }, detail.fields) : {};
      var $form = $(formHtml(ref, values));
      var acceptOverlaps = false;
      var shapeChanged = !editing; // a new Parcel always sends its polygon; an edit only if it was touched
      var esc = Nadlan.format.escapeHtml;

      function setGeometryState(hasShape) {
        $form.find(".geometry-state")
          .toggleClass("muted", !hasShape)
          .text(hasShape ? "Polygon ready - drag its corners to adjust." : "Click the map to add corners; double-click (or Finish) to close.");
        shapeChanged = true;
        acceptOverlaps = false; // a changed shape needs a fresh overlap check
        $form.find(".overlap-box").prop("hidden", true);
      }

      // The free text only for divided / united Parcels (the server drops it for regular ones).
      $form.on("change", "[name=divisionStatus]", function () {
        var $related = $form.find("[data-related]").prop("hidden", this.value === "regular");
        if (this.value !== "regular") { $related.find("textarea").trigger("focus"); }
      });

      $form.on("click", "[data-geo]", function () {
        d.showError("");
        switch ($(this).data("geo")) {
          case "draw": draw.start(); break;
          case "undo": draw.undo(); break;
          case "finish": if (!draw.finish()) { d.showError("Add at least 3 corners first."); } break;
          case "paste":
            try { draw.setCoordinates(Nadlan.parseCoordinates($form.find(".paste").val())); }
            catch (e) { d.showError(e.message); }
            break;
        }
      });

      var d = Nadlan.dialog.open({
        title: editing ? "Edit parcel " + (detail.fields.registryId || "#" + options.parcelId) : "New parcel",
        content: $form,
        width: 460,
        modal: false,
        position: { my: "left top", at: "left+12 top+60", of: window },
        buttons: [
          { text: "Save parcel", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ].concat(editing && Nadlan.session.user() && Nadlan.session.user().isAdmin ? [{ text: "Delete parcel", danger: true, click: remove }] : []),
        onClose: function () { draw.clear(); openDialog = null; }
      });
      openDialog = d;
      d.setGeometryState = setGeometryState;

      if (editing) {
        draw.setCoordinates(detail.geometry.coordinates); // current polygon with its holes, corners draggable
        shapeChanged = false;                                // showing it is not a change
        $form.find(".geometry-state").removeClass("muted").text("Current polygon - drag its corners to correct it, or Redraw.");
      } else {
        setGeometryState(false);
        draw.start();
      }

      function save() {
        var coordinates = draw.getCoordinates();
        if (!coordinates) { d.showError("Draw the polygon (or paste coordinates) first."); return; }
        var body = Nadlan.dialog.readForm($form);
        body.coordinates = shapeChanged ? coordinates : null;
        body.acceptOverlaps = acceptOverlaps;
        if (editing) { body.version = detail.version; } // edit check: refused if someone else saved meanwhile
        var notNumber = ["officialAreaSqm", "buildFactor", "inclination"].some(function (k) {
          return body[k] !== null && isNaN(body[k]);
        });
        if (notNumber) { d.showError("Area, build factor and inclination must be numbers."); return; }

        d.busy(true);
        d.showError("");
        var call = editing ? Nadlan.api.put("/api/parcels/" + options.parcelId, body) : Nadlan.api.post("/api/parcels", body);
        call.then(function (saved) {
          d.close();
          if (saved.warning) { Nadlan.dialog.notice("Overlap check skipped", saved.warning); }
          options.onSaved(saved.parcelId);
        }, function (err) {
          d.busy(false);
          if (err.status === 409 && err.code === "PARCEL_OVERLAPS") {
            showOverlaps(err.overlaps || []);
          } else if (err.status === 409 && err.code === "PARCEL_KAEK_EXISTS") {
            d.showError(err.message);
            if (err.existingParcelId != null) {
              $form.find(".overlap-box").prop("hidden", false).html(
                "<button type=\"button\" class=\"btn\" data-show=\"" + esc(err.existingParcelId) + "\">Show the existing parcel</button>");
            }
          } else {
            d.showError(err.message);
          }
        });
      }

      // Admin only (the server checks too). Refused while an Asset stands on the Parcel; its files go with it.
      function remove() {
        var name = detail.fields.registryId || "#" + options.parcelId;
        d.showError("");
        Nadlan.api.get("/api/parcels/" + options.parcelId + "/delete-preview").then(function (p) {
          if (p.assets > 0) {
            d.showError("Parcel " + name + " carries " + p.assets + " Asset(s), so it can't be deleted.");
            return null;
          }
          return Nadlan.dialog.confirm("Delete parcel", "Delete parcel <strong>" + esc(name) + "</strong>" +
            (p.files ? " and its <strong>" + p.files + " file(s)</strong>" : "") +
            "? Legal owners and access grants on it go too. This can't be undone (the history keeps the polygon).", "Delete");
        }).then(function (ok) {
          if (!ok) { return; }
          d.busy(true);
          return Nadlan.api.del("/api/parcels/" + options.parcelId).then(function () {
            d.close();
            if (options.onDeleted) { options.onDeleted(options.parcelId, name); }
          });
        }).catch(function (err) { d.busy(false); d.showError(err.message); });
      }

      function showOverlaps(overlaps) {
        acceptOverlaps = true;
        $form.find(".overlap-box").prop("hidden", false).html(
          "<div class=\"warn\"><strong>Overlaps existing parcels</strong> (check the polygon; press Save parcel again to keep it):</div>" +
          "<ul>" + overlaps.map(function (o) {
            return "<li><a href=\"#\" data-show=\"" + o.parcelId + "\">" + esc(o.registryId || "#" + o.parcelId) + "</a> - " +
              Math.round(o.overlapSqm).toLocaleString("en-US") + " m²</li>";
          }).join("") + "</ul>");
      }

      $form.on("click", "[data-show]", function (e) {
        e.preventDefault();
        options.onShowParcel(Number($(this).data("show")));
      });

      return d;
    });
  }

  /** Called by the page when the draw tool's shape appears/disappears or a corner is moved. */
  function notifyShapeChange(hasShape) {
    if (openDialog && openDialog.setGeometryState) { openDialog.setGeometryState(hasShape); }
  }

  Nadlan.parcelEditor = { open: open, notifyShapeChange: notifyShapeChange };
})(window, jQuery);
