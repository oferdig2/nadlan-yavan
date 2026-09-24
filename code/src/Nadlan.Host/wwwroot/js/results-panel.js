// ResultsPanel: the list twin of the map. Shows what the current query returned, lets the user pick Assets
// ("select all" = everything this query returned, nothing more) and push them into a Portfolio.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ selection: object, onItemClick: function(object): void, onAddToPortfolio: function(): void,
   *           onZoomToResults: function(): void }} options
   */
  function initializeResultsPanel(container, options) {
    var f = Nadlan.format;
    var mode = "parcels";
    var items = [];
    var $root = $(container).addClass("results-panel").html(
      "<div class=\"results-head\">" +
        "<span class=\"results-count\"></span>" +
        "<button type=\"button\" class=\"btn btn-small\" data-role=\"zoom\">Zoom to results</button>" +
      "</div>" +
      "<div class=\"results-actions\" data-assets-only>" +
        "<button type=\"button\" class=\"btn btn-small\" data-role=\"select-all\">Select all</button>" +
        "<button type=\"button\" class=\"btn btn-small\" data-role=\"clear\">Clear</button>" +
        "<button type=\"button\" class=\"btn btn-small btn-primary\" data-role=\"portfolio\" disabled>Add to portfolio</button>" +
      "</div>" +
      "<ul class=\"results-list\"></ul>");
    var $list = $root.find(".results-list");

    function rowHtml(it, index) {
      var s = it.summary;
      if (mode === "parcels") {
        return "<li data-index=\"" + index + "\"><div class=\"row-main\">" + f.registryId(s.registryId, s.registryIdIsProvisional) + "</div>" +
          "<div class=\"row-sub muted\">" + f.text(s.geographicArea) + " · OT " + f.text(s.ot) + " / Plot " + f.text(s.plot) + "</div></li>";
      }
      return "<li data-index=\"" + index + "\">" +
        "<input type=\"checkbox\" data-asset-id=\"" + s.assetId + "\"" + (options.selection.has(s.assetId) ? " checked" : "") + ">" +
        "<div class=\"row-body\"><div class=\"row-main\">" + f.price(s.askPrice, s.currencyCode) + " " + f.statusBadge(s.statusName, s.statusColor) + "</div>" +
        "<div class=\"row-sub\">" + f.text(s.managingContactName) + "</div>" +
        "<div class=\"row-sub muted\">" + f.escapeHtml(s.registryId || "") + (s.geographicArea ? " · " + f.escapeHtml(s.geographicArea) : "") + "</div></div></li>";
    }

    function renderSelection() {
      var n = options.selection.size();
      $root.find("[data-role=portfolio]").prop("disabled", n === 0).text(n ? "Add " + n + " to portfolio" : "Add to portfolio");
      $list.find("[data-asset-id]").each(function () { this.checked = options.selection.has(Number($(this).data("assetId"))); });
    }

    $list.on("click", "li", function (e) {
      if ($(e.target).is(":checkbox")) { return; }
      options.onItemClick(items[Number($(this).data("index"))]);
    });
    $list.on("change", "[data-asset-id]", function () {
      options.selection.toggle(Number($(this).data("assetId")), this.checked);
    });
    $root.on("click", "[data-role=select-all]", function () {
      options.selection.addMany(items.map(function (it) { return it.summary.assetId; }));
    });
    $root.on("click", "[data-role=clear]", function () { options.selection.clear(); });
    $root.on("click", "[data-role=portfolio]", function () { options.onAddToPortfolio(); });
    $root.on("click", "[data-role=zoom]", function () { options.onZoomToResults(); });
    options.selection.onChange(renderSelection);

    return {
      setMode: function (m) { mode = m; $root.find("[data-assets-only]").prop("hidden", m !== "assets"); },
      setItems: function (list, truncated) {
        items = list;
        var noun = mode === "assets" ? "asset" : "parcel";
        $root.find(".results-count").text(list.length + " " + noun + (list.length === 1 ? "" : "s") + (truncated ? " (limit reached - narrow the search)" : ""));
        $list.html(list.map(rowHtml).join(""));
        renderSelection();
      },
      setMessage: function (text) {
        items = [];
        $root.find(".results-count").text(text);
        $list.empty();
      }
    };
  }

  Nadlan.initializeResultsPanel = initializeResultsPanel;
})(window, jQuery);
