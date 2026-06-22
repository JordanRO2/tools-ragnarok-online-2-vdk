/* ======================================================================
   VDK Tool - SPA logic (vanilla JS, no build step).
   Talks to the local HttpListener backend in Web/WebHost.cs.
   Endpoints (exact):
     POST /api/pick-file   {title?,filter?}              -> {path}
     POST /api/pick-folder {title?}                      -> {path}
     POST /api/pick-save   {defaultName?,filter?}        -> {path}
     POST /api/vdk/list    {vdkPath}                      -> {version,fileCount,folderCount,files:[{path,size}]}
     POST /api/vdk/preview {vdkPath,entryPath,maxBytes?}  -> {size,truncated,hex,text}
     POST /api/vdk/extract {vdkPath,outDir}               -> 202 {jobId}
     POST /api/vdk/pack    {srcDir,outPath}               -> 202 {jobId}
     POST /api/ct/to       {ctPath,format}                -> {outPath,columns,rows}
     POST /api/ct/from     {inPath,outPath?}              -> {outPath,columns,rows}
     POST /api/ct/batch    {dir,format}                   -> 202 {jobId}
     GET  /api/jobs/{id}/events                           -> text/event-stream
       data: {type:"progress",current,total,item}
       data: {type:"done",result}
       data: {type:"cancelled"}
       data: {type:"error",error}
     POST /api/jobs/{id}/cancel                           -> {ok,jobId}
     GET  /api/health                                     -> {ok,version}

   Long operations (extract/pack/batch) run as JOBS: POST returns a jobId,
   then we open an EventSource to stream real progress and offer a Cancel
   button. Short synchronous ops (list/convert/pickers) use a thin inline
   activity bar, not a blocking modal.
   ====================================================================== */

"use strict";

const $  = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

/* Named constants (no magic numbers in the hot paths). */
const MAX_TABLE_ROWS = 5000;     // cap rows rendered before "refine the filter"
const PREVIEW_MAX = 65536;       // 64 KiB cap for the preview fetch
const FILTER_DEBOUNCE_MS = 180;

/* ----------------------------------------------------------- UI extension hub
   window.VDKUI is an optional, progressively-enhanced layer. The pretext
   integration module (pretext-integration.mjs, loaded as an ES module) plugs
   in here to measure/lay out preview + log text without DOM reflow. */
window.VDKUI = window.VDKUI || {
  renderPreviewText: null,
  onLogEntry: null,
  onThemeChanged: null,
  onPreviewReset: null,
};

/* ----------------------------------------------------------- API helper */
async function api(path, body) {
  const r = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body || {}),
  });
  let data = null;
  try { data = await r.json(); } catch (_) { /* non-JSON */ }
  if (!r.ok) {
    const msg = (data && data.error) ? data.error : `HTTP ${r.status}`;
    const err = new Error(msg);
    err.status = r.status;
    throw err;
  }
  return data || {};
}

/* ----------------------------------------------------------- formatting */
function fmtBytes(n) {
  if (n == null) return "";
  if (n < 1024) return n + " B";
  const u = ["KB", "MB", "GB", "TB"];
  let i = -1, v = n;
  do { v /= 1024; i++; } while (v >= 1024 && i < u.length - 1);
  return v.toFixed(v < 10 ? 1 : 0) + " " + u[i];
}
function baseName(p) {
  if (!p) return "";
  const parts = p.replace(/\\/g, "/").split("/");
  return parts[parts.length - 1];
}
/* Fixed HH:MM:SS timestamp (locale-independent, no AM/PM). */
function ts() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, "0");
  return p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
}

/* ----------------------------------------------------------- Toasts
   A toast is the transitory RESULT of a discrete action (auto-dismiss). The
   log is the persistent history. The toast summarizes; the log details. */
function toast(title, body, kind) {
  const host = $("#toasts");
  const el = document.createElement("div");
  el.className = "toast" + (kind ? " t-" + kind : "");
  el.appendChild(icon(kind === "err" ? "alert" : kind === "ok" ? "check" : "info", "t-icon"));
  const main = document.createElement("div");
  main.className = "t-main";
  const t = document.createElement("div"); t.className = "t-title"; t.textContent = title;
  main.appendChild(t);
  if (body) { const b = document.createElement("div"); b.className = "t-body"; b.textContent = body; main.appendChild(b); }
  el.appendChild(main);
  host.appendChild(el);
  setTimeout(() => {
    el.style.transition = "opacity .25s";
    el.style.opacity = "0";
    setTimeout(() => el.remove(), 250);
  }, kind === "err" ? 7000 : 4000);
}

/* ----------------------------------------------------------- Log */
function log(msg, kind) {
  const host = $("#log");
  const el = document.createElement("div");
  el.className = "log-entry l-" + (kind || "info");
  const dot = document.createElement("span"); dot.className = "lvl"; dot.setAttribute("aria-hidden", "true");
  const tsEl = document.createElement("span"); tsEl.className = "ts"; tsEl.textContent = ts();
  const m = document.createElement("span"); m.className = "msg"; m.textContent = msg;
  el.appendChild(dot); el.appendChild(tsEl); el.appendChild(m);
  host.appendChild(el);
  host.scrollTop = host.scrollHeight;
  if (window.VDKUI && window.VDKUI.onLogEntry) {
    try { window.VDKUI.onLogEntry(el, msg); } catch (_) {}
  }
}

