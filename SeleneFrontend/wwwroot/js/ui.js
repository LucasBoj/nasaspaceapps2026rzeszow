// Small browser helpers for the Blazor UI. Every storage access is guarded: private mode,
// blocked site data or previews can make localStorage throw or come back empty.

export function getItem(key) {
  try { return window.localStorage.getItem(key); } catch { return null; }
}

export function setItem(key, value) {
  try { window.localStorage.setItem(key, value); } catch { /* ignore */ }
}

export function prefersReducedMotion() {
  try { return window.matchMedia("(prefers-reduced-motion: reduce)").matches; } catch { return false; }
}

/** Resolve true once the deferred Cesium script has run (false on load error or timeout). */
export function whenCesiumReady(timeoutMs = 30000) {
  if (window.Cesium) return Promise.resolve(true);
  return new Promise((resolve) => {
    let settled = false;
    const done = (ok) => { if (!settled) { settled = true; resolve(ok); } };
    const script = document.querySelector('script[src*="Cesium.js"]');
    script?.addEventListener("load", () => done(!!window.Cesium), { once: true });
    script?.addEventListener("error", () => done(false), { once: true });
    const t0 = performance.now();
    const poll = () => {
      if (window.Cesium) return done(true);
      if (performance.now() - t0 > timeoutMs) return done(false);
      setTimeout(poll, 50);
    };
    poll();
  });
}

export function viewportHeight() {
  return window.innerHeight || document.documentElement.clientHeight || 800;
}

export function scrollToId(id) {
  const el = document.getElementById(id);
  if (el) el.scrollIntoView({ behavior: prefersReducedMotion() ? "auto" : "smooth", block: "start" });
}

export function focusElement(el) {
  try { el?.focus({ preventScroll: true }); } catch { /* ignore */ }
}

// Animate the number inside `el` from 0 to `to` (keeps prefix/suffix text untouched).
const running = new WeakMap();
export function countUp(el, to, decimals, durationMs) {
  if (!el) return;
  const fmt = (v) => v.toFixed(decimals);
  if (prefersReducedMotion() || !(durationMs > 0)) { el.textContent = fmt(to); return; }
  const token = {};
  running.set(el, token);
  const start = performance.now();
  const step = (now) => {
    if (running.get(el) !== token) return;
    const t = Math.min(1, (now - start) / durationMs);
    const eased = 1 - Math.pow(1 - t, 3);
    el.textContent = fmt(to * eased);
    if (t < 1) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}

// ---- Analyst mode: Moon / analysis panel heights (CSS variable --moon-h on .app-container) ----

const MIN_MOON_PX = 320;
const app = () => document.querySelector(".app-container");
const px = (sel) => document.querySelector(sel)?.offsetHeight ?? 0;

/**
 * Give the analysis panel the height it needs: the Moon area shrinks (not below MIN_MOON_PX) so the
 * timeline and heatmap are visible without scrolling inside the panel. If both still do not fit (small
 * screens), the page scrolls smoothly to the panel instead. `force` overrides a size the user dragged.
 */
export function fitResults(force) {
  const a = app(), strip = document.getElementById("results-strip");
  if (!a || !strip || !a.classList.contains("mode-analyst")) return;
  if (a.dataset.userSized && !force) return;
  if (force) delete a.dataset.userSized;
  const need = strip.scrollHeight;
  const room = window.innerHeight - px(".top-bar") - px(".app-footer") - need;
  a.style.setProperty("--moon-h", `${Math.max(MIN_MOON_PX, room)}px`);
  if (room < MIN_MOON_PX) {
    requestAnimationFrame(() => strip.scrollIntoView({ behavior: prefersReducedMotion() ? "auto" : "smooth", block: "start" }));
  }
}

export function resetMoonHeight() {
  const a = app();
  if (!a) return;
  delete a.dataset.userSized;
  a.style.removeProperty("--moon-h");
}

/** Drag handle (pointer) and arrow keys on the separator above the analysis panel. */
export function attachResultsResize(handle) {
  if (!handle || handle.__attached) return;
  handle.__attached = true;
  const moonH = () => document.querySelector(".main-content")?.getBoundingClientRect().height ?? 400;
  const set = (h) => {
    const a = app(); if (!a) return;
    const max = window.innerHeight - px(".top-bar") - 48;
    a.dataset.userSized = "1";
    const v = Math.round(Math.min(max, Math.max(MIN_MOON_PX - 80, h)));
    a.style.setProperty("--moon-h", `${v}px`);
    handle.setAttribute("aria-valuenow", String(Math.round((100 * v) / window.innerHeight)));
  };
  handle.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    handle.setPointerCapture(e.pointerId);
    const y0 = e.clientY, h0 = moonH();
    const move = (ev) => set(h0 + (ev.clientY - y0));
    const up = () => { handle.removeEventListener("pointermove", move); handle.removeEventListener("pointerup", up); };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", up);
  });
  handle.addEventListener("keydown", (e) => {
    if (e.key === "ArrowUp") { e.preventDefault(); set(moonH() - 40); }
    if (e.key === "ArrowDown") { e.preventDefault(); set(moonH() + 40); }
  });
}

export function focusSelector(selector) {
  try { document.querySelector(selector)?.focus({ preventScroll: true }); } catch { /* ignore */ }
}
