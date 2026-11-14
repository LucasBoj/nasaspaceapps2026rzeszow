// Mission replay: the simulated mission played back in ~24 s on the landed lander.
// Everything shown comes from the simulation result and the precomputed illumination series, hour by
// hour, interpolated between the hours: Sun direction (scene light → moving shadows), mission clock,
// battery, sunlit / in shadow, Earth contact. The end depends on the result and the lander class:
//   lost          — playback stops at the hour the battery ran empty → MISSION LOST
//   complete-clps — robotic CLPS landers stay on the Moon; the light fades out → end of operations
//   complete-hls  — crewed landers lift off (engine plume, camera follows) → MISSION COMPLETE
import { EngineFx } from "./engineFx.js";

const BASE_SECONDS = 24;          // playback length at 1×
const ASCENT_SECONDS = 5;
const FADE_SECONDS = 2.2;

let run = null;   // the running (or finished) replay

const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
const lerp = (a, b, t) => a + (b - a) * t;
const lerpAngle = (a, b, t) => { let d = ((b - a + 540) % 360) - 180; return (a + d * t + 360) % 360; };

/**
 * hooks: { setSun(az, el, pct), scaleLight(k), onStep(step), onEnd(outcome), landing: landingAnimation module }
 */
export function play(viewer, data, host, hooks) {
  stop(true);   // a replay of a replay: the lander back on the ground, full light
  const n = data.sunAz.length;
  const endStep = data.depletedAt >= 0 ? Math.min(data.depletedAt, n - 1) : n - 1;
  const state = {
    viewer, data, host, hooks, n, endStep,
    progress: 0,             // 0..1 of the mission (up to endStep)
    speed: 1, paused: false, phase: "mission", phaseT: 0,
    lastNow: performance.now(), lastStep: -1, lastStepSent: 0, frame: 0, fx: null, ended: false,
  };
  run = state;
  state.hud = buildHud(host, data, state);
  state.frame = requestAnimationFrame((t) => tick(state, t));
}

function buildHud(host, data, st) {
  host.replaceChildren();
  const root = el("div", "replay-hud");
  const head = el("div", "replay-head");
  head.append(el("span", "replay-badge", "MISSION REPLAY"), el("span", "replay-where", `${data.landerName} · ${data.siteName}`));
  const clock = el("div", "replay-clock num", "—");
  const rows = el("dl", "replay-rows");
  const battery = el("dd", "replay-battery");
  const bar = el("span", "replay-bar"); const fill = el("span", "replay-bar-fill"); bar.append(fill);
  const batText = el("span", "num", "—"); battery.append(bar, batText);
  const sun = el("dd", null, "—"), earth = el("dd", null, "—");
  rows.append(el("dt", null, "Battery"), battery, el("dt", null, "Sunlight"), sun, el("dt", null, "Earth"), earth);
  const banner = el("div", "replay-banner"); banner.hidden = true;
  banner.setAttribute("role", "status");

  const controls = el("div", "replay-controls");
  const playBtn = el("button", "hud-btn", "❚❚ Pause"); playBtn.type = "button";
  playBtn.addEventListener("click", () => { st.paused = !st.paused; playBtn.textContent = st.paused ? "▶ Play" : "❚❚ Pause"; });
  const scrub = el("input", "replay-scrub"); scrub.type = "range"; scrub.min = "0"; scrub.max = "1000"; scrub.value = "0";
  scrub.setAttribute("aria-label", "Mission time");
  scrub.addEventListener("input", () => { if (st.phase === "mission") { st.progress = Number(scrub.value) / 1000; } });
  const speeds = el("div", "segmented replay-speeds"); speeds.setAttribute("role", "group"); speeds.setAttribute("aria-label", "Playback speed");
  const speedBtns = [1, 2, 4].map((k) => {
    const b = el("button", `segmented-option${k === 1 ? " active" : ""}`, `${k}×`); b.type = "button";
    b.setAttribute("aria-pressed", k === 1 ? "true" : "false");
    b.addEventListener("click", () => { st.speed = k; speedBtns.forEach((x, i) => { const on = [1, 2, 4][i] === k; x.classList.toggle("active", on); x.setAttribute("aria-pressed", on ? "true" : "false"); }); });
    speeds.append(b); return b;
  });
  const skip = el("button", "hud-btn replay-skip", "Skip ▸▸"); skip.type = "button";
  skip.addEventListener("click", () => finish(st, true));
  controls.append(playBtn, scrub, speeds, skip);

  root.append(head, clock, rows, banner, controls);
  host.append(root);
  return {
    update(f, socPct, sunPct, earthOn) {
      const t = new Date(Date.parse(data.startUtc) + f * data.stepHours * 3600000);
      const day = Math.floor((f * data.stepHours) / 24) + 1;
      clock.textContent = `DAY ${day} · ${t.toISOString().slice(0, 16).replace("T", " ")} UTC`;
      fill.style.width = `${Math.max(0, Math.min(100, socPct))}%`;
      fill.classList.toggle("low", socPct < 20);
      batText.textContent = `${Math.round(socPct)}%`;
      sun.textContent = sunPct >= 50 ? "☀ sunlit" : sunPct > 0 ? "◐ partly behind terrain" : "● in shadow";
      sun.className = sunPct >= 50 ? "on" : "off";
      earth.textContent = earthOn ? "◉ contact" : "○ no contact";
      earth.className = earthOn ? "on" : "off";
      if (document.activeElement !== scrub) scrub.value = String(Math.round(st.progress * 1000));
    },
    banner(text, kind) { banner.textContent = text; banner.className = `replay-banner ${kind}`; banner.hidden = false; },
    ended() { playBtn.disabled = true; scrub.disabled = true; skip.disabled = true; },
    destroy() { host.replaceChildren(); },
  };
}

