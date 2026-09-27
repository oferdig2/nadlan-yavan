// MetadataTableEditor: one generic Admin editor for the small lookup tables (Appendix 2 §25):
// search, list, create, edit name/order, activate/deactivate, and colour (statuses) or category (file types).
// Codes are fixed after creation because the app refers to them.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var FILE_CATEGORIES = ["Legal", "Engineering", "Marketing", "Cadastral", "Permission", "General"];

  /**
   * @param {jQuery|HTMLElement} container
   * @param {{ table: string, title: string, hasColor?: boolean, hasCategory?: boolean }} options
   *        table = URL name, e.g. "asset-statuses"
   */
  function initializeMetadataTableEditor(container, options) {
    var esc = Nadlan.format.escapeHtml;
    var rows = [];
    var $root = $(container).addClass("meta-editor").html(
      "<div class=\"meta-toolbar\">" +
        "<h3>" + esc(options.title) + "</h3>" +
        "<input type=\"text\" class=\"input\" placeholder=\"Filter…\">" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"add\">+ Add</button>" +
      "</div>" +
      "<table class=\"meta-table\"><thead><tr>" +
        "<th>Code</th><th>Name</th>" + (options.hasColor ? "<th>Colour</th>" : "") + (options.hasCategory ? "<th>Category</th>" : "") +
        "<th>Order</th><th>Active</th><th></th>" +
      "</tr></thead><tbody></tbody></table>" +
      "<div class=\"form-error\" hidden></div>");
    var url = "/api/admin/reference/" + encodeURIComponent(options.table);

    function showError(message) { $root.find(".form-error").text(message || "").prop("hidden", !message); }

    function render() {
      var filter = $.trim($root.find(".meta-toolbar input").val()).toLowerCase();
      var visible = rows.filter(function (r) { return !filter || (r.code + " " + r.name).toLowerCase().indexOf(filter) >= 0; });
      $root.find("tbody").html(visible.map(function (r) {
        return "<tr class=\"" + (r.isActive ? "" : "inactive") + "\">" +
          "<td class=\"kaek\">" + esc(r.code) + "</td><td>" + esc(r.name) + "</td>" +
          (options.hasColor ? "<td><span class=\"swatch\" style=\"background:" + esc(r.color || "#ccc") + "\"></span> " + esc(r.color || "") + "</td>" : "") +
          (options.hasCategory ? "<td>" + esc(r.category || "") + "</td>" : "") +
          "<td>" + r.sortOrder + "</td>" +
          "<td>" + (r.isActive ? "Yes" : "<span class=\"muted\">No</span>") + "</td>" +
          "<td><button type=\"button\" class=\"btn btn-small\" data-edit=\"" + r.id + "\">Edit</button></td></tr>";
      }).join("") || "<tr><td colspan=\"7\" class=\"muted\">Nothing here.</td></tr>");
    }

    function load() {
      Nadlan.api.get(url).then(function (list) { rows = list; showError(""); render(); }, function (err) { showError(err.message); });
    }

    function openEditor(row) {
      var editing = !!row;
      var r = row || { code: "", name: "", isActive: true, sortOrder: (rows.length + 1) * 10, color: "#2563eb", category: "General" };
      var $form = $(
        "<form class=\"form\">" +
          "<label>Code<input class=\"input\" name=\"code\" value=\"" + esc(r.code) + "\"" + (editing ? " disabled" : " placeholder=\"e.g. UNDER_OFFER\"") + "></label>" +
          (editing ? "<div class=\"muted\">The code can't change: the app and imports refer to it.</div>" : "") +
          "<label>Name<input class=\"input\" name=\"name\" value=\"" + esc(r.name) + "\"></label>" +
          (options.hasColor ? "<label>Map colour<input class=\"input\" type=\"color\" name=\"color\" value=\"" + esc(r.color || "#2563eb") + "\"></label>" : "") +
          (options.hasCategory ? "<label>Category (drives who may see these files later)<select class=\"input\" name=\"category\">" +
            FILE_CATEGORIES.map(function (c) { return "<option" + (c === r.category ? " selected" : "") + ">" + c + "</option>"; }).join("") + "</select></label>" : "") +
          "<label>Sort order<input class=\"input\" name=\"sortOrder\" data-type=\"number\" value=\"" + r.sortOrder + "\"></label>" +
          "<label class=\"check\"><input type=\"checkbox\" name=\"isActive\"" + (r.isActive ? " checked" : "") + "> Active (inactive values stay on existing records but can't be chosen)</label>" +
        "</form>");

      var d = Nadlan.dialog.open({
        title: (editing ? "Edit " : "Add to ") + options.title,
        content: $form,
        buttons: [
          { text: "Save", primary: true, click: save },
          { text: "Cancel", click: function () { d.close(); } }
        ]
      });

      function save() {
        var data = Nadlan.dialog.readForm($form);
        if (data.sortOrder === null || isNaN(data.sortOrder) || data.sortOrder % 1 !== 0) { d.showError("Sort order must be a whole number."); return; }
        var body = { code: data.code, name: data.name, isActive: data.isActive, sortOrder: data.sortOrder, color: data.color, category: data.category };
        d.busy(true);
        var call = editing ? Nadlan.api.put(url + "/" + r.id, body) : Nadlan.api.post(url, body);
        call.then(function () { d.close(); load(); }, function (err) { d.busy(false); d.showError(err.message); });
      }
    }

    $root.on("input", ".meta-toolbar input", render);
    $root.on("click", "[data-act=add]", function () { openEditor(null); });
    $root.on("click", "[data-edit]", function () {
      var id = Number($(this).data("edit"));
      openEditor(rows.filter(function (r) { return r.id === id; })[0]);
    });

    load();
    return { reload: load };
  }

  Nadlan.initializeMetadataTableEditor = initializeMetadataTableEditor;
})(window, jQuery);
