// The parcel card's "Cadastre (lawyer research)" block (Admin only, owner 2026-11-24): the latest snapshot the KAEK importer
// read from the Cadastre's lawyer service - last sale, open seizures, each unit's owners and the act history. The server
// answers 404 to anyone but an Admin; the card only asks for Admins. Never used on presentations or public links.
// V2 (owner, 2026-11-26): every owner field (a "details" fold-out), the stored extracts and summary (links; each open is
// logged by the server), the measured area, and "Cadastre says OT 18" as a suggestion only - it is never applied.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };

  function money(v) { return v === null || v === undefined ? "no price" : "€" + Number(v).toLocaleString("en-US", { maximumFractionDigits: 0 }); }

  // "2024-03-28" -> "28/03/2024" (the dates are calendar days, never shifted by time zone).
  function day(iso) {
    if (!iso) { return "no date"; }
    var p = String(iso).slice(0, 10).split("-");
    return p.length === 3 ? p[2] + "/" + p[1] + "/" + p[0] : esc(iso);
  }

  function when(utc) {
    return utc ? new Date(utc).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "";
  }

  var TYPE = { Sale: "sale", Auction: "auction", Seizure: "seizure", SeizureLifted: "seizure lifted", ParentalGift: "parental gift", Inheritance: "inheritance" };
  function typeName(a) { return TYPE[a.actTypeCode] || (a.actTypeText || "other"); }
  function unitName(u) { return u.unitV === null ? "" : u.unitV + "/" + u.unitH; }

  function lastSaleHtml(r) {
    var s = r.lastSale;
    if (!s) { return "<div class=\"muted\">No sale or auction among the documents read.</div>"; }
    var several = (r.units || []).length > 1;
    var text = s.actTypeCode === "Auction"
      ? "auction " + money(s.price) + " on " + day(s.date)
      : money(s.price) + " on " + day(s.date) + " (sale)";
    return "<div class=\"cad-sale\"><b>Last sale:</b> " + text +
      (s.pricePerSqm !== null ? ", €" + Number(s.pricePerSqm).toLocaleString("en-US") + "/m²" : "") +
      (s.objectiveValue !== null && s.objectiveValue !== undefined ? " <span class=\"muted\">(objective " + money(s.objectiveValue) + ")</span>" : "") +
      (s.source === "Extract" ? " <span class=\"muted\">· from the descriptive extract; its summary (the price) was not read</span>" : "") +
      (several ? " <span class=\"muted\">· unit " + esc(s.unitV + "/" + s.unitH) + "</span>" : "") + "</div>";
  }

  function seizuresHtml(r) {
    return (r.seizures || []).map(function (s) {
      return "<div class=\"cad-warn\">⚠ Seized " + day(s.date) + (s.claim !== null ? ", claim " + money(s.claim) : "") +
        " <span class=\"muted\">(a possible auction)</span></div>";
    }).join("");
  }

  // The stored PDFs: "Descriptive extract (26 Nov 2026) · Diagram · Summary 5413/2024". Admin only; the server logs each open.
  function docLink(d, label) {
    return "<a href=\"/api/cadastre/documents/" + Number(d.cadastreDocumentId) + "/content\" target=\"_blank\" rel=\"noopener\"" +
      " title=\"" + esc(d.fileName) + (d.textReadable ? "" : " (a scan: not read)") + "\">" + esc(label) + "</a>";
  }

  function documentsHtml(docs, several) {
    if (!docs.length) { return ""; }
    return "<div class=\"cad-docs\">" + docs.map(function (d) {
      var unit = several && d.kind !== "SpatialExtract" ? " " + d.unitV + "/" + d.unitH : "";
      if (d.kind === "DescriptiveExtract") { return docLink(d, "Descriptive extract" + unit + " (" + when(d.fetchedUtc) + ")"); }
      if (d.kind === "SpatialExtract") { return docLink(d, "Diagram"); }
      return docLink(d, "Summary " + d.regNo + "/" + d.regYear);
    }).join(" · ") + "</div>";
  }

  function detailsHtml(o) {
    var d = o.details || {};
    var rows = [
      ["ΑΦΜ", o.taxId], ["Father", d.fatherName], ["Mother", d.motherName],
      ["Born", d.birthDate || d.birthPlace ? (d.birthDate ? day(d.birthDate) : "") + (d.birthPlace ? " " + esc(d.birthPlace) : "") : null, true],
      ["ID card", d.idCardNumber || d.idCardDate || d.idCardAuthority
        ? [esc(d.idCardNumber || ""), d.idCardDate ? day(d.idCardDate) : "", esc(d.idCardAuthority || "")].filter(Boolean).join(", ") : null, true],
      ["Address", o.address], ["Right", d.rightType]
    ].filter(function (r) { return r[1]; });
    if (!rows.length) { return ""; }
    return "<details class=\"cad-details\"><summary>details</summary><dl>" + rows.map(function (r) {
      return "<dt>" + r[0] + "</dt><dd>" + (r[2] ? r[1] : esc(r[1])) + "</dd>";
    }).join("") + "</dl></details>";
  }

  function ownerHtml(o, others) {
    var list = o.taxId ? (others[o.taxId] || []) : [];
    return "<li><div>" + esc(o.name) + (o.shareText ? " <span class=\"muted\">" + esc(o.shareText) + "</span>" : "") +
      (o.kind === "LegalEntity" ? " <span class=\"badge\">" + (o.entityTypeText ? esc(o.entityTypeText) : "company / public body") + "</span>" : "") +
      (o.isUnknown ? " <span class=\"muted\">(unknown owner)</span>" : "") + "</div>" +
      (o.address ? "<div class=\"muted\">" + esc(o.address) + "</div>" : "") +
      (o.taxId ? "<div class=\"muted\">ΑΦΜ " + esc(o.taxId) + " · " +
        (list.length
          ? "<a href=\"#\" data-cad-others=\"" + esc(o.taxId) + "\">Other parcels of this owner in GreekPlot (" + list.length + ")</a>" +
            "<ul class=\"cad-others\" data-cad-list=\"" + esc(o.taxId) + "\" hidden>" + list.map(function (p) {
              return "<li><a href=\"#\" class=\"kaek\" data-cad-parcel=\"" + p.parcelId + "\">" + esc(p.kaek) + "</a></li>";
            }).join("") + "</ul>"
          : "no other parcels of this owner in GreekPlot") + "</div>" : "") +
      detailsHtml(o) + "</li>";
  }

  function partyText(p) {
    return esc(p.name) + (p.commentText || p.roleText ? " <span class=\"muted\">(" + esc(p.commentText || p.roleText) + ")</span>" : "") +
      (p.shareText ? " <span class=\"muted\">" + esc(p.shareText) + "</span>" : "") +
      (p.taxId ? " <span class=\"muted\">ΑΦΜ " + esc(p.taxId) + "</span>" : "") +
      (p.address ? " <span class=\"muted\">· " + esc(p.address) + "</span>" : "");
  }

  var FROM = { OldTitle: "earlier title", RecordTitle: "title before the Cadastre", Extract: "descriptive extract" };

  function actHtml(a) {
    var reg = a.regNo ? a.regNo + "/" + a.regYear + (a.office ? " " + a.office : "") : (a.deedNo ? "no. " + a.deedNo : "");
    var values = a.transactionValue !== null || a.objectiveValue !== null
      ? (a.transactionValue !== null ? " · " + (a.actTypeCode === "Seizure" ? "claim " : "") + money(a.transactionValue) : "") +
        (a.objectiveValue !== null ? " <span class=\"muted\">(objective " + money(a.objectiveValue) + ")</span>" : "")
      : "";
    var from = a.source === "Summary" ? "" : " <span class=\"muted\">" + FROM[a.source] + "</span>";
    var parties = (a.parties || []);
    return "<li><div>" + day(a.actDate || a.regDate) + " · " + esc(typeName(a)) + (a.rightText ? " <span class=\"muted\">" + esc(a.rightText) + "</span>" : "") +
      values + from + "</div>" +
      (reg ? "<div class=\"muted\">" + esc(reg) + (a.documentText ? " · " + esc(a.documentText) : "") + "</div>" : "") +
      (parties.length ? "<div class=\"cad-parties\">" + parties.map(function (p) {
        return "<div>" + (p.role === "Acquirer" ? "to " : p.role === "Transferor" ? "from " : "") + partyText(p) + "</div>";
      }).join("") + "</div>" : "") + "</li>";
  }

  // A registration both the extract and a summary describe is shown once (the summary has the price).
  function historyOf(u) {
    var summarised = {};
    (u.acts || []).forEach(function (a) { if (a.source === "Summary" && a.regNo) { summarised[a.regNo + "/" + a.regYear] = true; } });
    return (u.acts || []).filter(function (a) { return !(a.source === "Extract" && a.regNo && summarised[a.regNo + "/" + a.regYear]); })
      .sort(function (x, y) { return String(y.actDate || y.regDate || "").localeCompare(String(x.actDate || x.regDate || "")); });
  }

  function unitHtml(u, others, several) {
    var area = u.titleAreaSqm !== null || u.measuredAreaSqm !== null
      ? [u.titleAreaSqm !== null ? Nadlan.format.sqm(u.titleAreaSqm) + " (title)" : "",
         u.measuredAreaSqm !== null && u.measuredAreaSqm !== undefined ? "measured " + Nadlan.format.sqm(u.measuredAreaSqm) : ""].filter(Boolean).join(", ")
      : "";
    var facts = [u.propertyKind ? esc(u.propertyKind) : "", u.floorText ? "floor " + esc(u.floorText) : "", area,
      u.plotShareText ? "share in the plot " + esc(u.plotShareText) : ""].filter(Boolean).join(" · ");
    var acts = historyOf(u);
    if (u.status === "NotRead") {
      return "<div class=\"cad-unit\"><div class=\"cad-unit-head\">Unit " + esc(unitName(u)) + (facts ? " · " + facts : "") + "</div>" +
        "<div class=\"muted\">Not read (over the run's unit cap).</div></div>";
    }
    return "<div class=\"cad-unit\">" +
      (several || facts ? "<div class=\"cad-unit-head\">" + (several ? "Unit " + esc(unitName(u)) + (facts ? " · " : "") : "") + facts + "</div>" : "") +
      (u.status === "NeedsOcr" ? "<div class=\"muted\">A summary of this unit is a scan without readable text: not read.</div>" : "") +
      (u.extractStatus === "NeedsOcr" ? "<div class=\"muted\">The descriptive extract has no readable text (stored, not read).</div>" : "") +
      (u.owners.length ? "<ul class=\"cad-owners\">" + u.owners.map(function (o) { return ownerHtml(o, others); }).join("") + "</ul>"
        : "<div class=\"muted\">No current owners listed.</div>") +
      (u.remarksText ? "<div class=\"muted\">Remarks: " + esc(u.remarksText) + "</div>" : "") +
      (acts.length ? "<details class=\"cad-history\"><summary>History (" + acts.length + ")</summary><ul>" + acts.map(actHtml).join("") + "</ul></details>" : "") +
      "</div>";
  }

  function html(r) {
    var footer = "<div class=\"cad-foot muted\">fetched " + esc(when(r.fetchedUtc)) + " · owners as listed by the Cadastre, updated weekly · " +
      "for research, not legal use</div>";
    var failed = r.failedAttempt
      ? "<div class=\"cad-warn\">The last attempt (" + esc(when(r.failedAttempt.fetchedUtc)) + ") failed: " + esc(r.failedAttempt.error || "error") + "</div>"
      : "";
    if (!r.fetchedUtc) { return failed; }
    if (r.status === "NotOperating") {
      return failed + "<div>The Cadastre isn't operating here yet: the lawyer service has no records for this KAEK.</div>" + footer;
    }
    var several = r.units.length > 1;
    var docs = [].concat.apply([], r.units.map(function (u) { return u.documents || []; }));
    // Suggestion only (owner, 2026-11-26): the parcel's OT is never changed from here.
    var ot = r.otSuggestion ? "<div class=\"cad-ot\">Cadastre says <b>OT " + esc(r.otSuggestion) + "</b> <span class=\"muted\">(a suggestion; not applied)</span></div>" : "";
    var neighbours = r.units.map(function (u) { return u.neighbourKaeks; }).filter(Boolean)[0];
    return failed + lastSaleHtml(r) + seizuresHtml(r) + documentsHtml(docs, several) + ot +
      r.units.map(function (u) { return unitHtml(u, r.otherParcels || {}, several); }).join("") +
      (neighbours ? "<div class=\"muted cad-neighbours\">Neighbours on the diagram: " + esc(neighbours.split(" ").join(", ")) + "</div>" : "") + footer;
  }

  /**
   * Fills $slot for the Parcel; resolves true when there is something to show (the section stays hidden otherwise).
   * onShowParcel(parcelId) opens another Parcel's card ("Other parcels of this owner").
   */
  function render($slot, parcelId, onShowParcel) {
    $slot.off("click.cad").on("click.cad", "[data-cad-others]", function (e) {
      e.preventDefault();
      var $list = $slot.find("[data-cad-list=\"" + $(this).attr("data-cad-others") + "\"]");
      $list.prop("hidden", !$list.prop("hidden"));
    }).on("click.cad", "[data-cad-parcel]", function (e) {
      e.preventDefault();
      if (onShowParcel) { onShowParcel(Number($(this).attr("data-cad-parcel"))); }
    });
    return Nadlan.api.get("/api/parcels/" + parcelId + "/cadastre").then(function (r) {
      if (!r.researched) { $slot.empty(); return false; }
      $slot.html(html(r));
      return true;
    }, function () { $slot.empty(); return false; });
  }

  Nadlan.cadastreRecords = { render: render, html: html };
})(window, jQuery);