/* ----------------------------------------------------------- SVG icons */
const ICONS = {
  list:    '<path d="M8 6h12M8 12h12M8 18h12"/><circle cx="4" cy="6" r="1.2"/><circle cx="4" cy="12" r="1.2"/><circle cx="4" cy="18" r="1.2"/>',
  extract: '<path d="M12 3v10m0 0 4-4m-4 4-4-4"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>',
  pack:    '<path d="M12 21V11m0 0 4 4m-4-4-4 4"/><path d="M4 7V5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v2"/>',
  convert: '<path d="M4 8h13l-3-3m3 3-3 3"/><path d="M20 16H7l3-3m-3 3 3 3"/>',
  // directional CT conversions: binary CT (file) -> spreadsheet (table), and back
  ctToSheet: '<path d="M6 3h6l3 3v4"/><path d="M12 3v4h4"/><rect x="9" y="12" width="12" height="9" rx="1.5"/><path d="M9 16h12M15 12v9"/>',
  sheetToCt: '<rect x="3" y="3" width="12" height="9" rx="1.5"/><path d="M3 7h12M9 3v9"/><path d="M18 21h-6a1 1 0 0 1-1-1v-7"/><path d="M15 14v-3h3"/>',
  batch:   '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  folder:  '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  file:    '<path d="M6 3h8l4 4v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z"/><path d="M14 3v4h4"/>',
  table:   '<rect x="3" y="4" width="18" height="16" rx="1.5"/><path d="M3 9h18M3 14h18M9 4v16"/>',
  check:   '<path d="M5 12.5 10 17 19 7"/>',
  cross:   '<path d="M6 6l12 12M18 6 6 18"/>',
  alert:   '<path d="M12 9v4m0 4h.01"/><path d="M10.3 3.9 2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/>',
  info:    '<circle cx="12" cy="12" r="9"/><path d="M12 11v5m0-8h.01"/>',
  cancel:  '<circle cx="12" cy="12" r="9"/><path d="M9 9l6 6m0-6-6 6"/>',
  moon:    '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
  sun:     '<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M4.9 4.9l1.4 1.4m11.4 11.4 1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/>',
  monitor: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8m-4-4v4"/>',
  search:  '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  inbox:   '<path d="M4 13h4l1.5 3h5L16 13h4"/><path d="M5 5h14a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z"/>',
  settings:'<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>',
  "win-min":     '<path d="M5 12h14"/>',
  "win-max":     '<rect x="6" y="6" width="12" height="12" rx="1.5"/>',
  "win-restore": '<rect x="8.5" y="5" width="10.5" height="10.5" rx="1.5"/><rect x="5" y="8.5" width="10.5" height="10.5" rx="1.5"/>',
};

function icon(name, cls) {
  const NS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("class", "ico" + (cls ? " " + cls : ""));
  svg.setAttribute("aria-hidden", "true");
  svg.innerHTML = ICONS[name] || "";
  return svg;
}
function hydrateIcons(root = document) {
  $$("[data-icon]", root).forEach((el) => {
    if (el.dataset.iconDone) return;
    el.prepend(icon(el.dataset.icon, el.dataset.iconCls));
    el.dataset.iconDone = "1";
  });
}

/* ----------------------------------------------------------- Busy state
   Short synchronous ops show a thin inline activity bar under the app-bar and
   lock the action buttons; they do NOT throw up a full-screen modal. */
let busyCount = 0;
function setBusy(on /*, text */) {
  busyCount += on ? 1 : -1;
  if (busyCount < 0) busyCount = 0;
  const active = busyCount > 0;
  $("#busy").hidden = !active;
  document.body.classList.toggle("busy-locked", active);
}

async function run(label, fn) {
  log(label + "...", "run");
  setBusy(true);
  try {
    const res = await fn();
    setBusy(false);
    return res;
  } catch (e) {
    setBusy(false);
    log(label + " FAILED: " + e.message, "err");
    toast(label + " failed", e.message, "err");
    throw e;
  }
}

/* ======================================================================
   Jobs: progress modal + cancel via SSE
   ====================================================================== */
let activeJob = null;

function showProgress(label) {
  const box = $("#progress");
  $("#progress-label").textContent = label;
  $("#progress-item").textContent = "Starting...";
  $("#progress-count").textContent = "";
  setBar(0);
  box.classList.add("indeterminate");
  box.hidden = false;
  $("#progress-cancel").disabled = false;
  document.body.classList.add("busy-locked");
  $("#progress-cancel").focus();
}
function setBar(pct) {
  $("#progress-fill").style.width = Math.max(0, Math.min(100, pct)) + "%";
}
function hideProgress() {
  $("#progress").hidden = true;
  document.body.classList.remove("busy-locked");
}

async function startJob(label, postPath, postBody, onDone) {
  if (activeJob) { toast("Busy", "Another job is already running.", "err"); return; }
  log(label + "...", "run");
  let jobId;
  try {
    const r = await api(postPath, postBody);
    jobId = r.jobId;
    if (!jobId) throw new Error("server did not return a jobId");
  } catch (e) {
    log(label + " FAILED: " + e.message, "err");
    toast(label + " failed", e.message, "err");
    return;
  }

  showProgress(label);
  const src = new EventSource(`/api/jobs/${jobId}/events`);
  activeJob = { id: jobId, source: src, label };

  src.onmessage = (ev) => {
    let m;
    try { m = JSON.parse(ev.data); } catch (_) { return; }

    if (m.type === "progress") {
      const box = $("#progress");
      if (m.total > 0) {
        box.classList.remove("indeterminate");
        setBar((m.current / m.total) * 100);
        $("#progress-count").textContent = `${m.current} / ${m.total}`;
      } else {
        box.classList.add("indeterminate");
        $("#progress-count").textContent = m.current ? String(m.current) : "";
      }
      if (m.item != null) $("#progress-item").textContent = baseName(m.item) || m.item;
      return;
    }

    if (m.type === "done") {
      endJob(src);
      log(label + " done.", "ok");
      onDone && onDone(m.result || {});
      return;
    }
    if (m.type === "cancelled") {
      endJob(src);
      log(label + " cancelled.", "err");
      toast(label + " cancelled", "Operation was cancelled.", "info");
      return;
    }
    if (m.type === "error") {
      endJob(src);
      log(label + " FAILED: " + (m.error || "error"), "err");
      toast(label + " failed", m.error || "error", "err");
      return;
    }
  };

  src.onerror = () => {
    if (!activeJob || activeJob.source !== src) return;
    if (src.readyState === EventSource.CLOSED) {
      endJob(src);
      log(label + ": stream closed unexpectedly.", "err");
      toast(label, "Progress stream closed.", "err");
    }
  };
}

function endJob(src) {
  try { src.close(); } catch (_) {}
  activeJob = null;
  hideProgress();
}

async function cancelActiveJob() {
  if (!activeJob) return;
  const id = activeJob.id;
  $("#progress-cancel").disabled = true;
  $("#progress-item").textContent = "Cancelling...";
  try {
    await api(`/api/jobs/${id}/cancel`, {});
    log("Cancel requested.", "run");
  } catch (e) {
    log("Cancel failed: " + e.message, "err");
  } finally {
    $("#progress-cancel").disabled = false;
  }
}

/* ----------------------------------------------------------- Theme
   Three-state selector: light | dark | system (default). The flip is made
   ATOMIC: a global .theme-anim-off class suppresses every transition, we force
   a reflow, swap data-theme, then re-enable transitions after two rAFs so the
   whole palette repaints in one frame (no staggered/laggy components). */
const THEME_KEY = "vdk.theme";
const THEME_CHOICES = ["light", "dark", "system"];
let mqlDark = null;

