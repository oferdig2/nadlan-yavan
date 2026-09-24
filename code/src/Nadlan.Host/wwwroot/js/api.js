// Thin JSON API wrapper. Every call resolves with the parsed body or rejects with { status, code, message }.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function request(method, url, body) {
    return new Promise(function (resolve, reject) {
      $.ajax({
        method: method,
        url: url,
        contentType: body === undefined ? undefined : "application/json",
        data: body === undefined ? undefined : JSON.stringify(body),
        dataType: "json"
      }).done(resolve).fail(function (xhr) {
        // Extra fields in the error body (e.g. overlaps, existingParcelId) stay available to the caller.
        var payload = xhr.responseJSON || {};
        reject($.extend({}, payload, {
          status: xhr.status,
          code: payload.error || "HTTP_" + xhr.status,
          message: payload.message || xhr.statusText || "Request failed"
        }));
      });
    });
  }

  Nadlan.api = {
    // traditional=true sends arrays as ids=1&ids=2, which ASP.NET binds to long[].
    // null/undefined/"" and empty arrays are dropped so "no filter" really means no filter.
    get: function (url, query) {
      var clean = {};
      $.each(query || {}, function (key, value) {
        if (value === null || value === undefined || value === "" || ($.isArray(value) && value.length === 0)) { return; }
        clean[key] = value;
      });
      var qs = $.param(clean, true);
      return request("GET", qs ? url + "?" + qs : url);
    },
    post: function (url, body) { return request("POST", url, body); },
    put: function (url, body) { return request("PUT", url, body); },
    del: function (url) { return request("DELETE", url); }
  };
})(window, jQuery);
