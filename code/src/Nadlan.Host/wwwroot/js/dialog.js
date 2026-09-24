// Thin wrapper over jQuery UI dialog so every quick editor behaves the same.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  /**
   * @param {{ title: string, content: jQuery|string, width?: number, modal?: boolean,
   *           buttons: Array<{ text: string, primary?: boolean, click: function(): void }>,
   *           onClose?: function(): void, position?: object }} options
   * @returns {{ $el: jQuery, close: function(): void, showError: function(string): void, busy: function(boolean): void }}
   */
  function open(options) {
    var $el = $("<div class=\"dialog-body\"></div>").append(options.content);
    var $error = $("<div class=\"form-error\" hidden></div>").prependTo($el);
    var closed = false;

    $el.dialog({
      title: options.title,
      width: options.width || 440,
      modal: options.modal !== false,
      position: options.position || { my: "center", at: "center", of: window },
      buttons: options.buttons.map(function (b) {
        return { text: b.text, "class": b.primary ? "btn-primary" : "", click: b.click };
      }),
      close: function () {
        if (closed) { return; }
        closed = true;
        if (options.onClose) { options.onClose(); }
        $el.dialog("destroy").remove();
      }
    });

    return {
      $el: $el,
      close: function () { if (!closed) { $el.dialog("close"); } },
      showError: function (message) { $error.text(message || "").prop("hidden", !message); },
      busy: function (isBusy) {
        $el.closest(".ui-dialog").find(".ui-dialog-buttonpane button").prop("disabled", !!isBusy);
      }
    };
  }

  function confirm(title, messageHtml, okText) {
    return new Promise(function (resolve) {
      var answered = false;
      var d = open({
        title: title,
        content: $("<div></div>").html(messageHtml),
        buttons: [
          { text: okText || "OK", primary: true, click: function () { answered = true; resolve(true); d.close(); } },
          { text: "Cancel", click: function () { d.close(); } }
        ],
        onClose: function () { if (!answered) { resolve(false); } }
      });
    });
  }

  // Reads a form's [name] fields into an object; numbers and checkboxes typed by data-type/checkbox.
  function readForm($form) {
    var data = {};
    $form.find("[name]").each(function () {
      var $f = $(this);
      var name = $f.attr("name");
      if ($f.is(":checkbox")) {
        data[name] = $f.prop("indeterminate") ? null : $f.prop("checked");
        return;
      }
      var raw = $.trim($f.val() || "");
      if ($f.data("type") === "number") {
        data[name] = Nadlan.format.parseNumber(raw);
      } else if ($f.data("type") === "id") {
        data[name] = raw === "" ? null : Number(raw);
      } else {
        data[name] = raw === "" ? null : raw;
      }
    });
    return data;
  }

  Nadlan.dialog = { open: open, confirm: confirm, readForm: readForm };
})(window, jQuery);