function readThemeChoice() {
  let v = null;
  try { v = localStorage.getItem(THEME_KEY); } catch (_) {}
  return THEME_CHOICES.includes(v) ? v : "system";
}

/* Reflect the active radio + set data-theme. The actual flip mechanics live in
   flipTheme(); this only mutates DOM state. */
function reflectThemeChoice(choice) {
  const root = document.documentElement;
  if (choice === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", choice);

  $$("#theme-switch .theme-opt").forEach((b) => {
    const on = b.dataset.themeChoice === choice;
    b.setAttribute("aria-checked", on ? "true" : "false");
    b.tabIndex = on ? 0 : -1;
  });
  if (window.VDKUI && window.VDKUI.onThemeChanged) window.VDKUI.onThemeChanged();
}

function flipTheme(choice) {
  const root = document.documentElement;
  const apply = () => reflectThemeChoice(choice);

  // Atomic instant flip: kill transitions, reflow, swap, re-enable after paint.
  // (No View Transition crossfade — it leaves a stuck blank overlay in WebView2.)
  root.classList.add("theme-anim-off");
  apply();
  void root.offsetWidth; // force reflow so pending transitions are discarded
  requestAnimationFrame(() => requestAnimationFrame(() => {
    root.classList.remove("theme-anim-off");
  }));
}

function setThemeChoice(choice) {
  if (!THEME_CHOICES.includes(choice)) choice = "system";
  try { localStorage.setItem(THEME_KEY, choice); } catch (_) {}
  flipTheme(choice);
}

function initTheme() {
  // Initial paint: reflect without the view-transition path.
  document.documentElement.classList.add("theme-anim-off");
  reflectThemeChoice(readThemeChoice());
  void document.documentElement.offsetWidth;
  requestAnimationFrame(() => requestAnimationFrame(() => {
    document.documentElement.classList.remove("theme-anim-off");
  }));

  const opts = $$("#theme-switch .theme-opt");
  opts.forEach((b, i) => {
    b.addEventListener("click", () => { setThemeChoice(b.dataset.themeChoice); b.focus(); });
    b.addEventListener("keydown", (e) => {
      let j = -1;
      if (e.key === "ArrowRight" || e.key === "ArrowDown") j = (i + 1) % opts.length;
      else if (e.key === "ArrowLeft" || e.key === "ArrowUp") j = (i - 1 + opts.length) % opts.length;
      else if (e.key === "Home") j = 0;
      else if (e.key === "End") j = opts.length - 1;
      else return;
      e.preventDefault();
      setThemeChoice(opts[j].dataset.themeChoice);
      opts[j].focus();
    });
  });

  try {
    mqlDark = window.matchMedia("(prefers-color-scheme: dark)");
    const onOsChange = () => {
      if (readThemeChoice() !== "system") return;
      if (window.VDKUI && window.VDKUI.onThemeChanged) window.VDKUI.onThemeChanged();
    };
    if (mqlDark.addEventListener) mqlDark.addEventListener("change", onOsChange);
    else if (mqlDark.addListener) mqlDark.addListener(onOsChange);
  } catch (_) {}
}

/* ----------------------------------------------------------- Tabs
   Full ARIA tabs pattern: roving tabindex, arrow/Home/End navigation with wrap,
   automatic activation (focus moves selection). */
function initTabs() {
  const tabs = $$(".tab");
  function activate(tab, focus) {
    tabs.forEach((x) => {
      const on = x === tab;
      x.classList.toggle("active", on);
      x.setAttribute("aria-selected", on ? "true" : "false");
      x.tabIndex = on ? 0 : -1;
    });
    $$(".tab-panel").forEach((x) => x.classList.remove("active"));
    $("#panel-" + tab.dataset.tab).classList.add("active");
    if (focus) tab.focus();
  }
  tabs.forEach((t, i) => {
    t.addEventListener("click", () => activate(t, false));
    t.addEventListener("keydown", (e) => {
      let j = -1;
      if (e.key === "ArrowRight" || e.key === "ArrowDown") j = (i + 1) % tabs.length;
      else if (e.key === "ArrowLeft" || e.key === "ArrowUp") j = (i - 1 + tabs.length) % tabs.length;
      else if (e.key === "Home") j = 0;
      else if (e.key === "End") j = tabs.length - 1;
      else return;
      e.preventDefault();
      activate(tabs[j], true);
    });
  });
}

/* ----------------------------------------------------------- Settings state
   Persisted preferences fetched from /api/settings. The only setting today is a
   default output folder: it pre-fills the Extract field (when empty) and seeds
   the Browse dialogs' starting directory. */
let appSettings = { defaultOutputFolder: "" };
/* The default we last wrote into the output fields. Changing the default then
   propagates to fields that still hold the OLD default, while leaving values the
   user typed by hand untouched. */
let appliedDefaultOutput = "";

/* Parent directory of a path (strips the last \ or / segment). */
function parentDir(p) {
  if (!p) return "";
  const s = String(p).replace(/[\\/]+$/, "");
  const i = Math.max(s.lastIndexOf("\\"), s.lastIndexOf("/"));
  return i > 0 ? s.slice(0, i) : "";
}

/* ----------------------------------------------------------- Pickers */
function setVal(id, v) {
  const el = document.getElementById(id);
  if (el) { el.value = v; el.dispatchEvent(new Event("input", { bubbles: true })); }
}

function curVal(id) {
  const el = document.getElementById(id);
  return el ? (el.value || "").trim() : "";
}

function initPickers() {
  // Input file pickers open in the current value's folder (no output default —
  // an input rarely lives in the output folder).
  $$("[data-pick-file]").forEach((b) => {
    b.addEventListener("click", async () => {
      const initialDir = parentDir(curVal(b.dataset.pickFile));
      const r = await run("Pick file", () =>
        api("/api/pick-file", { title: b.dataset.title, filter: b.dataset.filter, initialDir }));
      if (r && r.path) { setVal(b.dataset.pickFile, r.path); log("Selected: " + r.path); }
    });
  });
  // Folder pickers open in the current folder, else the default output folder.
  $$("[data-pick-folder]").forEach((b) => {
    b.addEventListener("click", async () => {
      const initialDir = curVal(b.dataset.pickFolder) || appSettings.defaultOutputFolder || "";
      const r = await run("Pick folder", () =>
        api("/api/pick-folder", { title: b.dataset.title, initialDir }));
      if (r && r.path) { setVal(b.dataset.pickFolder, r.path); log("Selected: " + r.path); }
    });
  });
  // Save (output) pickers open in the current value's folder, else the default.
  $$("[data-pick-save]").forEach((b) => {
    b.addEventListener("click", async () => {
      const cur = curVal(b.dataset.pickSave);
      const initialDir = cur ? parentDir(cur) : (appSettings.defaultOutputFolder || "");
      const r = await run("Pick save path", () =>
        api("/api/pick-save", { defaultName: b.dataset.default, filter: b.dataset.filter, initialDir }));
      if (r && r.path) { setVal(b.dataset.pickSave, r.path); log("Save to: " + r.path); }
    });
  });
}

/* ----------------------------------------------------------- Settings modal
   Gear in the title bar opens a small dialog to set/clear the default output
   folder. Saving persists it via /api/settings and re-applies the pre-fill. */
function applyDefaultOutput() {
  const d = appSettings.defaultOutputFolder || "";
  const cur = curVal("extractOut");
  // Seed the Extract folder when it is empty, OR replace a value we ourselves put
  // there earlier (the previous default) so changing the default updates it too.
  // A path the user typed by hand (≠ the old default) is preserved.
  if (!cur || cur === appliedDefaultOutput) setVal("extractOut", d);
  appliedDefaultOutput = d;
}

async function loadSettings() {
  try {
    const r = await fetch("/api/settings");
    const j = await r.json();
    appSettings.defaultOutputFolder = (j && j.defaultOutputFolder) || "";
  } catch (_) {
    appSettings.defaultOutputFolder = "";
  }
  applyDefaultOutput();
}

function openSettings() {
  setVal("setDefaultOut", appSettings.defaultOutputFolder || "");
  const m = $("#settings-modal");
  m.hidden = false;
  const input = $("#setDefaultOut");
  if (input) { input.focus(); input.select(); }
}

function closeSettings() {
  $("#settings-modal").hidden = true;
  const btn = $("#btn-settings");
  if (btn) btn.focus();
}

async function saveSettings() {
  const v = curVal("setDefaultOut");
  try {
    const r = await api("/api/settings", { defaultOutputFolder: v });
    appSettings.defaultOutputFolder = (r && r.defaultOutputFolder) || "";
    log(appSettings.defaultOutputFolder
      ? "Default output folder set: " + appSettings.defaultOutputFolder
      : "Default output folder cleared.", "ok");
    toast("Settings saved",
      appSettings.defaultOutputFolder ? baseName(appSettings.defaultOutputFolder) : "Default output folder cleared",
      "ok");
    applyDefaultOutput();
    closeSettings();
  } catch (e) {
    log("Save settings failed: " + e.message, "err");
    toast("Settings failed", e.message, "err");
  }
}

function initSettings() {
  const open = $("#btn-settings");
  if (open) open.addEventListener("click", openSettings);
  $("#settings-close").addEventListener("click", closeSettings);
  $("#settings-cancel").addEventListener("click", closeSettings);
  $("#settings-save").addEventListener("click", saveSettings);
  $("#setDefaultOut-clear").addEventListener("click", () => { setVal("setDefaultOut", ""); $("#setDefaultOut").focus(); });
  // Click on the dim backdrop (outside the box) closes.
  $("#settings-modal").addEventListener("mousedown", (e) => {
    if (e.target === $("#settings-modal")) closeSettings();
  });
  // Esc closes when open; Enter in the field saves.
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !$("#settings-modal").hidden) { e.preventDefault(); closeSettings(); }
  });
  $("#setDefaultOut").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); saveSettings(); }
  });
}

