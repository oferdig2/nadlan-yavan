// The contractor page's calls to /api/work-link. Not api.js: that one sends a 401 to the staff sign-in page. The link's
// token (after "#" in the address) goes in the X-Work-Link header; X-Nadlan-Request is the app's CSRF header, required
// on every write. Resolves with the body, rejects with { status, code, message, ... }.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};
  var token = (window.location.hash || "").replace(/^#/, "");

  function request(method, path, body) {
    return new Promise(function (resolve, reject) {
      $.ajax({
        method: method,
        url: "/api/work-link" + path,
        contentType: body === undefined ? undefined : "application/json",
        headers: { "X-Work-Link": token, "X-Nadlan-Request": "1" },
        data: body === undefined ? undefined : JSON.stringify(body),
        dataType: "json",
        cache: false
      }).done(resolve).fail(function (xhr) {
        var payload = xhr.responseJSON || {};
        reject($.extend({}, payload, { status: xhr.status, code: payload.error || "HTTP_" + xhr.status, message: payload.message || "" }));
      });
    });
  }

  Nadlan.workLinkApi = {
    hasToken: function () { return token.length > 0; },
    get: function (path) { return request("GET", path); },
    post: function (path, body) { return request("POST", path, body || {}); }
  };
})(window, jQuery);
