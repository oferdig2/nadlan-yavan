// Injected into gis.ktimanet.gr/gis/map by NadlanKaekImporter. Draws the control panel and the progress
// overlay; all the work happens in the .NET tool, which calls window.NadlanPanel.* and receives
// window.nadlanStart(areaId) / window.nadlanStop() (exposed by Playwright).
// window.__nadlanAreas ([{ id, name }]) is set by the tool just before this script.
(function () {
  "use strict";
  if (window.top !== window || !/\/gis\/map/i.test(location.pathname) || window.NadlanPanel) { return; }

  var COLORS = {
    created: { stroke: "#16a34a", fill: "rgba(22,163,74,0.25)" },
    exists: { stroke: "#2563eb", fill: "rgba(37,99,235,0.18)" },
    rejected: { stroke: "#dc2626", fill: "rgba(220,38,38,0.25)" },
    road: { stroke: "#9ca3af", fill: "rgba(156,163,175,0.12)" }
  };
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
      "#nadlan-panel.min .body{display:none}";
    document.head.appendChild(style);

    var areas = window.__nadlanAreas || [];
    panel = document.createElement("div");
    panel.id = "nadlan-panel";
    panel.innerHTML =
      "<header><span>Nadlan - import parcels</span><button type=\"button\" id=\"nadlan-min\" title=\"Minimise\">_</button></header>" +
      "<div class=\"body\">" +
      "<label>Geographic area for new parcels<select id=\"nadlan-area\"><option value=\"\">(none)</option>" +
      areas.map(function (a) { return "<option value=\"" + a.id + "\">" + esc(a.name) + "</option>"; }).join("") +
      "</select></label>" +
      "<div class=\"actions\"><button type=\"button\" id=\"nadlan-start\">Acquire polygons</button>" +
      "<button type=\"button\" id=\"nadlan-stop\" disabled>Stop</button></div>" +
      "<div class=\"status\" id=\"nadlan-status\">Zoom to the area to import, then click Acquire polygons.</div>" +
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
    document.getElementById("nadlan-stop").onclick = function () {
      document.getElementById("nadlan-stop").disabled = true;
      status("Stopping after the current step...");
      window.nadlanStop();
    };
    counts({});

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

  function setRunning(running) {
    document.getElementById("nadlan-start").disabled = running;
    document.getElementById("nadlan-stop").disabled = !running;
    document.getElementById("nadlan-area").disabled = running;
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
      "<div>" + sw("created") + "Created: <b>" + (c.created || 0) + "</b></div>" +
      "<div>" + sw("exists") + "Already in Nadlan: <b>" + (c.exists || 0) + "</b></div>" +
      "<div>" + sw("rejected") + "Rejected: <b>" + (c.rejected || 0) + "</b></div>" +
      "<div>" + sw("road") + "Roads skipped: <b>" + (c.roads || 0) + "</b></div>" +
      "<div>Requests to site: <b>" + (c.requests || 0) + "</b></div>" +
      "<div>Points: <b>" + (c.pointsDone || 0) + "</b> / " + (c.pointsTotal || 0) + "</div>";
  }

  window.NadlanPanel = {
    status: status,
    log: log,
    counts: counts,
    setRunning: setRunning,
    setSweep: function (extent) { sweep = extent; shapes = []; probe = null; },
    setProbe: function (x, y) { probe = [x, y]; },
    addShape: function (rings, kind) { shapes.push({ rings: rings, kind: kind }); },
    finished: function (text) { probe = null; setRunning(false); status(text); log(text, "ok"); }
  };

  if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", build); } else { build(); }
})();
