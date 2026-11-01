// The contractor's page in Greek and English (owner, 2026-11-01): opens in Greek, ΕΛ / EN switch at the top.
// Every text of work-link.html is here. The Greek needs the owner's check. {name} = a value filled in.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var TEXTS = {
    el: {
      pageTitle: "Εργασίες",
      loading: "Φόρτωση…",
      inactive: "Αυτός ο σύνδεσμος δεν είναι ενεργός. Ρωτήστε το άτομο που σας τον έστειλε.",
      passwordTitle: "Κωδικός",
      passwordHelp: "Γράψτε τον κωδικό που σας δόθηκε για αυτόν τον σύνδεσμο.",
      passwordOpen: "Άνοιγμα",
      passwordWrong: "Λάθος κωδικός.",
      passwordLocked: "Πολλές λάθος προσπάθειες. Δοκιμάστε ξανά μετά τις {time}.",
      parcels: "Οικόπεδα",
      back: "← Οικόπεδα",
      noParcels: "Κανένα οικόπεδο σε αυτή την κατηγορία.",
      mapUnavailable: "Ο χάρτης δεν είναι διαθέσιμος αυτή τη στιγμή.",
      biggerMap: "Μεγαλύτερος χάρτης",
      smallerMap: "Μικρότερος χάρτης",
      kaek: "ΚΑΕΚ",
      otPlot: "ΟΤ / Οικόπεδο",
      area: "Περιοχή",
      size: "Εμβαδόν",
      works: "Εργασίες",
      price: "Τιμή",
      estStart: "Εκτιμώμενη έναρξη (προαιρετικά)",
      sendOffer: "Αποστολή προσφοράς",
      changeOffer: "Αλλαγή προσφοράς",
      offerSentAt: "Η προσφορά στάλθηκε {date}. Μπορείτε να την αλλάξετε μέχρι να εγκριθεί.",
      offerSaved: "Η προσφορά στάλθηκε.",
      approvedPrice: "Εγκρίθηκε: {price}",
      startHint: "Πατήστε «Έναρξη» όταν ξεκινήσετε την εργασία.",
      start: "Έναρξη",
      inWorkSince: "Σε εξέλιξη από {date}.",
      done: "Ολοκληρώθηκε",
      doneWaiting: "Ολοκληρώθηκε {date} – αναμένεται πληρωμή.",
      paidOn: "Πληρώθηκε {date}.",
      confirmStart: "Ξεκινάτε την εργασία «{work}» στο οικόπεδο {kaek};",
      confirmDone: "Ολοκληρώθηκε η εργασία «{work}» στο οικόπεδο {kaek};",
      started: "Η εργασία ξεκίνησε.",
      finished: "Η εργασία σημειώθηκε ως ολοκληρωμένη.",
      priceRequired: "Γράψτε μια τιμή μεγαλύτερη από 0.",
      priceInvalid: "Η τιμή δεν είναι σωστή (π.χ. 350 ή 1.250,50).",
      startInvalid: "Η ημερομηνία έναρξης δεν είναι σωστή.",
      changed: "Η εργασία άλλαξε στο μεταξύ. Η λίστα ανανεώθηκε.",
      failed: "Κάτι πήγε στραβά. Δοκιμάστε ξανά.",
      filters: { all: "Όλα", open: "Ανοιχτές προσφορές", approved: "Εγκεκριμένα", inwork: "Σε εξέλιξη", done: "Ολοκληρωμένα", paid: "Πληρωμένα" },
      work: { REQUESTED: "Χωρίς προσφορά", OFFERED: "Προσφορά στάλθηκε", APPROVED: "Εγκρίθηκε", IN_WORK: "Σε εξέλιξη", DONE: "Ολοκληρώθηκε", PAID: "Πληρώθηκε" },
      parcel: { OPEN_OFFERS: "Ανοιχτές προσφορές", APPROVED: "Εγκεκριμένο", IN_WORK: "Σε εξέλιξη", DONE: "Ολοκληρωμένο", PAID: "Πληρωμένο" }
    },
    en: {
      pageTitle: "Works",
      loading: "Loading…",
      inactive: "This link isn't active. Ask the person who sent it.",
      passwordTitle: "Password",
      passwordHelp: "Type the password you were given for this link.",
      passwordOpen: "Open",
      passwordWrong: "Wrong password.",
      passwordLocked: "Too many wrong tries. Try again after {time}.",
      parcels: "Parcels",
      back: "← Parcels",
      noParcels: "No parcel in this group.",
      mapUnavailable: "The map isn't available right now.",
      biggerMap: "Bigger map",
      smallerMap: "Smaller map",
      kaek: "KAEK",
      otPlot: "OT / plot",
      area: "Area",
      size: "Size",
      works: "Works",
      price: "Price",
      estStart: "Estimated start (optional)",
      sendOffer: "Send offer",
      changeOffer: "Change offer",
      offerSentAt: "Offer sent {date}. You can change it until it is approved.",
      offerSaved: "Offer sent.",
      approvedPrice: "Approved: {price}",
      startHint: "Press \"Start\" when you begin the work.",
      start: "Start",
      inWorkSince: "In work since {date}.",
      done: "Done",
      doneWaiting: "Done {date} - waiting for payment.",
      paidOn: "Paid {date}.",
      confirmStart: "Start \"{work}\" on parcel {kaek}?",
      confirmDone: "Is \"{work}\" on parcel {kaek} done?",
      started: "The work has started.",
      finished: "The work is marked done.",
      priceRequired: "Type a price above 0.",
      priceInvalid: "The price isn't right (e.g. 350 or 1,250.50).",
      startInvalid: "The start date isn't right.",
      changed: "This work was changed meanwhile. The list was refreshed.",
      failed: "Something went wrong. Try again.",
      filters: { all: "All", open: "Open offers", approved: "Approved", inwork: "In work", done: "Done", paid: "Paid" },
      work: { REQUESTED: "No offer yet", OFFERED: "Offer sent", APPROVED: "Approved", IN_WORK: "In work", DONE: "Done", PAID: "Paid" },
      parcel: { OPEN_OFFERS: "Open offers", APPROVED: "Approved", IN_WORK: "In work", DONE: "Done", PAID: "Paid" }
    }
  };

  var KEY = "nadlan.workLink.lang";
  var lang = "el";
  try { if (window.localStorage.getItem(KEY) === "en") { lang = "en"; } } catch (e) { /* private mode: Greek */ }

  function t(key, values) {
    var text = key.split(".").reduce(function (o, k) { return o && o[k]; }, TEXTS[lang]);
    if (typeof text !== "string") { text = key; }
    return text.replace(/\{(\w+)\}/g, function (m, name) { return values && values[name] !== undefined ? values[name] : m; });
  }

  Nadlan.workI18n = {
    t: t,
    lang: function () { return lang; },
    locale: function () { return lang === "el" ? "el-GR" : "en-GB"; },
    setLang: function (value) {
      lang = value === "en" ? "en" : "el";
      try { window.localStorage.setItem(KEY, lang); } catch (e) { /* fine */ }
    }
  };
})(window);