/* ----------------------------------------------------------- Drag & drop
   Browsers cannot expose the real filesystem path of a dropped file; we use the
   name to PRE-FILL the field as a hint, flash a brief success outline, and open
   the matching native dialog so the user can confirm the real path. */
function initDrop() {
  $$(".drop-zone").forEach((zone) => {
    const targetId = zone.dataset.drop;
    const input = $("#" + targetId);
    const pickBtn = zone.querySelector("[data-pick-file],[data-pick-folder],[data-pick-save]");

    ["dragenter", "dragover"].forEach((ev) =>
      zone.addEventListener(ev, (e) => { e.preventDefault(); zone.classList.add("drag-over"); }));
    ["dragleave", "dragend"].forEach((ev) =>
      zone.addEventListener(ev, () => zone.classList.remove("drag-over")));

    zone.addEventListener("drop", (e) => {
      e.preventDefault();
      zone.classList.remove("drag-over");
      const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
      if (!f) return;
      zone.classList.remove("drop-ok");
      void zone.offsetWidth;
      zone.classList.add("drop-ok");
      input.value = "";
      toast("Dropped " + f.name, "Confirm its path in the dialog.", "info");
      log("Dropped '" + f.name + "' -> opening dialog to resolve real path.");
      if (pickBtn) pickBtn.click();
    });
  });
}

/* ----------------------------------------------------------- Segmented radios
   Each .seg is a radiogroup: roving tabindex, arrow/Home/End navigation, and
   aria-checked tracking. */
function initSegs() {
  $$(".seg").forEach((seg) => {
    const btns = Array.from(seg.querySelectorAll(".seg-btn"));
    function select(btn, focus) {
      btns.forEach((b) => {
        const on = b === btn;
        b.classList.toggle("active", on);
        b.setAttribute("aria-checked", on ? "true" : "false");
        b.setAttribute("aria-pressed", on ? "true" : "false");
        b.tabIndex = on ? 0 : -1;
      });
      if (focus) btn.focus();
    }
    seg.addEventListener("click", (e) => {
      const btn = e.target.closest(".seg-btn");
      if (btn && seg.contains(btn)) { select(btn, false); seg.dispatchEvent(new CustomEvent("seg-change", { detail: btn })); }
    });
    seg.addEventListener("keydown", (e) => {
      const i = btns.indexOf(document.activeElement);
      if (i < 0) return;
      let j = -1;
      if (e.key === "ArrowRight" || e.key === "ArrowDown") j = (i + 1) % btns.length;
      else if (e.key === "ArrowLeft" || e.key === "ArrowUp") j = (i - 1 + btns.length) % btns.length;
      else if (e.key === "Home") j = 0;
      else if (e.key === "End") j = btns.length - 1;
      else return;
      e.preventDefault();
      select(btns[j], true);
      seg.dispatchEvent(new CustomEvent("seg-change", { detail: btns[j] }));
    });
  });
}
function segValue(sel, attr) {
  const active = $(sel + " .seg-btn.active");
  return active ? active.dataset[attr] : null;
}

