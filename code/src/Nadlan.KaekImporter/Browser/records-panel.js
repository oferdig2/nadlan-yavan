// Injected by NadlanKaekImporter into the Cadastre's lawyer service (ktimatologio.gov.gr/Professionals/...), the tab opened by
// "Inspect KAEK" in the polygon panel. Draws the records panel ("owners & sales"); all the work happens in the .NET tool, which
// sends the requests from this tab (the lawyer's own session, signed in by hand) and calls window.NadlanRecords.*.
// The tool exposes (Playwright): nadlanRecordsState() -> { areas, userName, isAdmin, offline, needsConnect, running, status,
// progress }, nadlanRecordsSignedIn(bool), nadlanRecordsStart(json) -> "" or why not, nadlanRecordsStop().
// This script never sends a request to the site itself and never reads the page on screen except to see whether the
// lawyer is signed in (the Logout link). Drivable from a --debug-port session: NadlanRecords.start({ kaeks: "..." }) or
// NadlanRecords.start({ areaId: 12 }), NadlanRecords.stop(), NadlanRecords.state(), NadlanRecords.logLines().
(function () {
  "use strict";
  if (window.top !== window || !/^(www\.)?ktimatologio\.gov\.gr$/i.test(location.hostname) ||
    !/^\/Professionals\//i.test(location.pathname) || window.NadlanRecords) { return; }

  var LOGOUT = "a[href=\"/Professionals/Account/Logout\"]";
  var state = {};
  var signedIn = null;
  var running = false;
  var panel;
  var lines = [];

  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" }[c]; }); }
  function $(id) { return document.getElementById(id); }

  // The same rule as the tool (KaekList): commas, semicolons, spaces or new lines; only 12 digits count.
  function parseKaeks(text) {
    var seen = {}, kaeks = [], ignored = 0, duplicates = 0;
    String(text || "").split(/[,;\s]+/).forEach(function (t) {
      if (!t) { return; }
      if (!/^\d{12}$/.test(t)) { ignored++; } else if (seen[t]) { duplicates++; } else { seen[t] = 1; kaeks.push(t); }
    });
    return { kaeks: kaeks, ignored: ignored, duplicates: duplicates };
  }

  function build() {
    var style = document.createElement("style");
    style.textContent =
      "#nadlan-rec{position:fixed;right:12px;top:90px;width:340px;z-index:2147483000;background:#fff;color:#0f172a;" +
      "border:1px solid #cbd5e1;border-radius:10px;box-shadow:0 8px 24px rgba(15,23,42,.25);font:13px/1.4 system-ui,Segoe UI,Arial,sans-serif;text-align:left}" +
      "#nadlan-rec header{padding:8px 10px;font-weight:600;border-bottom:1px solid #e2e8f0;display:flex;justify-content:space-between;align-items:center}" +
      "#nadlan-rec header button{border:none;background:none;cursor:pointer;font-size:14px;color:#64748b}" +
      "#nadlan-rec .body{padding:8px 10px}#nadlan-rec [hidden]{display:none!important}" +
      "#nadlan-rec label{display:block;margin:4px 0}#nadlan-rec label.opt{display:flex;gap:6px;align-items:center;font-weight:600}" +
      "#nadlan-rec select,#nadlan-rec textarea{width:100%;box-sizing:border-box;padding:4px;margin-top:2px;font:12px/1.3 Consolas,monospace}" +
      "#nadlan-rec textarea{height:64px;resize:vertical}#nadlan-rec .missing{outline:2px solid #dc2626;background:#fef2f2}" +
      "#nadlan-rec .hint{color:#64748b;font-size:11px}" +
      "#nadlan-rec .actions{display:flex;gap:6px;margin:8px 0}" +
      "#nadlan-rec .actions button{flex:1;padding:6px;border-radius:6px;border:1px solid #1d4ed8;cursor:pointer;font-weight:600}" +
      "#nadlan-rec-start{background:#1d4ed8;color:#fff}#nadlan-rec-start:disabled{opacity:.5;cursor:default}" +
      "#nadlan-rec-stop{background:#fff;color:#b91c1c;border-color:#b91c1c!important}#nadlan-rec-stop:disabled{opacity:.4;cursor:default}" +
      "#nadlan-rec .status{color:#334155;min-height:18px}" +
      "#nadlan-rec .counts{display:grid;grid-template-columns:1fr 1fr;gap:2px 10px;margin:6px 0;font-size:12px}" +
      "#nadlan-rec-log{max-height:180px;overflow:auto;font-size:11px;border-top:1px solid #e2e8f0;padding-top:4px;margin-top:4px}" +
      "#nadlan-rec-log div{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
      "#nadlan-rec-log .err{color:#b91c1c}#nadlan-rec-log .warn{color:#b45309}#nadlan-rec-log .ok{color:#15803d}" +
      "#nadlan-rec.min .body{display:none}" +
      "#nadlan-rec .notice{margin:-8px -10px 8px;padding:8px 10px;font-size:12px;border-bottom:1px solid}" +
      "#nadlan-rec .notice.signin{background:#eff6ff;color:#1e3a8a;border-color:#bfdbfe}" +
      "#nadlan-rec .notice.off{background:#fef3c7;color:#92400e;border-color:#fcd34d}" +
      "#nadlan-rec-user{font-weight:400;color:#64748b;font-size:12px;margin-left:6px}" +
      "#nadlan-rec footer{color:#64748b;font-size:11px;border-top:1px solid #e2e8f0;margin-top:6px;padding-top:4px}";
    document.head.appendChild(style);

    panel = document.createElement("div");
    panel.id = "nadlan-rec";
    panel.innerHTML =
      "<header><span>GreekPlot - owners &amp; sales<span id=\"nadlan-rec-user\"></span></span>" +
        "<button type=\"button\" id=\"nadlan-rec-min\" title=\"Minimise\">_</button></header>" +
      "<div class=\"body\">" +
      "<div class=\"notice signin\" id=\"nadlan-rec-signin\" hidden><b>Sign in first.</b> Use the lawyer's own sign-in on this site " +
        "(Είσοδος, then the Bar Association portal). GreekPlot never sees or keeps the password. This panel waits until you are signed in.</div>" +
      "<div class=\"notice off\" id=\"nadlan-rec-off\" hidden></div>" +
      "<div id=\"nadlan-rec-form\">" +
        "<label class=\"opt\"><input type=\"radio\" name=\"nadlan-rec-src\" value=\"area\" checked> KAEKs in a GreekPlot area</label>" +
        "<select id=\"nadlan-rec-area\"><option value=\"\">- choose the area -</option></select>" +
        "<div class=\"hint\">KAEKs already fetched are skipped (except errors). At most 300 per run.</div>" +
        "<label class=\"opt\"><input type=\"radio\" name=\"nadlan-rec-src\" value=\"paste\"> KAEKs I paste</label>" +
        "<textarea id=\"nadlan-rec-paste\" placeholder=\"12-digit KAEKs, separated by commas, semicolons, spaces or new lines\"></textarea>" +
        "<div class=\"hint\" id=\"nadlan-rec-count\">Each pasted KAEK is fetched again (a new snapshot; history is kept).</div>" +
      "</div>" +
      "<div class=\"actions\"><button type=\"button\" id=\"nadlan-rec-start\">Start</button>" +
        "<button type=\"button\" id=\"nadlan-rec-stop\" disabled>Stop</button></div>" +
      "<div class=\"status\" id=\"nadlan-rec-status\"></div>" +
      "<div class=\"counts\" id=\"nadlan-rec-counts\"></div>" +
      "<div id=\"nadlan-rec-log\"></div>" +
      "<footer>One request to the site every 5-15 s, one KAEK at a time (about a minute each). Don't use this tab during a run; " +
        "closing the map tab closes the importer. Owners as listed by the Cadastre, updated weekly. For research, not legal use.</footer>" +
      "</div>";
    document.body.appendChild(panel);

    $("nadlan-rec-min").onclick = function () { panel.classList.toggle("min"); };
    $("nadlan-rec-area").onchange = function () { this.classList.remove("missing"); choose("area"); };
    $("nadlan-rec-paste").oninput = function () { this.classList.remove("missing"); choose("paste"); showCount(); };
    $("nadlan-rec-start").onclick = function () { start(); };
    $("nadlan-rec-stop").onclick = function () { stop(); };
    counts({});
    refreshState();
    checkSignIn();
    setInterval(checkSignIn, 2000);
  }

  function source() { var r = document.querySelector("input[name=nadlan-rec-src]:checked"); return r ? r.value : "area"; }
  function choose(value) { var r = document.querySelector("input[name=nadlan-rec-src][value=" + value + "]"); if (r) { r.checked = true; } }

  function showCount() {
    var p = parseKaeks($("nadlan-rec-paste").value);
    $("nadlan-rec-count").textContent = p.kaeks.length + " KAEK(s) accepted" +
      (p.ignored ? ", " + p.ignored + " ignored (not 12 digits)" : "") + (p.duplicates ? ", " + p.duplicates + " repeated" : "") +
      (p.kaeks.length > 300 ? " - at most 300 per run" : "") + ". Each is fetched again.";
    return p;
  }

  function checkSignIn() {
    var now = !!document.querySelector(LOGOUT);
    if (now !== signedIn) {
      signedIn = now;
      if (typeof window.nadlanRecordsSignedIn === "function") { window.nadlanRecordsSignedIn(now).catch(function () { }); }
      update();
    }
  }

  function refreshState() {
    if (typeof window.nadlanRecordsState !== "function") {
      state = { offline: "This tab was not opened by the GreekPlot importer." };
      update();
      return Promise.resolve(state);
    }
    return window.nadlanRecordsState().then(applyState, function () { return state; });
  }

  // Also pushed by the tool on each page load (a second automation client can make the call above fail).
  function applyState(s) {
    state = s || {};
    running = !!state.running;
    var select = $("nadlan-rec-area"), chosen = select.value;
    select.innerHTML = "<option value=\"\">- choose the area -</option>" + (state.areas || []).map(function (a) {
      return "<option value=\"" + a.id + "\"" + (String(a.id) === chosen ? " selected" : "") + ">" + esc(a.name) + "</option>";
    }).join("");
    if (state.status) { status(state.status); }
    if (state.progress) { counts(state.progress); }
    update();
    return state;
  }

  // Why a run can't start here (shown in the yellow notice), or null.
  function blocker() {
    if (state.offline) { return "Offline - " + state.offline + " Records can only be kept in GreekPlot."; }
    if (state.needsConnect) { return "The importer is not connected to GreekPlot: use \"Connect to GreekPlot\" in the map tab's panel."; }
    if (state.userName && !state.isAdmin) { return "Connected as " + state.userName + ", who is not an Admin: only Admins can research owners and sales."; }
    return null;
  }

  function update() {
    if (!panel) { return; }
    var why = blocker();
    $("nadlan-rec-signin").hidden = signedIn !== false;
    $("nadlan-rec-off").hidden = !why;
    $("nadlan-rec-off").textContent = why || "";
    $("nadlan-rec-user").textContent = state.userName && !why ? "as " + state.userName : "";
    var ready = signedIn && !why;
    $("nadlan-rec-start").disabled = running || !ready;
    $("nadlan-rec-stop").disabled = !running;
    $("nadlan-rec-area").disabled = running || !ready;
    $("nadlan-rec-paste").disabled = running || !ready;
    if (!running && !$("nadlan-rec-status").textContent) {
      status(signedIn ? "Choose an area or paste KAEKs, then Start." : "Sign in first.");
    }
  }

  function status(text) { $("nadlan-rec-status").textContent = text; }

  function log(text, kind) {
    lines.push({ at: new Date().toISOString(), text: text, kind: kind || "" });
    if (lines.length > 500) { lines.shift(); }
    var box = $("nadlan-rec-log");
    var line = document.createElement("div");
    line.className = kind || "";
    line.title = text;
    line.textContent = new Date().toLocaleTimeString() + "  " + text;
    box.insertBefore(line, box.firstChild);
    while (box.childNodes.length > 300) { box.removeChild(box.lastChild); }
  }

  function counts(c) {
    $("nadlan-rec-counts").innerHTML =
      "<div>KAEK <b>" + (c.current ? (c.done || 0) + 1 : (c.done || 0)) + "</b> of " + (c.total || 0) + "</div>" +
      "<div>Owners found: <b>" + (c.owners || 0) + "</b></div>" +
      "<div>Sales found: <b>" + (c.sales || 0) + "</b></div>" +
      "<div>Last price: <b>" + esc(c.lastPrice || "-") + "</b></div>" +
      "<div>Not operating: <b>" + (c.notOperating || 0) + "</b></div>" +
      "<div>Errors: <b>" + (c.errors || 0) + "</b></div>";
  }

  function setRunning(on) { running = on; update(); }

  // opts: { areaId } or { kaeks: "text" }; without opts, what the form says.
  function start(opts) {
    if (!opts) {
      if (source() === "paste") {
        var p = showCount();
        if (!p.kaeks.length) { $("nadlan-rec-paste").classList.add("missing"); status("Paste at least one 12-digit KAEK."); return Promise.resolve("no KAEK"); }
        if (p.kaeks.length > 300) { status("At most 300 KAEKs per run: " + p.kaeks.length + " pasted."); return Promise.resolve("too many"); }
        opts = { kaeks: $("nadlan-rec-paste").value };
      } else {
        var area = $("nadlan-rec-area").value;
        if (!area) { $("nadlan-rec-area").classList.add("missing"); status("Choose the GreekPlot area first."); return Promise.resolve("no area"); }
        opts = { areaId: area };
      }
    }
    if (typeof window.nadlanRecordsStart !== "function") { return Promise.resolve("not in the importer"); }
    setRunning(true);
    status("Starting...");
    return window.nadlanRecordsStart(JSON.stringify({ areaId: opts.areaId ? String(opts.areaId) : null, kaeks: opts.kaeks || null })).then(function (error) {
      if (error) { setRunning(false); status(error); log(error, "err"); }
      return error || "";
    }, function (e) {
      // A second automation client on the --debug-port answers the tool's functions with "not exposed" before the tool
      // does; the tool still got the call, and it tells this panel itself whether a run started (NadlanRecords.running).
      if (/not exposed/i.test(e.message)) { return ""; }
      setRunning(false); status("Could not start: " + e.message); return e.message;
    });
  }

  function stop() {
    $("nadlan-rec-stop").disabled = true;
    status("Stopping after the current request...");
    if (typeof window.nadlanRecordsStop === "function") { window.nadlanRecordsStop().catch(function () { /* see start() */ }); }
  }

  window.NadlanRecords = {
    status: status,
    log: log,
    progress: counts,
    finished: function (text) { setRunning(false); status(text); log(text, "ok"); },
    running: setRunning, // the tool says when its run starts (and finished() when it ends)
    refreshState: refreshState,
    applyState: function (s) { if (panel) { applyState(s); } },
    // For a --debug-port session (the reviewer's first run): the same as the buttons.
    start: start,
    stop: stop,
    state: function () { return { signedIn: !!signedIn, running: running, status: $("nadlan-rec-status").textContent, connection: state }; },
    logLines: function () { return lines.slice(); }
  };

  if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", build); } else { build(); }
})();
