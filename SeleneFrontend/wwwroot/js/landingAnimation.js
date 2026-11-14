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
const PANEL = "#1d3557";          // our own schematic solar plates (from landers.json → panels)

// Front of a glTF model after Cesium's axis correction: glTF +Z ("front" by the glTF spec) becomes
// the local +X axis, i.e. east in an east-north-up modelMatrix. Azimuth in degrees clockwise from north.
const GLB_FRONT_AZ_DEG = 90;
const YAW_MIN_RATE = Math.PI / 3; // rad/s: slowest yaw manoeuvre (60°/s); faster when the deadline is close
// Yaw window: starts below YAW_START_ALT_M and must be finished by YAW_DONE_ALT_M, i.e. about one
// animation second before touchdown, so the lander is already aligned when it touches the ground.
const YAW_START_ALT_M = 3000;
const YAW_DONE_ALT_M = 300;
const YAW_LATE_RATE = (4 * Math.PI) / 3;   // rad/s (240°/s): fastest yaw; catch-up rate for a late heading

let active = null;     // running animation state
let marker = [];       // entities of the landed model (kept after touchdown)
let generation = 0;    // bumped by play() and cancel(): a play() still awaiting setup aborts if it changed
const groundCache = new Map();   // "lat,lon" → sampled ground height (m)
let markerPrims = [];            // glTF model primitive(s) left on the surface
let pending = null;              // { hud, onKey } while play() is preparing (before the animation clock starts)
let settleTimer = null;          // post-touchdown terrain-following of the landed marker
let pose = null;                 // heading state of the lander in flight or on the surface (see setHeading)
let earlyHeading;                // setHeading() that arrived while play() was still preparing (undefined = none)
let orbitClearance = 3;          // minimum camera height above the terrain while orbiting the landed lander (m)

/** The descent is running (it owns the camera); moonViewer's camera guard waits. */
export function isAnimating() { return active !== null || pending !== null; }

/** Smallest camera height above the terrain in the landed orbit (scaled to the lander). */
export function orbitClearanceM() { return orbitClearance; }

// ------------------------------------------------------------------ optional glTF models (landers.json → modelUri)

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const modelLoads = new Map();    // uri → Promise<{ model, info } | null> — one Cesium.Model per lander, shared
let statusListener = null;       // (uri, "loading" | "ready" | "failed") → void, set by moonViewer.js
const modelStatus = new Map();   // uri → last status

function setStatus(uri, status) {
  modelStatus.set(uri, status);
  try { statusListener?.(uri, status); } catch { /* a listener must never break loading */ }
}

/** moonViewer.js forwards model load states to the HUD; the current states are replayed at once. */
export function onModelStatus(listener) {
  statusListener = listener;
  for (const [uri, status] of modelStatus) listener(uri, status);
}

const NORM = { 5120: 127, 5121: 255, 5122: 32767, 5123: 65535 };   // KHR_mesh_quantization: normalized integer → float

/**
 * Download a GLB once and measure it. The bounds come from the POSITION accessors (dequantized when
 * normalized) through the node transforms: the optimised models (tools/optimize-models) store
 * int16 positions plus a scale/translation on the mesh node.
 *
 * Orientation: glTF is Y-up by specification and the TRELLIS exports follow it (checked per model:
 * Starship and Blue Moon are tallest along Y). Cesium.Model converts Y-up to Z-up, so with an
 * east-north-up modelMatrix the model's up is the local vertical; landers.json → modelRotationDeg
 * only turns the model about that vertical. Height = Y extent, width = larger of the X/Z extents.
 */
function glbInfo(uri) {
  return (async () => {
    try {
      const res = await fetch(uri);
      if (!res.ok) return null;
      const buf = await res.arrayBuffer();
      const dv = new DataView(buf);
      if (buf.byteLength < 20 || dv.getUint32(0, true) !== 0x46546c67 || dv.getUint32(16, true) !== 0x4e4f534a) return null; // "glTF", "JSON"
      const gltf = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, 20, dv.getUint32(12, true))));
      const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
      const corner = new Cesium.Cartesian3();
      const visit = (index, parent) => {
        const node = gltf.nodes?.[index];
        if (!node) return;
        const local = node.matrix
          ? Cesium.Matrix4.fromColumnMajorArray(node.matrix)
          : Cesium.Matrix4.fromTranslationQuaternionRotationScale(
            Cesium.Cartesian3.fromArray(node.translation ?? [0, 0, 0]),
            Cesium.Quaternion.unpack(node.rotation ?? [0, 0, 0, 1]),
            Cesium.Cartesian3.fromArray(node.scale ?? [1, 1, 1]));
        const world = Cesium.Matrix4.multiply(parent, local, new Cesium.Matrix4());
        for (const prim of gltf.meshes?.[node.mesh]?.primitives ?? []) {
          const acc = gltf.accessors?.[prim.attributes?.POSITION];
          if (!acc?.min || !acc?.max) continue;
          const k = acc.normalized ? NORM[acc.componentType] ?? 1 : 1;
          for (let c = 0; c < 8; c++) {
            corner.x = (c & 1 ? acc.max[0] : acc.min[0]) / k;
            corner.y = (c & 2 ? acc.max[1] : acc.min[1]) / k;
            corner.z = (c & 4 ? acc.max[2] : acc.min[2]) / k;
            const p = Cesium.Matrix4.multiplyByPoint(world, corner, new Cesium.Cartesian3());
            [p.x, p.y, p.z].forEach((v, i) => { min[i] = Math.min(min[i], v); max[i] = Math.max(max[i], v); });
          }
        }
        for (const child of node.children ?? []) visit(child, world);
      };
      const scene = gltf.scenes?.[gltf.scene ?? 0];
      for (const root of scene?.nodes ?? []) visit(root, Cesium.Matrix4.IDENTITY);
      const height = max[1] - min[1];
      const width = Math.max(max[0] - min[0], max[2] - min[2]);
      if (!(height > 0 && width > 0)) return null;
      return { url: URL.createObjectURL(new Blob([buf], { type: "model/gltf-binary" })), height, width, bottom: min[1] };
    } catch {
      return null;
    }
  })();
}

