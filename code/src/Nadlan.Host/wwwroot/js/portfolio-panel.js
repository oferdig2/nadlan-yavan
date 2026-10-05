// Portfolio management: find a Portfolio, then edit name/type/description, reorder (drag) or remove its Assets,
// open its files and history, or show it on the map. Portfolio price is out of scope for V1.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /** @param {{ onShowOnMap: function(object): void }} options  receives { portfolioId, name } */
  function openList(options) {
    var esc = Nadlan.format.escapeHtml;
    var $body = $(
      "<div class=\"portfolio-list\">" +
        "<input type=\"text\" class=\"input\" placeholder=\"Search portfolios…\">" +
        "<ul class=\"results-list\"></ul>" +
      "</div>");
    var d = Nadlan.dialog.open({
      title: "Portfolios",
      content: $body,
      width: 460,
      modal: false,
      buttons: [{ text: "Close", click: function () { d.close(); } }]
    });

    var seq = 0;
    function search() {
      var mine = ++seq;
      Nadlan.api.get("/api/portfolios", { q: $body.find("input").val(), limit: 100 }).then(function (list) {
        if (mine !== seq) { return; }
        $body.find("ul").html(list.length ? list.map(function (p) {
          return "<li data-id=\"" + p.portfolioId + "\"><div class=\"row-body\"><div class=\"row-main\">" + esc(p.name) + "</div>" +
            "<div class=\"row-sub muted\">" + esc(p.typeName) + " · " + p.assetCount + " asset(s)</div></div></li>";
        }).join("") : "<li class=\"muted\">No portfolios yet - create one from selected Assets on the map.</li>");
      }, function (err) {
        if (mine !== seq) { return; }
        $body.find("ul").html("<li class=\"error\">" + esc(err.message) + "</li>");
      });
    }

    var timer = null;
    $body.on("input", "input", function () { clearTimeout(timer); timer = setTimeout(search, 250); });
    $body.on("click", "li[data-id]", function () {
      openPortfolio(Number($(this).data("id")), { onShowOnMap: options.onShowOnMap, onChanged: search })
        .catch(function (err) { Nadlan.dialog.showError("Could not open the portfolio", err); });
    });
    search();
    return d;
  }

  /** @param {{ onShowOnMap: function(object): void, onChanged?: function(): void }} options */
  function openPortfolio(portfolioId, options) {
    return Promise.all([Nadlan.reference.load(), Nadlan.api.get("/api/portfolios/" + portfolioId)]).then(function (loaded) {
      var ref = loaded[0];
      var p = loaded[1];
      var canEdit = !!(p.rights && p.rights.canEdit); // viewers (e.g. a customer with a Portfolio grant) get it read-only
      var f = Nadlan.format;
      var esc = f.escapeHtml;
      var $form = $(
        "<form class=\"form\">" +
          "<label>Name<input class=\"input\" name=\"name\" value=\"" + esc(p.name) + "\"></label>" +
          "<label>Type<select class=\"input\" name=\"portfolioTypeId\" data-type=\"id\">" +
            Nadlan.reference.optionsHtml(Nadlan.reference.forPicker(ref.portfolioTypes, p.portfolioTypeId), p.portfolioTypeId) + "</select></label>" +
          "<label>Description<textarea class=\"input\" name=\"description\" rows=\"2\">" + esc(p.description || "") + "</textarea></label>" +
          "<div class=\"btn-row\">" +
            "<button type=\"button\" class=\"btn\" data-act=\"map\">Show on map</button>" +
            "<button type=\"button\" class=\"btn\" data-act=\"files\">Files</button>" +
            "<button type=\"button\" class=\"btn\" data-act=\"history\">History</button>" +
          "</div>" +
          "<div class=\"card-section-head\"><span>Assets (" + p.assets.length + ")</span><span class=\"muted\">drag to reorder</span></div>" +
          "<ul class=\"portfolio-assets\"></ul>" +
        "</form>");

      function renderAssets() {
        $form.find(".portfolio-assets").html(p.assets.length ? p.assets.map(function (it) {
          var s = it.summary;
          return "<li data-asset-id=\"" + s.assetId + "\"><span class=\"drag-handle\" title=\"Drag to reorder\">⋮⋮</span>" +
            "<div class=\"row-body\"><div>" + f.assetPrice(s) + " " + f.statusBadge(s.statusName, s.statusColor) + "</div>" +
            "<div class=\"row-sub muted\">" + esc(s.registryId || "") + " · " + f.text(s.managingContactName) + " · #" + s.assetId + "</div></div>" +
            (canEdit ? "<button type=\"button\" class=\"btn btn-small\" data-remove=\"" + s.assetId + "\">Remove</button>" : "") + "</li>";
        }).join("") : "<li class=\"muted\">No Assets. Select Assets on the map and use \"Add to portfolio\".</li>");
        $form.find(".card-section-head span:first").text("Assets (" + p.assets.length + ")");
      }

      renderAssets();
      if (!canEdit) {
        $form.find("input, select, textarea").prop("disabled", true);
        $form.find(".drag-handle").remove(); // sorting only works by the handle
      }
      $form.find(".portfolio-assets").sortable({
        handle: ".drag-handle",
        items: "li[data-asset-id]",
        update: function () {
          var ids = $form.find(".portfolio-assets li[data-asset-id]").map(function () { return Number($(this).data("assetId")); }).get();
          Nadlan.api.put("/api/portfolios/" + portfolioId + "/order", { assetIds: ids }).then(function () {
            var byId = {};
            p.assets.forEach(function (a) { byId[a.summary.assetId] = a; });
            p.assets = ids.map(function (id) { return byId[id]; });
          }, function (err) {
            d.showError(err.message);
            renderAssets(); // undo the drag at once...
            // ...then reload: a conflict means someone else added/removed Assets, so the local list is stale.
            Nadlan.api.get("/api/portfolios/" + portfolioId).then(function (fresh) { p.assets = fresh.assets; renderAssets(); }, function () { /* keep the error shown */ });
          });
        }
      });

      $form.on("click", "[data-remove]", function () {
        var assetId = Number($(this).data("remove"));
        Nadlan.dialog.confirm("Remove from portfolio", "Remove Asset #" + assetId + " from <strong>" + esc(p.name) +
          "</strong>? The Asset itself is not changed.", "Remove").then(function (ok) {
          if (!ok) { return; }
          Nadlan.api.del("/api/portfolios/" + portfolioId + "/assets/" + assetId).then(function () {
            p.assets = p.assets.filter(function (a) { return a.summary.assetId !== assetId; });
            renderAssets();
            if (options.onChanged) { options.onChanged(); }
          }, function (err) { d.showError(err.message); });
        });
      });

      $form.on("click", "[data-act=map]", function () { options.onShowOnMap({ portfolioId: portfolioId, name: p.name }); });
      $form.on("click", "[data-act=files]", function () {
        Nadlan.filesDialog.open({ attachedToType: "Portfolio", attachedToId: portfolioId, title: "Portfolio \"" + p.name + "\" - files" });
      });
      $form.on("click", "[data-act=history]", function () {
        Nadlan.historyDialog.open({ entityType: "Portfolio", entityId: portfolioId, title: "Portfolio \"" + p.name + "\" - history" });
      });

      var d = Nadlan.dialog.open({
        title: "Portfolio",
        content: $form,
        width: 560,
        modal: false,
        buttons: (canEdit ? [{ text: "Save", primary: true, click: save }] : []).concat([{ text: "Close", click: function () { d.close(); } }])
      });

      function save() {
        var data = Nadlan.dialog.readForm($form);
        d.busy(true);
        Nadlan.api.put("/api/portfolios/" + portfolioId, {
          name: data.name, portfolioTypeId: data.portfolioTypeId || 0, description: data.description, version: p.version
        }).then(function (saved) {
          d.busy(false);
          p.name = saved.name;
          p.version = saved.version; // the panel stays open: the next save checks against this one
          d.showError("");
          if (options.onChanged) { options.onChanged(); }
        }, function (err) { d.busy(false); d.showError(err.message); });
      }

      return d;
    });
  }

  Nadlan.portfolioPanel = { openList: openList, openPortfolio: openPortfolio };
})(window, jQuery);
