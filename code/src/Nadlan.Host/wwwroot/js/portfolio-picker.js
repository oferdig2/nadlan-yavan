// Adds a set of Assets to an existing Portfolio or to a new one. Membership is an explicit snapshot:
// Assets that match the same filter later are NOT added automatically.
// Resolves with { portfolioId, name, added } or null when cancelled.
// The portfolio last added to is chosen at the start (owner, 2026-11-17: "remembers the selection for next add to portfolio"):
// kept in this browser only; forgotten when adding to it fails (e.g. it was deleted).
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var LastKey = "nadlan.portfolioPicker.last";

  function readLast() {
    try { var v = JSON.parse(window.localStorage.getItem(LastKey) || "null"); return v && v.portfolioId ? v : null; } catch (e) { return null; }
  }

  function writeLast(v) {
    try { if (v) { window.localStorage.setItem(LastKey, JSON.stringify(v)); } else { window.localStorage.removeItem(LastKey); } } catch (e) { /* private window */ }
  }

  function open(assetIds) {
    return Nadlan.reference.load().then(function (ref) {
      return new Promise(function (resolve) {
        var esc = Nadlan.format.escapeHtml;
        var chosen = readLast();
        var done = false;
        var $form = $(
          "<form class=\"form\">" +
            "<p class=\"muted\">" + assetIds.length + " asset(s) selected.</p>" +
            "<label class=\"check\"><input type=\"radio\" name=\"mode\" value=\"existing\" checked> Add to existing portfolio</label>" +
            "<div class=\"mode-existing\"><input type=\"text\" class=\"input picker-search\"><div class=\"picker-chosen muted\">Nothing selected</div></div>" +
            "<label class=\"check\"><input type=\"radio\" name=\"mode\" value=\"new\"> Create new portfolio</label>" +
            "<div class=\"mode-new\" hidden>" +
              "<label>Name<input class=\"input\" name=\"name\"></label>" +
              "<label>Type<select class=\"input\" name=\"portfolioTypeId\" data-type=\"id\">" +
                Nadlan.reference.optionsHtml(Nadlan.reference.active(ref.portfolioTypes), null, "Select…") + "</select></label>" +
              "<label>Description<textarea class=\"input\" name=\"description\" rows=\"2\"></textarea></label>" +
            "</div>" +
          "</form>");

        if (chosen) {
          $form.find(".picker-chosen").removeClass("muted").html(esc(chosen.name) + (chosen.typeName ? " <span class=\"muted\">(" + esc(chosen.typeName) + ")</span>" : "") +
            " <span class=\"muted\">- last used</span>");
        }

        Nadlan.initializeEntitySelector($form.find(".picker-search"), {
          source: Nadlan.entitySources.portfolio,
          placeholder: "Search portfolios…",
          onSelect: function (p) {
            chosen = p;
            $form.find(".picker-chosen").removeClass("muted").html(esc(p.name) + " <span class=\"muted\">(" + esc(p.typeName) + ")</span>");
          }
        });

        $form.on("change", "[name=mode]", function () {
          var isNew = $form.find("[name=mode]:checked").val() === "new";
          $form.find(".mode-new").prop("hidden", !isNew);
          $form.find(".mode-existing").prop("hidden", isNew);
        });

        var d = Nadlan.dialog.open({
          title: "Add to portfolio",
          content: $form,
          buttons: [
            { text: "Add", primary: true, click: save },
            { text: "Cancel", click: function () { d.close(); } }
          ],
          onClose: function () { if (!done) { resolve(null); } }
        });
        $form.on("submit", function (e) { e.preventDefault(); save(); });

        function save() {
          var isNew = $form.find("[name=mode]:checked").val() === "new";
          var call;
          if (isNew) {
            var data = Nadlan.dialog.readForm($form.find(".mode-new"));
            var typeName = $form.find("[name=portfolioTypeId] option:selected").text();
            call = Nadlan.api.post("/api/portfolios", {
              name: data.name, portfolioTypeId: data.portfolioTypeId || 0, description: data.description, assetIds: assetIds
            }).then(function (r) { writeLast({ portfolioId: r.portfolioId, name: r.name, typeName: typeName }); return r; });
          } else {
            if (!chosen) { d.showError("Choose a portfolio, or create a new one."); return; }
            var picked = chosen;
            call = Nadlan.api.post("/api/portfolios/" + picked.portfolioId + "/assets", { assetIds: assetIds })
              .then(function (r) {
                writeLast({ portfolioId: picked.portfolioId, name: picked.name, typeName: picked.typeName });
                return { portfolioId: picked.portfolioId, name: picked.name, added: r.added };
              }, function (err) {
                if (readLast() && readLast().portfolioId === picked.portfolioId) { writeLast(null); }
                throw err;
              });
          }
          d.busy(true);
          call.then(function (result) {
            done = true;
            d.close();
            resolve(result);
          }, function (err) {
            d.busy(false);
            d.showError(err.message);
          });
        }
      });
    });
  }

  Nadlan.portfolioPicker = { open: open };
})(window, jQuery);