/** Pooled models are only hidden (reused by the next landing); anything else is removed. */
function hideOrRemove(viewer, model) {
  if (!viewer || viewer.isDestroyed() || model.isDestroyed()) return;
  if (model._selenePooled) model.show = false;
  else viewer.scene.primitives.remove(model);
}

/** Wait until a model is ready to draw and one more frame has rendered (shader compile happens here). */
async function whenModelReady(viewer, model, timeoutMs) {
  if (!model.ready) {
    await Promise.race([new Promise((r) => { const off = model.readyEvent.addEventListener(() => { off(); r(); }); }), sleep(timeoutMs)]);
  }
  await Promise.race([new Promise((r) => { const off = viewer.scene.postRender.addEventListener(() => { off(); r(); }); }), sleep(500)]);
}

const inUse = (model) => (active && active.prims.includes(model)) || markerPrims.includes(model);

/**
 * Download, parse and upload a model once per session (and per viewer). Failures are cached too:
 * one request per session, no repeated 404s, and the caller falls back to the schematic model.
 */
function ensureModel(viewer, uri) {
  const cached = modelLoads.get(uri);
  if (cached) return cached;
  const load = (async () => {
    setStatus(uri, "loading");
    const info = await glbInfo(uri);
    if (!info || viewer.isDestroyed()) { setStatus(uri, "failed"); return null; }
    try {
      const model = await Cesium.Model.fromGltfAsync({
        url: info.url, minimumPixelSize: 64, maximumScale: 20000, shadows: Cesium.ShadowMode.ENABLED,
      });
      // Same uniform ambient as the terrain (see moonViewer.js) so the shaded side stays readable.
      const amb = Array.from({ length: 9 }, () => new Cesium.Cartesian3(0, 0, 0));
      amb[0] = new Cesium.Cartesian3(0.55, 0.55, 0.57);
      model.imageBasedLighting.sphericalHarmonicCoefficients = amb;
      model.errorEvent.addEventListener((e) => console.warn("[Landing] glTF model error:", e?.message ?? e));
      model._selenePooled = true;
      model._seleneViewer = viewer;
      if (viewer.isDestroyed()) { setStatus(uri, "failed"); return null; }
      viewer.scene.primitives.add(model);    // loads off-screen (identity matrix = Moon centre) until placed
      await whenModelReady(viewer, model, 30000);
      if (!model.ready) { setStatus(uri, "failed"); return null; }
      if (!inUse(model) && !model.isDestroyed()) model.show = false;
      setStatus(uri, "ready");
      return { model, info };
    } catch (e) {
      console.warn("[Landing] glTF model unavailable, using the schematic model:", e?.message ?? e);
      setStatus(uri, "failed");
      return null;
    }
  })();
  modelLoads.set(uri, load);
  return load;
}

/** Real-world size of a loaded model for the given spec: scale, lift (feet on the ground), height, width. */
function sized(loaded, spec) {
  const { info } = loaded;
  const scale = spec.sizeM / (spec.scaleBy === "width" ? info.width : info.height);
  return { scale, lift: -info.bottom * scale, heightM: info.height * scale, widthM: info.width * scale };
}

/**
 * Background preparation of a lander model (selected lander and the post-start preload): it is
 * downloaded, uploaded and kept hidden in the pool so CONFIRM can start instantly.
 */
export async function prefetchModel(viewer, spec) {
  if (!spec?.uri || !viewer || viewer.isDestroyed()) return;
  await currentModel(viewer, spec.uri);
}

/** ensureModel, but a model left over from a destroyed (re-created) viewer is loaded again. */
function currentModel(viewer, uri) {
  return ensureModel(viewer, uri).then((r) => {
    if (r && (r.model.isDestroyed() || r.model._seleneViewer !== viewer)) {
      if (modelLoads.get(uri)) modelLoads.delete(uri);
      return ensureModel(viewer, uri);
    }
    return r;
  });
}

