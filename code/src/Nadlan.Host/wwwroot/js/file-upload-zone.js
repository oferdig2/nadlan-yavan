// FileUploadZone: a drop area (or Browse) that uploads to one target with an initial document type.
// The zone decides the first classification ("Upload first, correct metadata afterwards").
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function formatSize(bytes) {
    if (bytes >= 1073741824) { return (bytes / 1073741824).toFixed(1) + " GB"; }
    if (bytes >= 1048576) { return (bytes / 1048576).toFixed(1) + " MB"; }
    return Math.max(1, Math.round(bytes / 1024)) + " KB";
  }

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ attachedToType: string, attachedToId: number,
   *           zone: { label: string, accept?: string, types: { image?: string, video?: string, other: string } },
   *           fileTypes: Array, maxFileSizeBytes: number, onUploaded: function(): void }} options
   *        zone.types maps the file's kind to a file_type code, e.g. Drone: { image: DRONE_PHOTO, video: DRONE_VIDEO }
   */
  function initializeFileUploadZone(container, options) {
    var esc = Nadlan.format.escapeHtml;
    var zone = options.zone;
    var $root = $(container).addClass("upload-zone").html(
      "<div class=\"drop-area\" tabindex=\"0\">" +
        "<div class=\"drop-label\">" + esc(zone.label) + "</div>" +
        "<div class=\"drop-hint muted\">Drop files or <u>browse</u></div>" +
        "<input type=\"file\" multiple hidden" + (zone.accept ? " accept=\"" + esc(zone.accept) + "\"" : "") + ">" +
      "</div>" +
      "<ul class=\"upload-queue\"></ul>");
    var $drop = $root.find(".drop-area");
    var $input = $root.find("input[type=file]");
    var $queue = $root.find(".upload-queue");

    function typeIdFor(file) {
      var kind = /^image\//.test(file.type) ? "image" : /^video\//.test(file.type) ? "video" : "other";
      var code = zone.types[kind] || zone.types.other;
      var type = options.fileTypes.filter(function (t) { return t.code === code; })[0];
      return type ? type.id : null;
    }

    function enqueue(file) {
      var $item = $(
        "<li class=\"upload-item\">" +
          "<div class=\"upload-name\" title=\"" + esc(file.name) + "\">" + esc(file.name) + "</div>" +
          "<div class=\"upload-meta muted\">" + formatSize(file.size) + " · <span class=\"upload-status\">Starting…</span></div>" +
          "<progress max=\"100\" value=\"0\"></progress>" +
          "<div class=\"upload-actions\">" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"cancel\">Cancel</button>" +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"retry\" hidden>Retry</button>" +
          "</div>" +
        "</li>").appendTo($queue);

      function status(text, isError) {
        $item.find(".upload-status").text(text).toggleClass("error", !!isError);
      }

      if (file.size > options.maxFileSizeBytes) {
        status("Too large (max " + formatSize(options.maxFileSizeBytes) + ")", true);
        $item.find("[data-act=cancel]").text("Dismiss");
        $item.on("click", "[data-act=cancel]", function () { $item.remove(); });
        return;
      }

      var fileTypeId = typeIdFor(file);
      if (!fileTypeId) { status("No document type configured for this zone", true); return; }

      var controller = Nadlan.fileUploads.upload(file,
        { attachedToType: options.attachedToType, attachedToId: options.attachedToId, fileTypeId: fileTypeId },
        {
          onProgress: function (sent, total) {
            var pct = total ? Math.floor(sent * 100 / total) : 0;
            $item.find("progress").val(pct);
            status("Uploading " + pct + "%");
          },
          onState: function (state, message) {
            $item.find("[data-act=retry]").prop("hidden", state !== "failed");
            // No cancel while finishing: S3 may already hold the complete file at that point.
            $item.find("[data-act=cancel]").prop("hidden", state === "done" || state === "cancelled" || state === "completing");
            if (state === "completing") { status("Finishing…"); }
            if (state === "failed") { status(message, true); }
            if (state === "cancelled") { $item.remove(); }
            if (state === "done") {
              status("Uploaded");
              $item.addClass("done");
              setTimeout(function () { $item.fadeOut(300, function () { $item.remove(); }); }, 1500);
              options.onUploaded();
            }
          }
        });

      $item.on("click", "[data-act=cancel]", function () { controller.cancel(); });
      $item.on("click", "[data-act=retry]", function () { controller.retry(); });
    }

    function addFiles(list) { Array.prototype.forEach.call(list || [], enqueue); }

    $drop.on("click keydown", function (e) {
      if (e.type === "click" || e.key === "Enter" || e.key === " ") { $input.trigger("click"); }
    });
    $input.on("click", function (e) { e.stopPropagation(); });
    $input.on("change", function () { addFiles(this.files); this.value = ""; });
    $drop.on("dragover dragenter", function (e) { e.preventDefault(); $drop.addClass("drag-over"); });
    $drop.on("dragleave dragend", function () { $drop.removeClass("drag-over"); });
    $drop.on("drop", function (e) {
      e.preventDefault();
      $drop.removeClass("drag-over");
      addFiles(e.originalEvent.dataTransfer && e.originalEvent.dataTransfer.files);
    });
  }

  Nadlan.initializeFileUploadZone = initializeFileUploadZone;
  Nadlan.formatFileSize = formatSize;
})(window, jQuery);
