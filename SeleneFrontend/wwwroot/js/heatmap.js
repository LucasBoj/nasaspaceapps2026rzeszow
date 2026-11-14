// Canvas heatmap "site × landing day". One canvas instead of thousands of DOM cells.

const MARGIN = { left: 150, right: 10, top: 20, bottom: 4 };
const NO_GO = "#2a2f36";
const LOW = [16, 44, 58];
const HIGH = [79, 195, 247];
const AMBER = "#ffb020";
const VERDICTS = ["FEASIBLE", "MARGINAL", "NOT FEASIBLE"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

const states = new WeakMap();

function cellColor(score) {
  if (!(score > 0)) return NO_GO;
  const t = Math.pow(Math.min(score, 100) / 100, 0.85);
  const c = LOW.map((lo, k) => Math.round(lo + (HIGH[k] - lo) * t));
  return `rgb(${c[0]},${c[1]},${c[2]})`;
}

function geometry(canvas, data) {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  const plotW = Math.max(1, w - MARGIN.left - MARGIN.right);
  const plotH = Math.max(1, h - MARGIN.top - MARGIN.bottom);
  return { w, h, plotW, plotH, cw: plotW / data.days, rh: plotH / data.sites.length };
}

function dayDate(data, d) {
  const t = Date.parse(data.firstDay + "T00:00:00Z") + d * 86400000;
  return new Date(t);
}

function draw(canvas, pulse = 0) {
  const st = states.get(canvas);
  if (!st || !st.data) return;
  const data = st.data;
  const dpr = window.devicePixelRatio || 1;
  const g = geometry(canvas, data);
  canvas.width = Math.round(g.w * dpr);
  canvas.height = Math.round(g.h * dpr);
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, g.w, g.h);

  ctx.font = '11px "IBM Plex Mono", monospace';
  ctx.textBaseline = "middle";

  // Cells (merge runs of identical colour per row for speed and crisp edges).
  for (let s = 0; s < data.sites.length; s++) {
    const y = MARGIN.top + s * g.rh;
    let runStart = 0, runColor = cellColor(data.scores[s * data.days]);
    for (let d = 1; d <= data.days; d++) {
      const color = d < data.days ? cellColor(data.scores[s * data.days + d]) : null;
      if (color !== runColor) {
        ctx.fillStyle = runColor;
        ctx.fillRect(MARGIN.left + runStart * g.cw, y + 1, (d - runStart) * g.cw + 0.5, g.rh - 2);
        runStart = d; runColor = color;
      }
    }
    ctx.fillStyle = s === data.selected[0] ? "#d7e2ec" : "#7f93a6";
    ctx.textAlign = "right";
    ctx.fillText(data.sites[s].toUpperCase(), MARGIN.left - 8, y + g.rh / 2);
  }

  // Month labels and separators.
  ctx.textAlign = "left";
  for (let d = 0; d < data.days; d++) {
    const date = dayDate(data, d);
    if (d === 0 || date.getUTCDate() === 1) {
      const x = MARGIN.left + d * g.cw;
      ctx.fillStyle = "rgba(127,147,166,0.35)";
      ctx.fillRect(x, MARGIN.top, 1, g.plotH);
      ctx.fillStyle = "#7f93a6";
      const label = MONTHS[date.getUTCMonth()] + (date.getUTCMonth() === 0 || d === 0 ? " " + String(date.getUTCFullYear()).slice(2) : "");
      if (g.cw * 28 > 30 || date.getUTCMonth() % 3 === 0 || d === 0) ctx.fillText(label, x + 2, MARGIN.top / 2);
    }
  }

  // Selected cell (thin light outline) and best cell (amber + label).
  const outline = (s, d, color, width) => {
    if (s < 0 || d < 0) return;
    const minW = 5;
    const cx = MARGIN.left + d * g.cw + g.cw / 2;
    const wpx = Math.max(minW, g.cw);
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.strokeRect(cx - wpx / 2, MARGIN.top + s * g.rh + 0.5, wpx, g.rh - 1);
  };
  outline(data.selected[0], data.selected[1], "#d7e2ec", 1);
  const [bs, bd] = data.best;
  if (bs >= 0 && bd >= 0) {
    if (pulse > 0) {
      // Attention pulse (QUICK DEMO): an expanding amber glow around the best cell.
      ctx.save();
      ctx.shadowColor = AMBER;
      ctx.shadowBlur = 18 * pulse;
      outline(bs, bd, `rgba(255,176,32,${0.35 + 0.65 * pulse})`, 2 + 4 * pulse);
      ctx.restore();
    }
    outline(bs, bd, AMBER, 2);
    const label = "BEST WINDOW";
    ctx.font = '600 10px "IBM Plex Mono", monospace';
    const tw = ctx.measureText(label).width + 8;
    let lx = MARGIN.left + bd * g.cw + Math.max(5, g.cw) / 2 + 6;
    if (lx + tw > g.w - MARGIN.right) lx = MARGIN.left + bd * g.cw - tw - 6;
    const ly = MARGIN.top + bs * g.rh + g.rh / 2;
    ctx.fillStyle = "rgba(10,14,19,0.9)";
    ctx.fillRect(lx, ly - 8, tw, 16);
    ctx.strokeStyle = AMBER; ctx.lineWidth = 1;
    ctx.strokeRect(lx + 0.5, ly - 7.5, tw - 1, 15);
    ctx.fillStyle = AMBER;
    ctx.fillText(label, lx + 4, ly);
  }
}

