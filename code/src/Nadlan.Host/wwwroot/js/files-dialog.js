// Files dialog for one entity: semantic drop zones on top, the entity's files below, grouped by category.
// Same component for Parcel and Asset; only the zone set differs.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  // Which drop zones each entity gets, and the initial document type per kind of file.
  // Codes are file_type.code values (migration 004); users can change the type afterwards.
  var ZONES = {
    Parcel: [
      { label: "Images", accept: "image/*", types: { image: "PHOTO", other: "PHOTO" } },
      { label: "Drone", accept: "image/*,video/*", types: { image: "DRONE_PHOTO", video: "DRONE_VIDEO", other: "DRONE_PHOTO" } },
      { label: "TABO", types: { other: "TABO" } },
      { label: "Legal documents", types: { other: "TITLE" } },
      { label: "Cadastral / plans", types: { other: "CADASTRAL_PLAN" } },
      { label: "Other", types: { other: "OTHER" } }
    ],
    // Scenario 15: e.g. a signed authorisation from an Agent belongs to the Contact, not to one Asset.
    Contact: [
      { label: "Signed permission", types: { other: "SIGNED_PERMISSION" } },
      { label: "ID / documents", types: { other: "DIGITAL_ID" } },
      { label: "Other", types: { other: "OTHER" } }
    ],
    Portfolio: [
      { label: "Images", accept: "image/*", types: { image: "PHOTO", other: "PHOTO" } },
      { label: "Documents", types: { other: "OTHER" } }
    ],
    Asset: [
      { label: "Images", accept: "image/*", types: { image: "PHOTO", other: "PHOTO" } },
      { label: "Videos", accept: "video/*", types: { video: "VIDEO", other: "VIDEO" } },
      { label: "Drone", accept: "image/*,video/*", types: { image: "DRONE_PHOTO", video: "DRONE_VIDEO", other: "DRONE_PHOTO" } },
      { label: "Legal documents", types: { other: "MANDATE" } },
      { label: "Engineering", types: { other: "FLOOR_PLANS" } },
      { label: "Other", types: { other: "OTHER" } }
    ]
  };

  var CATEGORY_ORDER = ["Marketing", "Legal", "Cadastral", "Engineering", "Permission", "General"];

  /**
   * @param {{ attachedToType: "Parcel"|"Asset", attachedToId: number, title: string, onChanged?: function(): void }} options
   */
  function open(options) {
    return Promise.all([Nadlan.reference.load(),
      Nadlan.api.get("/api/files/rights", { attachedToType: options.attachedToType, attachedToId: options.attachedToId })]).then(function (loaded) {
      var ref = loaded[0];
      var rights = loaded[1]; // { canUpload, categories } - the server filters and checks anyway
      var config = Nadlan.clientConfig || {};
      var $body = $("<div class=\"files-dialog\"></div>");
      var changed = false;

      var d = Nadlan.dialog.open({
        title: options.title,
        content: $body,
        width: Math.min(900, window.innerWidth - 40),
        modal: false,
        buttons: [{ text: "Close", click: function () { d.close(); } }],
        onClose: function () { if (changed && options.onChanged) { options.onChanged(); } }
      });

      if (!config.storageConfigured) {
        $body.html("<div class=\"form-error\">File storage is not configured yet. Set Nadlan:Storage:Bucket " +
          "(<code>.\\config.ps1 set ms:host Nadlan:Storage:Bucket &lt;bucket&gt;</code>) and restart.</div>");
        return d;
      }

      var $zones = $("<div class=\"zones\"></div>").appendTo($body);
      var $gallery = $("<div class=\"gallery\"><div class=\"muted\">Loading…</div></div>").appendTo($body);
      var files = [];

      // Drop zones only where the user may add files, and only for document kinds they may see.
      var categoryOf = {};
      ref.fileTypes.forEach(function (t) { categoryOf[t.code] = t.category; });
      var zones = rights.canUpload ? (ZONES[options.attachedToType] || ZONES.Asset).filter(function (zone) {
        return rights.categories.indexOf(categoryOf[zone.types.other]) >= 0;
      }) : [];
      $zones.prop("hidden", zones.length === 0);
      zones.forEach(function (zone) {
        Nadlan.initializeFileUploadZone($("<div></div>").appendTo($zones), {
          attachedToType: options.attachedToType,
          attachedToId: options.attachedToId,
          zone: zone,
          fileTypes: ref.fileTypes.filter(function (t) { return rights.categories.indexOf(t.category) >= 0; }),
          maxFileSizeBytes: config.maxFileSizeBytes,
          onUploaded: function () { changed = true; loadSoon(); }
        });
      });

      // Many files finishing close together (multi-file drop) cause one reload, not one per file.
      var loadTimer = null;
      function loadSoon() {
        clearTimeout(loadTimer);
        loadTimer = setTimeout(load, 700);
      }

      function load() {
        Nadlan.api.get("/api/files", { attachedToType: options.attachedToType, attachedToId: options.attachedToId })
          .then(function (list) { files = list; render(); },
                function (err) { $gallery.html("<div class=\"error\">" + Nadlan.format.escapeHtml(err.message) + "</div>"); });
      }

      function render() {
        if (!files.length) { $gallery.html("<div class=\"muted\">" + (rights.canUpload ? "No files yet - drop some above." : "No files you can see here.") + "</div>"); return; }
        var esc = Nadlan.format.escapeHtml;
        var byCategory = {};
        files.forEach(function (f) { (byCategory[f.category] = byCategory[f.category] || []).push(f); });
        $gallery.html(CATEGORY_ORDER.filter(function (c) { return byCategory[c]; }).map(function (c) {
          return "<section class=\"gallery-group\"><h4>" + esc(c) + " <span class=\"muted\">(" + byCategory[c].length + ")</span></h4>" +
            "<div class=\"file-grid\">" + byCategory[c].map(Nadlan.renderFileCard).join("") + "</div></section>";
        }).join(""));
      }

      $gallery.on("click", ".file-card", function () {
        var id = Number($(this).data("fileId"));
        var file = files.filter(function (f) { return f.fileAttachmentId === id; })[0];
        if (!file) { return; }
        if (!rights.canUpload) { window.open("/api/files/" + id + "/content", "_blank", "noopener"); return; } // view only
        Nadlan.fileMetadataEditor.open(file, ref.fileTypes.filter(function (t) { return rights.categories.indexOf(t.category) >= 0; })).then(function (result) {
          if (result) { changed = true; load(); }
        });
      });

      load();
      return d;
    });
  }

  Nadlan.filesDialog = { open: open };
})(window, jQuery);
