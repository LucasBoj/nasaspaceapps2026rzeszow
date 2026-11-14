#!/usr/bin/env node
// SELENE OPS — startup profiler. Loads the app in headless Chrome/Edge over the DevTools protocol,
// records a main-thread CPU profile and a probe of blocked intervals, and prints where the time went.
// No dependencies (Node ≥ 22: global WebSocket).
//
//   node tools/startup-profile/profile.mjs <url> [--seconds 12] [--cpu 1] [--gpu] [--headed] [--dump] [--save out.cpuprofile] [--no-profile] [--click <button text> --click-at <ms>] [--init <js>] [--sample <js expr>] [--browser <path>]
//   node tools/startup-profile/profile.mjs http://127.0.0.1:5290/ --cpu 4      # 4× slower CPU (weak laptop)
//
// Why headless: a hidden or occluded browser window throttles timers to ~1 Hz and makes every measurement
// look like a 1-second freeze. Headless Chrome renders at full rate.

import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, existsSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, basename } from 'node:path';

const args = process.argv.slice(2);
const url = args.find(a => !a.startsWith('--') && !/^\d/.test(a));
const opt = (name, def) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : def; };
const seconds = Number(opt('seconds', 12));
const cpu = Number(opt('cpu', 1));
const headed = args.includes('--headed');
// Real GPU instead of software WebGL (SwiftShader), which makes Cesium's rendering look CPU-bound.
const gpu = args.includes('--gpu') ? ['--enable-gpu', '--ignore-gpu-blocklist', '--use-angle=default', '--enable-features=Vulkan'] : [];
if (!url) { console.error('usage: profile.mjs <url> [--seconds 12] [--cpu 1] [--browser path] [--headed]'); process.exit(2); }

const candidates = [opt('browser'), process.env.CHROME,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'].filter(Boolean);
const browserPath = candidates.find(p => existsSync(p));
if (!browserPath) { console.error('No Chrome/Edge found; pass --browser <path>'); process.exit(3); }

// Runs in the page before any of its scripts: blocked intervals (timer gaps), long frames and milestones.
const PROBE = `(() => {
  const P = window.__probe = { gaps: [], frames: [], marks: [] };
  const t0 = P.t0 = performance.now();
  const m = n => P.marks.push([n, Math.round(performance.now() - t0)]);
  let last = t0;
  setInterval(() => { const n = performance.now(); if (n - last > 50) P.gaps.push([Math.round(last - t0), Math.round(n - last)]); last = n; }, 5);
  let lf = 0; const raf = t => { if (lf && t - lf > 50) P.frames.push([Math.round(lf - t0), Math.round(t - lf)]); lf = t; requestAnimationFrame(raf); };
  requestAnimationFrame(raf);
  const seen = {};
  const q = (k, sel) => { if (!seen[k] && document.querySelector(sel)) { seen[k] = 1; m(k); } };
  const check = () => {
    q('cesium canvas', '.cesium-widget canvas');
    q('first verdict', '.verdict-card, .results-summary, .robust-item');
    q('heatmap', '.best-date');
    q('3D model ready', '.model-status.ready, [data-model-state="ready"]');
  };
  addEventListener('DOMContentLoaded', () => { m('DOMContentLoaded'); new MutationObserver(check).observe(document.documentElement, { subtree: true, childList: true, attributes: true }); });
  const poll = setInterval(() => {
    if (window.Cesium && !seen.c) {
      seen.c = 1; m('Cesium.js executed');
      // Count globe renders (frame rate of the 3D view, independent of how loaded the machine is).
      const render = Cesium.Scene.prototype.render; P.renders = [];
      Cesium.Scene.prototype.render = function (...a) { P.renders.push(Math.round(performance.now() - t0)); return render.apply(this, a); };
    }
    if (window.Blazor && !seen.b) { seen.b = 1; m('blazor.webassembly.js executed'); }
    if (seen.c && seen.b) clearInterval(poll);
  }, 2);
})();`;

const userDir = mkdtempSync(join(tmpdir(), 'selene-profile-'));
const proc = spawn(browserPath, [
  ...(headed ? [] : ['--headless=new']), ...gpu, '--remote-debugging-port=0', `--user-data-dir=${userDir}`,
  '--no-first-run', '--no-default-browser-check', '--window-size=1920,1080', '--disable-extensions', 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

const wsUrl = await new Promise((resolve, reject) => {
  let buf = '';
  const timer = setTimeout(() => reject(new Error('browser did not report a DevTools URL')), 15000);
  proc.stderr.on('data', d => {
    buf += d; const mm = buf.match(/DevTools listening on (ws:\/\/\S+)/);
    if (mm) { clearTimeout(timer); resolve(mm[1]); }
  });
});

const ws = new WebSocket(wsUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));
let nextId = 1; const pending = new Map(); const listeners = [];
ws.addEventListener('message', ev => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { const { resolve, reject } = pending.get(msg.id); pending.delete(msg.id); msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result); }
  else listeners.forEach(l => l(msg));
});
const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
  const id = nextId++; pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
});

