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
    return Nadlan.reference.load().then(function (ref) {
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

      (ZONES[options.attachedToType] || ZONES.Asset).forEach(function (zone) {
        Nadlan.initializeFileUploadZone($("<div></div>").appendTo($zones), {
          attachedToType: options.attachedToType,
          attachedToId: options.attachedToId,
          zone: zone,
          fileTypes: ref.fileTypes,
          maxFileSizeBytes: config.maxFileSizeBytes,
          onUploaded: function () { changed = true; load(); }
        });
      });

      function load() {
        Nadlan.api.get("/api/files", { attachedToType: options.attachedToType, attachedToId: options.attachedToId })
          .then(function (list) { files = list; render(); },
                function (err) { $gallery.html("<div class=\"error\">" + Nadlan.format.escapeHtml(err.message) + "</div>"); });
      }

      function render() {
        if (!files.length) { $gallery.html("<div class=\"muted\">No files yet - drop some above.</div>"); return; }
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
        Nadlan.fileMetadataEditor.open(file, ref.fileTypes).then(function (result) {
          if (result) { changed = true; load(); }
        });
      });

      load();
      return d;
    });
  }

  Nadlan.filesDialog = { open: open };
})(window, jQuery);