/* ----------------------------------------------------------- Require helper */
function need(id, label) {
  const v = ($("#" + id).value || "").trim();
  if (!v) {
    toast("Missing input", (label || id) + " is required.", "err");
    log("Missing: " + (label || id), "err");
    return null;
  }
  return v;
}

/* ----------------------------------------------------------- Action gating
   Each action button starts DISABLED and is enabled ONLY when its required
   inputs hold a (trimmed) non-empty value. This is ADDITIVE to need(): it
   prevents invalid clicks before need() ever has to complain. The picker and
   drop-zone handlers set input.value programmatically and dispatch a bubbling
   "input" event, so refreshGates() re-runs after every programmatic fill too.

   Precondition map (button id -> required input ids, ALL must be non-empty). */
const GATES = {
  "btn-list":     ["vdkPath"],
  "btn-extract":  ["vdkPath", "extractOut"],
  "btn-pack":     ["packSrc", "packOut"],
  "btn-ct-to":    ["ctPath"],
  "btn-ct-from":  ["ctFromIn"],
  "btn-ct-batch": ["batchDir"],
};

function inputFilled(id) {
  const el = document.getElementById(id);
  return !!(el && (el.value || "").trim());
}

/* Recompute .disabled for the 6 action buttons from current input values, and
   dim the path-row of an action card whose button is still disabled (a subtle
   "complete this input" affordance — nothing is hidden). */
function refreshGates() {
  // Collect the set of inputs that gate at least one currently-disabled button,
  // so we can flag their path-rows as pending.
  const pendingInputs = new Set();
  for (const [btnId, reqs] of Object.entries(GATES)) {
    const btn = document.getElementById(btnId);
    if (!btn) continue;
    const ready = reqs.every(inputFilled);
    btn.disabled = !ready;
    if (!ready) reqs.forEach((id) => pendingInputs.add(id));
  }
  // Mark each gated input's enclosing .path-row as pending (CSS dims it).
  Object.values(GATES).flat().forEach((id) => {
    const el = document.getElementById(id);
    if (!el) return;
    const row = el.closest(".path-row");
    if (row) row.classList.toggle("input-pending", pendingInputs.has(id));
  });
}

/* Attach an "input" listener to every gating input so typing re-runs the gate.
   (Programmatic fills from pickers/drops dispatch a bubbling "input" event too,
   so they flow through the same path.) */
function initGates() {
  const ids = new Set(Object.values(GATES).flat());
  ids.forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("input", refreshGates);
  });
  refreshGates();
}

/* ======================================================================
   VDK operations
   ====================================================================== */
let vdkAllFiles = [];
let currentVdkPath = null;
let selectedFile = null;
let previewMode = "text";
let previewData = null;
let previewReqId = 0;

/* Render the file table. Preserves keyboard focus across re-renders (filter)
   by re-focusing the row for the same path, or the first row otherwise. */
function renderVdkFiles(filter) {
  const tbody = $("#vdk-files");
  const table = $("#vdk-table");
  const focusedPath = (() => {
    const a = document.activeElement;
    return (a && a.closest && a.closest("#vdk-files tr")) ? a.dataset.path : null;
  })();

  tbody.innerHTML = "";
  const q = (filter || "").toLowerCase();
  const rows = q ? vdkAllFiles.filter((f) => f.path.toLowerCase().includes(q)) : vdkAllFiles;
  const empty = $("#vdk-empty");

  if (!vdkAllFiles.length) {
    setEmpty(empty, "inbox", "No archive loaded", "List a VDK to see its contents.");
    empty.hidden = false; table.hidden = true;
    return;
  }
  if (!rows.length) {
    setEmpty(empty, "search", "No matches", "No files match the current filter.");
    empty.hidden = false; table.hidden = false;
  } else {
    empty.hidden = true; table.hidden = false;
  }

  const frag = document.createDocumentFragment();
  rows.slice(0, MAX_TABLE_ROWS).forEach((f) => {
    const tr = document.createElement("tr");
    tr.tabIndex = -1;
    tr.dataset.path = f.path;
    const tdP = document.createElement("td");
    tdP.className = "path";
    tdP.textContent = f.path;
    const tdS = document.createElement("td");
    tdS.className = "num";
    tdS.textContent = fmtBytes(f.size);
    tr.appendChild(tdP); tr.appendChild(tdS);
    tr.addEventListener("click", () => selectFile(f, tr));
    tr.addEventListener("keydown", (e) => onRowKey(e, tr, f));
    frag.appendChild(tr);
  });
  tbody.appendChild(frag);
  if (rows.length > MAX_TABLE_ROWS) {
    const tr = document.createElement("tr");
    tr.className = "more-row";
    const td = document.createElement("td");
    td.colSpan = 2;
    td.textContent = `Showing first ${MAX_TABLE_ROWS} of ${rows.length} matches - refine the filter.`;
    tr.appendChild(td); tbody.appendChild(tr);
  }

  // Make at least one row reachable via Tab, then restore focus if we had it.
  const first = tbody.querySelector("tr[data-path]");
  if (first) first.tabIndex = 0;
  if (focusedPath) {
    const same = tbody.querySelector(`tr[data-path="${cssEsc(focusedPath)}"]`) || first;
    if (same) { same.tabIndex = 0; same.focus(); }
  }
}