let exitCode = 0;
try {
  const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  const s = (m, p) => send(m, p, sessionId);
  const consoleErrors = [];
  listeners.push(msg => {
    if (msg.sessionId !== sessionId) return;
    if (msg.method === 'Runtime.exceptionThrown') consoleErrors.push(msg.params.exceptionDetails.exception?.description?.split('\n')[0] ?? msg.params.exceptionDetails.text);
    if (msg.method === 'Runtime.consoleAPICalled' && ['error', 'warning'].includes(msg.params.type))
      consoleErrors.push(`${msg.params.type}: ${msg.params.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 200)}`);
  });
  await s('Page.enable'); await s('Runtime.enable'); await s('Network.enable');
  await s('Network.setCacheDisabled', { cacheDisabled: true });
  await s('Emulation.setCPUThrottlingRate', { rate: cpu });
  await s('Page.addScriptToEvaluateOnNewDocument', { source: PROBE });
  if (opt('init')) await s('Page.addScriptToEvaluateOnNewDocument', { source: opt('init') });   // e.g. localStorage flags
  // --sample "<js expression>": evaluated every 500 ms, printed whenever its value changes.
  const samples = [];
  const sampler = opt('sample') ? setInterval(async () => {
    try {
      const v = JSON.stringify((await s('Runtime.evaluate', { expression: opt('sample'), returnByValue: true })).result.value);
      if (samples.at(-1)?.[1] !== v) samples.push([Date.now() - navStart, v]);
    } catch {}
  }, 500) : 0;
  const profiling = !args.includes('--no-profile');   // the sampling profiler itself slows WebAssembly down
  if (profiling) { await s('Profiler.enable'); await s('Profiler.setSamplingInterval', { interval: 1000 }); await s('Profiler.start'); }
  var navStart = Date.now();
  await s('Page.navigate', { url });
  // Optional: click a button by its text at a given time (e.g. --click "Quick demo" --click-at 8000) to measure
  // the landing animation; blocked intervals are then reported separately before and after the click.
  const clickText = opt('click'); const clickAt = Number(opt('click-at', 8000));
  if (clickText) {
    await new Promise(r => setTimeout(r, clickAt));
    const ok = (await s('Runtime.evaluate', { expression: `(() => { const b = [...document.querySelectorAll('button')].find(x => x.innerText.includes(${JSON.stringify(clickText)}) && !x.disabled); if (!b) return false; window.__probe?.marks.push(['click: ' + ${JSON.stringify(clickText)}, Math.round(performance.now() - window.__probe.t0)]); b.click(); return true; })()`, returnByValue: true })).result.value;
    if (!ok) console.log(`(button "${clickText}" not found or disabled at ${clickAt} ms)`);
  }
  await new Promise(r => setTimeout(r, Math.max(0, seconds * 1000 - (clickText ? clickAt : 0))));
  const { profile } = profiling ? await s('Profiler.stop') : { profile: { nodes: [], samples: [], timeDeltas: [] } };
  if (opt('save')) { writeFileSync(opt('save'), JSON.stringify(profile)); console.log(`CPU profile saved: ${opt('save')} (open in DevTools → Performance)`); }
  const probe = (await s('Runtime.evaluate', { expression: 'JSON.stringify(window.__probe || null)', returnByValue: true })).result.value;
  const gl = (await s('Runtime.evaluate', { expression: `(() => { const c = document.createElement('canvas').getContext('webgl2') || document.createElement('canvas').getContext('webgl'); if (!c) return 'no WebGL'; const e = c.getExtension('WEBGL_debug_renderer_info'); return e ? c.getParameter(e.UNMASKED_RENDERER_WEBGL) : c.getParameter(c.RENDERER); })()`, returnByValue: true })).result.value;
  report(JSON.parse(probe), profile, Date.now() - navStart);
  console.log(`
WebGL renderer: ${gl}${/swiftshader|software|llvmpipe/i.test(gl) ? '  ← software rendering: Cesium CPU time is inflated, try --gpu or --headed' : ''}`);
  if (sampler) { clearInterval(sampler); console.log('\nSamples (ms after navigation → value):'); for (const [t, v] of samples) console.log(`  ${String(t).padStart(6)}  ${v}`); }
  if (args.includes('--dump')) {
    const text = (await s('Runtime.evaluate', { expression: 'document.body.innerText.replace(/\\s+/g, " ").slice(0, 600)', returnByValue: true })).result.value;
    console.log(`\nPage text: ${text}`);
  }
  if (consoleErrors.length) console.log(`\nConsole errors/warnings (${consoleErrors.length}):\n  ` + consoleErrors.slice(0, 15).join('\n  '));
} catch (e) {
  console.error(e); exitCode = 1;
} finally {
  ws.close(); proc.kill();
  setTimeout(() => { try { rmSync(userDir, { recursive: true, force: true }); } catch {} process.exit(exitCode); }, 500);
}

