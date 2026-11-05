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
        "<button type=\"button\" class=\"btn btn-small\" data-role=\"present\" disabled title=\"Customer presentation of the selected Assets (3D), in a new tab\">Present</button>" +
      "</div>" +
      "<ul class=\"results-list\"></ul>");
    var $list = $root.find(".results-list");

    // OT / plot first when entered: that is how people know a plot; the KAEK comes after it (Alon, 2026-11-05).
    function otPlot(s) {
      return s.ot || s.plot ? "<span class=\"row-otplot\">OT " + f.escapeHtml(s.ot || "—") + " / Plot " + f.escapeHtml(s.plot || "—") + "</span>" : null;
    }

    function seaHtml(s) {
      return s.seaMetres !== null && s.seaMetres !== undefined ? "<span class=\"nowrap\">" + f.escapeHtml(f.seaDistance(s.seaMetres)) + "</span>" : null;
    }

    function rowHtml(it, index) {
      var s = it.summary;
      if (mode === "parcels") {
        var named = otPlot(s);
        // Same colour code as the map: fill = OT/plot entry (kind), purple ring = has an Asset.
        return "<li data-index=\"" + index + "\"><div class=\"row-main\"><span class=\"kind-dot kind-" + f.escapeHtml(s.kind || "todo") +
          (s.hasAssets ? " has-assets" : "") + "\" title=\"" + (s.hasAssets ? "Has Asset(s)" : "No Asset") + "\"></span>" +
          (named || f.registryId(s.registryId, s.registryIdIsProvisional)) + "</div>" +
          "<div class=\"row-sub muted\">" + [named ? f.registryId(s.registryId, s.registryIdIsProvisional) : null,
            s.geographicArea ? f.escapeHtml(s.geographicArea) : null, named ? null : "No OT / plot yet", seaHtml(s)].filter(Boolean).join(" · ") + "</div>" +
          // While the Work window is open, on hover: straight into the work (as a click on its polygon).
          "<button type=\"button\" class=\"btn btn-small row-work\" data-role=\"work\" title=\"Add it to the work (as a click on the parcel)\">Work</button></li>";
      }
      return "<li data-index=\"" + index + "\">" +
        // A beach can't go into a Portfolio or a presentation: nothing to select.
        (s.isBeach ? "<span class=\"row-no-select\"></span>" : "<input type=\"checkbox\" data-asset-id=\"" + s.assetId + "\"" + (options.selection.has(s.assetId) ? " checked" : "") + ">") +
        "<div class=\"row-body\"><div class=\"row-main\">" + f.assetPrice(s) + " " + f.statusBadge(s.statusName, s.statusColor) + "</div>" +
        "<div class=\"row-sub\">" + f.text(s.managingContactName) + "</div>" +
        "<div class=\"row-sub muted\">" + [otPlot(s), f.registryId(s.registryId, s.registryIdIsProvisional),
          s.geographicArea ? f.escapeHtml(s.geographicArea) : null, seaHtml(s)].filter(Boolean).join(" · ") + "</div></div></li>";
    }

    function renderSelection() {
      var n = options.selection.size();
      $root.find("[data-role=portfolio]").prop("disabled", n === 0).text(n ? "Add " + n + " to portfolio" : "Add to portfolio");
      $root.find("[data-role=present]").prop("disabled", n === 0 || n > 100);
      $list.find("[data-asset-id]").each(function () { this.checked = options.selection.has(Number($(this).data("assetId"))); });
    }

    $list.on("mouseenter", "li", function () { $(this).toggleClass("can-work", !!(options.canWork && options.canWork())); });
    $list.on("click", "[data-role=work]", function (e) {
      e.stopPropagation();
      if (options.onWork) { options.onWork(items[Number($(this).closest("li").data("index"))]); }
    });
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
    // The selected Assets as a customer presentation (present.html), in the order they were picked.
    $root.on("click", "[data-role=present]", function () {
      Nadlan.presentLink.open({ assetIds: options.selection.values() });
    });
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
      /** The same list with changed rows (e.g. after a quick save): redrawn where the user had scrolled to. */
      update: function (list, truncated) {
        var top = $list.scrollTop();
        this.setItems(list, truncated);
        $list.scrollTop(top);
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