function cellAt(canvas, evt) {
  const st = states.get(canvas);
  if (!st || !st.data) return null;
  const rect = canvas.getBoundingClientRect();
  const g = geometry(canvas, st.data);
  const x = evt.clientX - rect.left - MARGIN.left;
  const y = evt.clientY - rect.top - MARGIN.top;
  if (x < 0 || y < 0 || x >= g.plotW || y >= g.plotH) return null;
  return { s: Math.floor(y / g.rh), d: Math.floor(x / g.cw), x: evt.clientX - rect.left, y: evt.clientY - rect.top };
}

export function render(canvas, tooltip, data, dotNetRef) {
  let st = states.get(canvas);
  if (!st) {
    st = { data: null, dotNetRef };
    states.set(canvas, st);
    canvas.addEventListener("mousemove", (e) => {
      const c = cellAt(canvas, e);
      if (!c) { tooltip.hidden = true; canvas.style.cursor = ""; return; }
      const dd = st.data, i = c.s * dd.days + c.d;
      const score = dd.scores[i];
      tooltip.textContent = `${dd.sites[c.s]} · land ${dayDate(dd, c.d).toISOString().slice(0, 10)} · ` +
        (score > 0 ? `score ${Math.round(score)} · ${VERDICTS[dd.verdicts[i]]}` : "NOT FEASIBLE");
      tooltip.hidden = false;
      const left = Math.min(c.x + 12, canvas.clientWidth - tooltip.offsetWidth - 4);
      tooltip.style.left = `${Math.max(0, left)}px`;
      tooltip.style.top = `${c.y + 14}px`;
      canvas.style.cursor = "pointer";
    });
    canvas.addEventListener("mouseleave", () => { tooltip.hidden = true; });
    canvas.addEventListener("click", (e) => {
      const c = cellAt(canvas, e);
      if (c) st.dotNetRef.invokeMethodAsync("OnCellClicked", c.s, c.d);
    });
    st.resize = new ResizeObserver(() => draw(canvas));
    st.resize.observe(canvas);
  }
  st.data = data;
  st.dotNetRef = dotNetRef;
  draw(canvas);

  const reduced = (() => { try { return matchMedia("(prefers-reduced-motion: reduce)").matches; } catch { return false; } })();
  if (data.pulse && data.pulse !== st.lastPulse) {
    st.lastPulse = data.pulse;
    if (!reduced) {
      const start = performance.now(), dur = 2400;
      const tick = (now) => {
        if (states.get(canvas) !== st || st.data !== data) return;
        const t = (now - start) / dur;
        if (t >= 1) { draw(canvas); return; }
        draw(canvas, Math.abs(Math.sin(t * Math.PI * 3)) * (1 - t * 0.5));
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }
  }
}

export function dispose(canvas) {
  const st = states.get(canvas);
  if (st?.resize) st.resize.disconnect();
  states.delete(canvas);
}
