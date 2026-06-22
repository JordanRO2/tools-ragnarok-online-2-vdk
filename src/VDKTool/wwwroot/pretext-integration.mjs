/* ======================================================================
   VDK Toolkit — pretext integration (progressive enhancement, offline).

   Uses the vendored @chenglou/pretext (./vendor/pretext.mjs) to measure and
   lay out text WITHOUT triggering DOM reflow (no getBoundingClientRect /
   offsetHeight on the hot path). It plugs into the optional window.VDKUI hub
   that app.js exposes:

     - Preview pane (Text mode): exact line count + correct wrapping for the
       panel width, an "N lines · X/Y bytes" info line, and virtualization
       (only the visible line window is rendered) for large content.
     - Log panel: measures/lays out each growing log line without reflow.

   FALLBACK: if this module, the vendored bundle, or its runtime deps
   (Intl.Segmenter + Canvas 2D measureText) are missing, we leave the VDKUI
   hooks null so app.js keeps its plain <pre>/pre-wrap render. We never touch
   endpoints, app state, or the existing wiring — this is purely client-side
   rendering on top of data app.js already fetched.
   ====================================================================== */

"use strict";

/* ------------------------------------------------------------- capability gate */
function runtimeSupported() {
  try {
    if (typeof Intl === "undefined" || typeof Intl.Segmenter !== "function") return false;
    const c = document.createElement("canvas");
    const ctx = c.getContext && c.getContext("2d");
    if (!ctx || typeof ctx.measureText !== "function") return false;
    return true;
  } catch (_) {
    return false;
  }
}

/* Resolve a canvas-style font shorthand ("13px Consolas") from an element's
   computed style. pretext needs a NAMED font for accurate layout. */
function fontShorthandFor(el) {
  const cs = getComputedStyle(el);
  const style = cs.fontStyle && cs.fontStyle !== "normal" ? cs.fontStyle + " " : "";
  const weight = cs.fontWeight && cs.fontWeight !== "400" && cs.fontWeight !== "normal"
    ? cs.fontWeight + " " : "";
  const size = cs.fontSize || "13px";
  const family = cs.fontFamily || "monospace";
  return `${style}${weight}${size} ${family}`;
}

/* Numeric line-height in px from computed style, with a sane fallback derived
   from font-size when line-height is "normal". */
function lineHeightFor(el) {
  const cs = getComputedStyle(el);
  let lh = parseFloat(cs.lineHeight);
  if (!isFinite(lh) || lh <= 0) {
    const fs = parseFloat(cs.fontSize) || 13;
    lh = Math.round(fs * 1.5);
  }
  return lh;
}

/* Horizontal padding so the available text width matches what the browser
   would wrap at (content-box width). */
function horizontalPadding(el) {
  const cs = getComputedStyle(el);
  return (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0);
}

function fmtBytesShort(n) {
  if (n == null) return "";
  if (n < 1024) return n + " B";
  const u = ["KB", "MB", "GB", "TB"];
  let i = -1, v = n;
  do { v /= 1024; i++; } while (v >= 1024 && i < u.length - 1);
  return v.toFixed(v < 10 ? 1 : 0) + " " + u[i];
}

