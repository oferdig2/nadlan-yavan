// Work requests (staff): what the Work page's parts share - status names and colours, the filters, dates, the status line,
// and the contractor picker. Statuses mirror Nadlan.Core.Work (WorkStatuses, ParcelWorkStatuses); the server decides.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  var WORK = {
    REQUESTED: { name: "Requested", color: "#64748b" },
    OFFERED: { name: "Offered", color: "#b45309" },
    APPROVED: { name: "Approved", color: "#1d4ed8" },
    IN_WORK: { name: "In work", color: "#7c3aed" },
    DONE: { name: "Done", color: "#15803d" },
    PAID: { name: "Paid", color: "#0f766e" }
  };

  var PARCEL = {
    OPEN_OFFERS: { name: "Open offers", color: "#b45309" },
    APPROVED: { name: "Approved", color: "#1d4ed8" },
    IN_WORK: { name: "In work", color: "#7c3aed" },
    DONE: { name: "Done", color: "#15803d" },
    PAID: { name: "Paid", color: "#0f766e" }
  };

  var LINK = {
    active: { name: "Active", color: "#15803d" },
    disabled: { name: "Disabled", color: "#64748b" },
    expired: { name: "Expired", color: "#b91c1c" },
    locked: { name: "Locked", color: "#b91c1c" }
  };

  // The filters (owner): a Parcel shows when at least one of its works is in that status; Open offers = Requested or Offered.
  var FILTERS = [
    { key: "all", name: "All", statuses: null },
    { key: "open", name: "Open offers", statuses: ["REQUESTED", "OFFERED"] },
    { key: "approved", name: "Approved", statuses: ["APPROVED"] },
    { key: "inwork", name: "In work", statuses: ["IN_WORK"] },
    { key: "done", name: "Done", statuses: ["DONE"] },
    { key: "paid", name: "Paid", statuses: ["PAID"] }
  ];

  function badge(map, code) {
    var s = map[code] || { name: code, color: "#64748b" };
    return Nadlan.format.statusBadge(s.name, s.color);
  }

  function two(n) { return (n < 10 ? "0" : "") + n; }

  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

  // "1 Nov 2026 14:35" in the browser's time zone; "—" when empty.
  function dateTime(utc) {
    if (!utc) { return "—"; }
    var d = new Date(utc);
    if (isNaN(d.getTime())) { return "—"; }
    return d.getDate() + " " + MONTHS[d.getMonth()] + " " + d.getFullYear() + " " + two(d.getHours()) + ":" + two(d.getMinutes());
  }

  function date(utcOrDay) {
    if (!utcOrDay) { return "—"; }
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(utcOrDay); // a calendar day ("2026-11-05"): no time zone to apply
    if (m) { return Number(m[3]) + " " + MONTHS[Number(m[2]) - 1] + " " + m[1]; }
    var d = new Date(utcOrDay);
    return isNaN(d.getTime()) ? "—" : d.getDate() + " " + MONTHS[d.getMonth()] + " " + d.getFullYear();
  }

  function time(utc) {
    var d = new Date(utc);
    return two(d.getHours()) + ":" + two(d.getMinutes());
  }

  var $status = null;
  function setStatus(message, isError) {
    $status = $status || $("#work-status");
    $status.text(message || "").toggleClass("error", !!isError);
  }

  // Contractors = active Contacts with the role Contractor. Cached for the page; refresh() after "+ New contact".
  var contractors = null;
  function loadContractors(refresh) {
    if (contractors && !refresh) { return Promise.resolve(contractors); }
    return Nadlan.api.get("/api/work/contractors").then(function (res) { contractors = res; return res; });
  }

  /**
   * A contractor <select> (+ "New contact" when the user may create Contacts). Returns { $el, value(), set(id) }.
   * opts: { selected, disabled, onChange }
   */
  function contractorPicker(opts) {
    var $el = $("<span class=\"contractor-picker\"><select class=\"input\" name=\"contractor\" aria-label=\"Contractor\"></select></span>");
    var $select = $el.find("select");
    var canCreate = Nadlan.session.can("MANAGE_CONTACTS");

    function fill(list, selected) {
      var has = list.items.some(function (c) { return c.contactId === selected; });
      $select.html("<option value=\"\">Choose a contractor…</option>" +
        list.items.map(function (c) {
          return "<option value=\"" + c.contactId + "\">" + esc(c.displayName) + (c.phone ? " · " + esc(c.phone) : "") + "</option>";
        }).join("") +
        (selected && !has ? "<option value=\"" + selected + "\">" + esc(opts.selectedName || "#" + selected) + " (not a contractor now)</option>" : ""));
      $select.val(selected ? String(selected) : "");
    }

    $select.prop("disabled", !!opts.disabled).on("change", function () { if (opts.onChange) { opts.onChange(); } });
    loadContractors(false).then(function (list) {
      fill(list, opts.selected || null);
      if (!list.items.length) {
        $el.append(" <span class=\"muted\">No contractors yet: a Contact needs the role Contractor.</span>");
      }
      if (canCreate && list.roleId && !opts.disabled) {
        $("<button type=\"button\" class=\"btn btn-small\">+ New contact</button>").appendTo($el).on("click", function () {
          Nadlan.contactEditor.open(null, { roleIds: [list.roleId] }).then(function (c) {
            if (!c) { return; }
            loadContractors(true).then(function (fresh) { fill(fresh, c.contactId); if (opts.onChange) { opts.onChange(); } });
          }, function (err) { Nadlan.dialog.showError("New contact", err); });
        });
      }
    }, function (err) { $el.append(" <span class=\"error\">" + esc(err.message) + "</span>"); });

    return {
      $el: $el,
      value: function () { var v = $select.val(); return v ? Number(v) : null; }
    };
  }

  /** Copies text; when the browser refuses (no permission, http), shows it selected for Ctrl+C. */
  function copyText(title, text) {
    function fallback() {
      var $input = $("<input class=\"input copy-box\" readonly>").val(text);
      var d = Nadlan.dialog.open({
        title: title,
        content: $("<div class=\"form\"><div class=\"muted\">Copy it with Ctrl+C (on a phone: long-press).</div></div>").append($input),
        width: Math.min(560, window.innerWidth - 24),
        buttons: [{ text: "Close", primary: true, click: function () { d.close(); } }]
      });
      $input.trigger("focus").trigger("select");
      return Promise.resolve(false);
    }

    if (!navigator.clipboard || !window.isSecureContext) { return fallback(); }
    return navigator.clipboard.writeText(text).then(function () { return true; }, fallback);
  }

  Nadlan.work = {
    WORK: WORK,
    PARCEL: PARCEL,
    FILTERS: FILTERS,
    workBadge: function (code) { return badge(WORK, code); },
    parcelBadge: function (code) { return badge(PARCEL, code); },
    linkBadge: function (state) { return badge(LINK, state); },
    dateTime: dateTime,
    date: date,
    time: time,
    setStatus: setStatus,
    contractorPicker: contractorPicker,
    loadContractors: loadContractors,
    copyText: copyText
  };
})(window, jQuery);
