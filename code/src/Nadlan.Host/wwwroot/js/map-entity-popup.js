// Floating card for a clicked Parcel (both views): cadastral summary, the Assets on it, and actions.
// Unpinned: replaced when another Parcel is clicked. Pinned: stays while map work continues.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {{ container: HTMLElement, selection: object, onClose: function(): void,
   *           onCreateAsset: function(object): void, onEditAsset: function(number, object): void,
   *           onOpenFiles: function(string, number, string, number): void }} options  (type, id, title, parcelId)
   */
  function createMapEntityPopupManager(options) {
    var f = Nadlan.format;
    var $layer = $(options.container);
    var cards = []; // all open cards
    var unpinned = null;

    function row(label, valueHtml) {
      return "<div class=\"card-row\"><span class=\"card-label\">" + label + "</span><span class=\"card-value\">" + valueHtml + "</span></div>";
    }

    function parcelHtml(p) {
      return row("KAEK", f.registryId(p.registryId, p.registryIdIsProvisional)) +
        row("Area", f.text(p.geographicArea)) +
        row("OT / Plot", f.text(p.ot) + " / " + f.text(p.plot)) +
        row("Official area", f.sqm(p.officialAreaSqm));
    }

    function assetsHtml(assets) {
      if (!assets.length) { return "<div class=\"muted card-row\">No assets on this parcel yet.</div>"; }
      return "<ul class=\"card-assets\">" + assets.map(function (a) {
        return "<li>" +
          "<label class=\"check\"><input type=\"checkbox\" data-select=\"" + a.assetId + "\"" + (options.selection.has(a.assetId) ? " checked" : "") + "></label>" +
          "<div class=\"row-body\"><div>" + f.price(a.askPrice, a.currencyCode) + " " + f.statusBadge(a.statusName, a.statusColor) + "</div>" +
          "<div class=\"muted\">" + f.text(a.managingContactName) + (a.propertyType ? " · " + f.escapeHtml(a.propertyType) : "") + " · #" + a.assetId + "</div></div>" +
          "<span class=\"row-actions\">" +
            "<button type=\"button\" class=\"btn btn-small\" data-edit=\"" + a.assetId + "\">Edit</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-files=\"" + a.assetId + "\">Files</button>" +
          "</span>" +
          "</li>";
      }).join("") + "</ul>";
    }

    function detailsHtml(d) {
      return row("Build factor", f.text(d.buildFactor)) +
        row("Inclination", d.inclination === null ? "—" : f.escapeHtml(d.inclination) + "%") +
        row("Notes", f.text(d.notes));
    }

    function createCard(initial) {
      var state = { parcelId: initial.parcelId, parcel: initial, pinned: false, expanded: false, details: null };
      var $card = $(
        "<section class=\"map-card\" role=\"dialog\">" +
          "<header class=\"card-header\">" +
            "<span class=\"card-title\">Parcel</span>" +
            "<span class=\"card-actions\">" +
              "<button type=\"button\" data-action=\"expand\">Expand</button>" +
              "<button type=\"button\" data-action=\"pin\">Pin</button>" +
              "<button type=\"button\" data-action=\"close\" title=\"Close\">×</button>" +
            "</span>" +
          "</header>" +
          "<div class=\"card-body\">" + parcelHtml(initial) + "</div>" +
          "<div class=\"card-details\" hidden></div>" +
          "<div class=\"card-section\"><div class=\"card-section-head\"><span>Parcel files</span>" +
            "<button type=\"button\" class=\"btn btn-small\" data-action=\"parcel-files\">Files</button></div>" +
            "<div class=\"card-thumbs\"></div></div>" +
          "<div class=\"card-section\"><div class=\"card-section-head\"><span>Assets</span>" +
            "<button type=\"button\" class=\"btn btn-small btn-primary\" data-action=\"create-asset\">+ Create asset</button></div>" +
            "<div class=\"card-assets-slot muted\">Loading…</div></div>" +
        "</section>");

      function load() {
        Nadlan.api.get("/api/parcels/" + state.parcelId).then(function (d) {
          state.parcel = d.summary;
          state.details = d;
          $card.find(".card-body").html(parcelHtml(d.summary));
          if (state.expanded) { $card.find(".card-details").html(detailsHtml(d)); }
        }, function (err) { $card.find(".card-body").append("<div class=\"error\">" + f.escapeHtml(err.message) + "</div>"); });

        Nadlan.api.get("/api/parcels/" + state.parcelId + "/assets").then(function (assets) {
          $card.find(".card-assets-slot").removeClass("muted").html(assetsHtml(assets));
        }, function (err) { $card.find(".card-assets-slot").html("<span class=\"error\">" + f.escapeHtml(err.message) + "</span>"); });

        loadThumbs();
      }

      // Media first (Appendix 1 §5.1): up to 6 image/video thumbnails of the Parcel's own files.
      function loadThumbs() {
        var $thumbs = $card.find(".card-thumbs");
        if (!(Nadlan.clientConfig || {}).storageConfigured) { $thumbs.html("<span class=\"muted\">Storage not configured</span>"); return; }
        Nadlan.api.get("/api/files", { attachedToType: "Parcel", attachedToId: state.parcelId }).then(function (files) {
          var media = files.filter(function (x) { var k = Nadlan.fileKind(x); return k === "image" || k === "video"; }).slice(0, 6);
          var docs = files.length - media.length;
          $thumbs.html(
            (media.length ? "<div class=\"thumb-strip\">" + media.map(Nadlan.renderFileCard).join("") + "</div>" : "") +
            "<div class=\"muted\">" + (files.length ? files.length + " file(s)" + (docs ? ", " + docs + " document(s)" : "") : "No files yet") + "</div>");
        }, function () { $thumbs.empty(); });
      }

      $card.on("click", "[data-action=close]", function () { close(); });
      $card.on("click", "[data-action=pin]", function () {
        state.pinned = !state.pinned;
        $(this).text(state.pinned ? "Unpin" : "Pin");
        $card.toggleClass("pinned", state.pinned);
        if (state.pinned && unpinned === card) {
          unpinned = null;
        } else if (!state.pinned) {
          if (unpinned) { unpinned.close(true); }
          unpinned = card;
        }
        layout();
      });
      $card.on("click", "[data-action=expand]", function () {
        state.expanded = !state.expanded;
        $(this).text(state.expanded ? "Collapse" : "Expand");
        $card.find(".card-details").prop("hidden", !state.expanded).html(state.details ? detailsHtml(state.details) : "Loading…");
      });
      $card.on("click", "[data-action=create-asset]", function () { options.onCreateAsset(state.parcel); });
      $card.on("click", "[data-edit]", function () { options.onEditAsset(Number($(this).data("edit")), state.parcel); });
      $card.on("click", "[data-files]", function () {
        var assetId = Number($(this).data("files"));
        options.onOpenFiles("Asset", assetId, "Asset #" + assetId + " - files", state.parcelId);
      });
      $card.on("click", "[data-action=parcel-files], .card-thumbs .file-card", function () {
        options.onOpenFiles("Parcel", state.parcelId, "Parcel " + (state.parcel.registryId || "#" + state.parcelId) + " - files", state.parcelId);
      });
      $card.on("change", "[data-select]", function () { options.selection.toggle(Number($(this).data("select")), this.checked); });

      // silent = replaced by a newer card; the map selection already moved on, so don't clear it.
      function close(silent) {
        cards = cards.filter(function (c) { return c !== card; });
        if (unpinned === card) {
          unpinned = null;
          if (!silent) { options.onClose(); }
        }
        $card.remove();
      }

      var card = { state: state, $el: $card, close: close, reload: load };
      load();
      return card;
    }

    // Pinned cards stack first; the live (unpinned) card sits at the bottom.
    function layout() {
      $layer.children(".map-card.pinned").appendTo($layer);
      if (unpinned) { unpinned.$el.appendTo($layer); }
    }

    options.selection.onChange(function () {
      $layer.find("[data-select]").each(function () { this.checked = options.selection.has(Number($(this).data("select"))); });
    });

    return {
      /** @param {{ parcelId: number, registryId?: string }} parcel  any Parcel summary; the card loads the rest */
      showParcel: function (parcel) {
        if (unpinned) { unpinned.close(true); }
        unpinned = createCard(parcel);
        cards.push(unpinned);
        $layer.append(unpinned.$el);
        layout();
      },
      /** Re-fetches every open card showing this Parcel (e.g. after an Asset was created or edited). */
      refreshParcel: function (parcelId) {
        cards.forEach(function (c) { if (c.state.parcelId === parcelId) { c.reload(); } });
      }
    };
  }

  Nadlan.createMapEntityPopupManager = createMapEntityPopupManager;
})(window, jQuery);
