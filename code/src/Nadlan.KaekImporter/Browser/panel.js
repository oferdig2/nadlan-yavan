// Injected into gis.ktimanet.gr/gis/map by NadlanKaekImporter. Draws the control panel and the progress
// overlay; all the work happens in the .NET tool, which calls window.NadlanPanel.* and receives
// window.nadlanStart(areaId) / window.nadlanStop() / window.nadlanClickMode(on, areaId) / window.nadlanConnect() and
// window.nadlanState() -> { areas: [{ id, name }], userName, offline (null, or why nothing will be saved), needsConnect }
// (all exposed by Playwright). The state is asked for when the panel is built, so a reload never shows stale data.
(function () {
  "use strict";
  if (window.top !== window || !/\/gis\/map/i.test(location.pathname) || window.NadlanPanel) { return; }

  var COLORS = {
    created: { stroke: "#16a34a", fill: "rgba(22,163,74,0.25)" },
    exists: { stroke: "#2563eb", fill: "rgba(37,99,235,0.18)" },
    rejected: { stroke: "#dc2626", fill: "rgba(220,38,38,0.25)" },
    road: { stroke: "#9ca3af", fill: "rgba(156,163,175,0.12)" },
    found: { stroke: "#7c3aed", fill: "rgba(124,58,237,0.22)" } // offline: found but not saved
  };
  var offline = null;        // why nothing will be saved, or null
  var needsConnect = false;  // Nadlan reachable, but no (valid) token yet
  var running = false;
  var shapes = [];      // { rings: [[[x,y],...]], kind }
  var sweep = null;     // { left, right, top, bottom }
  var probe = null;     // [x, y]
  var svg, panel;

  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" }[c]; }); }

  function build() {
    var style = document.createElement("style");
    style.textContent =
      "#nadlan-panel{position:fixed;right:12px;top:120px;width:320px;z-index:100000;background:#fff;color:#0f172a;" +
      "border:1px solid #cbd5e1;border-radius:10px;box-shadow:0 8px 24px rgba(15,23,42,.25);font:13px/1.4 system-ui,Segoe UI,Arial,sans-serif}" +
      "#nadlan-panel header{padding:8px 10px;font-weight:600;border-bottom:1px solid #e2e8f0;display:flex;justify-content:space-between;align-items:center}" +
      "#nadlan-panel header button{border:none;background:none;cursor:pointer;font-size:14px;color:#64748b}" +
      "#nadlan-panel .body{padding:8px 10px}" +
      "#nadlan-panel label{display:block;margin-bottom:6px}" +
      "#nadlan-panel [hidden]{display:none!important}" +
      "#nadlan-panel label.check{display:flex;gap:6px;align-items:center;margin:4px 0 0}" +
      "#nadlan-panel select{width:100%;padding:4px;margin-top:2px}" +
      "#nadlan-panel .actions{display:flex;gap:6px;margin:8px 0}" +
      "#nadlan-panel .actions button{flex:1;padding:6px;border-radius:6px;border:1px solid #1d4ed8;cursor:pointer;font-weight:600}" +
      "#nadlan-start{background:#1d4ed8;color:#fff}#nadlan-start:disabled{opacity:.5;cursor:default}" +
      "#nadlan-stop{background:#fff;color:#b91c1c;border-color:#b91c1c!important}#nadlan-stop:disabled{opacity:.4;cursor:default}" +
      "#nadlan-panel .status{color:#334155;min-height:18px}" +
      "#nadlan-panel .counts{display:grid;grid-template-columns:1fr 1fr;gap:2px 10px;margin:6px 0;font-size:12px}" +
      "#nadlan-panel .sw{display:inline-block;width:10px;height:10px;border:2px solid;margin-right:5px;vertical-align:-1px}" +
      "#nadlan-log{max-height:180px;overflow:auto;font-size:11px;border-top:1px solid #e2e8f0;padding-top:4px;margin-top:4px}" +
      "#nadlan-log div{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
      "#nadlan-log .err{color:#b91c1c}#nadlan-log .warn{color:#b45309}#nadlan-log .ok{color:#15803d}" +
      "#nadlan-panel.min .body{display:none}" +
      "#nadlan-offline{margin:-8px -10px 8px;padding:8px 10px;background:#fef3c7;color:#92400e;border-bottom:1px solid #fcd34d;font-size:12px}" +
      "#nadlan-connect{margin:-8px -10px 8px;padding:10px;background:#eff6ff;color:#1e3a8a;border-bottom:1px solid #bfdbfe;font-size:12px}" +
      "#nadlan-connect button{display:block;width:100%;margin:8px 0 6px;padding:7px;border-radius:6px;border:1px solid #1d4ed8;" +
      "background:#1d4ed8;color:#fff;font-weight:600;cursor:pointer}#nadlan-connect button:disabled{opacity:.6;cursor:default}" +
      "#nadlan-user{font-weight:400;color:#64748b;font-size:12px;margin-left:6px}";
    document.head.appendChild(style);

    panel = document.createElement("div");
    panel.id = "nadlan-panel";
    panel.innerHTML =
      "<header><span>GreekPlot - import parcels<span id=\"nadlan-user\"></span></span><button type=\"button\" id=\"nadlan-min\" title=\"Minimise\">_</button></header>" +
      "<div class=\"body\">" +
      "<div id=\"nadlan-offline\" hidden></div>" +
      "<div id=\"nadlan-connect\" hidden><b>Not connected to GreekPlot.</b> Parcels can only be saved once the importer is signed in." +
        "<button type=\"button\" id=\"nadlan-connect-btn\">Connect to GreekPlot</button>" +
        "Opens GreekPlot in a new tab: sign in with your email and password, then click Connect. The tab closes by itself and " +
        "the importer remembers the connection. Your user must be allowed to create Parcels.</div>" +
      "<label id=\"nadlan-area-label\">Geographic area for new parcels<select id=\"nadlan-area\"><option value=\"\">(none)</option></select></label>" +
      "<label class=\"check\"><input type=\"checkbox\" id=\"nadlan-click\"> Import each parcel I click on the map</label>" +
      "<div class=\"actions\"><button type=\"button\" id=\"nadlan-start\">Acquire polygons</button>" +
      "<button type=\"button\" id=\"nadlan-stop\" disabled>Stop</button></div>" +
      "<div class=\"status\" id=\"nadlan-status\">Zoom in until the yellow parcel lines show, then click Acquire polygons - or tick the box and click parcels yourself.</div>" +
      "<div class=\"counts\" id=\"nadlan-counts\"></div>" +
      "<div id=\"nadlan-log\"></div></div>";
    document.body.appendChild(panel);

    document.getElementById("nadlan-min").onclick = function () { panel.classList.toggle("min"); };
    document.getElementById("nadlan-start").onclick = function () {
      var areaId = document.getElementById("nadlan-area").value;
      setRunning(true);
      window.nadlanStart(areaId).then(function (error) {
        if (error) { setRunning(false); status(error); log(error, "err"); }
      }, function (e) { setRunning(false); status("Could not start: " + e.message); });
    };
    document.getElementById("nadlan-click").onchange = function () {
      var box = this, on = box.checked;
      box.disabled = true;
      window.nadlanClickMode(on, document.getElementById("nadlan-area").value).then(function (error) {
        box.disabled = false;
        if (error) { box.checked = !on; status(error); log(error, "err"); return; }
        setClickMode(on);
      }, function (e) { box.disabled = false; box.checked = !on; status("Could not switch: " + e.message); });
    };
    document.getElementById("nadlan-stop").onclick = function () {
      document.getElementById("nadlan-stop").disabled = true;
      status("Stopping after the current step...");
      window.nadlanStop();
    };
    document.getElementById("nadlan-connect-btn").onclick = function () {
      var btn = this;
      btn.disabled = true;
      btn.textContent = "Waiting for the GreekPlot tab...";
      status("Sign in in the GreekPlot tab and click Connect.");
      window.nadlanConnect().then(function (error) {
        btn.disabled = false;
        btn.textContent = "Connect to GreekPlot";
        if (error) { status(error); log(error, "err"); } else { status("Connected. Zoom in and click Acquire polygons - or tick the box and click parcels."); }
        refreshState();
      }, function (e) { btn.disabled = false; btn.textContent = "Connect to GreekPlot"; status("Could not connect: " + e.message); });
    };
    counts({});
    refreshState();

    svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("id", "nadlan-overlay");
    svg.style.cssText = "position:fixed;pointer-events:none;z-index:99999;left:0;top:0";
    document.body.appendChild(svg);
    setInterval(render, 400);
  }

  function mapState() {
    var canvas = document.getElementById("KT.KTWebMap.0.ContainerDrawingCanvas");
    if (!canvas || typeof MyMap === "undefined") { return null; }
    var r = canvas.getBoundingClientRect();
    var e = MyMap.MapExtents();
    if (!r.width || !e) { return null; }
    return { r: r, e: e };
  }

  function render() {
    var m = mapState();
    if (!m) { return; }
    var r = m.r, e = m.e;
    svg.style.left = r.left + "px"; svg.style.top = r.top + "px";
    svg.setAttribute("width", r.width); svg.setAttribute("height", r.height);
    var sx = r.width / (e.right - e.left), sy = r.height / (e.top - e.bottom);
    function px(p) { return ((p[0] - e.left) * sx).toFixed(1) + "," + ((e.top - p[1]) * sy).toFixed(1); }
    var html = "";
    if (sweep) {
      html += "<rect x=\"" + ((sweep.left - e.left) * sx) + "\" y=\"" + ((e.top - sweep.top) * sy) + "\" width=\"" +
        ((sweep.right - sweep.left) * sx) + "\" height=\"" + ((sweep.top - sweep.bottom) * sy) +
        "\" fill=\"none\" stroke=\"#f59e0b\" stroke-width=\"2\" stroke-dasharray=\"8 6\"/>";
    }
    shapes.forEach(function (s) {
      var c = COLORS[s.kind] || COLORS.road;
      var d = s.rings.map(function (ring) { return "M" + ring.map(px).join("L") + "Z"; }).join("");
      html += "<path d=\"" + d + "\" fill=\"" + c.fill + "\" fill-rule=\"evenodd\" stroke=\"" + c.stroke + "\" stroke-width=\"2\"/>";
    });
    if (probe) {
      var p = px(probe).split(",");
      html += "<circle cx=\"" + p[0] + "\" cy=\"" + p[1] + "\" r=\"5\" fill=\"#f59e0b\" stroke=\"#fff\" stroke-width=\"2\"/>";
    }
    svg.innerHTML = html;
  }

  function setRunning(on) {
    running = on;
    document.getElementById("nadlan-start").disabled = on || needsConnect;
    document.getElementById("nadlan-stop").disabled = !on;
    document.getElementById("nadlan-area").disabled = on || needsConnect;
    document.getElementById("nadlan-click").disabled = on || needsConnect;
  }

  function setClickMode(on) {
    document.getElementById("nadlan-start").disabled = on || needsConnect;
    document.getElementById("nadlan-area").disabled = on || needsConnect;
    if (on) { status("Click parcels on the map; each one you click is imported."); }
  }

  // Asks the tool for the connection state (at build, after Connect, after Nadlan refused the token).
  function refreshState() {
    if (typeof window.nadlanState !== "function") { return; }
    window.nadlanState().then(applyState, function () { /* tool gone */ });
  }

  function applyState(s) {
    offline = s.offline || null;
    needsConnect = !!s.needsConnect && !offline;

    var select = document.getElementById("nadlan-area");
    var chosen = select.value;
    select.innerHTML = "<option value=\"\">(none)</option>" + (s.areas || []).map(function (a) {
      return "<option value=\"" + a.id + "\"" + (String(a.id) === chosen ? " selected" : "") + ">" + esc(a.name) + "</option>";
    }).join("");

    var off = document.getElementById("nadlan-offline");
    off.hidden = !offline;
    off.innerHTML = offline ? "<b>Offline - nothing will be saved.</b> " + esc(offline) +
      " The whole process runs and parcels are shown and listed in the report, but they are not stored in GreekPlot." : "";
    document.getElementById("nadlan-connect").hidden = !needsConnect;
    document.getElementById("nadlan-area-label").hidden = !!offline || needsConnect;
    document.getElementById("nadlan-user").textContent = s.userName && !needsConnect && !offline ? "as " + s.userName : "";

    var click = document.getElementById("nadlan-click");
    if (needsConnect && click.checked) { click.checked = false; }
    setRunning(running);
    if (!running) { setClickMode(click.checked); }
    counts({});
  }

  function status(text) { document.getElementById("nadlan-status").textContent = text; }

  function log(text, kind) {
    var box = document.getElementById("nadlan-log");
    var line = document.createElement("div");
    line.className = kind || "";
    line.title = text;
    line.textContent = new Date().toLocaleTimeString() + "  " + text;
    box.insertBefore(line, box.firstChild);
    while (box.childNodes.length > 300) { box.removeChild(box.lastChild); }
  }

  function counts(c) {
    function sw(kind) { var k = COLORS[kind]; return "<span class=\"sw\" style=\"border-color:" + k.stroke + ";background:" + k.fill + "\"></span>"; }
    document.getElementById("nadlan-counts").innerHTML =
      (offline
        ? "<div>" + sw("found") + "Found (not saved): <b>" + (c.found || 0) + "</b></div>"
        : "<div>" + sw("created") + "Created: <b>" + (c.created || 0) + "</b></div>" +
          "<div>" + sw("exists") + "Already in GreekPlot: <b>" + (c.exists || 0) + "</b></div>" +
          "<div>" + sw("rejected") + "Rejected: <b>" + (c.rejected || 0) + "</b></div>") +
      "<div>" + sw("road") + "Roads skipped: <b>" + (c.roads || 0) + "</b></div>" +
      "<div>Requests to site: <b>" + (c.requests || 0) + "</b></div>" +
      "<div>Areas: <b>" + (c.areasDone || 0) + "</b> / " + (c.areasTotal || 0) + "</div>";
  }

  // The enclosed areas of the parcel-lines layer ("ΚΤΗΜΑΤΟΓΡΑΦΗΣΗ", yellow lines on a transparent image) in the
  // current view. Each area is one parcel, road or the sea. For each: the point farthest from any line, and
  // sample points every sampleStepMetres with their distance to the nearest line. Coordinates are EGSA87 metres.
  // Returns { error } when the layer is not shown, or { retry: true } while its tiles are still loading.
  function findAreas(sampleStepMetres) {
    var canvas = document.getElementById("KT.KTWebMap.0.ContainerDrawingCanvas");
    var layer = (MyMap.ReturnLayers() || []).filter(function (l) { return l.visible && /ΚΤΗΜΑΤΟΓΡΑΦ/i.test(l.LayerName); })[0];
    if (!canvas || !layer) { return { error: "The parcel lines layer is not shown on the map. Zoom in until the yellow lines appear." }; }
    var box = canvas.getBoundingClientRect();
    var W = Math.round(box.width), H = Math.round(box.height);
    var tiles = [].slice.call(document.querySelectorAll("img")).filter(function (t) {
      if (t.id.indexOf("KT.KTWebMap.0.") !== 0 || t.src.indexOf("Layer=" + layer.LayerSource) < 0 || !t.offsetParent) { return false; }
      var r = t.getBoundingClientRect();
      return r.right > box.left && r.left < box.right && r.bottom > box.top && r.top < box.bottom;
    });
    if (!tiles.length) { return { error: "Zoom in until the yellow parcel lines show, then try again." }; }
    if (tiles.some(function (t) { return !t.complete || !t.naturalWidth; })) { return { retry: true }; }

    var cv = document.createElement("canvas"); cv.width = W; cv.height = H;
    var g = cv.getContext("2d");
    tiles.forEach(function (t) { var r = t.getBoundingClientRect(); g.drawImage(t, r.left - box.left, r.top - box.top, r.width, r.height); });
    var px = g.getImageData(0, 0, W, H).data;
    var N = W * H, line = new Uint8Array(N), i, x, y;
    for (i = 0; i < N; i++) { line[i] = px[i * 4 + 3] > 40 ? 1 : 0; }

    // Distance to the nearest line or view edge (chamfer 3-4, so /3 = pixels).
    var dist = new Float32Array(N);
    for (y = 0; y < H; y++) {
      for (x = 0; x < W; x++) {
        i = y * W + x;
        dist[i] = line[i] ? 0 : (x === 0 || y === 0 || x === W - 1 || y === H - 1) ? 3 : 1e9;
      }
    }
    function relax(i, j, cost) { if (dist[j] + cost < dist[i]) { dist[i] = dist[j] + cost; } }
    for (y = 1; y < H - 1; y++) {
      for (x = 1; x < W - 1; x++) { i = y * W + x; relax(i, i - 1, 3); relax(i, i - W, 3); relax(i, i - W - 1, 4); relax(i, i - W + 1, 4); }
    }
    for (y = H - 2; y > 0; y--) {
      for (x = W - 2; x > 0; x--) { i = y * W + x; relax(i, i + 1, 3); relax(i, i + W, 3); relax(i, i + W + 1, 4); relax(i, i + W - 1, 4); }
    }

    // Areas = 4-connected runs of non-line pixels.
    var label = new Int32Array(N), stack = new Int32Array(N), areas = [], n = 0;
    for (var s = 0; s < N; s++) {
      if (line[s] || label[s]) { continue; }
      n++;
      var top = 0, size = 0, best = s;
      stack[top++] = s; label[s] = n;
      while (top) {
        i = stack[--top]; size++;
        if (dist[i] > dist[best]) { best = i; }
        x = i % W;
        if (x > 0 && !line[i - 1] && !label[i - 1]) { label[i - 1] = n; stack[top++] = i - 1; }
        if (x < W - 1 && !line[i + 1] && !label[i + 1]) { label[i + 1] = n; stack[top++] = i + 1; }
        if (i >= W && !line[i - W] && !label[i - W]) { label[i - W] = n; stack[top++] = i - W; }
        if (i < N - W && !line[i + W] && !label[i + W]) { label[i + W] = n; stack[top++] = i + W; }
      }
      areas.push({ size: size, best: best, samples: [] });
    }

    var e = MyMap.MapExtents();
    var mx = (e.right - e.left) / W, my = (e.top - e.bottom) / H;
    function point(i) {
      return [+(e.left + (i % W + 0.5) * mx).toFixed(2), +(e.top - ((i / W | 0) + 0.5) * my).toFixed(2), +(dist[i] / 3 * mx).toFixed(2)];
    }
    var step = Math.max(2, Math.round(sampleStepMetres / mx));
    for (y = step >> 1; y < H; y += step) {
      for (x = step >> 1; x < W; x += step) { i = y * W + x; if (!line[i]) { areas[label[i] - 1].samples.push(point(i)); } }
    }

    return {
      extent: { left: e.left, right: e.right, top: e.top, bottom: e.bottom },
      metresPerPixel: mx,
      // Specks between nearly touching lines are not areas.
      areas: areas.filter(function (a) { return a.size >= 20 && dist[a.best] >= 3; })
        .map(function (a) { return { size: a.size, point: point(a.best), samples: a.samples }; })
    };
  }

  window.NadlanPanel = {
    status: status,
    log: log,
    counts: counts,
    setRunning: setRunning,
    findAreas: findAreas,
    setSweep: function (extent) { sweep = extent; shapes = []; probe = null; },
    setProbe: function (x, y) { probe = [x, y]; },
    addShape: function (rings, kind) { shapes.push({ rings: rings, kind: kind }); },
    finished: function (text) { probe = null; setRunning(false); status(text); log(text, "ok"); },
    refreshState: refreshState
  };

  if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", build); } else { build(); }
})();
