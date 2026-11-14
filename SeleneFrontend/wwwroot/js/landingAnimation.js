// Landing animation — fully isolated: it only receives a Cesium.Viewer and a host <div> for its HUD.
// play(viewer, options) → Promise<{ status: "landed" | "skipped" | "cancelled" | "reduced", fps, simplified }>
// cancel()               → removes every entity, listener and camera lock; resolves a running play() as "cancelled".
// clearMarker()          → removes the landed model left on the surface.
//
// The descent profile is ILLUSTRATIVE: altitude, vertical speed and range are derived from the
// same analytic curve that drives the animation, mapped to a plausible ~10-minute powered descent.

const DESCENT_S = 4.3;            // animation seconds from 15 km to touchdown
const SETTLE_S = 0.8;             // touchdown effects + camera settling
const START_ALT_M = 15000;
const START_RANGE_M = 1800;       // initial horizontal offset from the site
const MISSION_DESCENT_S = 600;    // illustrative real descent duration the telemetry is scaled to
const FPS_FLOOR = 30;

const BODY = "#c9d3dc";
const LINE = "#4fc3f7";

let active = null;     // running animation state
let marker = [];       // entities of the landed model (kept after touchdown)
let generation = 0;    // bumped by play() and cancel(): a play() still awaiting setup aborts if it changed
const groundCache = new Map();   // "lat,lon" → sampled ground height (m)

// ------------------------------------------------------------------ descent curve (shared by motion + telemetry)

/** u ∈ [0,1] animation progress → altitude above ground (m). Fast at first, slow near the ground. */
function altitude(u) {
  return START_ALT_M * Math.pow(1 - u, 2.6);
}

/** Horizontal offset in the local east/north frame (m): drift towards the site plus a late correction. */
function offset(u) {
  const drift = START_RANGE_M * Math.pow(1 - u, 1.5);
  const correction = 45 * Math.sin(Math.PI * Math.min(1, Math.max(0, (u - 0.78) / 0.2))); // brief sidestep before touchdown
  return { east: drift, north: correction };
}

function telemetry(u) {
  const du = 1e-3;
  const dt = MISSION_DESCENT_S * du;               // mission seconds per du
  const alt = altitude(u);
  const vs = (altitude(Math.min(1, u + du)) - alt) / dt;
  const o = offset(u);
  const phase = u >= 1 ? "TOUCHDOWN" : alt > 8000 ? "DEORBIT" : alt > 1500 ? "BRAKING" : "TERMINAL";
  return { alt, vs, range: Math.hypot(o.east, o.north), phase };
}

// ------------------------------------------------------------------ model (own primitives, schematic style)

function modelParts(landerClass) {
  // Offsets in metres in the site's east/north/up frame, measured from the feet centre.
  // Exaggerated (~10×) so the lander is readable from the chase camera — not to scale.
  if (landerClass === "hls") {
    const r = 6, legH = 6, len = 70;
    const legs = [0, 60, 120, 180, 240, 300].map((a) => {
      const c = Math.cos((a * Math.PI) / 180), s = Math.sin((a * Math.PI) / 180);
      return { from: [r * c, r * s, legH + 4], to: [r * 1.9 * c, r * 1.9 * s, 0] };
    });
    return {
      solids: [
        { kind: "cylinder", length: len, top: r, bottom: r, at: [0, 0, legH + len / 2] },
        { kind: "cylinder", length: 14, top: 0.01, bottom: r, at: [0, 0, legH + len + 7] },
        { kind: "box", size: [16, 0.6, 7], at: [0, 0, legH + len * 0.62], optional: true },   // solar wing
      ],
      legs,
      height: legH + len + 14,
    };
  }
  const half = 14, legH = 8, bodyH = 12;
  const legs = [[1, 1], [1, -1], [-1, 1], [-1, -1]].map(([x, y]) => ({ from: [x * half, y * half, legH], to: [x * half * 1.6, y * half * 1.6, 0] }));
  return {
    solids: [
      { kind: "box", size: [half * 2, half * 2, bodyH], at: [0, 0, legH + bodyH / 2] },
      { kind: "box", size: [half * 1.8, half * 1.8, 0.8], at: [0, 0, legH + bodyH + 0.4], optional: true },  // top panel
      { kind: "cylinder", length: 10, top: 0.8, bottom: 0.8, at: [half * 0.6, half * 0.6, legH + bodyH + 5], optional: true }, // antenna
    ],
    legs,
    height: legH + bodyH + 10,
  };
}

// ------------------------------------------------------------------ helpers

