// ResourceAccessEditor (Appendix 2 §27): explicit grants of one user on single objects -
// "Lawyer X may VIEW_ASSET on Asset 123". Lists existing grants with Revoke, and adds new ones through a type-ahead.
// A Portfolio grant also reaches every Asset in the Portfolio. Which files show is decided by the user's role.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  var TYPES = ["Asset", "Portfolio", "Parcel", "Contact"];

  function when(utc) {
    if (!utc) { return ""; }
    var d = new Date(utc.endsWith("Z") ? utc : utc + "Z");
    return d.toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" });
  }

  /** @param {number} userId */
  function open(userId) {
    var esc = Nadlan.format.escapeHtml;
    return Promise.all([Nadlan.api.get("/api/admin/users/" + userId), Nadlan.api.get("/api/admin/permissions")]).then(function (loaded) {
      var user = loaded[0];
      var grantable = loaded[1].filter(function (p) { return p.scope === "Resource"; });
      var chosen = null;

      var $body = $(
        "<div class=\"form access-editor\">" +
          "<div class=\"muted\">" + esc(user.displayName) + " (" + esc(user.roleName) + ") sees these objects in addition to what the role allows. " +
            "Files inside them follow the role's file categories.</div>" +
          "<table class=\"meta-table\"><thead><tr><th>Object</th><th>Permission</th><th>Expires</th><th>Granted by</th><th></th></tr></thead>" +
            "<tbody><tr><td colspan=\"5\" class=\"muted\">Loading…</td></tr></tbody></table>" +
          "<fieldset class=\"geometry-box\"><legend>Grant access</legend>" +
            "<div class=\"form-row\">" +
              "<label class=\"narrow\" style=\"flex-basis:120px\">Type<select class=\"input\" name=\"type\">" +
                TYPES.map(function (t) { return "<option>" + t + "</option>"; }).join("") + "</select></label>" +
              "<label>Object<input class=\"input\" name=\"object\" placeholder=\"Type an id, KAEK or name…\"></label>" +
            "</div>" +
            "<div class=\"form-row\">" +
              "<label>Permission<select class=\"input\" name=\"permission\"></select></label>" +
              "<label>Expires (optional)<input class=\"input\" name=\"expires\" type=\"date\"></label>" +
            "</div>" +
            "<div class=\"chosen muted\">Nothing chosen yet.</div>" +
            "<div class=\"btn-row\"><button type=\"button\" class=\"btn btn-primary\" data-act=\"grant\">Grant</button></div>" +
          "</fieldset>" +
        "</div>");

      var d = Nadlan.dialog.open({
        title: "Access - " + user.email,
        content: $body,
        width: 700,
        modal: false,
        buttons: [{ text: "Close", click: function () { d.close(); } }]
      });

      function type() { return $body.find("[name=type]").val(); }

      function renderPermissions() {
        $body.find("[name=permission]").html(grantable.filter(function (p) { return p.resourceType === type(); }).map(function (p) {
          return "<option value=\"" + esc(p.code) + "\">" + esc(p.name) + "</option>";
        }).join(""));
      }

      function setChosen(item) {
        chosen = item;
        $body.find(".chosen").html(item ? "<strong>" + esc(item.label) + "</strong>" + (item.detail ? " <span class=\"muted\">" + esc(item.detail) + "</span>" : "")
                                        : "Nothing chosen yet.");
      }

      Nadlan.initializeEntitySelector($body.find("[name=object]"), {
        source: {
          search: function (term) { return Nadlan.api.get("/api/admin/resources", { type: type(), q: term }); },
          id: function (r) { return r.id; },
          label: function (r) { return r.label; },
          detail: function (r) { return r.detail || ""; }
        },
        clearOnSelect: true,
        onSelect: setChosen
      });

      $body.on("change", "[name=type]", function () { renderPermissions(); setChosen(null); });

      function load() {
        Nadlan.api.get("/api/admin/users/" + userId + "/access").then(function (grants) {
          $body.find("tbody").html(grants.map(function (g) {
            var expired = g.expiresUtc && new Date(g.expiresUtc.endsWith("Z") ? g.expiresUtc : g.expiresUtc + "Z") < new Date();
            return "<tr class=\"" + (expired ? "inactive" : "") + "\">" +
              "<td>" + esc(g.resourceLabel || g.resourceType + " #" + g.resourceId) + "</td>" +
              "<td>" + esc(g.permissionName) + "</td>" +
              "<td>" + (g.expiresUtc ? esc(when(g.expiresUtc)) + (expired ? " <span class=\"muted\">(expired)</span>" : "") : "<span class=\"muted\">never</span>") + "</td>" +
              "<td>" + esc(g.grantedByName || "") + " <span class=\"muted\">" + esc(when(g.createdUtc)) + "</span></td>" +
              "<td><button type=\"button\" class=\"btn btn-small\" data-revoke=\"" + g.resourceAccessId + "\">Revoke</button></td></tr>";
          }).join("") || "<tr><td colspan=\"5\" class=\"muted\">No explicit access - only what the role allows.</td></tr>");
        }, function (err) { d.showError(err.message); });
      }

      $body.on("click", "[data-act=grant]", function () {
        if (!chosen) { d.showError("Choose the object first."); return; }
        var expires = $body.find("[name=expires]").val();
        d.busy(true);
        Nadlan.api.post("/api/admin/users/" + userId + "/access", {
          resourceType: type(), resourceId: chosen.id, permissionCode: $body.find("[name=permission]").val(),
          // End of the chosen day, UTC.
          expiresUtc: expires ? expires + "T23:59:59Z" : null
        }).then(function () {
          d.busy(false);
          d.showError("");
          setChosen(null);
          load();
        }, function (err) { d.busy(false); d.showError(err.message); });
      });

      $body.on("click", "[data-revoke]", function () {
        var id = Number($(this).data("revoke"));
        Nadlan.api.del("/api/admin/users/" + userId + "/access/" + id).then(load, function (err) { d.showError(err.message); });
      });

      renderPermissions();
      load();
      return d;
    });
  }

  Nadlan.resourceAccessEditor = { open: open };
})(window, jQuery);