/* Minimal CSS attribute-selector escaper for arbitrary path strings. */
function cssEsc(s) {
  if (window.CSS && CSS.escape) return CSS.escape(s);
  return String(s).replace(/["\\]/g, "\\$&");
}

/* Roving-tabindex keyboard navigation between rows. */
function onRowKey(e, tr, f) {
  if (e.key === "Enter" || e.key === " ") { e.preventDefault(); selectFile(f, tr); return; }
  const rows = $$("#vdk-files tr[data-path]");
  const i = rows.indexOf(tr);
  if (i < 0) return;
  let j = -1;
  if (e.key === "ArrowDown") j = Math.min(rows.length - 1, i + 1);
  else if (e.key === "ArrowUp") j = Math.max(0, i - 1);
  else if (e.key === "Home") j = 0;
  else if (e.key === "End") j = rows.length - 1;
  else if (e.key === "PageDown") j = Math.min(rows.length - 1, i + 12);
  else if (e.key === "PageUp") j = Math.max(0, i - 12);
  else return;
  e.preventDefault();
  tr.tabIndex = -1;
  rows[j].tabIndex = 0;
  rows[j].focus();
}

/* Fill an empty-state placeholder with an icon + title + hint. variant:
   undefined | "err" | "warn" tints the icon. */
function setEmpty(el, ic, title, hint, variant) {
  el.className = "empty" + (variant === "err" ? " empty-err" : variant === "warn" ? " empty-warn" : "");
  el.innerHTML = "";
  el.appendChild(icon(ic, "empty-ico"));
  const t = document.createElement("div"); t.className = "empty-title"; t.textContent = title;
  const h = document.createElement("div"); h.className = "empty-hint"; h.textContent = hint || "";
  el.appendChild(t); el.appendChild(h);
}

/* Render an empty/error/loading state INSIDE the preview body using the shared
   .empty pattern. */
function previewState(ic, title, hint, variant) {
  const body = $("#preview-body");
  if (window.VDKUI && window.VDKUI.onPreviewReset) {
    try { window.VDKUI.onPreviewReset(body, $("#preview-info")); } catch (_) {}
  }
  body.classList.remove("virt");
  body.classList.add("is-empty");
  body.textContent = "";
  const wrap = document.createElement("div");
  setEmpty(wrap, ic, title, hint, variant);
  body.appendChild(wrap);
  const info = $("#preview-info");
  if (info) info.hidden = true;
}

function selectFile(f, tr) {
  selectedFile = f;
  $$("#vdk-files tr.selected").forEach((x) => x.classList.remove("selected"));
  if (tr) tr.classList.add("selected");
  $("#preview-name").textContent = f.path;
  $("#preview-modes").hidden = false;
  fetchPreview(f);
}

async function fetchPreview(f) {
  const reqId = ++previewReqId;
  previewData = null;
  previewState("file", "Loading preview", baseName(f.path));
  $("#preview-trunc").hidden = true;
  if (!currentVdkPath) {
    previewState("info", "No archive", "List a VDK first.", "warn");
    return;
  }
  try {
    const r = await api("/api/vdk/preview", {
      vdkPath: currentVdkPath,
      entryPath: f.path,
      maxBytes: PREVIEW_MAX,
    });
    if (reqId !== previewReqId) return;
    previewData = r;
    renderPreview();
  } catch (e) {
    if (reqId !== previewReqId) return;
    previewState("alert", "Preview failed", e.message, "err");
    log("Preview failed for " + f.path + ": " + e.message, "err");
  }
}

function renderPreview() {
  const body = $("#preview-body");
  const trunc = $("#preview-trunc");
  const info = $("#preview-info");

  if (!selectedFile) { previewState("file", "Select a file to preview", "Pick an entry from the list."); trunc.hidden = true; return; }
  if (!previewData)  { previewState("file", "Loading preview", ""); trunc.hidden = true; return; }

  const d = previewData;
  if (d.size === 0) {
    previewState("info", "Empty file", "0 bytes.", "warn");
    trunc.hidden = true;
    return;
  }

  // leaving empty-state: switch back to text rendering mode
  body.classList.remove("is-empty");

  if (previewMode === "hex") {
    if (window.VDKUI && window.VDKUI.onPreviewReset) {
      try { window.VDKUI.onPreviewReset(body, info); } catch (_) {}
    }
    body.classList.remove("virt");
    if (info) info.hidden = true;
    body.textContent = hexDump(d.hex);
  } else {
    const text = d.text || "";
    let handled = false;
    if (window.VDKUI && window.VDKUI.renderPreviewText) {
      try {
        handled = window.VDKUI.renderPreviewText(body, info, text, {
          shownBytes: byteLen(d),
          totalBytes: d.size,
          truncated: !!d.truncated,
        });
      } catch (_) { handled = false; }
    }
    if (!handled) {
      if (window.VDKUI && window.VDKUI.onPreviewReset) {
        try { window.VDKUI.onPreviewReset(body, info); } catch (_) {}
      }
      body.classList.remove("virt");
      if (info) info.hidden = true;
      body.textContent = text;
    }
  }

  if (d.truncated) {
    trunc.hidden = false;
    trunc.textContent = `Truncated - showing first ${fmtBytes(byteLen(d))} of ${fmtBytes(d.size)}.`;
  } else {
    trunc.hidden = true;
  }
}

function byteLen(d) {
  if (d.hex != null) return d.hex.length / 2;
  return Math.min(d.size, PREVIEW_MAX);
}

function hexDump(hex) {
  if (!hex) return "";
  const n = hex.length / 2;
  const lines = [];
  for (let i = 0; i < n; i += 16) {
    const off = i.toString(16).padStart(8, "0");
    let h = "", asc = "";
    for (let j = 0; j < 16; j++) {
      const k = i + j;
      if (k < n) {
        const pair = hex.substr(k * 2, 2);
        const b = parseInt(pair, 16);
        h += pair + " ";
        asc += (b >= 32 && b < 127) ? String.fromCharCode(b) : ".";
      } else {
        h += "   ";
      }
      if (j === 7) h += " ";
    }
    lines.push(off + "  " + h + " " + asc);
  }
  return lines.join("\n");
}

function resetPreview() {
  selectedFile = null;
  previewData = null;
  $("#preview-name").textContent = "Preview";
  $("#preview-modes").hidden = true;
  $("#preview-trunc").hidden = true;
  previewState("file", "Select a file to preview", "Pick an entry from the list.");
}

/* Skeleton rows while a (potentially large) listing arrives. */
function showSkeleton() {
  const tbody = $("#vdk-files");
  const table = $("#vdk-table");
  const empty = $("#vdk-empty");
  empty.hidden = true; table.hidden = false;
  tbody.innerHTML = "";
  const frag = document.createDocumentFragment();
  for (let i = 0; i < 12; i++) {
    const tr = document.createElement("tr");
    tr.className = "skeleton-row";
    const tdP = document.createElement("td");
    const bar = document.createElement("div"); bar.className = "sk-bar";
    bar.style.width = (40 + ((i * 37) % 50)) + "%";
    tdP.appendChild(bar);
    const tdS = document.createElement("td"); tdS.className = "num";
    const bar2 = document.createElement("div"); bar2.className = "sk-bar"; bar2.style.width = "60%"; bar2.style.marginLeft = "auto";
    tdS.appendChild(bar2);
    tr.appendChild(tdP); tr.appendChild(tdS);
    frag.appendChild(tr);
  }
  tbody.appendChild(frag);
}

/* Archive info stat tile. Re-presents the /api/vdk/list payload (version,
   fileCount, folderCount) as three mini-stats — no new endpoint. The grid is
   ALWAYS visible and uses ONE layout for both states: passing null dims it to
   "—" placeholders and shows the hint line; a payload fills the values and
   hides the hint. */
function setVdkStats(r) {
  const grid = $("#vdk-stat-grid");
  const empty = $("#vdk-stat-empty");
  const meta = $("#vdk-meta");
  if (!grid || !empty) return;
  if (!r) {
    grid.dataset.empty = "true";
    empty.hidden = false;
    $("#stat-version").textContent = "—";
    $("#stat-files").textContent = "—";
    $("#stat-folders").textContent = "—";
    if (meta) meta.hidden = true;
    return;
  }
  const fmtN = (n) => (n == null ? "—" : Number(n).toLocaleString());
  $("#stat-version").textContent = r.version != null ? ("v" + r.version) : "—";
  $("#stat-files").textContent = fmtN(r.fileCount);
  $("#stat-folders").textContent = fmtN(r.folderCount);
  grid.dataset.empty = "false";
  empty.hidden = true;
  if (meta) meta.hidden = false;
}

function initVdk() {
  $("#btn-list").addEventListener("click", async () => {
    const vdkPath = need("vdkPath", "VDK file");
    if (!vdkPath) return;
    showSkeleton();
    let r;
    try {
      r = await run("List VDK", () => api("/api/vdk/list", { vdkPath }));
    } catch (_) {
      vdkAllFiles = []; renderVdkFiles(""); setVdkStats(null);
      return;
    }
    vdkAllFiles = r.files || [];
    currentVdkPath = vdkPath;
    resetPreview();
    // Same payload feeds the hidden #vdk-meta badge AND the Archive info stat tile.
    $("#vdk-meta").textContent =
      `v${r.version} - ${r.fileCount} files, ${r.folderCount} folders`;
    setVdkStats(r);
    $("#vdk-filter").value = "";
    renderVdkFiles("");
    log(`Listed ${r.fileCount} files (${r.folderCount} folders), VDISK ${r.version}.`, "ok");
    toast("Listed VDK", `${r.fileCount} files in ${baseName(vdkPath)}`, "ok");
  });

  let t = null;
  $("#vdk-filter").addEventListener("input", (e) => {
    clearTimeout(t);
    const v = e.target.value;
    t = setTimeout(() => renderVdkFiles(v), FILTER_DEBOUNCE_MS);
  });

  // Preview mode toggle (radiogroup wired by initSegs -> seg-change)
  $("#preview-modes").addEventListener("seg-change", (e) => {
    previewMode = e.detail.dataset.pmode;
    renderPreview();
  });
}

/* ======================================================================
   CT operations
   ====================================================================== */
function initCt() {
  $("#btn-ct-to").addEventListener("click", async () => {
    const ctPath = need("ctPath", "CT file");
    if (!ctPath) return;
    const format = segValue("#ctto-seg", "ctto") || "xlsx";
    const r = await run("CT -> " + format.toUpperCase(), () =>
      api("/api/ct/to", { ctPath, format }));
    log(`Converted ${baseName(ctPath)} -> ${r.outPath} (${r.columns} cols, ${r.rows} rows).`, "ok");
    toast("Converted", `${baseName(r.outPath)} (${r.rows} rows)`, "ok");
  });

  $("#btn-ct-from").addEventListener("click", async () => {
    const inPath = need("ctFromIn", "Input file");
    if (!inPath) return;
    const outPath = ($("#ctFromOut").value || "").trim() || undefined;
    const r = await run("Spreadsheet -> CT", () =>
      api("/api/ct/from", { inPath, outPath }));
    log(`Wrote ${r.outPath} (${r.columns} cols, ${r.rows} rows).`, "ok");
    toast("Wrote CT", `${baseName(r.outPath)} (${r.rows} rows)`, "ok");
  });

  $("#btn-ct-batch").addEventListener("click", async () => {
    const dir = need("batchDir", "Folder");
    if (!dir) return;
    const format = segValue("#ctbatch-seg", "ctbatch") || "xlsx";
    startJob("Batch CT -> " + format.toUpperCase(), "/api/ct/batch", { dir, format }, (res) => {
      renderBatch(res);
      const kind = res.failed > 0 ? (res.ok > 0 ? "info" : "err") : "ok";
      log(`Batch done: ${res.ok}/${res.total} ok, ${res.failed} failed.`, res.failed ? "err" : "ok");
      toast("Batch complete", `${res.ok} ok, ${res.failed} failed (of ${res.total})`, kind === "err" ? "err" : "ok");
    });
  });

  $("#btn-extract").addEventListener("click", () => {
    const vdkPath = need("vdkPath", "VDK file");
    const outDir = need("extractOut", "Extract folder");
    if (!vdkPath || !outDir) return;
    startJob("Extract VDK", "/api/vdk/extract", { vdkPath, outDir }, (res) => {
      const n = res.extracted != null ? res.extracted : "?";
      log(`Extracted ${n} files to ${outDir} (files + empty dirs).`, "ok");
      toast("Extracted", `${n} files -> ${baseName(outDir) || outDir}`, "ok");
    });
  });

  $("#btn-pack").addEventListener("click", () => {
    const srcDir = need("packSrc", "Source folder");
    const outPath = need("packOut", "Output VDK");
    if (!srcDir || !outPath) return;
    startJob("Pack VDK", "/api/vdk/pack", { srcDir, outPath }, (res) => {
      log(`Reconstructed ${res.files} files, ${res.folders} folders (${res.bytes} bytes) -> 1:1.`, "ok");
      toast("Packed VDK", `${res.files} files -> ${baseName(outPath)}`, "ok");
    });
  });
}

function renderBatch(r) {
  const tbody = $("#batch-rows");
  const table = $("#batch-table");
  tbody.innerHTML = "";
  $("#batch-meta").textContent = `${r.ok} ok / ${r.failed} failed / ${r.total} total`;
  const empty = $("#batch-empty");
  const results = r.results || [];
  if (!results.length) {
    setEmpty(empty, "inbox", "No CT files", "No .ct files were found in that folder.");
    empty.hidden = false; table.hidden = true;
    return;
  }
  empty.hidden = true; table.hidden = false;

  const frag = document.createDocumentFragment();
  results.forEach((row) => {
    const tr = document.createElement("tr");
    tr.className = row.ok ? "row-ok" : "row-bad";
    const tdS = document.createElement("td");
    tdS.className = "status-col";
    tdS.appendChild(icon(row.ok ? "check" : "cross", "status-ico"));
    const tdF = document.createElement("td");
    tdF.className = "path";
    tdF.textContent = row.file;
    const tdD = document.createElement("td");
    tdD.textContent = row.ok ? "ok" : (row.error || "error");
    tr.appendChild(tdS); tr.appendChild(tdF); tr.appendChild(tdD);
    frag.appendChild(tr);
  });
  tbody.appendChild(frag);
}

/* ======================================================================
   Health + boot
   ====================================================================== */
async function checkHealth() {
  const h = $("#health");
  const txt = $("#status-text");
  try {
    const r = await fetch("/api/health");
    const j = await r.json();
    if (j && j.ok) {
      h.className = "health ok";
      h.title = "server OK";
      if (txt) txt.textContent = "Online";
      $("#version").textContent = "v" + (j.version || "?");
      log("Connected to server v" + (j.version || "?") + ".", "ok");
    } else {
      h.className = "health bad"; h.title = "server not OK";
      if (txt) txt.textContent = "Offline";
    }
  } catch (e) {
    h.className = "health bad";
    h.title = "server unreachable";
    if (txt) txt.textContent = "Offline";
    log("Server unreachable: " + e.message, "err");
  }
}

/* ------------------------------------------------------------ Custom title bar
   The app runs as a CHROMELESS native window (no OS title bar); the app-bar is
   our title bar. Photino injects window.external.sendMessage — only then do we
   show the window controls and enable JS dragging. In --browser mode this is all
   inert (the browser keeps its own chrome). */
function setMaxIcon(isMax) {
  const b = $("#win-max");
  if (!b) return;
  const old = b.querySelector(".ico");
  if (old) old.remove();
  const name = isMax ? "win-restore" : "win-max";
  b.dataset.icon = name;
  b.prepend(icon(name));
  b.title = isMax ? "Restore" : "Maximize";
  b.setAttribute("aria-label", isMax ? "Restore" : "Maximize");
}

function initTitleBar() {
  const ext = window.external;
  const inApp = !!(ext && typeof ext.sendMessage === "function");
  document.body.classList.toggle("in-app", inApp);
  if (!inApp) return;

  const send = (m) => { try { ext.sendMessage(m); } catch (e) {} };
  $("#win-min").addEventListener("click", () => send("win:minimize"));
  $("#win-max").addEventListener("click", () => send("win:maximize"));
  $("#win-close").addEventListener("click", () => send("win:close"));

  const bar = $(".app-bar");
  const INTERACTIVE = "button, input, a, select, .tab, .theme-switch, .win-ctrls, .seg, [data-tip], [contenteditable]";

  // double-click an empty part of the title bar toggles maximize/restore
  bar.addEventListener("dblclick", (e) => {
    if (!e.target.closest(INTERACTIVE)) send("win:maximize");
  });

  // JS drag: post the cursor's SCREEN position on grab + each move; C# moves the
  // window by the same delta so the grab point stays under the cursor.
  let dragging = false;
  bar.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || e.target.closest(INTERACTIVE)) return;
    dragging = true;
    try { bar.setPointerCapture(e.pointerId); } catch (_) {}
    send("drag:start:" + Math.round(e.screenX) + ":" + Math.round(e.screenY));
  });
  bar.addEventListener("pointermove", (e) => {
    if (dragging) send("drag:move:" + Math.round(e.screenX) + ":" + Math.round(e.screenY));
  });
  const endDrag = (e) => {
    if (!dragging) return;
    dragging = false;
    try { bar.releasePointerCapture(e.pointerId); } catch (_) {}
  };
  bar.addEventListener("pointerup", endDrag);
  bar.addEventListener("pointercancel", endDrag);

  // keep the max/restore icon in sync with the actual OS window state, and hide
  // the resize handles while maximized (a maximized window must not resize).
  if (typeof ext.receiveMessage === "function") {
    ext.receiveMessage((msg) => {
      if (msg === "win:state:maximized") { setMaxIcon(true); document.body.classList.add("win-maximized"); }
      else if (msg === "win:state:restored") { setMaxIcon(false); document.body.classList.remove("win-maximized"); }
    });
  }
}