function enuRotation(position) {
  const m = Cesium.Transforms.eastNorthUpToFixedFrame(position);
  return Cesium.Matrix4.getMatrix3(m, new Cesium.Matrix3());
}

function localToWorld(origin, rot, east, north, up, result) {
  const v = Cesium.Matrix3.multiplyByVector(rot, new Cesium.Cartesian3(east, north, up), new Cesium.Cartesian3());
  return Cesium.Cartesian3.add(origin, v, result ?? new Cesium.Cartesian3());
}

async function groundHeight(viewer, carto, timeoutMs) {
  const scene = viewer.scene;
  if (!scene.sampleHeightSupported || scene.globe.show) return 0;   // 2D mosaic mode → ellipsoid
  const key = `${carto.latitude.toFixed(7)},${carto.longitude.toFixed(7)}`;
  if (groundCache.has(key)) return groundCache.get(key);
  try {
    const sampled = await Promise.race([
      scene.sampleHeightMostDetailed([carto.clone()]),
      new Promise((r) => setTimeout(() => r(null), timeoutMs)),
    ]);
    const h = sampled?.[0]?.height;
    if (Number.isFinite(h)) { groundCache.set(key, h); return h; }
    return 0;                                     // not available in time → ellipsoid fallback
  } catch {
    return 0;
  }
}

/** camera.lookAt only with finite inputs — a NaN target would poison the camera and stop rendering. */
function safeLookAt(camera, target, hpr) {
  if (!target || !Number.isFinite(target.x) || !Number.isFinite(target.y) || !Number.isFinite(target.z)) return;
  if (![hpr.heading, hpr.pitch, hpr.range].every(Number.isFinite)) return;
  camera.lookAt(target, hpr);
}

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

// ------------------------------------------------------------------ HUD (plain DOM inside the given host)

function buildHud(host, opts, onSkip) {
  host.replaceChildren();
  const root = el("div", "landing-hud");
  const catcher = el("div", "landing-catcher");          // click-anywhere-to-skip; also blocks map clicks
  catcher.addEventListener("click", onSkip);
  const panel = el("div", "landing-panel");
  const badge = el("span", "landing-badge", "SIMULATED DESCENT");
  badge.title = "Illustrative descent profile: altitude, vertical speed and range follow the animated trajectory, " +
    "not a flight-dynamics simulation. Lander model is schematic and not to scale.";
  badge.tabIndex = 0;
  const title = el("div", "landing-title", `${opts.landerName} → ${opts.siteName}`);
  const rows = el("dl", "landing-telemetry");
  const value = {};
  for (const [k, label] of [["alt", "ALT"], ["vs", "V/S"], ["range", "RANGE TO SITE"]]) {
    rows.append(el("dt", null, label));
    value[k] = el("dd", null, "—");
    rows.append(value[k]);
  }
  const phases = el("ol", "landing-phases");
  const phaseEl = {};
  for (const p of ["DEORBIT", "BRAKING", "TERMINAL", "TOUCHDOWN"]) {
    phaseEl[p] = el("li", null, p);
    phases.append(phaseEl[p]);
  }
  const skip = el("button", "hud-btn landing-skip", "SKIP ▸▸");
  skip.type = "button";
  skip.addEventListener("click", (e) => { e.stopPropagation(); onSkip(); });
  panel.append(badge, title, rows, phases, skip);
  root.append(catcher, panel);
  host.append(root);

  const fmt = (n) => Math.round(n).toLocaleString("en-US");
  return {
    update(t) {
      value.alt.textContent = `${fmt(t.alt)} m`;
      value.vs.textContent = `${t.vs <= 0 ? "−" : "+"}${Math.abs(t.vs).toFixed(1)} m/s`;
      value.range.textContent = `${fmt(t.range)} m`;
      for (const [p, node] of Object.entries(phaseEl)) node.classList.toggle("on", p === t.phase);
    },
    done() { root.classList.add("done"); skip.remove(); catcher.remove(); },
    destroy() { host.replaceChildren(); },
  };
}

// ------------------------------------------------------------------ public API

export function cancel() {
  generation++;
  if (!active) return;
  const a = active;
  active = null;
  cancelAnimationFrame(a.frame);
  document.removeEventListener("keydown", a.onKey, true);
  for (const e of [...a.entities, ...a.fx]) if (!a.viewer.isDestroyed()) a.viewer.entities.remove(e);
  a.hud?.destroy();
  if (!a.viewer.isDestroyed()) a.viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY);
  a.resolve({ status: "cancelled", fps: Math.round(a.fps), simplified: a.simplified });
}

