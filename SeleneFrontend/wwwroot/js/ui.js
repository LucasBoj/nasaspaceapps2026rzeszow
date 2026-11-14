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
