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

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ reference: object, onApply: function(): void, onScopeChange: function(string): void,
   *           onDrawRectangle: function(): void, onClearRectangle: function(): void }} options
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
        "<fieldset class=\"checks\"><legend>Geographic area</legend>" + checks("areaIds", Nadlan.reference.active(ref.geographicAreas)) + "</fieldset>" +
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

    var contacts = Nadlan.initializeMultiEntitySelector($root.find("[data-role=contacts]"), {
      source: Nadlan.entitySources.contact, placeholder: "Add contact…"
    });
    var portfolios = Nadlan.initializeMultiEntitySelector($root.find("[data-role=portfolios]"), {
      source: Nadlan.entitySources.portfolio, placeholder: "Add portfolio…"
    });
    var $scope = $root.find("[data-role=scope]");
    var $error = $root.find(".form-error");

    $root.find("form").on("submit", function (e) { e.preventDefault(); options.onApply(); });
    $scope.on("change", function () { options.onScopeChange($scope.val()); });
    $root.on("click", "[data-role=draw-rect]", function () { options.onDrawRectangle(); });
    $root.on("click", "[data-role=clear-rect]", function () { options.onClearRectangle(); });
    $root.on("click", "[data-role=reset]", function () {
      $root.find("input[data-filter]").val("");
      $root.find(":checkbox[data-filter]").prop("checked", false);
      contacts.clear();
      portfolios.clear();
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
          areaIds: checkedIds("areaIds")
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
      setMode: function (mode) { $root.find("[data-assets-only]").prop("hidden", mode !== "assets"); },
      setScope: function (scope) { $scope.val(scope); },
      setRectangleActive: function (active) { $root.find("[data-role=clear-rect]").prop("hidden", !active); }
    };
  }

  Nadlan.initializeMapFilterPanel = initializeMapFilterPanel;
})(window, jQuery);
