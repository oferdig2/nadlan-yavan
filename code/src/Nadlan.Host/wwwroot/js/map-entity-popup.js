// Floating card for a clicked Parcel (both views): cadastral summary, the Assets on it, and actions.
// Unpinned: replaced when another Parcel is clicked. Pinned: stays while map work continues.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {{ container: HTMLElement, selection: object, onClose: function(): void,
   *           onCreateAsset: function(object): void, onEditAsset: function(number, object): void,
   *           onOpenFiles: function(string, number, string, number): void,   (type, id, title, parcelId)
   *           onEditParcel: function(number): void }} options
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
          "<div class=\"row-body\"><div>" + f.assetPrice(a) + " " + f.statusBadge(a.statusName, a.statusColor) + "</div>" +
          "<div class=\"muted\">" + f.text(a.managingContactName) + (a.propertyType ? " · " + f.escapeHtml(a.propertyType) : "") + " · #" + a.assetId + "</div></div>" +
          "<span class=\"row-actions\">" +
            (a.canEdit ? "<button type=\"button\" class=\"btn btn-small\" data-edit=\"" + a.assetId + "\">Edit</button>" : "") +
            "<button type=\"button\" class=\"btn btn-small\" data-files=\"" + a.assetId + "\">Files</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-history=\"" + a.assetId + "\">History</button>" +
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
      var state = { parcelId: initial.parcelId, parcel: initial, pinned: false, expanded: false, minimized: false, chipPos: null, details: null };
      var $card = $(
        "<section class=\"map-card\" role=\"dialog\">" +
          "<header class=\"card-header\">" +
            "<span class=\"card-title\">Parcel</span>" +
            "<span class=\"card-actions\">" +
              "<button type=\"button\" data-action=\"edit-parcel\" hidden>Edit</button>" +
              "<button type=\"button\" data-action=\"history\">History</button>" +
              "<button type=\"button\" data-action=\"expand\">Expand</button>" +
              "<button type=\"button\" class=\"card-icon\" data-action=\"pin\" aria-pressed=\"false\" title=\"Pin: keep this card while you click other parcels\">📌</button>" +
              "<button type=\"button\" class=\"card-icon\" data-action=\"minimize\" title=\"Minimize to a small chip\">▁</button>" +
              "<button type=\"button\" class=\"card-icon\" data-action=\"close\" title=\"Close\">×</button>" +
            "</span>" +
          "</header>" +
          // Minimized: only this chip shows - the KAEK and Maximize; drag it anywhere.
          "<div class=\"card-chip\" title=\"Drag to move\"><span class=\"card-chip-kaek kaek\"></span>" +
            "<button type=\"button\" class=\"card-icon\" data-action=\"maximize\" title=\"Maximize\">⤢</button></div>" +
          "<div class=\"card-body\">" + parcelHtml(initial) + "</div>" +
          "<div class=\"card-details\" hidden></div>" +
          "<div class=\"card-section card-owners\" hidden><div class=\"card-section-head\"><span>Legal owners</span>" +
            "<button type=\"button\" class=\"btn btn-small\" data-action=\"add-owner\" hidden>+ Add</button></div>" +
            "<div class=\"card-owners-slot\"></div></div>" +
          "<div class=\"card-section\"><div class=\"card-section-head\"><span>Parcel files</span>" +
            "<button type=\"button\" class=\"btn btn-small\" data-action=\"parcel-files\">Files</button></div>" +
            "<div class=\"card-thumbs\"></div></div>" +
          "<div class=\"card-section\"><div class=\"card-section-head\"><span>Assets</span>" +
            "<button type=\"button\" class=\"btn btn-small btn-primary\" data-action=\"create-asset\" hidden>+ Create asset</button></div>" +
            "<div class=\"card-assets-slot muted\">Loading…</div></div>" +
        "</section>");

      function load() {
        Nadlan.api.get("/api/parcels/" + state.parcelId).then(function (d) {
          state.parcel = d.summary;
          state.details = d;
          $card.find(".card-body").html(parcelHtml(d.summary));
          chipText();
          // What this user may do here (the server decides; buttons only follow).
          var rights = d.rights || {};
          $card.find("[data-action=edit-parcel]").prop("hidden", !rights.canEdit);
          $card.find("[data-action=create-asset]").prop("hidden", !rights.canCreateAsset);
          $card.find(".card-owners").prop("hidden", !rights.canSeeLegalOwners);
          $card.find("[data-action=add-owner]").prop("hidden", !rights.canEdit);
          if (rights.canSeeLegalOwners) {
            if (owners) { owners.reload(); } else { owners = Nadlan.legalOwners.render($card.find(".card-owners-slot"), state.parcelId, !!rights.canEdit); }
          }
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
      function setPinned(pinned) {
        state.pinned = pinned;
        $card.toggleClass("pinned", pinned);
        $card.find("[data-action=pin]").attr({ "aria-pressed": String(pinned), title: pinned ? "Unpin" : "Pin: keep this card while you click other parcels" });
        if (pinned && unpinned === card) {
          unpinned = null;
        } else if (!pinned && unpinned !== card) {
          if (unpinned) { unpinned.close(true); }
          unpinned = card;
        }
        layout();
      }

      $card.on("click", "[data-action=pin]", function () { setPinned(!state.pinned); });

      // Minimized = a small draggable chip (KAEK + Maximize). It stays while other parcels are clicked, so it is pinned.
      function chipText() { $card.find(".card-chip-kaek").text(state.parcel.registryId || "#" + state.parcelId); }

      $card.on("click", "[data-action=minimize]", function () {
        var rect = $card[0].getBoundingClientRect();
        if (!state.pinned) { setPinned(true); }
        state.minimized = true;
        chipText();
        var pos = state.chipPos || { left: rect.left, top: rect.top };
        $card.addClass("minimized").css({ left: pos.left + "px", top: pos.top + "px" });
      });
      $card.on("click", "[data-action=maximize]", function () {
        state.minimized = false;
        $card.removeClass("minimized").css({ left: "", top: "" });
        layout();
      });

      // Drag the chip by any part but its button; kept inside the window.
      $card.on("pointerdown", ".card-chip", function (e) {
        if (!state.minimized || $(e.target).closest("button").length) { return; }
        var el = $card[0];
        var rect = el.getBoundingClientRect();
        var dx = e.clientX - rect.left, dy = e.clientY - rect.top;
        el.setPointerCapture(e.pointerId);
        $card.addClass("dragging");
        function move(ev) {
          var left = Math.min(Math.max(0, ev.clientX - dx), window.innerWidth - rect.width);
          var top = Math.min(Math.max(0, ev.clientY - dy), window.innerHeight - rect.height);
          state.chipPos = { left: left, top: top };
          el.style.left = left + "px";
          el.style.top = top + "px";
        }
        function up(ev) {
          el.releasePointerCapture(ev.pointerId);
          el.removeEventListener("pointermove", move);
          el.removeEventListener("pointerup", up);
          $card.removeClass("dragging");
        }
        el.addEventListener("pointermove", move);
        el.addEventListener("pointerup", up);
        e.preventDefault();
      });
      $card.on("click", "[data-action=expand]", function () {
        state.expanded = !state.expanded;
        $(this).text(state.expanded ? "Collapse" : "Expand");
        $card.find(".card-details").prop("hidden", !state.expanded).html(state.details ? detailsHtml(state.details) : "Loading…");
      });
      $card.on("click", "[data-action=create-asset]", function () { options.onCreateAsset(state.parcel); });
      $card.on("click", "[data-action=edit-parcel]", function () { options.onEditParcel(state.parcelId); });
      var owners = null; // rendered once the rights say legal owners may be shown
      $card.on("click", "[data-action=add-owner]", function () {
        owners.add().catch(function (err) { Nadlan.dialog.showError("Could not open", err); });
      });
      $card.on("click", "[data-edit]", function () { options.onEditAsset(Number($(this).data("edit")), state.parcel); });
      $card.on("click", "[data-history]", function () {
        var assetId = Number($(this).data("history"));
        Nadlan.historyDialog.open({ entityType: "Asset", entityId: assetId, title: "Asset #" + assetId + " - history" });
      });
      $card.on("click", "[data-action=history]", function () {
        Nadlan.historyDialog.open({ entityType: "Parcel", entityId: state.parcelId,
          title: "Parcel " + (state.parcel.registryId || "#" + state.parcelId) + " - history" });
      });
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
      /** Closes every card of a Parcel that no longer exists (deleted). */
      closeParcel: function (parcelId) {
        cards.filter(function (c) { return c.state.parcelId === parcelId; }).forEach(function (c) { c.close(true); });
      },
      /** Re-fetches every open card showing this Parcel (e.g. after an Asset was created or edited). */
      refreshParcel: function (parcelId) {
        cards.forEach(function (c) { if (c.state.parcelId === parcelId) { c.reload(); } });
      }
    };
  }

  Nadlan.createMapEntityPopupManager = createMapEntityPopupManager;
})(window, jQuery);