/** Preload several models one after another (first entry first), without blocking anything. */
export async function preloadModels(viewer, specs) {
  for (const spec of specs ?? []) {
    if (!viewer || viewer.isDestroyed()) return;
    await prefetchModel(viewer, spec);
  }
}

/** The model scaled for this landing; null on any failure or if it is not ready within timeoutMs. */
async function loadModel(viewer, spec, timeoutMs) {
  if (!spec?.uri || !(spec.sizeM > 0)) return null;
  const loaded = await Promise.race([currentModel(viewer, spec.uri), sleep(timeoutMs).then(() => null)]);
  if (!loaded || loaded.model.isDestroyed() || !viewer.scene.primitives.contains(loaded.model)) return null;
  const size = sized(loaded, spec);
  loaded.model.scale = size.scale;
  loaded.model.show = true;
  return { model: loaded.model, ...size, rotation: Cesium.Math.toRadians(-(spec.rotationDeg ?? 0)) };
}

// ------------------------------------------------------------------ descent curve (shared by motion + telemetry)

/** u ∈ [0,1] animation progress → altitude above ground (m). Fast at first, slow near the ground. */
function altitude(u) {
  return START_ALT_M * Math.pow(1 - u, 2.6);
}

/** Animation second at which the descent passes YAW_DONE_ALT_M (inverse of altitude()): the yaw deadline. */
const YAW_DONE_S = DESCENT_S * (1 - Math.pow(YAW_DONE_ALT_M / START_ALT_M, 1 / 2.6));

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
      // generic top panel, replaced by the data-driven plates when the lander declares a layout
      { kind: "box", size: [half * 1.8, half * 1.8, 0.8], at: [0, 0, legH + bodyH + 0.4], optional: true, panel: true },
      { kind: "cylinder", length: 10, top: 0.8, bottom: 0.8, at: [half * 0.6, half * 0.6, legH + bodyH + 5], optional: true }, // antenna
    ],
    legs,
    height: legH + bodyH + 10,
    body: { radius: half + 0.6, wallMid: legH + bodyH / 2, wallH: bodyH * 0.8, deckZ: legH + bodyH + 0.5, width: half * 1.5, thick: 0.6 },
  };
}

/**
 * Solar plates from landers.json → panels.facets, in the lander's own frame (heading 0).
 * Same geometry as SolarGeometry.cs: facet normal azimuth = heading + azOffsetDeg, elevation
 * normalElevDeg (0 = wall, 90 = deck). Returns [{ az, elev, at:[e,n,u] at heading 0, size:[w,t,h] }].
 * Our own schematic plates: the visual model itself carries no reliable panel geometry.
 */
function plateSpecs(facets, body) {
  if (!Array.isArray(facets) || !body) return [];
  const used = facets.filter((f) => f && f.share > 0);
  const maxShare = Math.max(0, ...used.map((f) => f.share));
  return used.map((f) => {
    const elev = Cesium.Math.toRadians(f.normalElevDeg ?? 0);
    const az = Cesium.Math.toRadians(f.azOffsetDeg ?? 0);
    const w = body.width * Math.sqrt(f.share / maxShare);
    if (f.normalElevDeg >= 60) {
      return { az, elev, at: [0, 0, body.deckZ], size: [w, body.thick, w] };
    }
    return { az, elev, at: [body.radius * Math.sin(az), body.radius * Math.cos(az), body.wallMid], size: [w, body.thick, body.wallH] };
  });
}

/** (east, north) offset defined at heading 0, turned by `heading` (rad, clockwise from north). */
function turnEN(east, north, heading) {
  const c = Math.cos(heading), s = Math.sin(heading);
  return [east * c + north * s, -east * s + north * c];
}

/**
 * Orientation of a plate whose normal points to azimuth `az` (rad, clockwise from north) and
 * elevation `elev`: box axes x = horizontal along the plate, y = normal (thickness), z = x × y.
 */
function plateOrientation(enuRot, az, elev) {
  const n = new Cesium.Cartesian3(Math.cos(elev) * Math.sin(az), Math.cos(elev) * Math.cos(az), Math.sin(elev));
  const x = new Cesium.Cartesian3(Math.cos(az), -Math.sin(az), 0);
  const z = Cesium.Cartesian3.cross(x, n, new Cesium.Cartesian3());
  const local = new Cesium.Matrix3(x.x, n.x, z.x, x.y, n.y, z.y, x.z, n.z, z.z);   // columns x, n, z
  const world = Cesium.Matrix3.multiply(enuRot, local, new Cesium.Matrix3());
  return Cesium.Quaternion.fromRotationMatrix(world);
}

