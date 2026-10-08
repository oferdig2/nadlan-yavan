// Admin page, Settings tab: the app_config rows (DB-first settings) as a form, or as raw JSON. Secrets come masked and stay
// on the server unless replaced. The server checks a save the way the app reads it at startup; the app reads settings only
// when it starts, so a save is followed by "Restart the app".
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var M = function () { return Nadlan.machine; };
  var esc = function (s) { return Nadlan.format.escapeHtml(s); };
  var API = "/api/admin/machine/config";

  Nadlan.initializeAdminSettings = function ($panel) {
    // saved: the row as loaded (masked); working: the edits so far, carried between Form and Raw JSON.
    var rows = [], appStarted = 0, doc = null, saved = null, working = null, mode = "form";
    var $layout = $("<div class=\"roles-layout settings-layout\"><ul class=\"roles-list\"></ul><div class=\"role-detail\"></div></div>");
    var $list = $layout.find(".roles-list"), $detail = $layout.find(".role-detail");
    var $status = $("<div class=\"done-note\" aria-live=\"polite\"></div>");
    $panel.append($("<div class=\"meta-toolbar\"><h3>Settings</h3><span class=\"muted\">Stored in the database (app_config). The app reads them when it starts.</span></div>"),
      $status, $layout);

    function report(html) { $status.html(html); }

    function loadRows(selectKey) {
      return Nadlan.api.get(API).then(function (r) {
        rows = r.rows;
        appStarted = r.appStartedUtcMs;
        $list.empty();
        rows.forEach(function (row) {
          var pending = row.updatedUtcMs > appStarted;
          $list.append($("<li></li>").attr("data-key", row.key).append(
            $("<div></div>").text(row.title),
            $("<div class=\"muted small\"></div>").text(row.key + " · " + M().time(row.updatedUtcMs)),
            pending ? $("<div class=\"small warn\"></div>").text("⟳ Changed after the app started: restart to apply") : $()));
        });
        var key = selectKey || (doc && doc.key) || (rows.some(function (x) { return x.key === "ms:host"; }) ? "ms:host" : rows[0] && rows[0].key);
        if (key) { open(key); }
      }, function (err) { $detail.empty().append($("<div class=\"form-error\"></div>").text(err.message)); });
    }

    $list.on("click", "li", function () {
      var key = $(this).data("key");
      if (doc && key === doc.key) { return; }
      if (isDirty()) {
        Nadlan.dialog.confirm("Unsaved changes", "<p>Leave " + esc(doc.key) + " without saving?</p>", "Leave").then(function (ok) { if (ok) { open(key); } });
      } else {
        open(key);
      }
    });

    function open(key) {
      Nadlan.api.get(API + "/" + encodeURIComponent(key)).then(function (d) {
        doc = d;
        saved = d.valid ? JSON.parse(d.json) : null;
        working = saved && JSON.parse(d.json);
        mode = d.valid ? "form" : "json";
        $list.find("li").removeClass("active").filter(function () { return $(this).data("key") === key; }).addClass("active");
        renderDetail();
      }, function (err) { Nadlan.dialog.showError("Settings", err); });
    }

    function renderDetail() {
      $detail.empty().append(
        $("<div class=\"meta-toolbar\"></div>").append(
          $("<h3></h3>").text(doc.title),
          $("<span class=\"view-switch\"></span>").append(
            $("<button type=\"button\" class=\"btn\" data-mode=\"form\">Form</button>").toggleClass("active", mode === "form").prop("disabled", !doc.valid),
            $("<button type=\"button\" class=\"btn\" data-mode=\"json\">Raw JSON</button>").toggleClass("active", mode === "json")),
          $("<button type=\"button\" class=\"btn btn-primary\" data-act=\"save\">Save…</button>"),
          $("<button type=\"button\" class=\"btn\" data-act=\"restart\">Restart the app</button>")),
        $("<div class=\"muted small\"></div>").text(doc.key + " · saved " + M().time(doc.updatedUtcMs) +
          ". Secrets show as " + doc.masked + " and are kept unless you type a new value."));
      if (!doc.valid) {
        $detail.append($("<div class=\"form-error\"></div>").text("This row is not valid JSON (a hand edit?). The app won't start until it is fixed: correct it below and save."));
      }
      var $editor = mode === "form" ? renderForm(working) : $("<textarea class=\"input code-area settings-json\" spellcheck=\"false\"></textarea>")
        .val(working ? JSON.stringify(working, null, 2) : doc.json);
      $detail.append($editor);
    }

    // -------------------------------------------------------------- form from the JSON tree + the field catalog

    function fieldFor(path) {
      return doc.fields.filter(function (f) { return f.path.toLowerCase() === path.toLowerCase(); })[0];
    }

    function sectionFor(path) {
      return doc.sections.filter(function (s) { return s.path.toLowerCase() === path.toLowerCase(); })[0];
    }

    function isSecretName(name) { return /secret|password|privatekey/i.test(name); }

    function renderForm(obj) {
      var $form = $("<form class=\"form settings-form\"></form>");
      (function walk(node, path) {
        var leaves = byCatalog(Object.keys(node).filter(function (k) { return !isObject(node[k]); }), path, doc.fields);
        var children = byCatalog(Object.keys(node).filter(function (k) { return isObject(node[k]); }), path, doc.sections);
        if (leaves.length) {
          var section = sectionFor(path);
          var $fs = $("<fieldset class=\"settings-section\"></fieldset>").append(
            $("<legend></legend>").text(section ? section.title : path || "(top level)"));
          if (section && section.help) { $fs.append($("<div class=\"muted small\"></div>").text(section.help)); }
          leaves.forEach(function (k) { $fs.append(renderField(path ? path + ":" + k : k, k, node[k])); });
          $form.append($fs);
        }
        children.forEach(function (k) { walk(node[k], path ? path + ":" + k : k); });
      })(obj, "");
      return $form;
    }

    /** Keys in the order of the catalog (fields or sections), unknown ones after them in their own order. */
    function byCatalog(keys, path, catalog) {
      function rank(k) {
        var full = (path ? path + ":" + k : k).toLowerCase();
        for (var i = 0; i < catalog.length; i++) {
          var p = catalog[i].path.toLowerCase();
          if (p === full || p.indexOf(full + ":") === 0) { return i; }
        }
        return catalog.length;
      }
      return keys.map(function (k, i) { return { k: k, r: rank(k), i: i }; })
        .sort(function (a, b) { return a.r - b.r || a.i - b.i; }).map(function (x) { return x.k; });
    }

    function isObject(v) { return v !== null && typeof v === "object" && !Array.isArray(v); }

    function renderField(path, key, value) {
      var f = fieldFor(path) || {};
      var kind = f.kind || (isSecretName(key) ? "secret" : typeof value === "boolean" ? "bool" : typeof value === "number" ? "number" : Array.isArray(value) ? "json" : "text");
      var original = value;
      var $field = $("<div class=\"settings-field\"></div>").attr({ "data-path": path, "data-kind": kind }).data("original", original);
      var $label = $("<label></label>").appendTo($field);
      var title = f.label || key;
      var $input;
      if (kind === "bool") {
        $label.addClass("check");
        $input = $("<input type=\"checkbox\">").prop("checked", value === true || String(value).toLowerCase() === "true");
        $label.append($input, $("<span></span>").text(title));
      } else {
        $label.append($("<span class=\"settings-name\"></span>").text(title));
        if (kind === "secret" || kind === "multiline-secret") {
          var isSet = value === doc.masked;
          $input = kind === "secret" ? $("<input type=\"password\" class=\"input\" autocomplete=\"new-password\">") : $("<textarea class=\"input\" rows=\"3\" spellcheck=\"false\"></textarea>");
          $input.attr("placeholder", isSet ? "Set (hidden). Type a new value to replace it." : "Not set");
          if (!isSet && value) { $input.val(value); } // typed in Raw JSON before switching here
          $label.append($input);
          if (isSet) { $field.append($("<label class=\"check small\"><input type=\"checkbox\" data-clear> Remove this secret</label>")); }
        } else if (kind === "choice") {
          $input = $("<select class=\"input\"></select>");
          var choices = (f.choices || []).slice();
          if (value !== null && value !== undefined && choices.indexOf(String(value)) < 0) { choices.unshift(String(value)); }
          choices.forEach(function (c) { $input.append($("<option></option>").val(c).text(c)); });
          $input.val(String(value));
          $label.append($input);
        } else {
          $input = $("<input type=\"text\" class=\"input\" spellcheck=\"false\">")
            .val(kind === "json" ? JSON.stringify(value) : value === null || value === undefined ? "" : String(value));
          if (kind === "integer" || kind === "number") { $input.attr("inputmode", "decimal"); }
          $label.append($input);
        }
      }
      $input.attr("data-value", "");
      $field.append($("<span class=\"muted small\"></span>").text((f.help ? f.help + " " : "") + (fieldFor(path) ? "" : "(" + path + ")")));
      return $field;
    }

    /** The edited document from the form; values keep their JSON type (a number stored as "12" stays a string). */
    function readForm() {
      var copy = JSON.parse(JSON.stringify(working)), errors = [];
      $detail.find(".settings-field").each(function () {
        var $l = $(this), path = String($l.data("path")), kind = $l.data("kind"), original = $l.data("original");
        var $input = $l.find("[data-value]");
        var value;
        if (kind === "bool") {
          value = $input.prop("checked");
          if (typeof original === "string") { value = value === (original.toLowerCase() === "true") ? original : value ? "true" : "false"; }
        } else if (kind === "secret" || kind === "multiline-secret") {
          var typed = $input.val();
          value = $l.find("[data-clear]").prop("checked") ? "" : typed !== "" ? typed : original;
        } else if (kind === "json") {
          try { value = JSON.parse($input.val()); } catch (e) { errors.push(path + ": not valid JSON"); return; }
        } else {
          var text = $.trim($input.val());
          if (kind === "integer" && !/^-?\d+$/.test(text)) { errors.push(($l.find(".settings-name").text() || path) + ": a whole number is needed"); return; }
          if (kind === "number" && !/^-?\d+(\.\d+)?$/.test(text)) { errors.push(($l.find(".settings-name").text() || path) + ": a number with a dot is needed (e.g. 38.62)"); return; }
          value = (kind === "integer" || kind === "number") && typeof original === "number" ? Number(text) : text;
        }
        set(copy, path.split(":"), value);
      });
      return { doc: copy, errors: errors };
    }

    function set(obj, parts, value) {
      var node = obj;
      parts.slice(0, -1).forEach(function (p) { node = node[p]; });
      node[parts[parts.length - 1]] = value;
    }

    function edited() {
      if (mode === "form") { return readForm(); }
      try {
        var parsed = JSON.parse($detail.find(".settings-json").val());
        return isObject(parsed) ? { doc: parsed, errors: [] } : { doc: null, errors: ["The settings must be one JSON object { … }."] };
      } catch (e) {
        return { doc: null, errors: ["Not valid JSON: " + e.message] };
      }
    }

    function isDirty() {
      if (!doc) { return false; }
      var e = edited();
      return !e.doc || JSON.stringify(e.doc) !== JSON.stringify(saved);
    }

    function leaves(obj, prefix, out) {
      out = out || {};
      Object.keys(obj || {}).forEach(function (k) {
        var p = prefix ? prefix + ":" + k : k;
        if (isObject(obj[k])) { leaves(obj[k], p, out); } else { out[p] = obj[k]; }
      });
      return out;
    }

    function show(path, v) {
      if (isSecretName(path.split(":").pop())) { return v === doc.masked ? "(set)" : v ? "(new value)" : "(empty)"; }
      return v === undefined ? "(none)" : JSON.stringify(v);
    }

    // -------------------------------------------------------------- toolbar

    $detail.on("click", "[data-mode]", function () {
      var next = $(this).data("mode");
      if (next === mode) { return; }
      var e = edited();
      if (e.errors.length) { Nadlan.dialog.showError("Settings", e.errors.join(" · ")); return; }
      working = e.doc;
      mode = next;
      renderDetail();
    });

    $detail.on("click", "[data-act=restart]", function () {
      M().restartService("app", "GreekPlot app", function (text) { report(esc(text)); }).then(function (done) { if (done) { loadRows(); } },
        function (err) { report(esc(err.message)); });
    });

    $detail.on("click", "[data-act=save]", function () {
      var e = edited();
      if (e.errors.length) { Nadlan.dialog.showError("Settings", e.errors.join(" · ")); return; }
      var before = leaves(saved || {}), after = leaves(e.doc);
      var changed = Object.keys($.extend({}, before, after)).filter(function (p) { return JSON.stringify(before[p]) !== JSON.stringify(after[p]); });
      if (!changed.length) { report("Nothing changed."); return; }
      var html = "<p>Save " + changed.length + " change(s) to <b>" + esc(doc.key) + "</b>?</p><table class=\"meta-table diff-table\"><thead><tr><th>Setting</th><th>Now</th><th>New</th></tr></thead><tbody>" +
        changed.map(function (p) {
          return "<tr><td>" + esc(p) + "</td><td>" + esc(show(p, before[p])) + "</td><td>" + esc(show(p, after[p])) + "</td></tr>";
        }).join("") + "</tbody></table><p class=\"muted\">They take effect when the app restarts.</p>";
      Nadlan.dialog.confirm("Save settings", html, "Save").then(function (ok) {
        if (!ok) { return; }
        Nadlan.api.put(API + "/" + encodeURIComponent(doc.key), { json: JSON.stringify(e.doc), updatedUtcMs: doc.updatedUtcMs }).then(function (r) {
          report("Saved " + esc(r.changed.length) + " change(s) to " + esc(doc.key) + ". <b>Restart the app to apply them</b> " +
            "<button type=\"button\" class=\"btn btn-small\" data-act=\"restart-now\">Restart now</button>");
          var key = doc.key;
          doc = null;
          loadRows(key);
        }, function (err) { Nadlan.dialog.showError("Not saved", err); });
      });
    });

    $status.on("click", "[data-act=restart-now]", function () { $detail.find("[data-act=restart]").trigger("click"); });

    loadRows();
  };
})(window, jQuery);
