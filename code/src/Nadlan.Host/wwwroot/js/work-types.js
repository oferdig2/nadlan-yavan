// The work types list (Work page, piece 4): code, English and Greek name, active, order. Add and edit; a type in use keeps
// its code and can't be deleted - make it inactive instead (not offered for new works; old ones still show it). Kept here,
// not in Admin, so the module doesn't touch the Admin page.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var W = Nadlan.work;
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };
  var $panel, types = [];

  function load() {
    return Nadlan.api.get("/api/work/work-types").then(function (list) { types = list; render(); },
      function (err) { $panel.html("<div class=\"error\">" + esc(err.message) + "</div>"); });
  }

  function render() {
    $panel.html(
      "<div class=\"meta-toolbar\"><h3>Work types</h3>" +
        "<button type=\"button\" class=\"btn btn-primary\" data-act=\"add-type\">+ Add work type</button>" +
        "<span class=\"muted\">The contractor sees the Greek name (or the English one in English).</span>" +
      "</div>" +
      "<div class=\"work-table-wrap\"><table class=\"meta-table work-types\"><thead><tr>" +
        "<th class=\"num\">Order</th><th>Code</th><th>English</th><th>Greek</th><th>Active</th><th class=\"num\">Used by</th><th></th>" +
      "</tr></thead><tbody>" +
      types.map(function (t) {
        return "<tr data-id=\"" + t.workTypeId + "\"" + (t.isActive ? "" : " class=\"inactive\"") + ">" +
          "<td class=\"num\">" + t.sortOrder + "</td><td><code>" + esc(t.code) + "</code></td><td>" + esc(t.name) + "</td><td>" + esc(t.nameEl) + "</td>" +
          "<td>" + (t.isActive ? "Yes" : "No") + "</td>" +
          "<td class=\"num\">" + (t.uses ? t.uses + " work" + (t.uses === 1 ? "" : "s") : "—") + "</td>" +
          "<td class=\"row-buttons\"><button type=\"button\" class=\"btn btn-small\" data-act=\"edit-type\">Edit</button>" +
            (t.uses ? "" : "<button type=\"button\" class=\"btn btn-small\" data-act=\"delete-type\">Delete</button>") + "</td></tr>";
      }).join("") +
      "</tbody></table></div>" +
      "<div class=\"meta-note muted\">A type that works use can't be deleted and keeps its code: make it inactive to stop offering it.</div>");
  }

  function typeOf(el) {
    var id = Number($(el).closest("tr").data("id"));
    return types.filter(function (t) { return t.workTypeId === id; })[0];
  }

  function openEdit(t) {
    var isNew = !t;
    t = t || { code: "", name: "", nameEl: "", isActive: true, sortOrder: (types.length ? Math.max.apply(null, types.map(function (x) { return x.sortOrder; })) : 0) + 10, uses: 0 };
    var $form = $(
      "<form class=\"form\">" +
        "<div class=\"form-row\"><label>Code<input class=\"input\" name=\"code\" maxlength=\"40\" autocomplete=\"off\"" + (t.uses ? " readonly title=\"Works use this type: its code stays\"" : "") + "></label>" +
        "<label class=\"narrow\">Order<input class=\"input\" name=\"sortOrder\" inputmode=\"numeric\"></label></div>" +
        "<label>English name<input class=\"input\" name=\"name\" maxlength=\"100\"></label>" +
        "<label>Greek name<input class=\"input\" name=\"nameEl\" maxlength=\"100\" lang=\"el\"></label>" +
        "<label class=\"check\"><input type=\"checkbox\" name=\"isActive\"> Active (offered for new works)</label>" +
        "<div class=\"muted\">Code: capital letters, digits and _ (e.g. CUT_TREES)." + (t.uses ? " Used by " + t.uses + " work(s): the code stays." : "") + "</div>" +
      "</form>");
    $form.find("[name=code]").val(t.code);
    $form.find("[name=sortOrder]").val(t.sortOrder);
    $form.find("[name=name]").val(t.name);
    $form.find("[name=nameEl]").val(t.nameEl);
    $form.find("[name=isActive]").prop("checked", t.isActive);
    var d = Nadlan.dialog.open({
      title: isNew ? "Add a work type" : "Edit " + t.name,
      content: $form,
      width: Math.min(480, window.innerWidth - 24),
      buttons: [
        { text: "Save", primary: true, click: save },
        { text: "Cancel", click: function () { d.close(); } }
      ]
    });
    $form.find(isNew ? "[name=code]" : "[name=name]").trigger("focus");

    function save() {
      var order = Number($.trim($form.find("[name=sortOrder]").val() || "0"));
      if (!isFinite(order) || Math.round(order) !== order) { d.showError("The order is a whole number."); return; }
      var body = {
        code: $.trim($form.find("[name=code]").val() || ""), name: $.trim($form.find("[name=name]").val() || ""),
        nameEl: $.trim($form.find("[name=nameEl]").val() || ""), isActive: $form.find("[name=isActive]").prop("checked"), sortOrder: order
      };
      d.busy(true);
      (isNew ? Nadlan.api.post("/api/work/work-types", body) : Nadlan.api.put("/api/work/work-types/" + t.workTypeId, body)).then(function () {
        d.close();
        W.setStatus(isNew ? "Work type added." : "Saved.");
        load();
      }, function (err) { d.busy(false); d.showError(err.message); });
    }
  }

  function open($target) {
    $panel = $target;
    $panel.off().html("<div class=\"muted\">Loading…</div>");
    $panel.on("click", "[data-act=add-type]", function () { openEdit(null); });
    $panel.on("click", "[data-act=edit-type]", function () { openEdit(typeOf(this)); });
    $panel.on("click", "[data-act=delete-type]", function () {
      var t = typeOf(this);
      Nadlan.dialog.confirm("Delete a work type", "Delete <strong>" + esc(t.name) + "</strong>? No work uses it.", "Delete").then(function (ok) {
        if (!ok) { return; }
        Nadlan.api.del("/api/work/work-types/" + t.workTypeId).then(function () { W.setStatus("Deleted."); load(); },
          function (err) { Nadlan.dialog.showError("Delete a work type", err); });
      });
    });
    load();
  }

  Nadlan.workTypes = { open: open };
})(window, jQuery);