/* ============================================================ main install */
async function install() {
  if (!runtimeSupported()) {
    // Leave hooks null; app.js fallback render stays in effect.
    console.info("[pretext] runtime unsupported (Intl.Segmenter/Canvas); using fallback render.");
    return;
  }

  let pretext;
  try {
    pretext = await import("/vendor/pretext.mjs");
  } catch (e) {
    console.warn("[pretext] vendored bundle failed to load; using fallback render.", e);
    return;
  }

  const { prepare, prepareWithSegments, layout, layoutWithLines } = pretext;
  if (typeof prepare !== "function" || typeof layout !== "function" ||
      typeof prepareWithSegments !== "function" || typeof layoutWithLines !== "function") {
    console.warn("[pretext] unexpected module shape; using fallback render.");
    return;
  }

  /* -------------------------------------------------- Preview virtualizer */
  // Per-body virtualization state. Keyed by the body element via a WeakMap so
  // multiple resets don't leak listeners.
  const VIRT = new WeakMap();

  // Render only the visible slice of lines into an absolutely-positioned window
  // inside a full-height sizer. Heights come from pretext's fixed line height.
  function paintWindow(state) {
    const { body, win, lines, lineHeight, overscan } = state;
    const scrollTop = body.scrollTop;
    const viewH = body.clientHeight || 1;
    let first = Math.floor(scrollTop / lineHeight) - overscan;
    let last = Math.ceil((scrollTop + viewH) / lineHeight) + overscan;
    if (first < 0) first = 0;
    if (last > lines.length) last = lines.length;

    // Avoid repaint if the visible range is unchanged.
    if (state.first === first && state.last === last) return;
    state.first = first;
    state.last = last;

    const frag = document.createDocumentFragment();
    for (let i = first; i < last; i++) {
      const div = document.createElement("div");
      div.className = "vt-line";
      div.style.height = lineHeight + "px";
      div.style.lineHeight = lineHeight + "px";
      // A blank line still needs height; keep the box but no glyphs.
      div.textContent = lines[i].text.length ? lines[i].text : "​";
      frag.appendChild(div);
    }
    win.replaceChildren(frag);
    win.style.transform = `translateY(${first * lineHeight}px)`;
  }

  function layoutPreview(state) {
    const { body } = state;
    const font = fontShorthandFor(body);
    const lineHeight = lineHeightFor(body);
    const width = Math.max(1, body.clientWidth - horizontalPadding(body));

    // Exact line count + height for the info line / sizer (pure arithmetic).
    const prepared = prepare(state.text, font, { whiteSpace: "pre-wrap" });
    const { lineCount, height } = layout(prepared, width, lineHeight);

    // Full set of wrapped lines for rendering (segmented handle).
    const seg = prepareWithSegments(state.text, font, { whiteSpace: "pre-wrap" });
    const { lines } = layoutWithLines(seg, width, lineHeight);

    state.lines = lines;
    state.lineHeight = lineHeight;
    state.first = -1; state.last = -1; // force repaint

    const total = (lines.length || lineCount) * lineHeight;
    state.sizer.style.height = total + "px";

    paintWindow(state);

    // Info line: "N lines · X/Y bytes" (truncated reflected by app.js banner).
    if (state.info) {
      const n = lines.length || Math.max(1, lineCount);
      const bytes = state.meta.truncated
        ? `${fmtBytesShort(state.meta.shownBytes)} / ${fmtBytesShort(state.meta.totalBytes)} bytes`
        : `${fmtBytesShort(state.meta.totalBytes)} bytes`;
      state.info.replaceChildren();
      const li = document.createElement("span");
      li.textContent = n.toLocaleString() + (n === 1 ? " line" : " lines");
      const dot = document.createElement("span");
      dot.className = "pi-dot"; dot.textContent = "·";
      const by = document.createElement("span");
      by.textContent = bytes;
      state.info.append(li, dot, by);
      state.info.hidden = false;
    }
  }

  function teardown(body) {
    const state = VIRT.get(body);
    if (!state) return;
    try { body.removeEventListener("scroll", state.onScroll); } catch (_) {}
    try { if (state.ro) state.ro.disconnect(); } catch (_) {}
    VIRT.delete(body);
  }

  window.VDKUI.renderPreviewText = function (body, info, text, meta) {
    // Reset any prior state, then (re)build the virtualized structure.
    teardown(body);
    body.textContent = "";
    body.classList.add("virt");

    const sizer = document.createElement("div");
    sizer.className = "vt-sizer";
    const win = document.createElement("div");
    win.className = "vt-window";
    sizer.appendChild(win);
    body.appendChild(sizer);

    const state = {
      body, info, sizer, win,
      text: text || "",
      meta: meta || {},
      lines: [],
      lineHeight: lineHeightFor(body),
      first: -1, last: -1,
      overscan: 8,
      onScroll: null,
      ro: null,
    };

    state.onScroll = () => paintWindow(state);
    body.addEventListener("scroll", state.onScroll, { passive: true });

    // Re-layout (rewrap) when the panel width changes.
    if (typeof ResizeObserver === "function") {
      let raf = 0, lastW = -1;
      state.ro = new ResizeObserver((entries) => {
        const w = Math.round(entries[0].contentRect.width);
        if (w === lastW) return; // height-only changes don't rewrap
        lastW = w;
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(() => { if (VIRT.get(body) === state) layoutPreview(state); });
      });
      state.ro.observe(body);
    }

    VIRT.set(body, state);
    layoutPreview(state);
    body.scrollTop = 0;
    return true; // handled
  };

  window.VDKUI.onPreviewReset = function (body /*, info */) {
    teardown(body);
  };

  /* ------------------------------------------------------------- Log panel */
  // Measure each log line so its box height is set from pretext rather than
  // forcing the browser to reflow-measure wrapped text. The log uses pre-wrap;
  // we measure against the line's own .msg metrics for the panel width.
  const logEl = document.getElementById("log");
  let logFont = null, logLineHeight = 16, logPad = 0;

  function refreshLogMetrics() {
    if (!logEl) return;
    logFont = fontShorthandFor(logEl);
    logLineHeight = lineHeightFor(logEl);
    logPad = horizontalPadding(logEl);
  }
  refreshLogMetrics();

  window.VDKUI.onLogEntry = function (entryEl, msg) {
    if (!logEl) return;
    try {
      if (!logFont) refreshLogMetrics();
      const width = Math.max(1, logEl.clientWidth - logPad);
      const prepared = prepare(String(msg == null ? "" : msg), logFont, { whiteSpace: "pre-wrap" });
      const { lineCount } = layout(prepared, width, logLineHeight);
      const lines = Math.max(1, lineCount);
      // Hint the box height from the measured wrap so layout is reflow-free.
      // (.msg is inline; the timestamp shares the first line, so this is a
      //  lower bound that prevents jitter as the log streams.)
      entryEl.style.minHeight = (lines * logLineHeight) + "px";
    } catch (_) { /* never break logging */ }
  };

  /* ------------------------------------------------------- theme re-measure */
  window.VDKUI.onThemeChanged = function () {
    // Font metrics can shift with the theme (weights/letter-spacing); re-derive
    // and re-layout the active preview + refresh log metrics.
    refreshLogMetrics();
    const body = document.getElementById("preview-body");
    if (body) {
      const state = VIRT.get(body);
      if (state) {
        // requestAnimationFrame so computed styles reflect the new theme first.
        requestAnimationFrame(() => { if (VIRT.get(body) === state) layoutPreview(state); });
      }
    }
  };

  console.info("[pretext] integration active (preview virtualization + log layout).");
}

install();
