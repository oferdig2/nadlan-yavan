// ParcelQuickEntry: "Quick OT/plot entry" mode. A click on a polygon opens a small form with OT focused, so OT and plot
// can be typed straight away: Enter in OT goes to Plot, Enter in Plot saves, Esc closes.
// "OT digits" (per browser): when the OT is typed from empty and reaches that many digits, the cursor jumps to Plot; a
// letter typed right after the jump still goes to the OT (171, a -> OT 171a), digits go to the plot.
// OT "47A" is saved as OT 47 + ext A (the same for plot). Saves go through POST /api/parcels/numbers: only these change.
// Row entry: Ctrl/⌘+click, or the Row button (touch), starts a row; while it is open every click adds or removes a
// Parcel. One form gives them one OT and plots counting up in click order, previewed inside the polygons, saved together.
// The forms open over the side panel, not over the parcels; dragged elsewhere, they open there next time (per browser).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var DIGITS_KEY = "nadlan.quickEntry.otDigits";
  var POSITION_KEY = "nadlan.quickEntry.position";
  var current = null; // the open single form: { dialog, replaced, summary }
  var row = null;     // the open row entry (several Parcels): { dialog, toggle, add }
  var MAX_ROW = 50;   // server: ParcelService.MaxNumbersBatch

  function join(value, ext) { return value ? value + (ext || "") : ""; }

  // "47A" -> 47 + A, "47 A" too. Anything not starting with digits (e.g. "Α12") stays whole, without an ext.
  // Keypad keys are no extension: "47/3", "47+", "47." stay whole. Same rule as the server (ParcelNumberKey.Split).
  function split(text) {
    var t = $.trim(text);
    if (!t) { return { value: null, ext: null }; }
    var m = /^([0-9]+)\s*[-.]?\s*(\p{L}{1,3})$/u.exec(t);
    return m ? { value: m[1], ext: m[2] } : { value: t, ext: null };
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

  // Where the forms open: where the user last dragged one, else over the top of the side panel (the map stays free),
  // else (no side panel, e.g. a phone) at the bottom of the screen.
  var savedPosition = (function () {
    try {
      var p = JSON.parse(window.localStorage.getItem(POSITION_KEY) || "null");
      return p && p.left >= 0 && p.top >= 0 ? p : null;
    } catch (e) { return null; }
  })();

  function position() {
    if (savedPosition) {
      return { my: "left top", at: "left+" + savedPosition.left + " top+" + savedPosition.top, of: window, collision: "fit" };
    }
    var side = document.querySelector(".sidebar");
    return side && side.offsetWidth > 0
      ? { my: "left top", at: "left+8 top+8", of: side, collision: "fit" }
      : { my: "center bottom", at: "center bottom-12", of: window, collision: "fit" };
  }

  function rememberPosition(d) {
    d.$el.on("dialogdragstop", function (e, ui) {
      savedPosition = { left: Math.max(0, Math.round(ui.position.left)), top: Math.max(0, Math.round(ui.position.top)) };
      try { window.localStorage.setItem(POSITION_KEY, JSON.stringify(savedPosition)); } catch (err) { /* this page only */ }
    });
  }

  function close() {
    if (current) { current.dialog.close(); }
    if (row) { row.dialog.close(); }
  }

  /**
   * @param {object} summary  the clicked Parcel's summary ({ parcelId, registryId, ot, plot, ... })
   * @param {{ onSaved: function(number, string, Array): void, onOpenCard: function(object): void, onStartRow?: function(object): void,
   *           onClose?: function(): void, onError?: function(string): void }} options
   *        onSaved(parcelId, message, saved) after a save, saved = [{ parcelId, ot, plot }] as now stored (joined text);
   *        onOpenCard when the caller may not edit it, or asks for the card; onStartRow when the user presses Row.
   */
  function open(summary, options) {
    if (current) { current.replaced = true; current.dialog.close(); } // the next parcel: no onClose for the old one
    if (row) { row.dialog.close(); }
    var esc = Nadlan.format.escapeHtml;
    var name = summary.registryId || "#" + summary.parcelId;
    var $form = $("<form class=\"form compact quick-entry-form\" autocomplete=\"off\">" +
      "<div class=\"form-row\">" +
        "<label>OT<input class=\"input\" name=\"quickOt\" maxlength=\"40\" value=\"" + esc(summary.ot || "") + "\"></label>" +
        "<label>Plot<input class=\"input\" name=\"quickPlot\" maxlength=\"40\" value=\"" + esc(summary.plot || "") + "\"></label>" +
      "</div>" +
      "<div class=\"muted quick-entry-hint\">Enter: next / save · Esc: close · Row: several parcels in a row</div>" +
      "</form>");
    var $ot = $form.find("[name=quickOt]");
    var $plot = $form.find("[name=quickPlot]");
    var shown = { ot: summary.ot || "", plot: summary.plot || "" }; // what the fields started with
    var saving = false;
    var me = { replaced: false, summary: summary };

    var buttons = [
      { text: "Save", primary: true, click: save },
      { text: "Card", click: function () { d.close(); options.onOpenCard(summary); } }
    ];
    if (options.onStartRow) {
      buttons.splice(1, 0, { text: "Row", click: function () { options.onStartRow(summary); } });
    }
    var d = Nadlan.dialog.open({
      title: "Parcel " + name,
      content: $form,
      width: 284,
      modal: false,
      position: position(),
      buttons: buttons,
      onClose: function () {
        if (current === me) { current = null; }
        if (!me.replaced && options.onClose) { options.onClose(); }
      }
    });
    me.dialog = d;
    current = me;
    d.$el.closest(".ui-dialog").addClass("quick-entry-dialog");
    rememberPosition(d);
    $ot.trigger("focus").trigger("select");

    // The edit form's values and version. Not allowed to edit this one: the normal card instead.
    // A save pressed before this arrives still goes through, even when the user has clicked the next parcel meanwhile.
    var loaded = Nadlan.api.get("/api/parcels/" + summary.parcelId).then(function (res) {
      if (!res.rights || !res.rights.canEdit) {
        if (current === me) { d.close(); options.onOpenCard(summary); }
        return null;
      }
      if (current !== me) { return res; }
      var f = res.fields;
      // Someone changed it since the map was drawn: show the latest - unless the user already typed over it.
      var latest = { ot: join(f.ot, f.otExt), plot: join(f.plotNumber, f.plotExt) };
      if ($ot.val() === shown.ot && latest.ot !== shown.ot) { $ot.val(latest.ot); }
      if ($plot.val() === shown.plot && latest.plot !== shown.plot) { $plot.val(latest.plot); }
      shown = latest;
      return res;
    }, function (err) {
      report("Could not load parcel " + name + ": " + err.message);
      return null;
    });

    // An error goes into the form while it is open, else to the page's status line (the user is on the next parcel).
    function report(message) {
      if (current === me) { d.showError(message); } else if (options.onError) { options.onError(message); }
    }

    // Auto-advance: only while the OT is being typed from empty (or over all of it), never while editing part of it.
    var fromScratch = false;
    var jumped = false; // the cursor jumped to Plot and nothing was typed there yet
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
        jumped = true;
        $plot.trigger("focus").trigger("select");
      }
    });
    // Right after the jump a letter belongs to the OT (171 -> a = 171a, up to 3 letters); the first digit starts the plot.
    $plot.on("keydown", function (e) {
      if (!jumped || e.key.length !== 1 || e.ctrlKey || e.metaKey || e.altKey) { return; }
      var whole = this.selectionStart === 0 && this.selectionEnd === this.value.length;
      var letters = (/\p{L}*$/u.exec($ot.val()) || [""])[0].length;
      if (whole && /^\p{L}$/u.test(e.key) && letters < 3) {
        e.preventDefault();
        $ot.val($ot.val() + e.key);
        return;
      }
      jumped = false;
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
      var otText = $.trim($ot.val()); // as typed now: the form may be gone by the time the parcel has loaded
      var plotText = $.trim($plot.val());
      loaded.then(function (res) {
        if (!res) { saving = false; d.busy(false); return; } // no edit rights (the card opened instead), or the load failed (reported)
        if (otText === shown.ot && plotText === shown.plot) { d.close(); return; } // nothing changed
        var f = res.fields;
        // An untouched field keeps its stored value and ext as they are.
        var ot = otText === shown.ot ? { value: f.ot, ext: f.otExt } : split(otText);
        var plot = plotText === shown.plot ? { value: f.plotNumber, ext: f.plotExt } : split(plotText);
        // The row save with one parcel: only the numbers change, on one database connection (no edit-form locks).
        var item = { parcelId: summary.parcelId, ot: ot.value, otExt: ot.ext, plotNumber: plot.value, plotExt: plot.ext, version: res.version };
        return Nadlan.api.post("/api/parcels/numbers", { items: [item] }).then(function () {
          var now = { parcelId: summary.parcelId, ot: join(ot.value, ot.ext), plot: join(plot.value, plot.ext) };
          d.close();
          options.onSaved(summary.parcelId, "Parcel " + name + ": OT " + (now.ot || "—") + ", plot " + (now.plot || "—") + " saved.", [now]);
        });
      }).catch(function (err) {
        saving = false;
        d.busy(false);
        report("Parcel " + name + " not saved: " + err.message);
      });
    }
  }

  // ---- Row entry: several Parcels, one OT and counting plot numbers. ------------------------------------------------

  /** True while a row entry is open: then every click on a parcel adds or removes it. */
  function inRow() { return !!row; }

  /**
   * Adds the Parcel to the row (starting one if none is open), or takes it out again. A single form that is open becomes
   * the row's first Parcel, so "click the first plot, then Ctrl+click (or Row) the others" works.
   * @param {{ onSaved: function(number[], string, Array): void, onPreview: function(Array): void, onClose?: function(): void,
   *           onError?: function(string): void }} options
   *        onPreview(list) shows the planned "OT / plot" inside each polygon (empty list = none); onSaved after the save,
   *        with [{ parcelId, ot, plot }] as now stored.
   */
  function toggleInRow(summary, options) {
    if (!row) {
      var first = current && current.summary.parcelId !== summary.parcelId ? current.summary : null;
      if (current) { current.replaced = true; current.dialog.close(); }
      row = openRow(options);
      if (first) { row.add(first); }
    }
    row.toggle(summary);
  }

  function openRow(options) {
    var esc = Nadlan.format.escapeHtml;
    var rows = []; // { summary, detail (GET /api/parcels/{id}), loaded (promise), plot (text), fixed (typed by the user) }
    var saving = false;
    var $form = $("<form class=\"form compact quick-entry-form\" autocomplete=\"off\">" +
      "<label>OT for all of them<input class=\"input\" name=\"rowOt\" maxlength=\"40\" placeholder=\"empty = keep each one's OT\"></label>" +
      "<ul class=\"quick-group-list\"></ul>" +
      "<div class=\"quick-group-warn\" hidden></div>" +
      "<div class=\"muted quick-entry-hint\">Click (or tap) a parcel to add or remove it. Plots count up in click order; type over one to skip a number (4 → 6) or split (4a, 4b). A plot you clear is kept as it is. Enter saves, Esc cancels.</div>" +
      "</form>");
    var $ot = $form.find("[name=rowOt]");
    var $list = $form.find(".quick-group-list");
    var $warn = $form.find(".quick-group-warn");
    var me = {};

    var d = Nadlan.dialog.open({
      title: "Row entry",
      content: $form,
      width: 284,
      modal: false,
      position: position(),
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
    rememberPosition(d);
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

    // Rows nobody typed in count up from the last number before them ("4a" -> 5). A row the user cleared keeps its plot
    // and the counting goes on past it; a row with something else than a number stops the counting.
    function renumber() {
      var prev = null;
      rows.forEach(function (r, i) {
        if (i > 0 && !r.fixed) {
          r.plot = prev === null ? "" : String(prev + 1);
          $list.find("[data-row=" + i + "]").val(r.plot);
        }
        var text = $.trim(r.plot), m = /^(\d+)/.exec(text);
        if (m) { prev = Number(m[1]); } else if (!(r.fixed && i > 0 && text === "")) { prev = null; }
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
          "<input class=\"input" + (r.fixed && i > 0 ? " fixed" : "") + "\" data-row=\"" + i + "\" maxlength=\"40\" placeholder=\"" + (i > 0 && r.fixed ? "keep" : "plot") + "\" title=\"Plot number\" value=\"" + esc(r.plot) + "\">" +
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
        if (!res.rights || !res.rights.canEdit) {
          if (row === me) { d.showError("You may not edit parcel " + name(r) + ", so it was left out."); remove(r); }
          return null;
        }
        r.detail = res; // also once the form is gone: a Save pressed before still needs it
        if (row === me) { showPlan(); }
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
    // A row the user typed in is theirs from then on, also when cleared (= keep that plot): it is never refilled under
    // the cursor, so Backspace and a new number work as expected.
    $list.on("input", "[data-row]", function () {
      var i = Number($(this).attr("data-row")), r = rows[i];
      r.plot = this.value;
      r.fixed = true;
      $(this).toggleClass("fixed", i > 0).attr("placeholder", i > 0 ? "keep" : "plot");
      renumber();
    });
    $list.on("click", "[data-drop]", function () { remove(rows[Number($(this).attr("data-drop"))]); });

    function save() {
      if (saving) { return; }
      saving = true;
      d.busy(true);
      d.showError("");
      // What is in the form now: a save pressed goes through even if the user moves on before the parcels have loaded.
      var otText = $.trim($ot.val());
      var list = rows.slice();
      Promise.all(list.map(function (r) { return r.loaded; })).then(function () {
        if (!list.length) { d.close(); return; }
        var items = list.map(function (r) {
          if (!r.detail) { throw new Error("Parcel " + name(r) + " could not be loaded. Take it out (×) or try again."); }
          var f = r.detail.fields, plotText = $.trim(r.plot);
          var ot = otText ? split(otText) : { value: f.ot, ext: f.otExt };
          var plot = plotText ? split(plotText) : { value: f.plotNumber, ext: f.plotExt };
          return { parcelId: r.summary.parcelId, ot: ot.value, otExt: ot.ext, plotNumber: plot.value, plotExt: plot.ext, version: r.detail.version };
        });
        return Nadlan.api.post("/api/parcels/numbers", { items: items }).then(function (res) {
          var saved = list.map(function (r) { var p = planned(r); return { parcelId: r.summary.parcelId, ot: p.ot, plot: p.plot }; });
          var first = saved[0], last = saved[saved.length - 1];
          var message = res.changed === 0 ? "Nothing to change in those " + list.length + " parcels."
            : res.changed + " parcel" + (res.changed === 1 ? "" : "s") + " saved: OT " + (otText || "kept") +
              (list.length > 1 ? ", plots " + (first.plot || "—") + " to " + (last.plot || "—") : ", plot " + (first.plot || "—")) + ".";
          d.close();
          options.onSaved(saved.map(function (s) { return s.parcelId; }), message, saved);
        });
      }).catch(function (err) {
        saving = false;
        d.busy(false);
        if (row !== me) { // the form is gone (the user moved on): say it on the page
          if (options.onError) { options.onError("Row of " + list.length + " parcels not saved: " + err.message); }
          return;
        }
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

  Nadlan.parcelQuickEntry = {
    open: open, toggleInRow: toggleInRow, inRow: inRow, close: close,
    getDigits: function () { return sessionDigits; }, setDigits: setDigits, split: split
  };
})(window, jQuery);