function category(frame) {
  const u = frame.url || '';
  const f = frame.functionName || '';
  if (!u) {
    if (f === '(garbage collector)') return 'GC';
    if (f === '(idle)') return 'idle';
    if (f === '(program)') return 'browser (layout, paint, parse)';
    return f || '(native)';
  }
  const b = basename(u.split('?')[0]);
  if (/^Cesium\.js$/.test(b)) return 'Cesium.js';
  if (/^dotnet\.native.*\.wasm$/.test(b) || u.startsWith('wasm://')) return '.NET (wasm)';
  if (/^dotnet\./.test(b)) return '.NET (js runtime)';
  if (/^blazor\./.test(b)) return 'blazor.webassembly.js';
  return b;
}

function report(probe, profile, wallMs) {
  // Self time per category, overall and per 250 ms window.
  const byId = new Map(profile.nodes.map(n => [n.id, n]));
  const totals = new Map(); const windows = new Map();
  let t = 0;
  for (let i = 0; i < profile.samples.length; i++) {
    t += profile.timeDeltas[i] / 1000;                     // ms since profile start
    const dt = (profile.timeDeltas[i + 1] ?? 0) / 1000;
    const cat = category(byId.get(profile.samples[i]).callFrame);
    totals.set(cat, (totals.get(cat) || 0) + dt);
    const w = Math.floor(t / 250);
    if (!windows.has(w)) windows.set(w, new Map());
    windows.get(w).set(cat, (windows.get(w).get(cat) || 0) + dt);
  }
  const fmt = ms => `${Math.round(ms)} ms`.padStart(8);
  console.log(`\nSELENE startup profile · ${url} · CPU ×${cpu} · ${seconds} s · ${basename(browserPath)}\n`);
  console.log('Milestones (ms after navigation):');
  for (const [name, at] of probe?.marks ?? []) console.log(`  ${String(at).padStart(6)}  ${name}`);
  const gaps = probe?.gaps ?? [];
  const blocked = gaps.reduce((a, g) => a + g[1], 0);
  console.log(`\nMain thread blocked > 50 ms: ${gaps.length}× · ${blocked} ms total · longest ${Math.max(0, ...gaps.map(g => g[1]))} ms`);
  for (const [at, len] of gaps) {
    const cats = new Map();
    for (let w = Math.floor(at / 250); w <= Math.floor((at + len) / 250); w++)
      for (const [c, v] of windows.get(w) ?? []) if (c !== 'idle') cats.set(c, (cats.get(c) || 0) + v);
    const top = [...cats].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([c, v]) => `${c} ${Math.round(v)}`).join(' · ');
    console.log(`  at ${String(at).padStart(5)} ms  ${String(len).padStart(4)} ms   ${top}`);
  }
  const click = (probe?.marks ?? []).find(([n]) => n.startsWith('click: '));
  if (click) {
    const before = gaps.filter(g => g[0] < click[1]), after = gaps.filter(g => g[0] >= click[1]);
    const sum = a => a.reduce((x, g) => x + g[1], 0);
    console.log(`  before the click: ${before.length}× · ${sum(before)} ms · after (${click[0].slice(7)}): ${after.length}× · ${sum(after)} ms · longest after ${Math.max(0, ...after.map(g => g[1]))} ms`);
  }
  const renders = probe?.renders ?? [];
  if (renders.length) {
    const fps = (a, b) => (renders.filter(r => r >= a && r < b).length / ((b - a) / 1000)).toFixed(1);
    const end = Math.round(wallMs);
    const parts = click ? [[0, click[1]], [click[1], click[1] + 7000], [click[1] + 7000, end]] : [[0, end]];
    console.log(`Cesium renders: ${renders.length} · fps ` + parts.map(([a, b]) => `${(a / 1000).toFixed(0)}–${(b / 1000).toFixed(0)} s: ${fps(a, b)}`).join(' · '));
  }
  const frames = probe?.frames ?? [];
  console.log(`Frames longer than 50 ms: ${frames.length} (longest ${Math.max(0, ...frames.map(f => f[1]))} ms)`);
  console.log(`\nCPU self time, first ${Math.round(wallMs / 1000)} s:`);
  for (const [c, v] of [...totals].sort((a, b) => b[1] - a[1]).slice(0, 12)) console.log(`  ${fmt(v)}  ${c}`);
}