function sample(st, f) {
  const d = st.data, i = Math.min(Math.floor(f), st.n - 1), j = Math.min(i + 1, st.n - 1), t = f - i;
  return {
    az: lerpAngle(d.sunAz[i], d.sunAz[j], t),
    el: lerp(d.sunEl[i], d.sunEl[j], t),
    pct: lerp(d.sunPct[i], d.sunPct[j], t),
    soc: lerp(d.soc[i], d.soc[j], t),
    earth: d.earth[t < 0.5 ? i : j] === 1,
    step: i,
  };
}

function tick(st, now) {
  if (run !== st) return;
  const real = Math.min(0.1, (now - st.lastNow) / 1000);
  const dt = st.phase === "mission" ? real * st.speed : real;   // the ending always plays at 1×
  st.lastNow = now;
  const { viewer, hooks, data } = st;
  if (viewer.isDestroyed()) return;

  if (st.phase === "mission") {
    if (!st.paused) st.progress = Math.min(1, st.progress + dt / BASE_SECONDS);
    const f = st.progress * st.endStep;
    const s = sample(st, f);
    hooks.setSun(s.az, s.el, s.pct);
    st.hud.update(f, s.soc, s.pct, s.earth);
    if (s.step !== st.lastStep && now - st.lastStepSent > 120) {   // timeline cursor, ~8 updates/s
      st.lastStep = s.step; st.lastStepSent = now;
      hooks.onStep(s.step);
    }
    if (st.progress >= 1) startEnding(st);
  } else if (st.phase === "ascent") {
    st.phaseT += dt;
    const k = Math.min(1, st.phaseT / ASCENT_SECONDS);
    const info = hooks.landing.landedInfo();
    if (info) {
      const target = 0.5 * st.accel * (k * ASCENT_SECONDS) ** 2;     // constant acceleration
      hooks.landing.liftMarker(viewer, target - info.lift);
      const base = Cesium.Cartesian3.add(info.base, Cesium.Cartesian3.multiplyByScalar(info.up, target, new Cesium.Cartesian3()), new Cesium.Cartesian3());
      const nz = info.nozzle ?? [0, 0, 0.1 * info.heightM];
      const nozzle = Cesium.Cartesian3.add(base, Cesium.Cartesian3.multiplyByScalar(info.up, nz[2], new Cesium.Cartesian3()), new Cesium.Cartesian3());
      st.fx?.update({ nozzle, ground: info.base, throttle: 1, dust: Math.max(0, 1 - target / (3 * info.sizeM)) });
      const cam = viewer.camera;   // follow the lander: same heading and pitch, range grows a little
      if (Number.isFinite(cam.heading)) {
        cam.lookAt(Cesium.Cartesian3.add(base, Cesium.Cartesian3.multiplyByScalar(info.up, 0.5 * info.heightM, new Cesium.Cartesian3()), new Cesium.Cartesian3()),
          new Cesium.HeadingPitchRange(cam.heading, cam.pitch, st.range0 * (1 + 1.5 * k)));
      }
    }
    if (k >= 1) finish(st, false);
  } else if (st.phase === "fade") {
    st.phaseT += dt;
    const k = Math.min(1, st.phaseT / FADE_SECONDS);
    hooks.scaleLight(1 - 0.92 * k);
    if (k >= 1) finish(st, false);
  }
  if (!st.ended) st.frame = requestAnimationFrame((t) => tick(st, t));
}