/** Shortest signed angle from a to b (rad). */
function angleDiff(a, b) {
  return Cesium.Math.negativePiToPi(b - a);
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
  for (const [k, label] of [["alt", "ALT"], ["vs", "V/S"], ["range", "RANGE TO SITE"], ["yaw", "YAW"]]) {
    rows.append(el("dt", null, label));
    value[k] = el("dd", null, "—");
    rows.append(value[k]);
  }
  value.yaw.title = "Heading at touchdown that points the fixed solar panels at the Sun (from the mission analysis).";
  value.yaw.textContent = opts.panels?.length ? "awaiting analysis…" : "—";
  const phases = el("ol", "landing-phases");
  const phaseEl = {};
  for (const p of ["DEORBIT", "BRAKING", "TERMINAL", "TOUCHDOWN"]) {
    phaseEl[p] = el("li", null, p);
    phases.append(phaseEl[p]);
  }
  const status = el("div", "landing-status", "PREPARING DESCENT…");
  const skip = el("button", "hud-btn landing-skip", "SKIP ▸▸");
  skip.type = "button";
  skip.addEventListener("click", (e) => { e.stopPropagation(); onSkip(); });
  panel.append(badge, title, status, rows, phases, skip);
  root.append(catcher, panel);
  host.append(root);

  const fmt = (n) => Math.round(n).toLocaleString("en-US");
  return {
    update(t) {
      status.remove();
      value.alt.textContent = `${fmt(t.alt)} m`;
      value.vs.textContent = `${t.vs <= 0 ? "−" : "+"}${Math.abs(t.vs).toFixed(1)} m/s`;
      value.range.textContent = `${fmt(t.range)} m`;
      for (const [p, node] of Object.entries(phaseEl)) node.classList.toggle("on", p === t.phase);
    },
    setYaw(text) { value.yaw.textContent = text; },
    done() { root.classList.add("done"); skip.remove(); catcher.remove(); },
    status(text) { status.textContent = text; },
    model(credit) {
      badge.title = "Illustrative descent profile: altitude, vertical speed and range follow the animated trajectory, " +
        "not a flight-dynamics simulation. Lander model scaled to its approximate real size." + (credit ? ` ${credit}.` : "");
    },
    destroy() { host.replaceChildren(); },
  };
}

// ------------------------------------------------------------------ public API

function stopPending() {
  if (!pending) return;
  document.removeEventListener("keydown", pending.onKey, true);
  pending.hud.destroy();
  pending = null;
}

function stopSettle() {
  if (settleTimer) clearInterval(settleTimer);
  settleTimer = null;
}

export function cancel() {
  generation++;
  stopPending();
  stopSettle();
  if (!active) return;
  if (pose && !pose.landed) pose = null;   // the descending lander is removed below
  const a = active;
  active = null;
  cancelAnimationFrame(a.frame);
  document.removeEventListener("keydown", a.onKey, true);
  for (const e of [...a.entities, ...a.fx]) if (!a.viewer.isDestroyed()) a.viewer.entities.remove(e);
  for (const m of a.prims) hideOrRemove(a.viewer, m);
  a.hud?.destroy();
  if (!a.viewer.isDestroyed()) a.viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY);
  a.resolve({ status: "cancelled", fps: Math.round(a.fps), simplified: a.simplified });
}

/** Warm the ground-height cache for a site (called when a site is selected, before CONFIRM). */
export async function prefetchGround(viewer, lat, lon) {
  if (!viewer || viewer.isDestroyed()) return;
  await groundHeight(viewer, Cesium.Cartographic.fromDegrees(lon, lat, 0), 4000);
}

let turnFrame = 0;   // requestAnimationFrame id of a landed-marker turn

/**
 * Heading of the lander (degrees clockwise from north, null = unknown / ideal tracking), from the
 * mission analysis. In flight it becomes the target of the terminal yaw manoeuvre; on the surface
 * the landed marker turns to it (immediately with reduced motion). `auto` labels the HUD value.
 */
export function setHeading(viewer, deg, auto = true, reducedMotion = false) {
  const p = pose;
  if (!p) {
    if (pending) earlyHeading = { deg, auto };   // play() is preparing: apply once the lander exists
    return;
  }
  const target = Number.isFinite(deg) ? Cesium.Math.toRadians(deg) : null;
  p.yaw.target = target;
  p.yaw.auto = auto;
  if (p.plates) p.hud?.setYaw(p.yawText());
  if (!p.landed || target === null || !viewer || viewer.isDestroyed()) return;

  cancelAnimationFrame(turnFrame);
  if (reducedMotion) { p.yaw.heading = target; p.freeze(); return; }
  let last = performance.now();
  const step = (now) => {
    if (pose !== p || viewer.isDestroyed()) return;
    const diff = angleDiff(p.yaw.heading, target);
    const max = Cesium.Math.toRadians(120) * ((now - last) / 1000);
    last = now;
    p.yaw.heading += Math.sign(diff) * Math.min(Math.abs(diff), max);
    p.freeze();
    if (Math.abs(diff) > max) turnFrame = requestAnimationFrame(step);
  };
  turnFrame = requestAnimationFrame(step);
}

