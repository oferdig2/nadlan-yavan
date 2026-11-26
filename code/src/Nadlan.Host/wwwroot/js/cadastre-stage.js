// A Parcel's Cadastre stage (migration 034): where it stands in the Cadastre, seen on its map by the KAEK importer (the click
// request that answered) or by the lawyer service. Staff only - the map's card and filter, the Asset table's column; never on
// presentations, public links or the deals portal.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var LABELS = {
    InOperation: "In operation",
    SurveyPost: "Survey post",
    PrePosting: "Pre-posting",
    NotOperating: "Not operating",
    Unknown: "Unknown"
  };

  var SOURCES = { Polygon: "map", Lawyer: "lawyer" };

  function esc(s) { return Nadlan.format.escapeHtml(s); }

  function day(utc) {
    return utc ? new Date(utc).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" }) : "";
  }

  Nadlan.cadastreStage = {
    /** The map filter's values, in the order shown (Unknown = none known). */
    FILTER_VALUES: ["InOperation", "SurveyPost", "PrePosting", "NotOperating", "Unknown"],

    label: function (stage) { return LABELS[stage] || LABELS.Unknown; },

    /** "In operation · seen 26 Nov 2026 (map)", or "" when unknown. @param {{ stage, seenUtc, source }|null} cs */
    cardHtml: function (cs) {
      if (!cs || !cs.stage) { return ""; }
      return esc(LABELS[cs.stage] || cs.stage) + " <span class=\"muted\">· seen " + esc(day(cs.seenUtc)) +
        (SOURCES[cs.source] ? " (" + SOURCES[cs.source] + ")" : "") + "</span>";
    },

    /** The Asset table's cell: "In operation" with the date as a tooltip; "—" when unknown. */
    cellHtml: function (stage, seenUtc) {
      if (!stage) { return "<span class=\"muted\">—</span>"; }
      return "<span title=\"seen " + esc(day(seenUtc)) + "\">" + esc(LABELS[stage] || stage) + "</span>";
    },

    day: day
  };
})(window);
