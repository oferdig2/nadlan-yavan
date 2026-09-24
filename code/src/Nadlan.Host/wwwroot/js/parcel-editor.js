// ParcelEditor: new-Parcel form. Geometry comes from ParcelDrawTool (draw on the map, or paste coordinates).
// The dialog is non-modal so the polygon stays editable on the map while the form is open.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var openDialog = null;

  function formHtml(ref) {
    return "<form class=\"form\">" +
      "<fieldset class=\"geometry-box\"><legend>Polygon</legend>" +
        "<div class=\"geometry-state muted\">Click the map to add corners; double-click (or Finish) to close.</div>" +
        "<div class=\"btn-row\">" +
          "<button type=\"button\" class=\"btn\" data-geo=\"draw\">Draw again</button>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"undo\">Undo corner</button>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"finish\">Finish</button>" +
        "</div>" +
        "<details><summary>…or paste coordinates</summary>" +
          "<textarea class=\"input paste\" rows=\"3\" placeholder=\"23.3669,38.5089 23.3667,38.5086 ...  (lon,lat - KML order) or GeoJSON\"></textarea>" +
          "<button type=\"button\" class=\"btn\" data-geo=\"paste\">Show on map</button>" +
        "</details>" +
      "</fieldset>" +
      "<label>KAEK<input class=\"input\" name=\"registryId\" placeholder=\"Leave empty if unknown - a provisional id is generated\"></label>" +
      "<label>Geographic area<select class=\"input\" name=\"geographicAreaId\" data-type=\"id\">" +
        Nadlan.reference.optionsHtml(Nadlan.reference.active(ref.geographicAreas), null, "—") + "</select></label>" +
      "<div class=\"form-row\">" +
        "<label>OT<input class=\"input\" name=\"ot\"></label><label class=\"narrow\">Ext<input class=\"input\" name=\"otExt\"></label>" +
        "<label>Plot<input class=\"input\" name=\"plotNumber\"></label><label class=\"narrow\">Ext<input class=\"input\" name=\"plotExt\"></label>" +
      "</div>" +
      "<div class=\"form-row\">" +
        "<label>Official area m²<input class=\"input\" name=\"officialAreaSqm\" data-type=\"number\"></label>" +
        "<label>Build factor<input class=\"input\" name=\"buildFactor\" data-type=\"number\"></label>" +
        "<label>Inclination %<input class=\"input\" name=\"inclination\" data-type=\"number\"></label>" +
      "</div>" +
      "<label>Notes<textarea class=\"input\" name=\"notes\" rows=\"2\"></textarea></label>" +
      "<div class=\"overlap-box\" hidden></div>" +
      "</form>";
  }

  /**
   * @param {{ drawTool: object, onCreated: function(number): void, onShowParcel: function(number): void }} options
   */
  function open(options) {
    if (openDialog) { openDialog.close(); }
    var draw = options.drawTool;

    return Nadlan.reference.load().then(function (ref) {
      var $form = $(formHtml(ref));
      var acceptOverlaps = false;
      var esc = Nadlan.format.escapeHtml;

      function setGeometryState(hasShape) {
        $form.find(".geometry-state")
          .toggleClass("muted", !hasShape)
          .text(hasShape ? "Polygon ready - drag its corners to adjust." : "Click the map to add corners; double-click (or Finish) to close.");
        acceptOverlaps = false; // a changed shape needs a fresh overlap check
        $form.find(".overlap-box").prop("hidden", true);
      }

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
        title: "New parcel",
        content: $form,
        width: 460,
        modal: false,
        position: { my: "left top", at: "left+12 top+60", of: window },
        buttons: [
          { text: "Save parcel", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ],
        onClose: function () { draw.clear(); openDialog = null; }
      });
      openDialog = d;
      d.setGeometryState = setGeometryState;
      draw.start();

      function save() {
        var coordinates = draw.getCoordinates();
        if (!coordinates) { d.showError("Draw the polygon (or paste coordinates) first."); return; }
        var body = Nadlan.dialog.readForm($form);
        body.coordinates = coordinates;
        body.acceptOverlaps = acceptOverlaps;
        var notNumber = ["officialAreaSqm", "buildFactor", "inclination"].some(function (k) {
          return body[k] !== null && isNaN(body[k]);
        });
        if (notNumber) { d.showError("Area, build factor and inclination must be numbers."); return; }

        d.busy(true);
        d.showError("");
        Nadlan.api.post("/api/parcels", body).then(function (created) {
          d.close();
          options.onCreated(created.parcelId);
        }, function (err) {
          d.busy(false);
          if (err.status === 409 && err.code === "PARCEL_OVERLAPS") {
            showOverlaps(err.overlaps || []);
          } else if (err.status === 409 && err.code === "PARCEL_KAEK_EXISTS") {
            d.showError(err.message);
            $form.find(".overlap-box").prop("hidden", false).html(
              "<button type=\"button\" class=\"btn\" data-show=\"" + esc(err.existingParcelId) + "\">Show the existing parcel</button>");
          } else {
            d.showError(err.message);
          }
        });
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

  /** Called by the page when the draw tool's shape appears/disappears. */
  function notifyShapeChange(hasShape) {
    if (openDialog && openDialog.setGeometryState) { openDialog.setGeometryState(hasShape); }
  }

  Nadlan.parcelEditor = { open: open, notifyShapeChange: notifyShapeChange };
})(window, jQuery);