/** Test hook: current heading state of the lander in flight or on the surface. */
export function getPose() {
  if (!pose) return null;
  const deg = (r) => (r === null ? null : Math.round(Cesium.Math.toDegrees(Cesium.Math.zeroToTwoPi(r)) * 10) / 10);
  return {
    headingDeg: deg(pose.yaw.heading), targetDeg: deg(pose.yaw.target), plates: pose.plates, landed: pose.landed,
    plateAzimuths: pose.plateAzimuths(),
  };
}

export function clearMarker(viewer) {
  cancelAnimationFrame(turnFrame);
  pose = null;
  stopSettle();
  for (const e of marker) if (viewer && !viewer.isDestroyed()) viewer.entities.remove(e);
  for (const m of markerPrims) hideOrRemove(viewer, m);
  marker = [];
  markerPrims = [];
}

/**
 * options: { lat, lon, landerClass: "clps"|"hls", landerName, siteName, reducedMotion, hudHost: HTMLElement,
 *            model: { uri, sizeM, scaleBy: "height"|"width", rotationDeg, credit } | null,
 *            panels?: [{ azOffsetDeg, normalElevDeg, share }] (fixed panels only),
 *            headingDeg?: heading from the analysis if already known (later via setHeading), headingAuto? }
 */
export async function play(viewer, options) {
  cancel();
  clearMarker(viewer);
  earlyHeading = undefined;
  const myGeneration = generation;
  options.hudHost.classList.remove("fade");
  // An unfinished camera flight (intro, fly-to) fights lookAt() and drives the camera to NaN.
  viewer.camera.cancelFlight();

  // Immediate feedback: the HUD appears at once ("PREPARING DESCENT…"); skipping already works.
  let prepSkipped = false;
  let skipHandler = () => { prepSkipped = true; };
  const hud = buildHud(options.hudHost, options, () => skipHandler());
  const onKey = (e) => { if (e.key === "Escape") { e.preventDefault(); skipHandler(); } };
  document.addEventListener("keydown", onKey, true);
  pending = { hud, onKey };
  const aborted = () => myGeneration !== generation || viewer.isDestroyed();

  const site = Cesium.Cartographic.fromDegrees(options.lon, options.lat, 0);
  const ground = await groundHeight(viewer, site, options.reducedMotion ? 300 : 700);
  if (aborted()) return { status: "cancelled", fps: 0, simplified: false };
  const siteGround = Cesium.Cartesian3.fromRadians(site.longitude, site.latitude, ground, Cesium.Ellipsoid.MOON);
  const rot = enuRotation(siteGround);
  // Heading (rad, clockwise from north): the lander starts at 0 and yaws to the analysis heading
  // during the terminal phase (setHeading). Reduced motion lands at the target heading directly.
  const toRad = (deg) => (Number.isFinite(deg) ? Cesium.Math.toRadians(deg) : null);
  const yaw = { heading: 0, target: toRad(options.headingDeg), auto: options.headingAuto !== false };
  if (options.reducedMotion && yaw.target !== null) yaw.heading = yaw.target;
  const orientationAt = (h) => Cesium.Transforms.headingPitchRollQuaternion(siteGround, new Cesium.HeadingPitchRoll(h, 0, 0));

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

  // glTF model (landers.json → modelUri); not ready within 4 s or failed → schematic primitives.
  const prims = [];
  if (options.model && modelStatus.get(options.model.uri) !== "ready") hud.status("LOADING 3D MODEL…");
  const glb = options.model ? await loadModel(viewer, options.model, 4000) : null;
  if (glb) prims.push(glb.model);
  if (aborted()) {
    for (const m of prims) hideOrRemove(viewer, m);
    return { status: "cancelled", fps: 0, simplified: false };
  }

  const parts = glb ? { solids: [], legs: [], height: glb.heightM } : modelParts(options.landerClass);
  // Size the camera and the effects frame: the largest real dimension of the lander (Starship 50 m
  // needs a far wider shot than Blue Ghost's 2 m). The schematic model keeps its exaggerated height.
  const frame = glb ? Math.max(glb.heightM, glb.widthM) : parts.height;
  if (glb) hud.model(options.model.credit);
  if (glb) {
    // Plates sit just outside the hull: hull faces of the Nova-C GLB are at ~0.55-0.75 of its half-width.
    const half = glb.widthM / 2, h = glb.heightM;
    parts.body = {
      radius: half * 0.66, wallMid: h * 0.42, wallH: h * 0.3, deckZ: h * 0.88,
      width: half * 0.62, thick: Math.max(0.04, h * 0.012),
    };
  }
  const plates = plateSpecs(options.panels, parts.body);
  if (plates.length) parts.solids = parts.solids.filter((s) => !s.panel);
  let simplified = false;
  // Vertical correction (m) towards the terrain that is actually rendered near the site. The
  // pre-landing height sample is taken from far away and can differ from the detailed tiles that
  // stream in during the final approach (tens of metres on the Cesium Moon tileset).
  let groundAdj = 0;
  const pathAt = (s) => {
    const p = at(s);
    return p ? localToWorld(p, rot, 0, 0, groundAdj) : localToWorld(siteGround, rot, 0, 0, groundAdj);
  };
  const surface = () => localToWorld(siteGround, rot, 0, 0, groundAdj);
  const renderedGround = () => {
    if (!viewer.scene.sampleHeightSupported) return undefined;
    try {
      const h = viewer.scene.sampleHeight(site, [...prims, ...entities, ...(active?.fx ?? [])]);
      return Number.isFinite(h) ? h : undefined;
    } catch { return undefined; }
  };
  let current = pathAt(0);
  const placeModel = () => {
    if (!glb) return;
    // Only turns about the local vertical: the model's own correction (rotationDeg from landers.json)
    // plus the heading. The glTF front (+Z -> east after Cesium's axis correction) is rotated
    // counter-clockwise by (front azimuth - heading) so it points at the heading.
    const enu = Cesium.Transforms.eastNorthUpToFixedFrame(localToWorld(current, rot, 0, 0, glb.lift));
    const turn = (glb.rotation ?? 0) + Cesium.Math.toRadians(GLB_FRONT_AZ_DEG) - yaw.heading;
    glb.model.modelMatrix = Cesium.Matrix4.multiplyByMatrix3(enu, Cesium.Matrix3.fromRotationZ(turn), enu);
  };
  placeModel();
  // First draw of a large model compiles shaders and can stall the page for a moment; start the
  // animation clock only afterwards so no phase is swallowed by the stall.
  if (glb) await whenModelReady(viewer, glb.model, 3000);
  if (aborted()) {
    for (const m of prims) hideOrRemove(viewer, m);
    return { status: "cancelled", fps: 0, simplified: false };
  }
  // Every part has pose functions of (current, yaw.heading). In flight they back CallbackProperties;
  // on the surface freeze() turns them into constants (cheap) and re-runs whenever the heading changes.
  const entities = [];
  const at3 = (off) => {
    const [e, n] = turnEN(off[0], off[1], yaw.heading);
    return localToWorld(current, rot, e, n, off[2]);
  };
  const add = (spec, fns) => {
    const e = viewer.entities.add({ properties: { kind: "lander" }, ...spec });
    e._pose = fns;
    entities.push(e);
    return e;
  };
  const live = (fn) => new Cesium.CallbackProperty(fn, false);

  for (const s of parts.solids) {
    const graphics = s.kind === "box"
      ? { box: { dimensions: new Cesium.Cartesian3(...s.size), material: Cesium.Color.fromCssColorString(BODY), outline: true, outlineColor: Cesium.Color.fromCssColorString(LINE) } }
      : { cylinder: { length: s.length, topRadius: s.top, bottomRadius: s.bottom, material: Cesium.Color.fromCssColorString(BODY), outline: true, outlineColor: Cesium.Color.fromCssColorString(LINE), slices: 24 } };
    const fns = { pos: () => at3(s.at), ori: () => orientationAt(yaw.heading) };
    const e = add({ position: live(fns.pos), orientation: live(fns.ori), ...graphics }, fns);
    e._optional = !!s.optional;
  }
  parts.legs.forEach((leg, i) => {
    const fns = { line: () => [at3(leg.from), at3(leg.to)] };
    const e = add({
      polyline: { positions: live(fns.line), width: 2, material: Cesium.Color.fromCssColorString(LINE), arcType: Cesium.ArcType.NONE },
    }, fns);
    e._optional = i >= 4 || (parts.legs.length === 4 && i % 2 === 1);   // first to go when simplifying
  });
  for (const p of plates) {
    const fns = { pos: () => at3(p.at), ori: () => plateOrientation(rot, p.az + yaw.heading, p.elev) };
    add({
      position: live(fns.pos), orientation: live(fns.ori),
      box: {
        dimensions: new Cesium.Cartesian3(...p.size), material: Cesium.Color.fromCssColorString(PANEL),
        outline: true, outlineColor: Cesium.Color.fromCssColorString(LINE),
      },
      properties: { kind: "lander-panel" },
    }, fns);
  }

  /** Constant properties from the pose functions (landed marker). */
  const freeze = () => {
    for (const e of entities) {
      if (!e._pose) continue;
      if (e._pose.pos) e.position = new Cesium.ConstantPositionProperty(e._pose.pos());
      if (e._pose.ori) e.orientation = new Cesium.ConstantProperty(e._pose.ori());
      if (e._pose.line) e.polyline.positions = new Cesium.ConstantProperty(e._pose.line());
    }
  };
  const yawText = () => (yaw.target === null
    ? (plates.length ? "awaiting analysis…" : "—")
    : `→ ${Math.round(Cesium.Math.toDegrees(Cesium.Math.zeroToTwoPi(yaw.target))) % 360}°${yaw.auto ? " AUTO" : ""} · panels to Sun`);
  if (plates.length) hud.setYaw(yawText());
  /** Heading state shared with setHeading(); `landed` switches it to rotating the frozen marker. */
  // Test hook: wall-plate azimuths measured from the rendered entity positions (not from the specs).
  const plateAzimuths = () => {
    const toEnu = Cesium.Matrix3.transpose(rot, new Cesium.Matrix3());
    const now = Cesium.JulianDate.now();
    return entities.filter((e) => e.properties?.kind?.getValue?.() === "lander-panel").map((e) => {
      const d = Cesium.Cartesian3.subtract(e.position.getValue(now), current, new Cesium.Cartesian3());
      const enu = Cesium.Matrix3.multiplyByVector(toEnu, d, new Cesium.Cartesian3());
      if (Math.hypot(enu.x, enu.y) < 1e-3) return null;   // deck plate (centred)
      return Math.round(Cesium.Math.toDegrees(Cesium.Math.zeroToTwoPi(Math.atan2(enu.x, enu.y))));
    });
  };
  const myPose = { yaw, plates: plates.length, landed: false, freeze: () => { freeze(); placeModel(); }, hud, yawText, plateAzimuths };
  pose = myPose;
  if (earlyHeading !== undefined) {   // the analysis answered while the model was still loading
    setHeading(viewer, earlyHeading.deg, earlyHeading.auto, options.reducedMotion);
    earlyHeading = undefined;
    if (options.reducedMotion && yaw.target !== null) yaw.heading = yaw.target;
  }

  const simplify = () => {
    if (simplified) return;
    simplified = true;
    for (const e of entities.filter((x) => x._optional)) viewer.entities.remove(e);
    if (glb) glb.model.shadows = Cesium.ShadowMode.DISABLED;
  };

  const camera = viewer.camera;
  // Landed shot: the lander fills ~30 % of the view height (and at most half its width), seen slightly
  // from above, with the Sun behind-left of the camera so the lit side and the long shadow both show.
  const fovy = camera.frustum.fovy ?? 0.6;
  const fovx = 2 * Math.atan(Math.tan(fovy / 2) * (camera.frustum.aspectRatio ?? 1.6));
  const widthM = glb ? glb.widthM : parts.height;
  const finalRange = glb
    ? Math.max(parts.height / (0.3 * 2 * Math.tan(fovy / 2)), widthM / (0.5 * 2 * Math.tan(fovx / 2)))
    : frame * 9;
  const finalPitch = Cesium.Math.toRadians(-22);
  const finalHeading = Number.isFinite(options.sunAzDeg)
    ? Cesium.Math.toRadians(options.sunAzDeg + 120)          // looking away from the Sun, 60° off its line
    : Cesium.Math.toRadians(270);
  const heading0 = finalHeading - Cesium.Math.toRadians(55);
  const finalHpr = new Cesium.HeadingPitchRange(finalHeading, finalPitch, finalRange);
  // Aim below the lander so it sits above the middle of the view (the verdict card covers the bottom).
  const drop = 0.12 * 2 * finalRange * Math.tan(fovy / 2) / Math.cos(finalPitch);
  const landedAt = () => localToWorld(surface(), rot, 0, 0, parts.height * 0.5 - drop);
  orbitClearance = Math.max(2, 0.3 * frame);

  return new Promise((resolve) => {
    const state = { viewer, entities, prims, fx: [], frame: 0, hud, onKey, resolve, fps: 0, simplified: false };
    pending = null;          // the running animation owns the HUD and the key listener now
    active = state;

    const finish = (status) => {
      if (active !== state) return;
      active = null;
      cancelAnimationFrame(state.frame);
      document.removeEventListener("keydown", state.onKey, true);
      const g = renderedGround();
      if (g !== undefined && Math.abs(g - ground) < 500) groundAdj = g - ground;   // final snap to the rendered terrain
      current = surface();
      if (yaw.target !== null) yaw.heading = yaw.target;   // skip / short descent: end at the target heading
      placeModel();
      for (const e of state.fx) viewer.entities.remove(e);
      safeLookAt(camera, landedAt(), finalHpr);   // stays in orbit around the site until another camera command
      state.hud?.update(telemetry(1));
      state.hud?.done();
      // Freeze the landed model as constants (cheap) and keep it as a marker; setHeading() can still turn it.
      freeze();
      myPose.landed = true;
      marker = entities;
      markerPrims = prims;
      startSettle();
      resolve({ status, fps: Math.round(state.fps), simplified, model: glb ? "gltf" : "schematic" });
    };

    const onSkip = () => finish("skipped");
    skipHandler = onSkip;

    // After touchdown keep the landed marker on the terrain that is actually rendered: finer tiles
    // can keep streaming in for a few seconds and shift the surface by tens of metres.
    const startSettle = () => {
      stopSettle();
      const t0s = performance.now();
      settleTimer = setInterval(() => {
        if (viewer.isDestroyed() || performance.now() - t0s > 8000) { stopSettle(); return; }
        const g = renderedGround();
        if (g === undefined || Math.abs(g - ground) > 500) return;
        const delta = g - ground - groundAdj;
        if (Math.abs(delta) < 0.3) return;
        groundAdj += delta;
        current = surface();
        freeze();   // re-place every part from its pose function (keeps the current heading)
        placeModel();
        // The camera still orbits the old target: move it along (keeping the user's heading, pitch and
        // range), or a +68 m tile jump buries a small lander's close-up camera under the terrain.
        if (!viewer.isDestroyed() && !Cesium.Matrix4.equals(camera.transform, Cesium.Matrix4.IDENTITY)) {
          safeLookAt(camera, landedAt(), new Cesium.HeadingPitchRange(camera.heading, camera.pitch, Cesium.Cartesian3.magnitude(camera.position)));
        }
      }, 400);
    };

    if (prepSkipped) { onSkip(); return; }

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
    let ringR = frame * 0.4;
    const ring = {
      position: new Cesium.CallbackProperty(() => localToWorld(surface(), rot, 0, 0, 0.5), false),
      ellipse: {
        semiMajorAxis: new Cesium.CallbackProperty(() => ringR, false),
        semiMinorAxis: new Cesium.CallbackProperty(() => ringR, false),
        height: new Cesium.CallbackProperty(() => ground + groundAdj + 0.5, false),
        material: new Cesium.ColorMaterialProperty(new Cesium.CallbackProperty(
          () => Cesium.Color.fromCssColorString("#d7e2ec").withAlpha(0.55 * Math.max(0, 1 - fxT / 0.9)), false)),
      },
      properties: { kind: "lander-fx" },
    };
    const flash = {
      position: new Cesium.CallbackProperty(() => localToWorld(surface(), rot, 0, 0, frame * 0.3), false),
      point: {
        pixelSize: new Cesium.CallbackProperty(() => 6 + 50 * Math.min(1, fxT / 0.25), false),
        color: new Cesium.CallbackProperty(() => Cesium.Color.WHITE.withAlpha(Math.max(0, 1 - fxT / 0.45)), false),
        disableDepthTestDistance: Number.POSITIVE_INFINITY,
      },
      properties: { kind: "lander-fx" },
    };

    const t0 = performance.now();
    let last = t0, frames = 0, frameTime = 0, frameNo = 0;
    const tick = (now) => {
      if (active !== state) return;
      const s = (now - t0) / 1000;
      const dt = now - last; last = now;
      if (s > 0.6) { frames++; frameTime += dt; state.fps = 1000 / (frameTime / frames); }   // skip start-up jank
      if (!simplified && frames >= 25 && state.fps < FPS_FLOOR) simplify();

      const u = Math.min(1, s / DESCENT_S);
      // Final approach: follow the rendered terrain (sampled every few frames, eased in).
      if (s > DESCENT_S * 0.55 && (frameNo++ % 6) === 0) {
        const g = renderedGround();
        if (g !== undefined && Math.abs(g - ground) < 500) groundAdj += (g - ground - groundAdj) * 0.35;
      }
      current = pathAt(s);
      // Yaw manoeuvre: from the terminal phase on, turn towards the analysis heading so the turn
      // ends around touchdown (at least 60°/s; faster when little time is left).
      if (yaw.target !== null && altitude(u) < YAW_START_ALT_M) {
        const diff = angleDiff(yaw.heading, yaw.target);
        if (Math.abs(diff) > 1e-4) {
          // Before the deadline (altitude YAW_DONE_ALT_M) the rate is chosen so the turn ends exactly
          // at the deadline (the last frame snaps); a target that arrives after it is caught up at a
          // fixed fast rate. No exponential tail, so the lander is aligned well before touchdown.
          const left = YAW_DONE_S - s;
          const rate = left > 0 ? Math.min(YAW_LATE_RATE, Math.max(YAW_MIN_RATE, Math.abs(diff) / left)) : YAW_LATE_RATE;
          const step = rate * (dt / 1000);
          yaw.heading += Math.sign(diff) * Math.min(Math.abs(diff), step);
        }
      }
      placeModel();
      state.hud.update(telemetry(u));

      if (u < 1) {
        // Chase camera: slow orbit, closing in as the lander slows down, ending exactly on the landed shot.
        const k = Math.pow(u, 0.7);
        const hpr = new Cesium.HeadingPitchRange(
          heading0 + (finalHeading - heading0) * u,
          Cesium.Math.toRadians(-14) + (finalPitch - Cesium.Math.toRadians(-14)) * u,
          finalRange * (6.5 - 5.5 * k));
        safeLookAt(camera, localToWorld(current, rot, 0, 0, parts.height * 0.5 - drop * u), hpr);
      } else {
        if (touchdownAt != null) {
          fxT = (now - touchdownAt) / 1000;
          ringR = frame * (0.4 + 2.8 * Math.min(1, fxT / 0.9));   // dust ring scales with the lander
        }
        if (touchdownAt == null) {
          touchdownAt = now;
          if (!simplified) state.fx.push(viewer.entities.add(ring));
          state.fx.push(viewer.entities.add(flash));
        }
        safeLookAt(camera, landedAt(), finalHpr);
        if (s >= DESCENT_S + SETTLE_S) { finish("landed"); return; }
      }
      state.frame = requestAnimationFrame(tick);
    };
    state.frame = requestAnimationFrame(tick);
  });
}