/** Warm the ground-height cache for a site (called when a site is selected, before CONFIRM). */
export async function prefetchGround(viewer, lat, lon) {
  if (!viewer || viewer.isDestroyed()) return;
  await groundHeight(viewer, Cesium.Cartographic.fromDegrees(lon, lat, 0), 4000);
}

export function clearMarker(viewer) {
  for (const e of marker) if (viewer && !viewer.isDestroyed()) viewer.entities.remove(e);
  marker = [];
}

/**
 * options: { lat, lon, landerClass: "clps"|"hls", landerName, siteName, reducedMotion, hudHost: HTMLElement }
 */
export async function play(viewer, options) {
  cancel();
  clearMarker(viewer);
  const myGeneration = generation;
  options.hudHost.classList.remove("fade");
  // An unfinished camera flight (intro, fly-to) fights lookAt() and drives the camera to NaN.
  viewer.camera.cancelFlight();
  const site = Cesium.Cartographic.fromDegrees(options.lon, options.lat, 0);
  const ground = await groundHeight(viewer, site, options.reducedMotion ? 300 : 700);
  if (myGeneration !== generation || viewer.isDestroyed()) return { status: "cancelled", fps: 0, simplified: false };
  const siteGround = Cesium.Cartesian3.fromRadians(site.longitude, site.latitude, ground, Cesium.Ellipsoid.MOON);
  const rot = enuRotation(siteGround);
  const orientation = Cesium.Transforms.headingPitchRollQuaternion(siteGround, new Cesium.HeadingPitchRoll(0, 0, 0));

  // Trajectory as a SampledPositionProperty on a private epoch (independent of viewer.clock).
  const epoch = Cesium.JulianDate.fromDate(new Date(Date.UTC(2000, 0, 1)));
  const path = new Cesium.SampledPositionProperty();
  path.setInterpolationOptions({ interpolationDegree: 2, interpolationAlgorithm: Cesium.LagrangePolynomialApproximation });
  for (let i = 0; i <= 120; i++) {
    const u = i / 120;
    const o = offset(u);
    path.addSample(Cesium.JulianDate.addSeconds(epoch, u * DESCENT_S, new Cesium.JulianDate()),
      localToWorld(siteGround, rot, o.east, o.north, altitude(u)));
  }
  const at = (s) => path.getValue(Cesium.JulianDate.addSeconds(epoch, Math.min(s, DESCENT_S), new Cesium.JulianDate()));

  const parts = modelParts(options.landerClass);
  let simplified = false;
  let current = at(0) ?? siteGround;
  const entities = [];
  const posOf = (off) => new Cesium.CallbackProperty(() => localToWorld(current, rot, off[0], off[1], off[2]), false);
  const add = (spec) => { const e = viewer.entities.add({ properties: { kind: "lander" }, ...spec }); entities.push(e); return e; };

  for (const s of parts.solids) {
    const graphics = s.kind === "box"
      ? { box: { dimensions: new Cesium.Cartesian3(...s.size), material: Cesium.Color.fromCssColorString(BODY), outline: true, outlineColor: Cesium.Color.fromCssColorString(LINE) } }
      : { cylinder: { length: s.length, topRadius: s.top, bottomRadius: s.bottom, material: Cesium.Color.fromCssColorString(BODY), outline: true, outlineColor: Cesium.Color.fromCssColorString(LINE), slices: 24 } };
    const e = add({ position: posOf(s.at), orientation, ...graphics });
    e._optional = !!s.optional;
  }
  parts.legs.forEach((leg, i) => {
    const e = add({
      polyline: {
        positions: new Cesium.CallbackProperty(() => [
          localToWorld(current, rot, ...leg.from), localToWorld(current, rot, ...leg.to)], false),
        width: 2, material: Cesium.Color.fromCssColorString(LINE), arcType: Cesium.ArcType.NONE,
      },
    });
    e._optional = i >= 4 || (parts.legs.length === 4 && i % 2 === 1);   // first to go when simplifying
  });

  const simplify = () => {
    if (simplified) return;
    simplified = true;
    for (const e of entities.filter((x) => x._optional)) viewer.entities.remove(e);
  };

  const camera = viewer.camera;
  const heading0 = Cesium.Math.toRadians(215);
  const finalHpr = new Cesium.HeadingPitchRange(heading0 + Cesium.Math.toRadians(55), Cesium.Math.toRadians(-28), parts.height * 9);
  const landedAt = localToWorld(siteGround, rot, 0, 0, parts.height * 0.4);

  return new Promise((resolve) => {
    const state = { viewer, entities, fx: [], frame: 0, hud: null, onKey: null, resolve, fps: 0, simplified: false };
    active = state;

    const finish = (status) => {
      if (active !== state) return;
      active = null;
      cancelAnimationFrame(state.frame);
      document.removeEventListener("keydown", state.onKey, true);
      current = siteGround;
      for (const e of state.fx) viewer.entities.remove(e);
      safeLookAt(camera, landedAt, finalHpr);   // stays in orbit around the site until another camera command
      state.hud?.update(telemetry(1));
      state.hud?.done();
      // Freeze the landed model as plain constant positions (cheap) and keep it as a marker.
      const now = Cesium.JulianDate.now();
      for (const e of entities) {
        if (e.position) {
          const p = e.position.getValue(now);
          if (p) e.position = new Cesium.ConstantPositionProperty(p);
        }
        if (e.polyline) e.polyline.positions = new Cesium.ConstantProperty(e.polyline.positions.getValue(now));
      }
      marker = entities;
      resolve({ status, fps: Math.round(state.fps), simplified });
    };

    const onSkip = () => finish("skipped");
    state.onKey = (e) => { if (e.key === "Escape") { e.preventDefault(); onSkip(); } };
    document.addEventListener("keydown", state.onKey, true);
    state.hud = buildHud(options.hudHost, options, onSkip);

    if (options.reducedMotion) {
      // No motion: the host fades (CSS), the lander is placed and the result shows right away.
      options.hudHost.classList.add("fade");
      setTimeout(() => finish("reduced"), 250);
      return;
    }

    // Touchdown effects: expanding dust ring + short flash. Both read values computed ONCE per
    // frame in tick(): sampling the clock separately for each semi-axis could make the minor axis
    // larger than the major one, which yields an invalid ellipse and stops Cesium rendering.
    let touchdownAt = null;
    let fxT = 0;                       // seconds since touchdown, updated in tick()
    let ringR = 2;
    const ring = {
      position: localToWorld(siteGround, rot, 0, 0, 0.5),
      ellipse: {
        semiMajorAxis: new Cesium.CallbackProperty(() => ringR, false),
        semiMinorAxis: new Cesium.CallbackProperty(() => ringR, false),
        height: ground + 0.5,
        material: new Cesium.ColorMaterialProperty(new Cesium.CallbackProperty(
          () => Cesium.Color.fromCssColorString("#d7e2ec").withAlpha(0.55 * Math.max(0, 1 - fxT / 0.9)), false)),
      },
      properties: { kind: "lander-fx" },
    };
    const flash = {
      position: localToWorld(siteGround, rot, 0, 0, 2),
      point: {
        pixelSize: new Cesium.CallbackProperty(() => 6 + 50 * Math.min(1, fxT / 0.25), false),
        color: new Cesium.CallbackProperty(() => Cesium.Color.WHITE.withAlpha(Math.max(0, 1 - fxT / 0.45)), false),
        disableDepthTestDistance: Number.POSITIVE_INFINITY,
      },
      properties: { kind: "lander-fx" },
    };

    const t0 = performance.now();
    let last = t0, frames = 0, frameTime = 0;
    const tick = (now) => {
      if (active !== state) return;
      const s = (now - t0) / 1000;
      const dt = now - last; last = now;
      if (s > 0.6) { frames++; frameTime += dt; state.fps = 1000 / (frameTime / frames); }   // skip start-up jank
      if (!simplified && frames >= 25 && state.fps < FPS_FLOOR) simplify();

      const u = Math.min(1, s / DESCENT_S);
      current = at(s) ?? siteGround;
      state.hud.update(telemetry(u));

      if (u < 1) {
        // Chase camera: slow orbit, closing in as the lander slows down.
        const hpr = new Cesium.HeadingPitchRange(
          heading0 + Cesium.Math.toRadians(55) * u,
          Cesium.Math.toRadians(-14 - 14 * u),
          parts.height * (60 - 51 * Math.pow(u, 0.7)));
        safeLookAt(camera, localToWorld(current, rot, 0, 0, parts.height * 0.4), hpr);
      } else {
        if (touchdownAt != null) {
          fxT = (now - touchdownAt) / 1000;
          ringR = 2 + parts.height * 3.2 * Math.min(1, fxT / 0.9);
        }
        if (touchdownAt == null) {
          touchdownAt = now;
          if (!simplified) state.fx.push(viewer.entities.add(ring));
          state.fx.push(viewer.entities.add(flash));
        }
        safeLookAt(camera, landedAt, finalHpr);
        if (s >= DESCENT_S + SETTLE_S) { finish("landed"); return; }
      }
      state.frame = requestAnimationFrame(tick);
    };
    state.frame = requestAnimationFrame(tick);
  });
}
