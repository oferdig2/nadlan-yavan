// ParcelQuickEntry: "Quick OT/plot entry" mode. A click on a polygon opens a small form next to it with OT focused, so
// OT and plot can be typed straight away: Enter in OT goes to Plot, Enter in Plot saves, Esc closes.
// "OT digits" (per browser): when the OT is typed from empty and reaches that many digits, the cursor jumps to Plot.
// OT "47A" is saved as OT 47 + ext A (the same for plot). Only these four fields change; the rest is sent back as loaded.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var DIGITS_KEY = "nadlan.quickEntry.otDigits";
  var current = null; // the open form: { dialog, replaced }

  function join(value, ext) { return value ? value + (ext || "") : ""; }

  // "47A" -> 47 + A, "47 A" too. Anything not starting with digits (e.g. "Α12") stays whole, without an ext.
  function split(text) {
    var t = $.trim(text);
    if (!t) { return { value: null, ext: null }; }
    var m = /^(\d+)\s*(\S.*)?$/.exec(t);
    return m ? { value: m[1], ext: m[2] || null } : { value: t, ext: null };
  }

  /** 1-9, or null (auto-advance off). Browser storage can be missing or refused (private mode): then off. */
  function getDigits() {
    try {
      var n = Number(window.localStorage.getItem(DIGITS_KEY));
      return n >= 1 && n <= 9 ? n : null;
    } catch (e) { return null; }
  }

  function setDigits(n) {
    try {
      if (n) { window.localStorage.setItem(DIGITS_KEY, String(n)); } else { window.localStorage.removeItem(DIGITS_KEY); }
    } catch (e) { /* not remembered - still used for this page */ }
    sessionDigits = n || null;
  }

  var sessionDigits = getDigits();

  function close() {
    if (current) { current.dialog.close(); }
  }

  /**
   * @param {object} summary   the clicked Parcel's summary ({ parcelId, registryId, ot, plot, ... })
   * @param {Event} [domEvent] the click, to open the form next to it
   * @param {{ onSaved: function(number, string): void, onOpenCard: function(object): void, onClose?: function(): void }} options
   *        onSaved(parcelId, message) after a save; onOpenCard when the caller may not edit it, or asks for the card.
   */
  function open(summary, domEvent, options) {
    if (current) { current.replaced = true; current.dialog.close(); } // the next parcel: no onClose for the old one
    var esc = Nadlan.format.escapeHtml;
    var name = summary.registryId || "#" + summary.parcelId;
    var $form = $("<form class=\"form compact quick-entry-form\" autocomplete=\"off\">" +
      "<div class=\"form-row\">" +
        "<label>OT<input class=\"input\" name=\"quickOt\" maxlength=\"40\" value=\"" + esc(summary.ot || "") + "\"></label>" +
        "<label>Plot<input class=\"input\" name=\"quickPlot\" maxlength=\"40\" value=\"" + esc(summary.plot || "") + "\"></label>" +
      "</div>" +
      "<div class=\"muted quick-entry-hint\">Enter: next / save · Esc: close</div>" +
      "</form>");
    var $ot = $form.find("[name=quickOt]");
    var $plot = $form.find("[name=quickPlot]");
    var shown = { ot: summary.ot || "", plot: summary.plot || "" }; // what the fields started with
    var detail = null;
    var saving = false;
    var me = { replaced: false };

    var d = Nadlan.dialog.open({
      title: "Parcel " + name,
      content: $form,
      width: 300,
      modal: false,
      position: domEvent && domEvent.pageX !== undefined
        ? { my: "left+18 top+18", at: "left top", of: domEvent, collision: "fit" }
        : { my: "center", at: "center", of: window },
      buttons: [
        { text: "Save", primary: true, click: save },
        { text: "Card", click: function () { d.close(); options.onOpenCard(summary); } }
      ],
      onClose: function () {
        if (current === me) { current = null; }
        if (!me.replaced && options.onClose) { options.onClose(); }
      }
    });
    me.dialog = d;
    current = me;
    d.$el.closest(".ui-dialog").addClass("quick-entry-dialog");
    $ot.trigger("focus").trigger("select");

    // The edit form's values and version. Not allowed to edit this one: the normal card instead.
    var loaded = Nadlan.api.get("/api/parcels/" + summary.parcelId).then(function (res) {
      if (current !== me) { return null; }
      if (!res.rights || !res.rights.canEdit) {
        d.close();
        options.onOpenCard(summary);
        return null;
      }
      detail = res;
      var f = res.fields;
      // Someone changed it since the map was drawn: show the latest - unless the user already typed over it.
      var latest = { ot: join(f.ot, f.otExt), plot: join(f.plotNumber, f.plotExt) };
      if ($ot.val() === shown.ot && latest.ot !== shown.ot) { $ot.val(latest.ot); }
      if ($plot.val() === shown.plot && latest.plot !== shown.plot) { $plot.val(latest.plot); }
      shown = latest;
      return res;
    }, function (err) {
      if (current === me) { d.showError("Could not load the parcel: " + err.message); }
      return null;
    });

    // Auto-advance: only while the OT is being typed from empty (or over all of it), never while editing part of it.
    var fromScratch = false;
    $ot.on("beforeinput", function (e) {
      var el = this;
      var inserting = /^insert/.test(e.originalEvent.inputType || "");
      var whole = el.selectionStart === 0 && el.selectionEnd === el.value.length;
      if (whole) { fromScratch = inserting; } else if (!inserting || el.selectionEnd !== el.value.length) { fromScratch = false; }
    });
    $ot.on("input", function () {
      var n = sessionDigits;
      if (fromScratch && n && /^\d+$/.test(this.value) && this.value.length === n) {
        fromScratch = false;
        $plot.trigger("focus").trigger("select");
      }
    });
    // Enter in OT = on to Plot (stopped here, so the dialog's "Enter saves" doesn't fire).
    $ot.on("keydown", function (e) {
      if (e.key !== "Enter") { return; }
      e.preventDefault();
      e.stopPropagation();
      $plot.trigger("focus").trigger("select");
    });

    function save() {
      if (saving) { return; }
      saving = true;
      d.busy(true);
      d.showError("");
      loaded.then(function (res) {
        if (!res) { saving = false; d.busy(false); return; } // closed, or the load failed (its error is shown)
        var otText = $.trim($ot.val());
        var plotText = $.trim($plot.val());
        if (otText === shown.ot && plotText === shown.plot) { d.close(); return; } // nothing changed
        var f = res.fields;
        // An untouched field keeps its stored value and ext as they are (e.g. OT 12 with ext "3" must not become 123).
        var ot = otText === shown.ot ? { value: f.ot, ext: f.otExt } : split(otText);
        var plot = plotText === shown.plot ? { value: f.plotNumber, ext: f.plotExt } : split(plotText);
        var body = {
          registryId: null, // empty = keep the KAEK (real or provisional)
          geographicAreaId: f.geographicAreaId,
          coordinates: null, // polygon unchanged
          officialAreaSqm: f.officialAreaSqm,
          ot: ot.value, otExt: ot.ext, plotNumber: plot.value, plotExt: plot.ext,
          inclination: f.inclination, buildFactor: f.buildFactor, notes: f.notes,
          acceptOverlaps: false,
          version: res.version
        };
        return Nadlan.api.put("/api/parcels/" + summary.parcelId, body).then(function () {
          d.close();
          options.onSaved(summary.parcelId, "Parcel " + name + ": OT " + (join(ot.value, ot.ext) || "—") +
            ", plot " + (join(plot.value, plot.ext) || "—") + " saved.");
        });
      }).catch(function (err) {
        saving = false;
        d.busy(false);
        d.showError(err.message);
      });
    }
  }

  Nadlan.parcelQuickEntry = { open: open, close: close, getDigits: function () { return sessionDigits; }, setDigits: setDigits, split: split };
})(window, jQuery);
