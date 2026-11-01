// One work request on the Work page: its name and contractor, the link panel (copy, enable / disable, extend, password),
// the Parcels and works table with the filters, adding Parcels with their works, "Copy for another contractor" and the
// history. Every write answers with the whole request, which is drawn again. The server checks every rule.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var W = Nadlan.work;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };
  var LAST_WORKS_KEY = "nadlan.work.lastWorks";

  var $panel, req, types = [], filter = "all", sort = null;
  var picked = {}, highlighted = null; // works ticked for "Approve selected"; the Parcel clicked on the map
  var mapApi = null, mapHasShapes = false, picking = false;

  function api(promise, okMessage) {
    return promise.then(function (res) {
      if (okMessage) { W.setStatus(okMessage); }
      return res;
    }, function (err) {
      W.setStatus(err.message, true);
      throw err;
    });
  }

  function url(path) { return "/api/work/requests/" + req.workRequestId + (path || ""); }

  function update(detail, message) {
    req = detail;
    render();
    if (message) { W.setStatus(message); }
  }

  // ---- the header ---------------------------------------------------------------------------------------------

  function headerHtml() {
    return "<section class=\"work-card work-head\">" +
      "<div class=\"form\">" +
        "<div class=\"form-row work-head-row\">" +
          "<label>Name<input class=\"input\" name=\"name\" maxlength=\"200\" value=\"" + esc(req.name) + "\"></label>" +
          "<label class=\"contractor-label\">Contractor</label>" +
        "</div>" +
        (req.contractorLocked ? "<div class=\"muted\">The contractor has made offers, so the request stays with this contractor. For another one use \"Copy for another contractor\".</div>" : "") +
        "<label class=\"check\"><input type=\"checkbox\" name=\"showSize\"" + (req.showSizeToContractor ? " checked" : "") + "> Show the parcels' size (m²) to the contractor</label>" +
        "<div class=\"btn-row\">" +
          "<button type=\"button\" class=\"btn btn-primary\" data-act=\"save\" disabled>Save</button>" +
          "<button type=\"button\" class=\"btn\" data-act=\"copy-request\">Copy for another contractor</button>" +
        "</div>" +
        "<div class=\"muted work-meta\">Created " + W.dateTime(req.createdUtc) + (req.createdBy ? " by " + esc(req.createdBy) : "") +
          (req.copiedFrom ? " · copied from <a href=\"#request=" + req.copiedFrom.workRequestId + "\">" + esc(req.copiedFrom.name) + "</a>" : "") + "</div>" +
      "</div>" +
    "</section>";
  }

  function bindHeader() {
    var $head = $panel.find(".work-head");
    var picker = W.contractorPicker({
      selected: req.contractor.contactId, selectedName: req.contractor.displayName, disabled: req.contractorLocked, onChange: changed
    });
    $head.find(".contractor-label").append(picker.$el);

    function changed() {
      var dirty = $.trim($head.find("[name=name]").val()) !== req.name
        || (picker.value() || req.contractor.contactId) !== req.contractor.contactId
        || $head.find("[name=showSize]").prop("checked") !== req.showSizeToContractor;
      $head.find("[data-act=save]").prop("disabled", !dirty);
    }

    $head.on("input change", "[name=name], [name=showSize]", changed);
    $head.on("keydown", "[name=name]", function (e) { if (e.key === "Enter") { e.preventDefault(); save(); } });
    $head.on("click", "[data-act=save]", save);

    function save() {
      var $btn = $head.find("[data-act=save]").prop("disabled", true);
      api(Nadlan.api.put(url(), {
        name: $.trim($head.find("[name=name]").val()),
        contractorContactId: picker.value() || req.contractor.contactId,
        showSizeToContractor: $head.find("[name=showSize]").prop("checked")
      })).then(function (detail) { update(detail, "Saved."); }, function () { $btn.prop("disabled", false); });
    }
  }

  // ---- the link panel -----------------------------------------------------------------------------------------

  function linkHtml() {
    var l = req.link;
    return "<section class=\"work-card work-link\">" +
      "<h3>Link for the contractor " + W.linkBadge(l.state) + "</h3>" +
      "<div class=\"muted\">Works without a login, on the phone: the contractor sees these parcels on a map, sends prices, starts and finishes the works. " +
        (l.hasPassword ? "Send the password separately." : "") + "</div>" +
      (l.lockedUntilUtc ? "<div class=\"warn\">Locked after wrong passwords until " + W.time(l.lockedUntilUtc) + " (" + W.date(l.lockedUntilUtc) + ").</div>" : "") +
      "<div class=\"btn-row\">" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"copy-link\">Copy link</button>" +
        (l.enabled
          ? "<button type=\"button\" class=\"btn\" data-act=\"disable\">Disable</button>"
          : "<button type=\"button\" class=\"btn\" data-act=\"enable\">Enable</button>") +
      "</div>" +
      "<div class=\"work-link-line\">" + (l.state === "expired" ? "<span class=\"error\">Expired</span> " : "Expires ") + W.date(l.expiresUtc) +
        " <button type=\"button\" class=\"btn btn-small\" data-act=\"extend\">Extend 1 year</button></div>" +
      "<div class=\"work-link-line\">Password: " + (l.hasPassword
        ? "<strong>set</strong> <button type=\"button\" class=\"btn btn-small\" data-act=\"password\">Change…</button>" +
          " <button type=\"button\" class=\"btn btn-small\" data-act=\"generate\">Generate new</button>" +
          " <button type=\"button\" class=\"btn btn-small\" data-act=\"remove-password\">Remove</button>"
        : "<span class=\"muted\">none</span> <button type=\"button\" class=\"btn btn-small\" data-act=\"password\">Set…</button>" +
          " <button type=\"button\" class=\"btn btn-small\" data-act=\"generate\">Generate</button>") +
      "</div>" +
    "</section>";
  }

  function bindLink() {
    var $link = $panel.find(".work-link");
    $link.on("click", "[data-act=copy-link]", function () {
      api(Nadlan.api.get(url("/link"))).then(function (res) {
        var address = window.location.origin + "/work-link.html#" + res.token;
        W.copyText("Link for " + req.contractor.displayName, address).then(function (copied) {
          if (copied) { W.setStatus("Link copied." + (req.link.hasPassword ? " Send the password separately." : "")); }
        });
      });
    });
    $link.on("click", "[data-act=enable], [data-act=disable]", function () {
      var on = $(this).data("act") === "enable";
      api(Nadlan.api.post(url("/link/" + (on ? "enable" : "disable")))).then(function (d) { update(d, on ? "Link enabled." : "Link disabled: it opens nothing now."); });
    });
    $link.on("click", "[data-act=extend]", function () {
      api(Nadlan.api.post(url("/link/extend"))).then(function (d) { update(d, "Link extended to " + W.date(d.link.expiresUtc) + "."); });
    });
    $link.on("click", "[data-act=generate]", function () { setPassword({ generate: true }); });
    $link.on("click", "[data-act=password]", openPassword);
    $link.on("click", "[data-act=remove-password]", function () {
      Nadlan.dialog.confirm("Remove the password", "The link will open without a password.", "Remove").then(function (ok) {
        if (ok) { api(Nadlan.api.del(url("/password"))).then(function (d) { update(d, "Password removed."); }); }
      });
    });
  }

  function openPassword() {
    var $form = $("<form class=\"form\"><label>Password for the link<input class=\"input\" name=\"password\" autocomplete=\"off\" maxlength=\"64\" " +
      "placeholder=\"At least 4 characters\"></label><div class=\"muted\">The contractor types it once on the phone; it is remembered there for 30 days.</div></form>");
    var d = Nadlan.dialog.open({
      title: req.link.hasPassword ? "Change the password" : "Set a password",
      content: $form,
      width: Math.min(440, window.innerWidth - 24),
      buttons: [
        { text: "Save", primary: true, click: function () {
          d.busy(true);
          setPassword({ password: $form.find("[name=password]").val() || "" }).then(function () { d.close(); }, function (err) { d.busy(false); d.showError(err.message); });
        } },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    $form.find("[name=password]").trigger("focus");
  }

  // Shown once, right after it is set: afterwards the page only knows "set".
  function setPassword(body) {
    return api(Nadlan.api.post(url("/password"), { password: body.password || null, generate: !!body.generate })).then(function (res) {
      update(res.request, "Password set.");
      var $content = $("<div class=\"form\"><div class=\"work-password\"></div>" +
        "<div class=\"muted\">Shown only now. Send it to the contractor separately from the link (e.g. by phone). Phones that " +
        "remembered the old password are signed out.</div></div>");
      $content.find(".work-password").text(res.password);
      var d = Nadlan.dialog.open({
        title: "Password for " + req.contractor.displayName,
        content: $content,
        width: Math.min(420, window.innerWidth - 24),
        buttons: [
          { text: "Copy", click: function () { W.copyText("Password", res.password).then(function (ok) { if (ok) { W.setStatus("Password copied."); } }); } },
          { text: "Done", primary: true, click: function () { d.close(); } }
        ]
      });
      return res;
    });
  }

  // ---- Parcels and works --------------------------------------------------------------------------------------

  var COLUMNS = [
    { key: "kaek", label: "KAEK", value: function (r) { return r.registryId || ""; } },
    { key: "otPlot", label: "OT / plot", value: function (r) { return (r.ot || "") + " / " + (r.plot || ""); } },
    { key: "area", label: "Area", value: function (r) { return r.geographicArea || ""; } },
    { key: "sqm", label: "m²", num: true, value: function (r) { return r.officialAreaSqm; } },
    { key: "work", label: "Work", value: function (r) { return r.workTypeName; } },
    { key: "status", label: "Status", value: function (r) { return Object.keys(W.WORK).indexOf(r.status); } },
    { key: "price", label: "Price", num: true, value: function (r) { return r.price; } },
    { key: "estStart", label: "Est. start", value: function (r) { return r.estStartDate || ""; } },
    { key: "offered", label: "Offered", value: function (r) { return r.offeredUtc || ""; } },
    { key: "approved", label: "Approved", value: function (r) { return r.approvedUtc || ""; } },
    { key: "started", label: "Started", value: function (r) { return r.startedUtc || ""; } },
    { key: "done", label: "Done", value: function (r) { return r.doneUtc || ""; } },
    { key: "paid", label: "Paid", value: function (r) { return r.paidUtc || ""; } }
  ];

  function parcelsOf(items) {
    var seen = {};
    return items.filter(function (r) { if (seen[r.parcelId]) { return false; } seen[r.parcelId] = true; return true; });
  }

  // The Parcels with at least one work in the filter's statuses (all their works are listed).
  function parcelIdsIn(f) {
    var ids = {};
    req.items.forEach(function (r) { if (!f.statuses || f.statuses.indexOf(r.status) >= 0) { ids[r.parcelId] = true; } });
    return ids;
  }

  function worksHtml() {
    var parcels = parcelsOf(req.items).length;
    var chips = W.FILTERS.map(function (f) {
      var n = Object.keys(parcelIdsIn(f)).length;
      return "<button type=\"button\" class=\"work-chip" + (f.key === filter ? " active" : "") + "\" data-filter=\"" + f.key + "\"" +
        (f.key === filter ? " aria-pressed=\"true\"" : " aria-pressed=\"false\"") + ">" + esc(f.name) + " <span class=\"work-chip-count\">" + n + "</span></button>";
    }).join("");

    return "<section class=\"work-card work-works\">" +
      "<div class=\"meta-toolbar\"><h3>Parcels and works</h3>" +
        "<span class=\"muted\">" + parcels + " parcel" + (parcels === 1 ? "" : "s") + " · " + req.items.length + " work" + (req.items.length === 1 ? "" : "s") + "</span>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"add-parcel\">+ Add parcel</button>" +
        "<button type=\"button\" class=\"btn\" data-act=\"approve-selected\"" + (pickedIds().length ? "" : " disabled") + ">Approve selected" +
          (pickedIds().length ? " (" + pickedIds().length + ")" : "") + "</button>" +
      "</div>" +
      "<div class=\"work-chips\" role=\"group\" aria-label=\"Filter by status\">" + chips + "</div>" +
      "<div class=\"work-table-wrap\">" + tableHtml() + "</div>" +
    "</section>";
  }

  function tableHtml() {
    if (!req.items.length) {
      return "<div class=\"table-empty muted\">No parcels yet. Press \"+ Add parcel\" and choose the works to price on each.</div>";
    }

    var f = W.FILTERS.filter(function (x) { return x.key === filter; })[0] || W.FILTERS[0];
    var ids = parcelIdsIn(f);
    var rows = req.items.filter(function (r) { return ids[r.parcelId]; });
    if (sort) {
      var col = COLUMNS.filter(function (c) { return c.key === sort.key; })[0];
      rows = rows.map(function (r, i) { return { r: r, i: i }; }).sort(function (a, b) {
        var x = col.value(a.r), y = col.value(b.r);
        var empty = function (v) { return v === null || v === undefined || v === ""; };
        if (empty(x) !== empty(y)) { return empty(x) ? 1 : -1; } // empty last, both ways
        var c = col.num ? Number(x) - Number(y) : String(x).localeCompare(String(y), undefined, { numeric: true });
        return (c || a.i - b.i) * (c ? sort.dir : 1);
      }).map(function (x) { return x.r; });
    }

    if (!rows.length) { return "<div class=\"table-empty muted\">No parcel has a work in this status.</div>"; }

    // "Remove parcel" once per Parcel (its first row shown), only while every one of its works is still Requested.
    var removable = {}, firstShown = {};
    parcelsOf(req.items).forEach(function (p) {
      removable[p.parcelId] = req.items.every(function (r) { return r.parcelId !== p.parcelId || r.status === "REQUESTED"; });
    });

    var offered = rows.filter(function (r) { return r.status === "OFFERED"; });
    var allPicked = offered.length > 0 && offered.every(function (r) { return picked[r.workItemId]; });
    return "<table class=\"meta-table work-items\"><thead><tr>" +
      "<th class=\"pick\">" + (offered.length ? "<input type=\"checkbox\" data-act=\"pick-all\" aria-label=\"Select every offered work shown\"" + (allPicked ? " checked" : "") + ">" : "") + "</th>" +
      COLUMNS.map(function (c) {
        var on = sort && sort.key === c.key;
        return "<th class=\"" + (c.num ? "num " : "") + "sortable\" data-sort=\"" + c.key + "\" aria-sort=\"" +
          (on ? (sort.dir > 0 ? "ascending" : "descending") : "none") + "\" tabindex=\"0\">" + esc(c.label) + (on ? (sort.dir > 0 ? " ▲" : " ▼") : "") + "</th>";
      }).join("") + "<th></th></tr></thead><tbody>" +
      rows.map(function (r) {
        var first = !firstShown[r.parcelId];
        firstShown[r.parcelId] = true;
        return "<tr data-work=\"" + r.workItemId + "\" data-parcel-row=\"" + r.parcelId + "\"" + (r.parcelId === highlighted ? " class=\"is-highlighted\"" : "") + ">" +
          "<td class=\"pick\">" + (r.status === "OFFERED" ? "<input type=\"checkbox\" data-act=\"pick\" aria-label=\"Select to approve\"" + (picked[r.workItemId] ? " checked" : "") + ">" : "") + "</td>" +
          "<td>" + Nadlan.format.registryId(r.registryId, r.registryIdIsProvisional) + "</td>" +
          "<td>" + Nadlan.format.text(r.ot) + " / " + Nadlan.format.text(r.plot) + "</td>" +
          "<td>" + Nadlan.format.text(r.geographicArea) + "</td>" +
          "<td class=\"num\">" + (r.officialAreaSqm === null ? "—" : esc(Number(r.officialAreaSqm).toLocaleString("en-US", { maximumFractionDigits: 0 }))) + "</td>" +
          "<td>" + esc(r.workTypeName) + "</td>" +
          "<td>" + W.workBadge(r.status) + "</td>" +
          "<td class=\"num\">" + (r.price === null ? "—" : W.money(r.price, r.currencyCode)) + "</td>" +
          "<td>" + W.date(r.estStartDate) + "</td>" +
          "<td>" + W.dateTime(r.offeredUtc) + "</td>" +
          "<td>" + W.dateTime(r.approvedUtc) + "</td>" +
          "<td>" + W.dateTime(r.startedUtc) + "</td>" +
          "<td>" + W.dateTime(r.doneUtc) + "</td>" +
          "<td>" + W.dateTime(r.paidUtc) + "</td>" +
          "<td class=\"row-buttons\">" +
            (r.status === "OFFERED" ? "<button type=\"button\" class=\"btn btn-primary btn-small\" data-act=\"approve\" title=\"Approve this price: it is then locked for the contractor\">Approve</button>" : "") +
            (r.status === "DONE" ? "<button type=\"button\" class=\"btn btn-primary btn-small\" data-act=\"paid\">Mark paid</button>" : "") +
            (r.status !== "REQUESTED" ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"correct\" title=\"One step back, with a reason\">Correct…</button>" : "") +
            (r.status === "REQUESTED" ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"remove-work\" title=\"Remove this work from the parcel\">Remove</button>" : "") +
            (first ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"parcel-media\" data-parcel=\"" + r.parcelId + "\" title=\"The contractor's pictures of this parcel's works, and the conversation\">" +
              "Pictures " + parcelPictures(r.parcelId) + " · Messages " + (r.messages || 0) + "</button>" : "") +
            (first && removable[r.parcelId] ? "<button type=\"button\" class=\"btn btn-small\" data-act=\"remove-parcel\" data-parcel=\"" + r.parcelId +
              "\" title=\"Remove the parcel and all its works from the request\">Remove parcel</button>" : "") +
          "</td>" +
        "</tr>";
      }).join("") + "</tbody></table>";
  }

  function bindWorks() {
    var $works = $panel.find(".work-works");
    $works.on("click", "[data-filter]", function () {
      filter = $(this).data("filter");
      render();
    });
    $works.on("click keydown", "th[data-sort]", function (e) {
      if (e.type === "keydown" && e.key !== "Enter" && e.key !== " ") { return; }
      e.preventDefault();
      var key = $(this).data("sort");
      sort = sort && sort.key === key ? (sort.dir > 0 ? { key: key, dir: -1 } : null) : { key: key, dir: 1 }; // up, down, as added
      render();
      $panel.find("th[data-sort=" + key + "]").trigger("focus");
    });
    $works.on("click", "[data-act=add-parcel]", function () { openAddParcel(); });
    $works.on("click", "[data-act=remove-work]", function () {
      var id = Number($(this).closest("tr").data("work"));
      var row = req.items.filter(function (r) { return r.workItemId === id; })[0];
      var last = req.items.filter(function (r) { return r.parcelId === row.parcelId; }).length === 1;
      Nadlan.dialog.confirm("Remove a work", esc(row.workTypeName) + " on parcel " + esc(row.registryId || "") +
        (last ? "<br><span class=\"muted\">It is the parcel's last work: the parcel leaves the request too.</span>" : ""), "Remove").then(function (ok) {
        if (ok) { api(Nadlan.api.del(url("/works/" + id))).then(function (d) { update(d, "Work removed."); }); }
      });
    });
    $works.on("change", "[data-act=pick]", function () {
      var id = Number($(this).closest("tr").data("work"));
      if (this.checked) { picked[id] = true; } else { delete picked[id]; }
      renderWorks();
    });
    $works.on("change", "[data-act=pick-all]", function () {
      var on = this.checked;
      $works.find("[data-act=pick]").each(function () {
        var id = Number($(this).closest("tr").data("work"));
        if (on) { picked[id] = true; } else { delete picked[id]; }
      });
      renderWorks();
    });
    $works.on("click", "[data-act=approve-selected]", function () {
      var ids = pickedIds();
      if (!ids.length) { return; }
      api(Nadlan.api.post(url("/approve"), { workItemIds: ids })).then(function (res) {
        picked = {};
        update(res.request, res.approved + " price" + (res.approved === 1 ? "" : "s") + " approved.");
      });
    });
    $works.on("click", "[data-act=approve]", function () {
      var row = rowOf(this);
      api(Nadlan.api.post(url("/approve"), { workItemIds: [row.workItemId] })).then(function (res) {
        delete picked[row.workItemId];
        update(res.request, row.workTypeName + " on " + (row.registryId || "the parcel") + " approved at " + W.money(row.price, row.currencyCode) + ".");
      });
    });
    $works.on("click", "[data-act=paid]", function () {
      var row = rowOf(this);
      Nadlan.dialog.confirm("Mark paid", esc(row.workTypeName) + " on parcel " + esc(row.registryId || "") + ": " +
        W.money(row.price, row.currencyCode) + " paid?", "Mark paid").then(function (ok) {
        if (ok) { api(Nadlan.api.post(url("/works/" + row.workItemId + "/paid"))).then(function (d) { update(d, "Marked paid."); }); }
      });
    });
    $works.on("click", "[data-act=correct]", function () { openCorrect(rowOf(this)); });
    $works.on("click", "[data-act=parcel-media]", function () { openParcelMedia(Number($(this).data("parcel"))); });
    $works.on("click", "[data-act=remove-parcel]", function () {
      var parcelId = Number($(this).data("parcel"));
      var row = req.items.filter(function (r) { return r.parcelId === parcelId; })[0];
      Nadlan.dialog.confirm("Remove a parcel", "Remove parcel " + esc(row.registryId || "") + " and its works from this request?", "Remove").then(function (ok) {
        if (ok) { api(Nadlan.api.del(url("/parcels/" + parcelId))).then(function (d) { update(d, "Parcel removed."); }); }
      });
    });
  }

  function rowOf(el) {
    var id = Number($(el).closest("tr").data("work"));
    return req.items.filter(function (r) { return r.workItemId === id; })[0];
  }

  function pickedIds() {
    var offered = {};
    req.items.forEach(function (r) { if (r.status === "OFFERED") { offered[r.workItemId] = true; } });
    return Object.keys(picked).map(Number).filter(function (id) { return offered[id]; });
  }

  // One step back, with a reason (owner): what each step undoes, as Nadlan.Core.Work.WorkTransitions says.
  var BACK = {
    OFFERED: { to: "REQUESTED", what: "The offered price and start date are cleared; the contractor offers again." },
    APPROVED: { to: "OFFERED", what: "\"Reopen the price\": the approval is cleared; the contractor may change the price." },
    IN_WORK: { to: "APPROVED", what: "The start is cleared." },
    DONE: { to: "IN_WORK", what: "\"Done\" is cleared." },
    PAID: { to: "DONE", what: "\"Paid\" is cleared." }
  };

  function openCorrect(row) {
    var back = BACK[row.status];
    if (!back) { return; }
    var $form = $("<form class=\"form\">" +
      "<div>" + esc(row.workTypeName) + " on parcel " + esc(row.registryId || "") + ": " + W.workBadge(row.status) + " → " + W.workBadge(back.to) + "</div>" +
      "<div class=\"muted\">" + esc(back.what) + "</div>" +
      "<label>Reason (goes into the history)<textarea class=\"input\" name=\"reason\" rows=\"3\" maxlength=\"300\"></textarea></label>" +
      "</form>");
    var d = Nadlan.dialog.open({
      title: "Correct a status",
      content: $form,
      width: Math.min(480, window.innerWidth - 24),
      buttons: [
        { text: "Step back", primary: true, click: function () {
          var reason = $.trim($form.find("[name=reason]").val() || "");
          if (!reason) { d.showError("Write a short reason."); return; }
          d.busy(true);
          Nadlan.api.post(url("/works/" + row.workItemId + "/correct"), { reason: reason }).then(function (detail) {
            d.close();
            update(detail, "Corrected: back to " + W.WORK[back.to].name + ".");
          }, function (err) { d.busy(false); d.showError(err.message); });
        } },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    $form.find("[name=reason]").trigger("focus");
  }

  // ---- + Add parcel -------------------------------------------------------------------------------------------

  function lastWorks() {
    try { return JSON.parse(window.localStorage.getItem(LAST_WORKS_KEY) || "[]") || []; } catch (e) { return []; }
  }

  function rememberWorks(ids) {
    try { window.localStorage.setItem(LAST_WORKS_KEY, JSON.stringify(ids)); } catch (e) { /* private mode: fine */ }
  }

  /** @param {object=} preset  parcels already found (picked on the map): shown at once, the search stays below */
  function openAddParcel(preset) {
    var active = types.filter(function (t) { return t.isActive; });
    var checked = lastWorks();
    var $form = $(
      "<form class=\"form add-work-parcel\">" +
        "<fieldset class=\"checks work-type-checks\"><legend>Works to price on the parcel</legend>" +
          active.map(function (t) {
            return "<label class=\"check\"><input type=\"checkbox\" data-type=\"" + t.workTypeId + "\"" + (checked.indexOf(t.workTypeId) >= 0 ? " checked" : "") + "> " + esc(t.name) + "</label>";
          }).join("") +
        "</fieldset>" +
        "<div class=\"muted\">Each parcel you add starts with these works; change them before adding the next one.</div>" +
        "<label>KAEK<input class=\"input\" name=\"kaek\" autocomplete=\"off\" placeholder=\"At least 3 characters\" enterkeyhint=\"search\"></label>" +
        "<div class=\"add-or muted\">or</div>" +
        // No numeric keypad: OT and plot may have a letter (47A).
        "<div class=\"form-row\"><label>OT<input class=\"input\" name=\"ot\" autocomplete=\"off\" enterkeyhint=\"search\"></label>" +
        "<label>Plot<input class=\"input\" name=\"plot\" autocomplete=\"off\" enterkeyhint=\"search\"></label></div>" +
        "<div class=\"add-results\" aria-live=\"polite\"></div>" +
      "</form>");
    var d = Nadlan.dialog.open({
      title: "Add parcels",
      content: $form,
      width: Math.min(680, window.innerWidth - 24),
      buttons: [
        { text: "Search", primary: true, click: search },
        { text: "Close", click: function () { d.close(); } }
      ]
    });
    if (!preset) { $form.find("[name=kaek]").trigger("focus"); }
    $form.on("input", "[name=kaek]", function () { if (this.value) { $form.find("[name=ot], [name=plot]").val(""); } });
    $form.on("input", "[name=ot], [name=plot]", function () { if (this.value) { $form.find("[name=kaek]").val(""); } });

    function chosen() {
      return $form.find("[data-type]:checked").map(function () { return Number($(this).data("type")); }).get();
    }

    function search() {
      var data = Nadlan.dialog.readForm($form);
      d.showError("");
      d.busy(true);
      Nadlan.api.get("/api/work/parcels", { kaek: data.kaek || "", ot: data.ot || "", plot: data.plot || "", requestId: req.workRequestId }).then(function (res) {
        d.busy(false);
        showResults(res, "No parcel found in your areas.");
      }, function (err) { d.busy(false); d.showError(err.message); });
    }

    function showResults(res, none) {
      var $results = $form.find(".add-results");
      if (!res.items.length) {
        $results.html("<div class=\"warn\">" + esc(none) + "</div>");
        return;
      }

      $results.html(
        "<table class=\"add-table\"><thead><tr><th>KAEK</th><th>Area</th><th>OT</th><th>Plot</th><th>m²</th><th></th></tr></thead><tbody>" +
        res.items.map(function (p) {
          return "<tr><td>" + Nadlan.format.registryId(p.registryId, false) + "</td><td>" + Nadlan.format.text(p.geographicArea) + "</td>" +
            "<td>" + Nadlan.format.text(p.ot) + "</td><td>" + Nadlan.format.text(p.plot) + "</td>" +
            "<td>" + (p.officialAreaSqm === null ? "—" : esc(Number(p.officialAreaSqm).toLocaleString("en-US", { maximumFractionDigits: 0 }))) + "</td>" +
            "<td><button type=\"button\" class=\"btn btn-primary btn-small\" data-parcel=\"" + p.parcelId + "\">" + (p.inRequest ? "Add works" : "Add") + "</button>" +
              (p.inRequest ? " <span class=\"muted\">(in this request)</span>" : "") + "</td></tr>";
        }).join("") + "</tbody></table>" +
        (res.more ? "<div class=\"muted\">More parcels match - type more of the KAEK.</div>" : ""));
      $results.find("[data-parcel]").first().trigger("focus");
    }

    if (preset) { showResults(preset, "No parcel of yours at that point on the map."); }

    $form.on("click", "[data-parcel]", function () {
      var $btn = $(this);
      var ids = chosen();
      if (!ids.length) { d.showError("Tick at least one work for the parcel."); return; }
      d.showError("");
      $btn.prop("disabled", true);
      Nadlan.api.post(url("/parcels"), { parcelId: Number($btn.data("parcel")), workTypeIds: ids }).then(function (res) {
        rememberWorks(ids);
        update(res.request);
        $btn.replaceWith("<span class=\"done-note\">" + (res.added ? "Added ✓" : "Already had these works") + "</span>");
        W.setStatus(res.added ? "Parcel added with " + res.added + " work" + (res.added === 1 ? "" : "s") + "." : "");
        $form.find("[name=kaek], [name=ot], [name=plot]").first().trigger("focus");
      }, function (err) {
        $btn.prop("disabled", false);
        d.showError(err.message);
      });
    });
  }

  // ---- Copy for another contractor ----------------------------------------------------------------------------

  function openCopy() {
    var $form = $(
      "<form class=\"form\">" +
        "<div class=\"muted\">The same parcels and works, for another contractor: a new link, no password, nothing offered yet. " +
          "This request stays as it is.</div>" +
        "<label>Name<input class=\"input\" name=\"name\" maxlength=\"200\"></label>" +
        "<label class=\"contractor-label\">Contractor</label>" +
      "</form>");
    $form.find("[name=name]").val(req.name);
    var picker = W.contractorPicker({});
    $form.find(".contractor-label").append(picker.$el);
    var d = Nadlan.dialog.open({
      title: "Copy for another contractor",
      content: $form,
      width: Math.min(520, window.innerWidth - 24),
      buttons: [
        { text: "Copy", primary: true, click: copy },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });

    function copy() {
      var contractorContactId = picker.value();
      if (!contractorContactId) { d.showError("Choose the contractor."); return; }
      if (contractorContactId === req.contractor.contactId) { d.showError("This request is already for " + req.contractor.displayName + ". Choose another contractor."); return; }
      d.busy(true);
      Nadlan.api.post(url("/copy"), { name: $.trim($form.find("[name=name]").val() || ""), contractorContactId: contractorContactId }).then(function (res) {
        d.close();
        window.location.hash = "request=" + res.workRequestId;

      }, function (err) { d.busy(false); d.showError(err.message); });
    }
  }

  // ---- history ------------------------------------------------------------------------------------------------

  function historyHtml() {
    return "<section class=\"work-card work-history\">" +
      "<button type=\"button\" class=\"btn\" data-act=\"history\" aria-expanded=\"false\">Show history</button>" +
      "<div class=\"work-history-list\" hidden></div>" +
    "</section>";
  }

  function bindHistory() {
    var $h = $panel.find(".work-history");
    $h.on("click", "[data-act=history]", function () {
      var $btn = $(this), $list = $h.find(".work-history-list");
      if (!$list.prop("hidden")) { $list.prop("hidden", true); $btn.text("Show history").attr("aria-expanded", "false"); return; }
      $btn.text("Hide history").attr("aria-expanded", "true");
      $list.prop("hidden", false).html("<div class=\"muted\">Loading…</div>");
      api(Nadlan.api.get(url("/history"))).then(function (events) {
        $list.html(events.length ? "<table class=\"meta-table\"><thead><tr><th>When</th><th>Who</th><th>What</th></tr></thead><tbody>" +
          events.map(function (e) {
            var who = e.actorKind === "STAFF" ? esc(e.actorName || "Staff") : e.actorKind === "CONTRACTOR" ? "Contractor" : "GreekPlot";
            return "<tr><td class=\"nowrap\">" + W.dateTime(e.createdUtc) + "</td><td>" + who + "</td><td>" + esc(e.summary) + "</td></tr>";
          }).join("") + "</tbody></table>" : "<div class=\"muted\">Nothing yet.</div>");
      }, function (err) { $list.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
    });
  }

  // ---- the contractor's location (piece 3) --------------------------------------------------------------------

  var personTimer = null;

  function today() {
    var d = new Date();
    return d.getFullYear() + "-" + ("0" + (d.getMonth() + 1)).slice(-2) + "-" + ("0" + d.getDate()).slice(-2);
  }

  function ago(utc) {
    var min = Math.max(0, Math.round((Date.now() - new Date(utc).getTime()) / 60000));
    if (min < 1) { return "just now"; }
    if (min < 60) { return min + " min ago"; }
    if (min < 48 * 60) { return Math.round(min / 60) + " h ago"; }
    return W.dateTime(utc);
  }

  // Every 30 s while the request is open and the tab is visible: the last position, and the chosen day's trail.
  function startPersonTimer() {
    if (personTimer) { window.clearInterval(personTimer); }
    var id = req.workRequestId;
    personTimer = window.setInterval(function () {
      if (!req || req.workRequestId !== id || !$panel.find("#work-request-map").length) {
        window.clearInterval(personTimer);
        personTimer = null;
        return;
      }
      if (document.visibilityState === "visible") { refreshPerson(); }
    }, 30000);
  }

  function refreshPerson() {
    if (!mapApi) { return; }
    var day = $panel.find("[data-act=trail-day]").val() || today();
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
    var from = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])); // the user's own day, midnight to midnight
    var to = new Date(from.getTime());
    to.setDate(to.getDate() + 1);
    Nadlan.api.get(url("/locations"), { fromUtc: from.toISOString(), toUtc: to.toISOString() }).then(function (res) {
      var $seen = $panel.find(".work-seen");
      if (res.latest) {
        var label = req.contractor.displayName + " · " + ago(res.latest.recordedUtc);
        mapApi.setPosition({ lat: res.latest.latitude, lng: res.latest.longitude, accuracy: Number(res.latest.accuracyM || 0) }, label);
        $seen.text("Last seen " + ago(res.latest.recordedUtc) + (res.latest.accuracyM !== null ? " (±" + Math.round(res.latest.accuracyM) + " m)" : "") +
          " · trail: " + res.trail.length + " point" + (res.trail.length === 1 ? "" : "s") + (res.truncated ? " (first 5,000)" : ""));
      } else {
        mapApi.setPosition(null);
        $seen.text("The contractor hasn't shared a location yet.");
      }
      mapApi.setTrail(res.trail);
    }, function () { /* the next round tries again */ });
  }

  // ---- a Parcel's pictures and conversation (piece 3) -------------------------------------------------------------

  function parcelPictures(parcelId) {
    return req.items.reduce(function (n, r) { return n + (r.parcelId === parcelId ? r.pictures || 0 : 0); }, 0);
  }

  function place(lat, lng) { return Number(lat).toFixed(6) + ", " + Number(lng).toFixed(6); }

  function pictureCardHtml(f, i) {
    var facts = [];
    if (f.takenLocal) { facts.push("Taken " + Nadlan.takenDate(f.takenLocal, true)); }
    facts.push("Uploaded " + W.dateTime(f.uploadedUtc));
    if (f.cameraMake || f.cameraModel) { facts.push([f.cameraMake, f.cameraModel].filter(Boolean).join(" ")); }
    if (f.width && f.height) { facts.push(f.width + " × " + f.height); }
    if (f.durationS) { facts.push(Nadlan.mediaViewer.formatTime(f.durationS * 1000)); }
    var where = f.latitude !== null && f.longitude !== null ? { lat: f.latitude, lng: f.longitude, what: "in the photo" }
      : f.uploadLatitude !== null && f.uploadLongitude !== null ? { lat: f.uploadLatitude, lng: f.uploadLongitude, what: "the phone at upload" } : null;
    return "<div class=\"work-pic\" data-index=\"" + i + "\">" +
      "<button type=\"button\" class=\"work-pic-thumb\" data-act=\"view-pic\" title=\"View\">" +
        (f.thumbUrl ? "<img src=\"" + esc(f.thumbUrl) + "\" alt=\"\" loading=\"lazy\">" : "<span>" + (f.kind === "video" ? "▶ Video" : "Photo") + "</span>") +
        (f.kind === "video" && f.thumbUrl ? "<span class=\"work-pic-play\">▶</span>" : "") +
      "</button>" +
      "<div class=\"work-pic-name\"></div>" +
      "<div class=\"muted work-pic-facts\">" + esc(facts.join(" · ")) + "</div>" +
      (where ? "<div class=\"muted work-pic-facts\">Place (" + esc(where.what) + "): " + esc(place(where.lat, where.lng)) +
        " <button type=\"button\" class=\"btn btn-small\" data-act=\"pic-on-map\" data-lat=\"" + where.lat + "\" data-lng=\"" + where.lng + "\">Show on map</button></div>" : "") +
      "<div class=\"work-pic-actions\">" + (f.copied
        ? "<span class=\"done-note\">In the parcel's files ✓</span>"
        : "<button type=\"button\" class=\"btn btn-small btn-primary\" data-act=\"copy-to-parcel\" title=\"Copy into the parcel's files (Photo / Video, Marketing). The work keeps its own.\">Copy to parcel</button>") +
      "</div>" +
    "</div>";
  }

  function openParcelMedia(parcelId) {
    var rows = req.items.filter(function (r) { return r.parcelId === parcelId; });
    var kaek = rows.length ? rows[0].registryId || "" : "";
    var $box = $("<div class=\"work-parcel-media\"><div class=\"work-pics muted\">Loading…</div><h4>Messages</h4>" +
      "<div class=\"work-msgs\"></div>" +
      "<form class=\"form work-msg-form\"><textarea class=\"input\" name=\"body\" rows=\"3\" maxlength=\"4000\" placeholder=\"A message to the contractor (shown as from GreekPlot)\"></textarea>" +
      "<div><button type=\"submit\" class=\"btn btn-primary\">Send</button></div></form></div>");
    var files = [];
    var d = Nadlan.dialog.open({
      title: "Parcel " + kaek + " - pictures and messages",
      content: $box,
      width: Math.min(820, window.innerWidth - 24),
      buttons: [{ text: "Close", click: function () { d.close(); } }]
    });

    function drawFiles() {
      var $pics = $box.find(".work-pics").removeClass("muted");
      if (!files.length) { $pics.html("<div class=\"muted\">No pictures from the contractor yet.</div>"); return; }
      $pics.html(rows.map(function (r) {
        var mine = files.filter(function (f) { return f.workItemId === r.workItemId; });
        return mine.length ? "<h4>" + esc(r.workTypeName) + " " + W.workBadge(r.status) + "</h4><div class=\"work-pic-grid\">" +
          mine.map(function (f) { return pictureCardHtml(f, files.indexOf(f)); }).join("") + "</div>" : "";
      }).join(""));
      $pics.find(".work-pic").each(function () { $(this).find(".work-pic-name").text(files[Number($(this).data("index"))].name); });
    }

    function drawMessages(list) {
      var $m = $box.find(".work-msgs");
      $m.html(list.length ? list.map(function () {
        return "<div class=\"work-msg\"><div class=\"muted work-msg-head\"></div><div class=\"work-msg-body\"></div></div>";
      }).join("") : "<div class=\"muted\">No messages yet.</div>");
      $m.find(".work-msg").each(function (i) {
        $(this).toggleClass("from-contractor", list[i].fromContractor);
        $(this).find(".work-msg-head").text(list[i].author + " · " + W.dateTime(list[i].createdUtc));
        $(this).find(".work-msg-body").text(list[i].body);
      });
    }

    Nadlan.api.get(url("/parcels/" + parcelId + "/files")).then(function (list) { files = list; drawFiles(); },
      function (err) { $box.find(".work-pics").html("<div class=\"error\">" + esc(err.message) + "</div>"); });
    Nadlan.api.get(url("/parcels/" + parcelId + "/messages")).then(drawMessages, function (err) { d.showError(err.message); });

    $box.on("click", "[data-act=view-pic]", function () {
      var i = Number($(this).closest(".work-pic").data("index"));
      Nadlan.mediaViewer.open(files[i].kind, files[i].url, files[i].name, null, null, {
        items: files.map(function (f) { return { kind: f.kind, url: f.url, name: f.name + " (" + f.workTypeName + ")", download: f.downloadUrl }; }),
        index: i
      });
    });
    $box.on("click", "[data-act=pic-on-map]", function () {
      if (!mapApi) { W.setStatus("The map isn't available.", true); return; }
      mapApi.showPin({ lat: Number($(this).data("lat")), lng: Number($(this).data("lng")) }, "Picture");
      d.close();
      $panel.find(".work-map-card").get(0).scrollIntoView({ behavior: "smooth", block: "start" });
    });
    $box.on("click", "[data-act=copy-to-parcel]", function () {
      var $btn = $(this).prop("disabled", true);
      var i = Number($btn.closest(".work-pic").data("index"));
      Nadlan.api.post(url("/files/" + files[i].workFileId + "/copy-to-parcel")).then(function (copied) {
        files[i] = copied;
        drawFiles();
        W.setStatus(copied.name + " copied to parcel " + kaek + "'s files.");
      }, function (err) { $btn.prop("disabled", false); d.showError(err.message); });
    });
    $box.on("submit", ".work-msg-form", function (e) {
      e.preventDefault();
      var $form = $(this), body = $.trim($form.find("[name=body]").val() || "");
      if (!body) { return; }
      var $btn = $form.find("button").prop("disabled", true);
      Nadlan.api.post(url("/parcels/" + parcelId + "/messages"), { body: body }).then(function (list) {
        $btn.prop("disabled", false);
        $form.find("[name=body]").val("");
        drawMessages(list);
        Nadlan.api.get(url()).then(function (detail) { req = detail; renderWorks(); }); // the message count on the row
      }, function (err) { $btn.prop("disabled", false); d.showError(err.message); });
    });
  }

  // ---- the page -----------------------------------------------------------------------------------------------

  // ---- the request's map (piece 2) ----------------------------------------------------------------------------

  function mapHtml() {
    return "<section class=\"work-card work-map-card\">" +
      "<div class=\"meta-toolbar\"><h3>Map</h3>" +
        "<button type=\"button\" class=\"btn\" data-act=\"pick-on-map\" aria-pressed=\"false\">Pick a parcel on the map</button>" +
        "<span class=\"muted work-map-hint\"></span>" +
        "<label class=\"work-day\">Contractor's trail on <input type=\"date\" class=\"input\" data-act=\"trail-day\"></label>" +
        "<span class=\"muted work-seen\" aria-live=\"polite\"></span>" +
      "</div>" +
      "<div class=\"work-map\" id=\"work-request-map\"></div>" +
    "</section>";
  }

  function setPicking(on) {
    picking = on;
    var $card = $panel.find(".work-map-card");
    $card.toggleClass("is-picking", on).find("[data-act=pick-on-map]").toggleClass("active", on).attr("aria-pressed", on ? "true" : "false")
      .text(on ? "Cancel picking" : "Pick a parcel on the map");
    $card.find(".work-map-hint").text(on ? "Click the parcel to add." : "");
  }

  function refreshMap() {
    if (!mapApi) { return; }
    Nadlan.api.get(url("/shapes")).then(function (shapes) {
      var first = !mapHasShapes;
      mapHasShapes = shapes.length > 0;
      mapApi.setParcels(shapes);
      mapApi.select(highlighted);
      if (first && shapes.length) { mapApi.fit(); }
    }, function (err) { W.setStatus(err.message, true); });
  }

  function startMap() {
    var $map = $panel.find("#work-request-map");
    Nadlan.api.get("/api/config/client").then(function (config) {
      return Nadlan.workMap.load(config.googleMapsApiKey).then(function () {
        mapApi = Nadlan.workMap.create($map.get(0), {
          center: config.defaultCenter, zoom: config.defaultZoom,
          onParcelClick: function (parcelId, at) {
            if (picking && at) { pickAt(at); return; }
            showParcel(parcelId);
          },
          onMapClick: function (at) { if (picking) { pickAt(at); } }
        });
        refreshMap();
        refreshPerson();
      });
    }).catch(function (err) {
      $panel.find(".work-map-card").addClass("no-map");
      $map.html("<div class=\"muted\">The map isn't available" + (err && err.message === "NO_MAPS_KEY" ? ": no Google Maps key is set (Admin > Settings)." : ".") + "</div>");
    });
  }

  // A click on the map while picking: the Parcels there (in the user's areas) go into "+ Add parcel".
  function pickAt(at) {
    setPicking(false);
    api(Nadlan.api.get("/api/work/parcels/at", { lat: at.lat, lng: at.lng, requestId: req.workRequestId })).then(function (res) {
      openAddParcel(res);
    });
  }

  // A Parcel clicked on the map: its rows light up in the table (and the filter shows them).
  function showParcel(parcelId) {
    highlighted = parcelId;
    if (mapApi) { mapApi.select(parcelId); }
    var f = W.FILTERS.filter(function (x) { return x.key === filter; })[0];
    if (!parcelIdsIn(f)[parcelId]) { filter = "all"; }
    renderWorks();
    var $row = $panel.find("tr[data-parcel-row=" + parcelId + "]").first();
    if ($row.length) { $row.get(0).scrollIntoView({ block: "center", behavior: "smooth" }); }
  }

  // ---- the page -----------------------------------------------------------------------------------------------

  function renderWorks() {
    $panel.find(".wr-works").html(worksHtml());
    bindWorks();
  }

  function render() {
    $panel.find(".work-title").html(esc(req.name) + " <span class=\"muted\">· " + esc(req.contractor.displayName) + "</span>");
    $panel.find(".wr-top").html(headerHtml() + linkHtml());
    bindHeader();
    bindLink();
    renderWorks();
    refreshMap();
  }

  function open($target, id) {
    $panel = $target;
    $panel.off().html("<div class=\"muted\">Loading…</div>");
    filter = "all";
    sort = null;
    picked = {};
    highlighted = null;
    mapApi = null;
    mapHasShapes = false;
    picking = false;
    Promise.all([Nadlan.api.get("/api/work/requests/" + id), Nadlan.api.get("/api/work/work-types")]).then(function (loaded) {
      types = loaded[1];
      req = loaded[0];
      // The parts are drawn again after each change; the map stays (it only gets the Parcels again).
      $panel.html(
        "<div class=\"work-back\"><a href=\"#\" data-act=\"back\">← All requests</a></div>" +
        "<h2 class=\"work-title\"></h2>" +
        "<div class=\"work-top wr-top\"></div>" +
        mapHtml() +
        "<div class=\"wr-works\"></div>" +
        historyHtml());
      $panel.on("click", "[data-act=back]", function (e) { e.preventDefault(); Nadlan.workPage.showList(); });
      $panel.on("click", "[data-act=copy-request]", openCopy);
      $panel.on("click", "[data-act=pick-on-map]", function () { setPicking(!picking); });
      $panel.find("[data-act=trail-day]").val(today()).on("change", refreshPerson);
      startPersonTimer();
      bindHistory();
      render();
      startMap();
    }, function (err) {
      $panel.html("<div class=\"error\">" + esc(err.message) + "</div><div><a href=\"#\">← All requests</a></div>");
    });
  }

  Nadlan.workRequest = { open: open };
})(window, jQuery);