/* ------------------------------------------------------------ Window resize
   A chromeless native window has no OS resize border, so we put thin invisible
   grab zones at the edges/corners (.rz, see index.html) and drive the resize
   over the bridge: post the grabbed edge + cursor SCREEN position on grab and
   each move; C# resizes the window so the dragged edge tracks the cursor. Inert
   in --browser mode (no window.external bridge). */
function initResize() {
  const ext = window.external;
  if (!(ext && typeof ext.sendMessage === "function")) return;
  const send = (m) => { try { ext.sendMessage(m); } catch (e) {} };

  $$(".rz").forEach((h) => {
    let resizing = false;
    h.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      resizing = true;
      try { h.setPointerCapture(e.pointerId); } catch (_) {}
      send("resize:start:" + h.dataset.edge + ":" + Math.round(e.screenX) + ":" + Math.round(e.screenY));
    });
    h.addEventListener("pointermove", (e) => {
      if (resizing) send("resize:move:" + Math.round(e.screenX) + ":" + Math.round(e.screenY));
    });
    const end = (e) => {
      if (!resizing) return;
      resizing = false;
      try { h.releasePointerCapture(e.pointerId); } catch (_) {}
    };
    h.addEventListener("pointerup", end);
    h.addEventListener("pointercancel", end);
  });
}

window.addEventListener("DOMContentLoaded", () => {
  hydrateIcons();
  initTitleBar();
  initResize();
  initTheme();
  initTabs();
  initPickers();
  initDrop();
  initSegs();
  initVdk();
  initCt();
  initSettings();
  // Initial empty-states.
  setEmpty($("#vdk-empty"), "inbox", "No archive loaded", "List a VDK to see its contents.");
  $("#vdk-empty").hidden = false; $("#vdk-table").hidden = true;
  setVdkStats(null);
  setEmpty($("#batch-empty"), "inbox", "No results yet", "Run a batch conversion to see results.");
  $("#batch-empty").hidden = false; $("#batch-table").hidden = true;
  previewState("file", "Select a file to preview", "Pick an entry from the list.");
  $("#log-clear").addEventListener("click", () => { $("#log").innerHTML = ""; });
  $("#progress-cancel").addEventListener("click", cancelActiveJob);
  // Input-driven gating: wire listeners + compute initial disabled state.
  initGates();
  checkHealth();
  // Load persisted settings (default output folder) and apply the pre-fill.
  loadSettings();
  log("VDK Tool UI ready.");
});
