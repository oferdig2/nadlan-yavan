// MapFilterPanel: collects the query filters. Submits nothing itself - the page asks for getFilter() on Apply.
// Asset-only filters are hidden in Parcels view.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function checks(name, list) {
    var esc = Nadlan.format.escapeHtml;
    return list.map(function (x) {
      return "<label class=\"check\"><input type=\"checkbox\" data-filter=\"" + name + "\" value=\"" + x.id + "\"> " + esc(x.name) + "</label>";
    }).join("");
  }

  // Geographic areas as one dropdown (remark 4, 2026-10-24): a checkbox per area, alphabetical, all ticked = no area
  // filter; each row keeps its 📍 "show this area on the map" (shown on hover).
  function areaSelect(list) {
    var esc = Nadlan.format.escapeHtml;
    var sorted = list.slice().sort(function (a, b) { return String(a.name).localeCompare(String(b.name)); });
    return "<div class=\"area-select\" data-role=\"areas\">" +
      "<button type=\"button\" class=\"input area-select-toggle\" aria-haspopup=\"true\" aria-expanded=\"false\">" +
        "<span class=\"area-select-summary\">All areas</span><span aria-hidden=\"true\">&#9662;</span></button>" +
      "<div class=\"area-select-menu\" hidden>" +
        "<div class=\"area-select-head\"><button type=\"button\" class=\"link\" data-area-all>Select all</button></div>" +
        sorted.map(function (x) {
          return "<div class=\"check-go area-row\"><label class=\"check\"><input type=\"checkbox\" data-filter=\"areaIds\" value=\"" + x.id + "\" checked> " +
            esc(x.name) + "</label><button type=\"button\" class=\"area-go\" data-area-go=\"" + x.id + "\" title=\"Show " + esc(x.name) +
            " on the map\" aria-label=\"Show " + esc(x.name) + " on the map\">&#x1F4CD;</button></div>";
        }).join("") +
      "</div></div>";
  }

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ reference: object, onApply: function(): void, onScopeChange: function(string): void,
   *           onDrawRectangle: function(): void, onClearRectangle: function(): void, onShowArea?: function(number): void }} options
   *        onShowArea(geographicAreaId): the 📍 beside an area name - the map shows that whole area
   */
  function initializeMapFilterPanel(container, options) {
    var ref = options.reference;
    var $root = $(container).addClass("filter-panel").html(
      "<form class=\"form compact\">" +
        "<label>Search in<select class=\"input\" data-role=\"scope\">" +
          "<option value=\"view\">Visible map</option><option value=\"rectangle\">Drawn rectangle</option>" +
          "<option value=\"everywhere\">Everywhere</option></select></label>" +
        "<div class=\"btn-row rect-row\">" +
          "<button type=\"button\" class=\"btn\" data-role=\"draw-rect\">▭ Draw rectangle</button>" +
          "<button type=\"button\" class=\"btn\" data-role=\"clear-rect\" hidden>Clear rectangle</button>" +
        "</div>" +
        "<label>KAEK<input class=\"input\" data-filter=\"registryId\" placeholder=\"contains…\"></label>" +
        "<div class=\"form-row\">" +
          "<label>OT<input class=\"input\" data-filter=\"ot\" placeholder=\"e.g. 47\" title=\"Exact OT (block) number, with or without its letter\"></label>" +
          "<label>Plot<input class=\"input\" data-filter=\"plot\" placeholder=\"e.g. 22\" title=\"Exact plot number, with or without its letter\"></label>" +
        "</div>" +
        "<div class=\"field\"><span class=\"field-label\">Geographic area</span>" + areaSelect(Nadlan.reference.active(ref.geographicAreas)) + "</div>" +
        "<div data-assets-only>" +
          "<div class=\"form-row\">" +
            "<label>Price from<input class=\"input\" data-filter=\"priceMin\" inputmode=\"decimal\"></label>" +
            "<label>to<input class=\"input\" data-filter=\"priceMax\" inputmode=\"decimal\"></label>" +
          "</div>" +
          "<label>Managing contacts<div data-role=\"contacts\"></div></label>" +
          "<label>Portfolios<div data-role=\"portfolios\"></div></label>" +
          "<fieldset class=\"checks\"><legend>Status</legend>" + checks("statusIds", Nadlan.reference.active(ref.assetStatuses)) + "</fieldset>" +
          "<fieldset class=\"checks\"><legend>Property type</legend>" + checks("typeIds", Nadlan.reference.active(ref.propertyTypes)) + "</fieldset>" +
        "</div>" +
        "<div class=\"btn-row\">" +
          "<button type=\"submit\" class=\"btn btn-primary\">Apply</button>" +
          "<button type=\"button\" class=\"btn\" data-role=\"reset\">Reset</button>" +
        "</div>" +
        "<div class=\"form-error\" hidden></div>" +
      "</form>");

    // Picking or removing a contact / portfolio searches at once (remark 15); the panel's own clear/set stays quiet.
    var quiet = false;
    function pickedChanged() { if (!quiet) { options.onApply(); } }
    var contacts = Nadlan.initializeMultiEntitySelector($root.find("[data-role=contacts]"), {
      source: Nadlan.entitySources.contact, placeholder: "Add contact…", onChange: pickedChanged
    });
    var portfolios = Nadlan.initializeMultiEntitySelector($root.find("[data-role=portfolios]"), {
      source: Nadlan.entitySources.portfolio, placeholder: "Add portfolio…", onChange: pickedChanged
    });
    function quietly(fn) { quiet = true; try { fn(); } finally { quiet = false; } }

    // ---- the area dropdown ----
    var $areas = $root.find("[data-role=areas]");
    var $areaMenu = $areas.find(".area-select-menu");
    var $areaBoxes = $areas.find(":checkbox[data-filter=areaIds]");
    function areaSummary() {
      var on = $areaBoxes.filter(":checked");
      var text = on.length === 0 || on.length === $areaBoxes.length ? "All areas"
        : on.length <= 2 ? on.map(function () { return $.trim($(this).parent().text()); }).get().join(", ")
        : on.length + " areas";
      $areas.find(".area-select-summary").text(text);
    }
    function setAreaMenu(open) {
      $areaMenu.prop("hidden", !open);
      $areas.find(".area-select-toggle").attr("aria-expanded", String(open));
    }
    function allAreas() { $areaBoxes.prop("checked", true); areaSummary(); }
    $areas.on("click", ".area-select-toggle", function () { setAreaMenu($areaMenu.prop("hidden")); });
    $areas.on("change", ":checkbox", areaSummary);
    $areas.on("click", "[data-area-all]", allAreas);
    $areas.on("keydown", function (e) { if (e.key === "Escape") { setAreaMenu(false); $areas.find(".area-select-toggle").trigger("focus"); } });
    $(document).on("click", function (e) { if (!$(e.target).closest($areas).length) { setAreaMenu(false); } });
    var $scope = $root.find("[data-role=scope]");
    var $error = $root.find(".form-error");

    $root.find("form").on("submit", function (e) { e.preventDefault(); options.onApply(); });
    $scope.on("change", function () { options.onScopeChange($scope.val()); });
    $root.on("click", "[data-role=draw-rect]", function () { options.onDrawRectangle(); });
    $root.on("click", "[data-area-go]", function (e) { e.preventDefault(); options.onShowArea(Number($(this).data("areaGo"))); });
    $root.on("click", "[data-role=clear-rect]", function () { options.onClearRectangle(); });
    $root.on("click", "[data-role=reset]", function () {
      $root.find("input[data-filter]:not(:checkbox)").val(""); // checkbox values are the ids - never blank them
      $root.find(":checkbox[data-filter]").not($areaBoxes).prop("checked", false);
      allAreas();
      quietly(function () { contacts.clear(); portfolios.clear(); });
      options.onApply();
    });

    function checkedIds(name) {
      return $root.find(":checkbox[data-filter=" + name + "]:checked").map(function () { return Number(this.value); }).get();
    }

    function number(name) {
      return Nadlan.format.parseNumber($root.find("[data-filter=" + name + "]").val());
    }

    return {
      /** Returns the filter in API query-string shape, or null (with a message shown) when it is invalid. */
      getFilter: function (mode) {
        $error.prop("hidden", true);
        var f = {
          registryId: $.trim($root.find("[data-filter=registryId]").val()) || null,
          ot: $.trim($root.find("[data-filter=ot]").val()) || null,
          plot: $.trim($root.find("[data-filter=plot]").val()) || null,
          // All areas ticked (or none) = no area filter. Real and provisional KAEKs are always both searched (remark 1).
          areaIds: checkedIds("areaIds").length === $areaBoxes.length ? [] : checkedIds("areaIds")
        };
        if (mode === "assets") {
          f.priceMin = number("priceMin");
          f.priceMax = number("priceMax");
          if ((f.priceMin !== null && isNaN(f.priceMin)) || (f.priceMax !== null && isNaN(f.priceMax))) {
            $error.text("Price must be a number.").prop("hidden", false);
            return null;
          }
          f.contactIds = contacts.getValue();
          f.portfolioIds = portfolios.getValue();
          f.statusIds = checkedIds("statusIds");
          f.typeIds = checkedIds("typeIds");
        }
        return f;
      },
      setMode: function (mode) {
        $root.find("[data-assets-only]").prop("hidden", mode !== "assets");
        $root.find("[data-parcels-only]").prop("hidden", mode !== "parcels");
      },
      /** "Show Portfolio on map": clear every other filter and keep only this Portfolio. */
      showOnlyPortfolio: function (portfolio) {
        $root.find("input[data-filter]:not(:checkbox)").val(""); // checkbox values are the ids - never blank them
        $root.find(":checkbox[data-filter]").not($areaBoxes).prop("checked", false);
        allAreas();
        quietly(function () { contacts.clear(); portfolios.setValue([{ id: portfolio.portfolioId, label: portfolio.name }]); });
      },
      setScope: function (scope) { $scope.val(scope); },
      setRectangleActive: function (active) { $root.find("[data-role=clear-rect]").prop("hidden", !active); }
    };
  }

  Nadlan.initializeMapFilterPanel = initializeMapFilterPanel;
})(window, jQuery);
