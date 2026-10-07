// ParcelQuickEntry: "Quick OT/plot entry" mode. A click on a polygon opens a small form next to it with OT focused, so
// OT and plot can be typed straight away: Enter in OT goes to Plot, Enter in Plot saves, Esc closes.
// "OT digits" (per browser): when the OT is typed from empty and reaches that many digits, the cursor jumps to Plot.
// OT "47A" is saved as OT 47 + ext A (the same for plot). Only these four fields change; the rest is sent back as loaded.
// Row entry: Ctrl/⌘+click several Parcels; one form gives them one OT and plots counting up in click order, previewed
// inside the polygons, and saves them together (POST /api/parcels/numbers).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var DIGITS_KEY = "nadlan.quickEntry.otDigits";
  var current = null; // the open single form: { dialog, replaced, summary }
  var row = null;     // the open row entry (several Parcels): { dialog, toggle, add }
  var MAX_ROW = 50;   // server: ParcelService.MaxNumbersBatch

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
    if (row) { row.dialog.close(); }
  }

  function position(domEvent) {
    return domEvent && domEvent.pageX !== undefined
      ? { my: "left+18 top+18", at: "left top", of: domEvent, collision: "fit" }
      : { my: "center", at: "center", of: window };
  }

  /**
   * @param {object} summary   the clicked Parcel's summary ({ parcelId, registryId, ot, plot, ... })
   * @param {Event} [domEvent] the click, to open the form next to it
   * @param {{ onSaved: function(number, string): void, onOpenCard: function(object): void, onClose?: function(): void }} options
   *        onSaved(parcelId, message) after a save; onOpenCard when the caller may not edit it, or asks for the card.
   */
  function open(summary, domEvent, options) {
    if (current) { current.replaced = true; current.dialog.close(); } // the next parcel: no onClose for the old one
    if (row) { row.dialog.close(); } // a plain click leaves the row entry
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
    var me = { replaced: false, summary: summary };

    var d = Nadlan.dialog.open({
      title: "Parcel " + name,
      content: $form,
      width: 300,
      modal: false,
      position: position(domEvent),
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

  // ---- Row entry: Ctrl/⌘+click picks several Parcels; one form gives them one OT and counting plot numbers. ----------

  /**
   * Ctrl/⌘+click in quick entry: adds the Parcel to the row, or takes it out again. A single form that is open becomes the
   * row's first Parcel, so "click the first plot, Ctrl+click the others" works.
   * @param {{ onSaved: function(number[], string): void, onPreview: function(Array): void, onClose?: function(): void }} options
   *        onPreview(list) shows the planned "OT / plot" inside each polygon (empty list = none); onSaved after the save.
   */
  function toggleInRow(summary, domEvent, options) {
    if (!row) {
      var first = current && current.summary.parcelId !== summary.parcelId ? current.summary : null;
      if (current) { current.replaced = true; current.dialog.close(); }
      row = openRow(domEvent, options);
      if (first) { row.add(first); }
    }
    row.toggle(summary);
  }

  function openRow(domEvent, options) {
    var esc = Nadlan.format.escapeHtml;
    var rows = []; // { summary, detail (GET /api/parcels/{id}), loaded (promise), plot (text), fixed (typed by the user) }
    var saving = false;
    var $form = $("<form class=\"form compact quick-entry-form\" autocomplete=\"off\">" +
      "<label>OT for all of them<input class=\"input\" name=\"rowOt\" maxlength=\"40\" placeholder=\"empty = keep each one's OT\"></label>" +
      "<ul class=\"quick-group-list\"></ul>" +
      "<div class=\"quick-group-warn\" hidden></div>" +
      "<div class=\"muted quick-entry-hint\">Ctrl+click adds or removes a parcel. Plots count up in click order; type over one to skip a number (4 → 6) or split (4a, 4b). Empty plot = keep. Enter saves, Esc cancels.</div>" +
      "</form>");
    var $ot = $form.find("[name=rowOt]");
    var $list = $form.find(".quick-group-list");
    var $warn = $form.find(".quick-group-warn");
    var me = {};

    var d = Nadlan.dialog.open({
      title: "Row entry",
      content: $form,
      width: 380,
      modal: false,
      position: position(domEvent),
      buttons: [
        { text: "Save all", primary: true, click: save },
        { text: "Cancel", click: function () { d.close(); } }
      ],
      onClose: function () {
        if (row === me) { row = null; }
        options.onPreview([]);
        if (options.onClose) { options.onClose(); }
      }
    });
    d.$el.closest(".ui-dialog").addClass("quick-row-dialog");
    me.dialog = d;

    function name(r) { return r.summary.registryId || "#" + r.summary.parcelId; }
    function stored(r) {
      if (!r.detail) { return { ot: r.summary.ot || "", plot: r.summary.plot || "" }; }
      var f = r.detail.fields;
      return { ot: join(f.ot, f.otExt), plot: join(f.plotNumber, f.plotExt) };
    }
    // What a row will be saved as: an empty field keeps the stored value (and its ext) as it is.
    function planned(r) {
      var now = stored(r), otText = $.trim($ot.val()), plotText = $.trim(r.plot);
      return { ot: otText ? join(split(otText).value, split(otText).ext) : now.ot, plot: plotText ? join(split(plotText).value, split(plotText).ext) : now.plot };
    }
    function replaces(r) {
      var now = stored(r), p = planned(r);
      return (now.ot || now.plot) && (now.ot !== p.ot || now.plot !== p.plot);
    }

    // Rows nobody typed in count up from the one before ("4a" -> 5); a row without a number stops the counting.
    function renumber() {
      var prev = null;
      rows.forEach(function (r, i) {
        if (i > 0 && !r.fixed) {
          r.plot = prev === null ? "" : String(prev + 1);
          $list.find("[data-row=" + i + "]").val(r.plot);
        }
        var m = /^(\d+)/.exec($.trim(r.plot));
        prev = m ? Number(m[1]) : null;
      });
      showPlan();
    }

    function showPlan() {
      var replacing = 0;
      rows.forEach(function (r, i) {
        var now = stored(r), will = replaces(r);
        if (will) { replacing++; }
        $list.find("[data-now=" + i + "]").toggleClass("replaces", !!will)
          .text(now.ot || now.plot ? "now " + (now.ot || "—") + " / " + (now.plot || "—") : "empty");
      });
      $warn.prop("hidden", !replacing).text(replacing ? replacing + " of them already have an OT / plot - Save all replaces it." : "");
      options.onPreview(rows.map(function (r, i) {
        var p = planned(r);
        return { parcelId: r.summary.parcelId, badge: String(i + 1), text: p.ot || p.plot ? (p.ot || "—") + " / " + (p.plot || "—") : "", warn: !!replaces(r) };
      }));
    }

    function render() {
      d.$el.closest(".ui-dialog").find(".ui-dialog-title").text("Row entry - " + rows.length + " parcel" + (rows.length === 1 ? "" : "s"));
      $list.html(rows.map(function (r, i) {
        return "<li><span class=\"qg-n\">" + (i + 1) + "</span>" +
          "<span class=\"qg-name\" title=\"" + esc(name(r)) + "\">" + esc(name(r)) + "</span>" +
          "<span class=\"qg-now\" data-now=\"" + i + "\"></span>" +
          "<input class=\"input" + (r.fixed ? " fixed" : "") + "\" data-row=\"" + i + "\" maxlength=\"40\" placeholder=\"plot\" title=\"Plot number\" value=\"" + esc(r.plot) + "\">" +
          "<button type=\"button\" data-drop=\"" + i + "\" title=\"Take out of the row\">×</button></li>";
      }).join(""));
      renumber();
    }

    function remove(r) {
      var i = rows.indexOf(r);
      if (i < 0) { return; }
      rows.splice(i, 1);
      if (rows.length && i === 0) { rows[0].fixed = true; } // the new first row keeps its number
      if (!rows.length) { d.close(); return; }
      render();
    }

    function add(summary) {
      if (rows.length >= MAX_ROW) { d.showError("At most " + MAX_ROW + " parcels in one row - save these first."); return; }
      var r = { summary: summary, detail: null, plot: "", fixed: rows.length === 0 };
      r.loaded = Nadlan.api.get("/api/parcels/" + summary.parcelId).then(function (res) {
        if (row !== me) { return null; }
        if (!res.rights || !res.rights.canEdit) {
          d.showError("You may not edit parcel " + name(r) + ", so it was left out.");
          remove(r);
          return null;
        }
        r.detail = res;
        showPlan();
        return res;
      }, function (err) {
        if (row === me) { d.showError("Could not load parcel " + name(r) + ": " + err.message); remove(r); }
        return null;
      });
      rows.push(r);
      render();
    }

    function focusForm() {
      if (!$.contains(d.$el[0], document.activeElement)) { $ot.trigger("focus").trigger("select"); }
    }

    $ot.on("input", showPlan);
    // Enter in OT = on to the first plot (stopped here, so the dialog's "Enter saves" doesn't fire).
    $ot.on("keydown", function (e) {
      if (e.key !== "Enter") { return; }
      e.preventDefault();
      e.stopPropagation();
      $list.find("[data-row=0]").trigger("focus").trigger("select");
    });
    $list.on("input", "[data-row]", function () {
      var i = Number($(this).attr("data-row")), r = rows[i];
      r.plot = this.value;
      r.fixed = i === 0 || $.trim(this.value) !== ""; // cleared again: counts along with the others
      $(this).toggleClass("fixed", r.fixed && i > 0);
      renumber();
    });
    $list.on("click", "[data-drop]", function () { remove(rows[Number($(this).attr("data-drop"))]); });

    function save() {
      if (saving) { return; }
      saving = true;
      d.busy(true);
      d.showError("");
      Promise.all(rows.map(function (r) { return r.loaded; })).then(function () {
        if (row !== me) { return; }
        if (!rows.length) { d.close(); return; }
        var otText = $.trim($ot.val());
        var items = rows.map(function (r) {
          if (!r.detail) { throw new Error("Parcel " + name(r) + " could not be loaded. Take it out (×) or try again."); }
          var f = r.detail.fields, plotText = $.trim(r.plot);
          var ot = otText ? split(otText) : { value: f.ot, ext: f.otExt };
          var plot = plotText ? split(plotText) : { value: f.plotNumber, ext: f.plotExt };
          return { parcelId: r.summary.parcelId, ot: ot.value, otExt: ot.ext, plotNumber: plot.value, plotExt: plot.ext, version: r.detail.version };
        });
        return Nadlan.api.post("/api/parcels/numbers", { items: items }).then(function (res) {
          var first = planned(rows[0]), last = planned(rows[rows.length - 1]);
          var ids = rows.map(function (r) { return r.summary.parcelId; });
          var message = res.changed === 0 ? "Nothing to change in those " + rows.length + " parcels."
            : res.changed + " parcel" + (res.changed === 1 ? "" : "s") + " saved: OT " + (otText || "kept") +
              (rows.length > 1 ? ", plots " + (first.plot || "—") + " to " + (last.plot || "—") : ", plot " + (first.plot || "—")) + ".";
          d.close();
          options.onSaved(ids, message);
        });
      }).catch(function (err) {
        saving = false;
        d.busy(false);
        if (err.code === "EDITED_ELSEWHERE") {
          // Someone saved one of them meanwhile: reload all (values and versions), show it, let the user save again.
          rows.forEach(function (r) {
            r.detail = null;
            r.loaded = Nadlan.api.get("/api/parcels/" + r.summary.parcelId)
              .then(function (res) { r.detail = res; showPlan(); return res; }, function () { return null; });
          });
          d.showError("Someone else changed one of these parcels meanwhile. Their current values are shown now - check, then Save all again.");
          return;
        }
        d.showError(err.message);
      });
    }

    me.add = add;
    me.toggle = function (summary) {
      var found = rows.filter(function (r) { return r.summary.parcelId === summary.parcelId; })[0];
      if (found) { remove(found); } else { add(summary); }
      if (row === me) { focusForm(); }
    };
    return me;
  }

  Nadlan.parcelQuickEntry = { open: open, toggleInRow: toggleInRow, close: close, getDigits: function () { return sessionDigits; }, setDigits: setDigits, split: split };
})(window, jQuery);