function startEnding(st) {
  const { data, hooks, viewer } = st;
  hooks.onStep(st.endStep);
  if (data.outcome === "lost") {
    const day = Math.floor((st.endStep * data.stepHours) / 24) + 1;
    st.hud.banner(`MISSION LOST · day ${day} · battery depleted${/darkness/.test(data.reason) ? " in darkness" : ""}`, "lost");
    st.phase = "fade"; st.phaseT = FADE_SECONDS * 0.4;      // a short fade to dark
  } else if (data.outcome === "complete-hls" && hooks.landing.landedInfo()) {
    st.hud.banner("LIFT-OFF", "ok");
    const info = hooks.landing.landedInfo();
    st.phase = "ascent"; st.phaseT = 0;
    st.accel = 2.5 * info.sizeM / ASCENT_SECONDS;           // ≈ 30 lander heights in 5 s
    st.range0 = Cesium.Cartesian3.magnitude(viewer.camera.position) || 6 * info.sizeM;
    st.fx = new EngineFx(viewer, { sizeM: info.sizeM, up: () => info.up });
  } else {
    st.hud.banner("Night falls — the lander shuts down", "ok");
    st.phase = "fade"; st.phaseT = 0;
  }
}

function finish(st, skipped) {
  if (st.ended) return;
  st.ended = true;
  cancelAnimationFrame(st.frame);
  const { data, hooks } = st;
  if (skipped && st.phase === "mission") {
    // Jump to the end state without the ending animation.
    const s = sample(st, st.endStep);
    hooks.setSun(s.az, s.el, s.pct);
    st.hud.update(st.endStep, s.soc, s.pct, s.earth);
    hooks.onStep(st.endStep);
  }
  st.fx?.destroy(); st.fx = null;
  const text = data.outcome === "lost"
    ? `MISSION LOST · day ${Math.floor((st.endStep * data.stepHours) / 24) + 1} · battery depleted${/darkness/.test(data.reason) ? " in darkness" : ""}`
    : data.outcome === "complete-hls" ? "MISSION COMPLETE" : "MISSION COMPLETE · end of operations";
  st.hud.banner(text, data.outcome === "lost" ? "lost" : "ok");
  st.hud.ended();
  hooks.onEnd(data.outcome);
  st.hud.destroy();   // the summary card takes over (same title, plus the numbers and next steps)
}

/** Stop the replay; with reset=true the lander goes back to the ground and the HUD is removed. */
export function stop(reset = true) {
  const st = run;
  if (!st) return null;
  run = null;
  st.ended = true;
  cancelAnimationFrame(st.frame);
  st.fx?.destroy();
  if (reset) {
    st.hud.destroy();
    const info = st.hooks.landing.landedInfo();
    if (info && info.lift) st.hooks.landing.liftMarker(st.viewer, -info.lift);
    st.hooks.scaleLight(1);
  } else {
    st.hud.destroy();
  }
  return st;
}

export function isPlaying() { return !!run && !run.ended; }
