// The parcel card's "Cadastre (lawyer research)" block (Admin only, owner 2026-11-24): the latest snapshot the KAEK importer
// read from the Cadastre's lawyer service - last sale, open seizures, each unit's owners and the act history. The server
// answers 404 to anyone but an Admin; the card only asks for Admins. Never used on presentations or public links.
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

  var TYPE = { Sale: "sale", Auction: "auction", Seizure: "seizure", ParentalGift: "parental gift", Inheritance: "inheritance" };
  function typeName(a) { return TYPE[a.actTypeCode] || (a.actTypeText || "other"); }
  function unitName(u) { return u.unitV === null ? "" : u.unitV + "/" + u.unitH; }

  function lastSaleHtml(r) {
    var s = r.lastSale;
    if (!s) { return "<div class=\"muted\">No sale or auction among the summaries read.</div>"; }
    var several = (r.units || []).length > 1;
    var text = s.actTypeCode === "Auction"
      ? "auction " + money(s.price) + " on " + day(s.date)
      : money(s.price) + " on " + day(s.date) + " (sale)";
    return "<div class=\"cad-sale\"><b>Last sale:</b> " + text +
      (s.pricePerSqm !== null ? ", €" + Number(s.pricePerSqm).toLocaleString("en-US") + "/m²" : "") +
      (several ? " <span class=\"muted\">· unit " + esc(s.unitV + "/" + s.unitH) + "</span>" : "") + "</div>";
  }

  function seizuresHtml(r) {
    return (r.seizures || []).map(function (s) {
      return "<div class=\"cad-warn\">⚠ Seized " + day(s.date) + (s.claim !== null ? ", claim " + money(s.claim) : "") +
        " <span class=\"muted\">(a possible auction)</span></div>";
    }).join("");
  }

  function ownerHtml(o, others) {
    var list = o.entityTaxId ? (others[o.entityTaxId] || []) : [];
    return "<li><div>" + esc(o.name) + (o.shareText ? " <span class=\"muted\">" + esc(o.shareText) + "</span>" : "") +
      (o.kind === "LegalEntity" ? " <span class=\"badge\">" + (o.entityTypeText ? esc(o.entityTypeText) : "company / public body") + "</span>" : "") +
      (o.isUnknown ? " <span class=\"muted\">(unknown owner)</span>" : "") + "</div>" +
      (o.address ? "<div class=\"muted\">" + esc(o.address) + "</div>" : "") +
      (o.entityTaxId ? "<div class=\"muted\">ΑΦΜ " + esc(o.entityTaxId) + " · " +
        (list.length
          ? "<a href=\"#\" data-cad-others=\"" + esc(o.entityTaxId) + "\">Other parcels of this owner in GreekPlot (" + list.length + ")</a>" +
            "<ul class=\"cad-others\" data-cad-list=\"" + esc(o.entityTaxId) + "\" hidden>" + list.map(function (p) {
              return "<li><a href=\"#\" class=\"kaek\" data-cad-parcel=\"" + p.parcelId + "\">" + esc(p.kaek) + "</a></li>";
            }).join("") + "</ul>"
          : "no other parcels of this owner in GreekPlot") + "</div>" : "") +
      "</li>";
  }

  function partyText(p) {
    return esc(p.name) + (p.commentText || p.roleText ? " <span class=\"muted\">(" + esc(p.commentText || p.roleText) + ")</span>" : "") +
      (p.entityTaxId ? " <span class=\"muted\">ΑΦΜ " + esc(p.entityTaxId) + "</span>" : "") +
      (p.address ? " <span class=\"muted\">· " + esc(p.address) + "</span>" : "");
  }

  function actHtml(a) {
    var reg = a.regNo ? a.regNo + "/" + a.regYear + (a.office ? " " + a.office : "") : (a.deedNo ? "no. " + a.deedNo : "");
    var values = a.source === "Summary"
      ? (a.transactionValue !== null ? " · " + (a.actTypeCode === "Seizure" ? "claim " : "") + money(a.transactionValue) : "") +
        (a.objectiveValue !== null ? " <span class=\"muted\">(objective " + money(a.objectiveValue) + ")</span>" : "")
      : "";
    var from = a.source === "Summary" ? "" : " <span class=\"muted\">" + (a.source === "OldTitle" ? "earlier title" : "title before the Cadastre") + "</span>";
    var parties = (a.parties || []);
    return "<li><div>" + day(a.actDate) + " · " + esc(typeName(a)) + values + from + "</div>" +
      (reg ? "<div class=\"muted\">" + esc(reg) + "</div>" : "") +
      (parties.length ? "<div class=\"cad-parties\">" + parties.map(function (p) {
        return "<div>" + (p.role === "Acquirer" ? "to " : p.role === "Transferor" ? "from " : "") + partyText(p) + "</div>";
      }).join("") + "</div>" : "") + "</li>";
  }

  function unitHtml(u, others, several) {
    var facts = [u.floorText ? "floor " + esc(u.floorText) : "", u.titleAreaSqm !== null ? Nadlan.format.sqm(u.titleAreaSqm) : "",
      u.plotShareText ? "share in the plot " + esc(u.plotShareText) : ""].filter(Boolean).join(" · ");
    var acts = (u.acts || []).slice().sort(function (x, y) { return String(y.actDate || "").localeCompare(String(x.actDate || "")); });
    return "<div class=\"cad-unit\">" +
      (several || facts ? "<div class=\"cad-unit-head\">" + (several ? "Unit " + esc(unitName(u)) + (facts ? " · " : "") : "") + facts + "</div>" : "") +
      (u.status === "NeedsOcr" ? "<div class=\"muted\">A summary of this unit is a scan without readable text: not read.</div>" : "") +
      (u.owners.length ? "<ul class=\"cad-owners\">" + u.owners.map(function (o) { return ownerHtml(o, others); }).join("") + "</ul>"
        : "<div class=\"muted\">No current owners listed.</div>") +
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
    return failed + lastSaleHtml(r) + seizuresHtml(r) +
      r.units.map(function (u) { return unitHtml(u, r.otherParcels || {}, several); }).join("") + footer;
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

  Nadlan.cadastreRecords = { render: render };
})(window, jQuery);
