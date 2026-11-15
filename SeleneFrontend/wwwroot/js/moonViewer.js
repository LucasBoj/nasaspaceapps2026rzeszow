// Cesium ion asset ID of the lunar 3D terrain tileset — sprawdź w Cesium ion (Asset Depot → Moon terrain).
import * as sky from "./skyView.js";

const MOON_TERRAIN_ION_ASSET_ID = 2684829;

const MOON_TREK_WAC_URL =
  "https://trek.nasa.gov/tiles/Moon/EQ/LRO_WAC_Mosaic_Global_303ppd_v02/1.0.0/default/default028mm/{z}/{y}/{x}.jpg";

const SITE_COLOR = Cesium.Color.fromCssColorString("#4fc3f7");
const SELECTED_COLOR = Cesium.Color.fromCssColorString("#ffb020");
const CUSTOM_COLOR = Cesium.Color.fromCssColorString("#3ddc84");
const LABEL_BG_COLOR = Cesium.Color.fromCssColorString("#0a0e13").withAlpha(0.75);
const CUSTOM_SITE_ID = "custom-site";
const FLY_DURATION_S = 1.8;
const PIN_SIZE = { site: 8, selected: 12, custom: 10 };
const HOVER_GROW_PX = 3;

const SELECTION_RING_ID = "selection-ring";
const SELECTION_LEADER_ID = "selection-leader";
const LEADER = { w: 34, h: 26 };            // px, from the pin to the label anchor
const LABEL_OFFSET = new Cesium.Cartesian2(10, -10);
const SELECTED_LABEL_OFFSET = new Cesium.Cartesian2(LEADER.w + 4, -LEADER.h - 2);

let viewer = null;
let dotNetRef = null;
let landingModule = null;   // loaded on first use only (feature can be switched off)
let terrainTileset = null;  // Cesium Moon Terrain (3D mode only)

/**
 * Uniform ambient light as spherical-harmonic coefficients (only the L00 band). The Moon has no
 * sky, so without it surfaces facing away from a grazing polar Sun render pitch black. This
 * stands in for regolith bounce light; the Sun's direction itself is unchanged.
 */
function ambientCoefficients(level) {
  const c = Array.from({ length: 9 }, () => new Cesium.Cartesian3(0, 0, 0));
  c[0] = new Cesium.Cartesian3(level, level, level * 1.04);
  return c;
}
let reducedMotion = false;
let selectedSiteId = null;
let locked = false;          // a lander is on the surface: site picking is off until NEW LANDING
let enhance = true;          // visual-only brightening (ambient + light intensity)
let flying = 0;              // camera flights in progress (the camera guard leaves them alone)
let lastSunAzDeg = null;     // for the landed shot: look across the long shadow
let clickTimer = 0;          // single click waits briefly so a double click can zoom instead
let guardSample = { t: 0, ground: null, carto: null };

// Camera limits (free navigation; the landing animation's orbit has its own, smaller clearance).
const MIN_CLEARANCE_M = 300;                 // never closer to the terrain than this
const TILE_MARGIN_M = 25;                    // terrain tiles refine under the camera (tens of metres): keep a margin
const MAX_ZOOM_M = 6_000_000;                // whole southern hemisphere in view
const PITCH_MAX_DEG = -10;                   // never flatter than this (no looking into empty space)
const PITCH_MIN_DEG = -89;                   // never exactly straight down (heading would flip)
const LOCKED_TITLE = "Landed — start a new landing to change the site";
let hoveredId = null;
let listHoverId = null;   // site row under the mouse in the panel's site list (highlights its pin)
let pendingHoverPos = null;
let hoverFrame = 0;

// ---- Frame rate: every frame only while something moves ----
// Cesium redraws the whole globe on every animation frame by default. The startup profile (2026-11-14,
// tools/startup-profile) showed Scene.render taking about half of the main thread with a still camera — the
// same thread the .NET analysis runs on. A still scene (selection pulse, hover, streaming tiles) is drawn at
// IDLE_FPS; camera moves, the landing animation and its post-touchdown terrain settling get every frame.
// Nothing is frozen: unlike requestRenderMode, the idle loop still draws everything, only less often.
const IDLE_FPS = 24;
const LANDING_SETTLE_MS = 9000;   // landingAnimation follows refining terrain tiles for ~8 s after touchdown
let cameraMoving = false;
let wasLanding = false;
let fullRateUntil = 0;
let frameRateTimer = 0;

/** Full frame rate for at least the next <ms> (e.g. a marker turn that is not a camera move). */
function fullRate(ms) {
  fullRateUntil = Math.max(fullRateUntil, performance.now() + ms);
  updateFrameRate();
}

function updateFrameRate() {
  if (!viewer || viewer.isDestroyed()) return;
  const landing = landingModule?.isAnimating?.() ?? false;
  if (wasLanding && !landing) fullRateUntil = Math.max(fullRateUntil, performance.now() + LANDING_SETTLE_MS);
  wasLanding = landing;
  const target = cameraMoving || landing || performance.now() < fullRateUntil ? undefined : IDLE_FPS;
  if (viewer.targetFrameRate !== target) viewer.targetFrameRate = target;
}

// Test hook: the current target frame rate (undefined = every animation frame).
window.__seleneFrameRate = () => (viewer && !viewer.isDestroyed() ? viewer.targetFrameRate ?? "full" : null);

export async function initViewer(elementId, token, dotNetReference) {
  dotNetRef = dotNetReference;
  const hasToken = typeof token === "string" && token.trim().length > 0;
  if (hasToken) {
    Cesium.Ion.defaultAccessToken = token.trim();
  }

  // Moon must be the default ellipsoid BEFORE the viewer (and its globe) is created.
  Cesium.Ellipsoid.default = Cesium.Ellipsoid.MOON;

  viewer = new Cesium.Viewer(elementId, {
    terrainProvider: new Cesium.EllipsoidTerrainProvider({ ellipsoid: Cesium.Ellipsoid.MOON }),
    baseLayer: false, // no default Bing (Earth) imagery
    baseLayerPicker: false,
    geocoder: false,
    homeButton: false,
    sceneModePicker: false,
    navigationHelpButton: false,
    animation: false,
    timeline: false,
    fullscreenButton: false,
    infoBox: false,
    selectionIndicator: false,
    skyAtmosphere: false,
    scene3DOnly: true,
  });

  const scene = viewer.scene;
  scene.globe.showGroundAtmosphere = false;
  scene.globe.baseColor = Cesium.Color.fromCssColorString("#1a1d21");
  scene.backgroundColor = Cesium.Color.BLACK;
  // Stars: Cesium makes its default sky box only for the Earth ellipsoid, so add the bundled Tycho-2 one here
  // (self-hosted in lib/cesium, no network). Faint enough not to compete with the grazing-lit terrain.
  try { scene.skyBox = Cesium.SkyBox.createEarthSkyBox(); scene.skyBox.show = true; } catch { /* no stars */ }
  // Sun and Moon billboards belong to Earth's sky — hide them.
  if (scene.sun) scene.sun.show = false;
  if (scene.moon) scene.moon.show = false;
  // Attribution is shown in the app footer.
  viewer.cesiumWidget.creditContainer.style.display = "none";

  // Safety net: a render error normally stops Cesium for good. Drop the only dynamic geometry we
  // add (the landing animation) and resume the render loop — at most a few times per session.
  let renderRecoveries = 0;
  viewer.cesiumWidget.showRenderLoopErrors = false;
  scene.renderError.addEventListener((_scene, error) => {
    window.__seleneRenderErrors = (window.__seleneRenderErrors ?? 0) + 1;
    console.warn("[MoonViewer] render error — recovering:", error?.message ?? error);
    landingModule?.cancel();
    if (renderRecoveries++ < 3) setTimeout(() => { if (!viewer.isDestroyed()) viewer.useDefaultRenderLoop = true; }, 0);
  });

  // NASA Moon Trek LRO WAC mosaic — works without any token.
  viewer.imageryLayers.addImageryProvider(
    new Cesium.UrlTemplateImageryProvider({
      url: MOON_TREK_WAC_URL,
      tilingScheme: new Cesium.GeographicTilingScheme({ ellipsoid: Cesium.Ellipsoid.MOON }),
      maximumLevel: 7,
      credit: "NASA/JPL Moon Trek — LRO WAC mosaic",
    })
  );

  let terrainLoaded = false;
  if (hasToken) {
    try {
      const tileset = await Cesium.Cesium3DTileset.fromIonAssetId(MOON_TERRAIN_ION_ASSET_ID);
      scene.primitives.add(tileset);
      scene.globe.show = false;
      terrainTileset = tileset;
      terrainLoaded = true;
    } catch (e) {
      console.warn("Lunar 3D terrain unavailable, falling back to Moon Trek mosaic:", e);
    }
  }

  try { reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches; } catch { reducedMotion = false; }
  // Start far out; introFlight() brings the camera down over the south pole.
  viewer.camera.setView({
    destination: Cesium.Cartesian3.fromDegrees(0, -35, 9000000, Cesium.Ellipsoid.MOON),
    orientation: { heading: 0, pitch: Cesium.Math.toRadians(-90), roll: 0 },
  });
  viewer.screenSpaceEventHandler.setInputAction(onClickDeferred, Cesium.ScreenSpaceEventType.LEFT_CLICK);
  viewer.screenSpaceEventHandler.setInputAction(onDoubleClick, Cesium.ScreenSpaceEventType.LEFT_DOUBLE_CLICK);
  viewer.screenSpaceEventHandler.setInputAction(onMouseMove, Cesium.ScreenSpaceEventType.MOUSE_MOVE);
  setupCameraControls();
  // Test hook: the same path as a click on the terrain at a given point (E2E tests pick exact coordinates).
  window.__seleneTestClick = (lat, lon) => {
    if (locked) return false;
    placeCustomPin(lat, lon);
    selectSite(null);
    decorate(CUSTOM_SITE_ID);
    dotNetRef?.invokeMethodAsync("OnCustomSiteCreated", lat, lon);
    return true;
  };

  return { terrainLoaded, hasToken };
}

// ---- Camera: no inertia, bounded zoom and tilt, never under the terrain ----

function setupCameraControls() {
  const scene = viewer.scene;
  const ssc = scene.screenSpaceCameraController;
  ssc.inertiaSpin = 0;
  ssc.inertiaTranslate = 0;
  ssc.inertiaZoom = 0;
  ssc.maximumZoomDistance = MAX_ZOOM_M;
  ssc.minimumZoomDistance = 1;            // the landed close-up orbits a 2 m lander; the guard keeps free flight higher
  ssc.enableCollisionDetection = true;   // globe (2D mode) only
  // Not Cesium3DTileset.enableCollision: its per-frame raycasts against the terrain tiles stalled rendering
  // for seconds at start-up (1 fps, measured 2026-11-14). guardCamera() keeps the clearance instead.
  scene.preRender.addEventListener(guardCamera);
  document.addEventListener("keydown", onKeyDown);
  viewer.canvas.addEventListener("pointerdown", viewerInteraction, { passive: true });
  viewer.canvas.addEventListener("wheel", viewerInteraction, { passive: true });

  viewer.camera.moveStart.addEventListener(() => { cameraMoving = true; updateFrameRate(); });
  viewer.camera.moveEnd.addEventListener(() => { cameraMoving = false; fullRate(300); });
  if (frameRateTimer) clearInterval(frameRateTimer);
  frameRateTimer = setInterval(updateFrameRate, 200);
  fullRate(1500);   // first tiles and the intro flight
}

let lastInteraction = 0;
/** Camera drag / wheel on the globe, or Esc: tell the app (it folds the mission summary). Throttled. */
function viewerInteraction() {
  const now = performance.now();
  if (now - lastInteraction < 400) return;
  lastInteraction = now;
  dotNetRef?.invokeMethodAsync("OnViewerInteraction").catch(() => {});
}

function onKeyDown(e) {
  if (e.key === "Escape" && !e.defaultPrevented && !document.querySelector("[role=dialog][aria-modal=true]")) { viewerInteraction(); return; }
  if (e.key !== "Home" || e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
  const t = e.target;
  if (t && (t.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(t.tagName))) return;
  e.preventDefault();
  resetView(true);
}

/** Track our own camera flights so the guard does not fight them. */
function fly(options) {
  flying++;
  let done = false;
  const end = () => { if (!done) { done = true; flying = Math.max(0, flying - 1); } };
  viewer.camera.flyTo({ ...options, complete: () => { end(); options.complete?.(); }, cancel: () => { end(); options.cancel?.(); } });
  setTimeout(end, ((options.duration ?? 3) + 1) * 1000);   // safety net
}

/**
 * Terrain height under a point: globe (2D mosaic) or the rendered 3D tiles. sampleHeight on 3D tiles is a
 * render pass, so it runs at most every 250 ms (the last value is reused in between, even while moving) and
 * only near the surface — above 30 km no lunar terrain can reach the camera.
 */
const GUARD_SAMPLE_MS = 250;
const GUARD_NEAR_M = 30_000;
function groundUnder(carto) {
  const scene = viewer.scene;
  if (carto.height > GUARD_NEAR_M) return null;
  if (scene.globe.show) return scene.globe.getHeight(carto) ?? 0;
  const now = performance.now();
  // Close to the limit (or no sample yet) the terrain is re-sampled almost every frame; higher up, every 250 ms.
  const near = guardSample.ground == null || carto.height - guardSample.ground < 1500;
  if (now - guardSample.t < (near ? 60 : GUARD_SAMPLE_MS)) return guardSample.ground;
  let h = null;
  if (scene.sampleHeightSupported) {
    try { h = scene.sampleHeight(carto); } catch { h = null; }
  }
  guardSample = { t: now, ground: Number.isFinite(h) ? h : guardSample.ground, carto: null };
  return guardSample.ground;
}

let lastGoodView = null;   // { destination, orientation } of the last frame with a valid camera

const finite = (c) => Number.isFinite(c.x) && Number.isFinite(c.y) && Number.isFinite(c.z);

function guardCamera() {
  if (!viewer || viewer.isDestroyed()) return;
  const camera = viewer.camera;
  // Safety: input fighting the clearance limit at the floor can drive the camera to NaN in some engines
  // (seen in WebKit), which stops Cesium rendering. Restore the last valid view before this frame renders.
  if (!finite(camera.positionWC) || !Number.isFinite(camera.pitch)) {
    camera.lookAtTransform(Cesium.Matrix4.IDENTITY);
    if (lastGoodView) camera.setView(lastGoodView); else resetView(false);
    return;
  }
  if (flying > 0) return;
  if (surface) return;   // Surface view keeps its own clearance (keepAboveTerrain)
  const lockedOrbit = !Cesium.Matrix4.equals(camera.transform, Cesium.Matrix4.IDENTITY);
  if (!lockedOrbit) {
    lastGoodView = {
      destination: Cesium.Cartesian3.clone(camera.positionWC),
      orientation: { heading: camera.heading, pitch: camera.pitch, roll: 0 },
    };
  }
  if (lockedOrbit) {
    viewer.scene.screenSpaceCameraController.minimumZoomDistance = 1;   // close-up orbit of a 2–50 m lander
    // Orbit around the landed lander (lookAt): the landing module owns it while it animates.
    if (landingModule?.isAnimating?.()) return;
    const min = landingModule?.orbitClearanceM?.() ?? 3;
    const carto = Cesium.Cartographic.fromCartesian(camera.positionWC, Cesium.Ellipsoid.MOON);
    const ground = carto && groundUnder(carto);
    if (ground == null || carto.height - ground >= min) return;
    // Too low: tilt the orbit down a little (keeps the target in the middle, no jump).
    const pitch = Math.max(camera.pitch - Cesium.Math.toRadians(2), Cesium.Math.toRadians(-80));
    const range = Cesium.Cartesian3.magnitude(camera.position);
    const center = Cesium.Matrix4.getTranslation(camera.transform, new Cesium.Cartesian3());
    camera.lookAt(center, new Cesium.HeadingPitchRange(camera.heading, pitch, range));
    return;
  }

  const carto = Cesium.Cartographic.fromCartesian(camera.positionWC, Cesium.Ellipsoid.MOON);
  if (!carto) return;
  // 1) Tilt: flatter views are allowed only near the surface. From far away the centre of the view must
  //    stay on the Moon's disk (depression ≥ 90° − its angular radius), so the Moon never leaves the frame.
  const R = Cesium.Ellipsoid.MOON.maximumRadius;
  const half = Cesium.Math.toDegrees(Math.asin(R / (R + Math.max(carto.height, 1))));
  const maxPitch = Math.min(PITCH_MAX_DEG, -(90 - half));
  const pitchDeg = Cesium.Math.toDegrees(camera.pitch);
  const clampedPitch = Math.max(PITCH_MIN_DEG, Math.min(maxPitch, pitchDeg));
  // 2) Height: at least MIN_CLEARANCE_M above the terrain under the camera.
  const ground = groundUnder(carto);
  const minH = ground == null ? -Infinity : ground + MIN_CLEARANCE_M + TILE_MARGIN_M;
  const tooLow = carto.height < minH;
  // Near the floor, let Cesium stop zooming by itself instead of fighting it every frame.
  viewer.scene.screenSpaceCameraController.minimumZoomDistance =
    ground != null && carto.height - ground < 3 * MIN_CLEARANCE_M ? MIN_CLEARANCE_M : 1;
  if (Math.abs(clampedPitch - pitchDeg) < 0.01 && !tooLow) return;
  camera.setView({
    destination: tooLow ? Cesium.Cartesian3.fromRadians(carto.longitude, carto.latitude, minH, Cesium.Ellipsoid.MOON) : camera.positionWC,
    orientation: { heading: camera.heading, pitch: Cesium.Math.toRadians(clampedPitch), roll: 0 },
  });
}

/** Double click on the terrain: smooth zoom towards that point (does not pick a site). */
function onDoubleClick(click) {
  if (clickTimer) { clearTimeout(clickTimer); clickTimer = 0; }
  if (!viewer || !Cesium.Matrix4.equals(viewer.camera.transform, Cesium.Matrix4.IDENTITY)) return;
  let target = viewer.scene.pickPositionSupported ? viewer.scene.pickPosition(click.position) : undefined;
  if (!Cesium.defined(target)) target = viewer.camera.pickEllipsoid(click.position, Cesium.Ellipsoid.MOON);
  if (!Cesium.defined(target)) return;
  const cam = viewer.camera.positionWC;
  const destination = Cesium.Cartesian3.lerp(target, cam, 0.4, new Cesium.Cartesian3());
  fly({ destination, orientation: { heading: viewer.camera.heading, pitch: viewer.camera.pitch, roll: 0 },
        duration: reducedMotion ? 0 : 0.6, easingFunction: Cesium.EasingFunction.QUADRATIC_OUT });
}

function onClickDeferred(click) {
  if (clickTimer) clearTimeout(clickTimer);
  const position = Cesium.Cartesian2.clone(click.position);
  clickTimer = setTimeout(() => { clickTimer = 0; onLeftClick({ position }); }, 250);
}

/** LANDED locks site picking; ENHANCE brightens the scene (visual only). */
export function setViewerFlags(isLocked, isEnhanced) {
  if (!viewer) return;
  locked = !!isLocked;
  const enhanceChanged = enhance !== !!isEnhanced;
  enhance = !!isEnhanced;
  viewer.scene.canvas.title = locked ? LOCKED_TITLE : "";
  if (locked && hoveredId) { const prev = hoveredId; hoveredId = null; restylePin(prev); }
  viewer.scene.canvas.style.cursor = "";
  for (const e of viewer.entities.values) if (entityKind(e) === "site") styleSitePin(e, e.id === selectedSiteId);
  if (enhanceChanged && lastSun) { sunLightKey = null; setSunLight(...lastSun); }
}

/** Resolve true when the globe has finished loading its visible tiles, false after timeoutMs. */
export function waitForTiles(timeoutMs) {
  return new Promise((resolve) => {
    if (!viewer) return resolve(false);
    const globe = viewer.scene.globe;
    let settled = false;
    const finish = (ok) => { if (settled) return; settled = true; remove(); clearTimeout(timer); resolve(ok); };
    const remove = globe.tileLoadProgressEvent.addEventListener((pending) => {
      if (pending === 0 && globe.tilesLoaded) finish(true);
    });
    const timer = setTimeout(() => finish(globe.tilesLoaded), timeoutMs);
  });
}

/** Intro: from far space down to the south-pole overview (instant with reduced motion). */
/** Release a camera lock left by the landing animation (orbit around the landed site). */
function releaseCamera() {
  if (viewer && !viewer.isDestroyed()) viewer.camera.lookAtTransform(Cesium.Matrix4.IDENTITY);
}

export function introFlight() {
  if (!viewer) return;
  releaseCamera();
  if (reducedMotion) { resetView(false); return; }
  const view = southPoleView();
  fly({
    ...view,
    duration: 3.2,
    easingFunction: Cesium.EasingFunction.QUADRATIC_IN_OUT,
  });
}

function southPoleView() {
  return {
    // The 9 regions sit in the middle of the frame with a thin strip of horizon above (was -72°, 420 km, -38°:
    // half the view was black sky). Compared on screenshots 2026-11-15.
    destination: Cesium.Cartesian3.fromDegrees(0, -76, 380000, Cesium.Ellipsoid.MOON),
    orientation: { heading: Cesium.Math.toRadians(180), pitch: Cesium.Math.toRadians(-50), roll: 0 },
  };
}

/** South-pole overview. Instant by default; pass animate=true for a camera flight. */
export function resetView(animate = false) {
  if (!viewer) return;
  exitSurfaceView();
  releaseCamera();
  const view = southPoleView();
  if (animate && !reducedMotion) {
    viewer.camera.cancelFlight();
    fly({ ...view, duration: FLY_DURATION_S, easingFunction: Cesium.EasingFunction.QUADRATIC_OUT });
  } else {
    viewer.camera.setView(view);
  }
}

function isPin(entity) {
  const kind = entityKind(entity);
  return kind === "site" || kind === "custom" || kind === "candidate";
}

/**
 * LANDED: the site cannot change until New landing — instead of ignoring the click, offer a new landing at the
 * clicked pin or terrain point (80–90°S). LEFT_CLICK only fires without a drag, so moving the camera never asks.
 */
function offerNewLanding(click) {
  if (surface) return;   // looking around from the surface: clicks belong to the camera
  const picked = viewer.scene.pick(click.position);
  const entity = Cesium.defined(picked) ? picked.id : undefined;
  const { x, y } = click.position;
  if (entity instanceof Cesium.Entity && entityKind(entity) === "site") {
    dotNetRef?.invokeMethodAsync("OnLandedClick", entity.id, 0, 0, x, y, viewer.canvas.clientWidth, viewer.canvas.clientHeight).catch(() => {});
    return;
  }
  let position = viewer.scene.pickPositionSupported ? viewer.scene.pickPosition(click.position) : undefined;
  if (!Cesium.defined(position)) position = viewer.camera.pickEllipsoid(click.position, Cesium.Ellipsoid.MOON);
  if (!Cesium.defined(position)) return;
  const carto = Cesium.Cartographic.fromCartesian(position, Cesium.Ellipsoid.MOON);
  if (!carto) return;
  const lat = Cesium.Math.toDegrees(carto.latitude), lon = Cesium.Math.toDegrees(carto.longitude);
  if (lat > -80) return;   // custom analysis covers 80–90°S only
  dotNetRef?.invokeMethodAsync("OnLandedClick", "", lat, lon, x, y, viewer.canvas.clientWidth, viewer.canvas.clientHeight).catch(() => {});
}

/** "Land here" on a terrain point: the custom pin, as a normal click would place it. */
export function markCustomSite(lat, lon) {
  if (!viewer || viewer.isDestroyed()) return;
  placeCustomPin(lat, lon);
  selectSite(null);
  decorate(CUSTOM_SITE_ID);
}

/** Test hook (E2E): the screen position of a site pin, so a test can click it. */
window.__seleneSitePx = (id) => {
  const e = viewer?.entities.getById(id);
  if (!e) return null;
  // Pins are clamped to the 3D terrain (kilometres above the ellipsoid): project the rendered ground point.
  const c = Cesium.Cartographic.fromCartesian(e.position.getValue(Cesium.JulianDate.now()), Cesium.Ellipsoid.MOON);
  let h = null;
  try { h = viewer.scene.sampleHeightSupported ? viewer.scene.sampleHeight(c) : null; } catch { h = null; }
  c.height = Number.isFinite(h) ? h : (viewer.scene.globe.getHeight(c) ?? 0);
  const w = Cesium.SceneTransforms.worldToWindowCoordinates(viewer.scene, Cesium.Cartographic.toCartesian(c, Cesium.Ellipsoid.MOON));
  return w ? { x: w.x, y: w.y } : null;
};

// Hover: pick at most once per animation frame.
function onMouseMove(movement) {
  pendingHoverPos = movement.endPosition;
  if (!hoverFrame) hoverFrame = requestAnimationFrame(updateHover);
}

function updateHover() {
  hoverFrame = 0;
  if (!viewer || viewer.isDestroyed() || !pendingHoverPos) return;

  if (locked) return;   // landed: no hover feedback, plain cursor (the canvas title explains why)
  const picked = viewer.scene.pick(pendingHoverPos);
  const entity = Cesium.defined(picked) && picked.id instanceof Cesium.Entity ? picked.id : undefined;
  const nextId = entity && isPin(entity) ? entity.id : null;
  if (nextId === hoveredId) return;

  const prevId = hoveredId;
  hoveredId = nextId;
  restylePin(prevId);
  restylePin(nextId);
  viewer.scene.canvas.style.cursor = nextId ? "pointer" : "";
}

function restylePin(id) {
  if (!id) return;
  const entity = viewer.entities.getById(id);
  if (!entity) return;
  const hover = id === hoveredId ? HOVER_GROW_PX : 0;
  if (entityKind(entity) === "custom") {
    entity.point.pixelSize = PIN_SIZE.custom + hover;
  } else if (entityKind(entity) === "candidate") {
    entity.point.pixelSize = 6 + hover;
    entity.label.show = id === hoveredId;
  } else if (entityKind(entity) === "site") {
    styleSitePin(entity, id === selectedSiteId);
  }
}

function entityKind(entity) {
  const kind = entity?.properties?.kind;
  return kind ? kind.getValue(viewer.clock.currentTime) : undefined;
}

function onLeftClick(click) {
  if (!viewer || viewer.isDestroyed()) return;
  if (locked) { offerNewLanding(click); return; }   // landed: a click asks "start a new landing here?"
  const picked = viewer.scene.pick(click.position);
  const entity = Cesium.defined(picked) ? picked.id : undefined;

  if (entity instanceof Cesium.Entity && entityKind(entity) === "site") {
    selectSite(entity.id);
    dotNetRef?.invokeMethodAsync("OnSiteSelected", entity.id);
    return;
  }
  if (entity instanceof Cesium.Entity && ["candidate", "decor", "lander", "lander-fx"].includes(entityKind(entity))) return;

  let position;
  if (viewer.scene.pickPositionSupported) {
    position = viewer.scene.pickPosition(click.position);
  }
  if (!Cesium.defined(position)) {
    position = viewer.camera.pickEllipsoid(click.position, Cesium.Ellipsoid.MOON);
  }
  if (!Cesium.defined(position)) return; // clicked empty space

  const carto = Cesium.Cartographic.fromCartesian(position, Cesium.Ellipsoid.MOON);
  if (!carto) return;
  const lat = Cesium.Math.toDegrees(carto.latitude);
  const lon = Cesium.Math.toDegrees(carto.longitude);

  placeCustomPin(lat, lon);
  selectSite(null);
  decorate(CUSTOM_SITE_ID);
  dotNetRef?.invokeMethodAsync("OnCustomSiteCreated", lat, lon);
}

function pinGraphics(color, pixelSize, text) {
  return {
    point: {
      pixelSize,
      color,
      outlineColor: Cesium.Color.BLACK,
      outlineWidth: 1,
      heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,   // sits on 3D terrain when loaded
      disableDepthTestDistance: Number.POSITIVE_INFINITY,
    },
    label: {
      text,
      font: '600 12px "IBM Plex Sans", sans-serif',
      fillColor: color,
      style: Cesium.LabelStyle.FILL_AND_OUTLINE,
      outlineColor: Cesium.Color.BLACK,
      outlineWidth: 2,
      showBackground: true,
      backgroundColor: LABEL_BG_COLOR,
      horizontalOrigin: Cesium.HorizontalOrigin.LEFT,
      verticalOrigin: Cesium.VerticalOrigin.BOTTOM,
      pixelOffset: new Cesium.Cartesian2(10, -10),
      heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
      disableDepthTestDistance: Number.POSITIVE_INFINITY,
    },
  };
}

function placeCustomPin(lat, lon) {
  const existing = viewer.entities.getById(CUSTOM_SITE_ID);
  if (existing) viewer.entities.remove(existing);
  viewer.entities.add({
    id: CUSTOM_SITE_ID,
    position: Cesium.Cartesian3.fromDegrees(lon, lat, 0, Cesium.Ellipsoid.MOON),
    properties: { kind: "custom" },
    ...pinGraphics(CUSTOM_COLOR, PIN_SIZE.custom, `Custom site ${lat.toFixed(2)}°, ${lon.toFixed(2)}°`),
  });
}

export function loadSites(sites) {
  if (!viewer) return;

  const stale = viewer.entities.values.filter((e) => entityKind(e) === "site");
  for (const e of stale) viewer.entities.remove(e);

  for (const s of sites ?? []) {
    const e = viewer.entities.add({
      id: s.id,
      position: Cesium.Cartesian3.fromDegrees(s.lon, s.lat, 0, Cesium.Ellipsoid.MOON),
      properties: { kind: "site", approximate: !!s.approximate },
      ...pinGraphics(SITE_COLOR, PIN_SIZE.site, s.name ?? s.id),
    });
    styleSitePin(e, false);
  }

  if (selectedSiteId) {
    const id = selectedSiteId;
    selectedSiteId = null;
    selectSite(id);
  }
}

/**
 * Site pins: the full name only for the selected site and the one under the mouse (globe or the panel's site list),
 * so labels never pile up over each other. While landed only the selected site keeps its label (it must not cover the lander).
 */
function styleSitePin(entity, selected) {
  const color = selected ? SELECTED_COLOR : SITE_COLOR;
  const hot = !locked && (entity.id === hoveredId || entity.id === listHoverId);
  entity.point.color = color;
  entity.point.pixelSize = (selected ? PIN_SIZE.selected : PIN_SIZE.site) + (hot ? HOVER_GROW_PX : 0);
  if (entity.label) {
    entity.label.fillColor = color;
    entity.label.show = selected || hot;
  }
}

/** Test hook: ids of the site pins whose name label is shown (E2E checks that labels do not pile up). */
window.__seleneSiteLabels = () => viewer && !viewer.isDestroyed()
  ? viewer.entities.values.filter((e) => entityKind(e) === "site" && e.label?.show?.getValue(viewer.clock.currentTime)).map((e) => e.id)
  : [];

/** The panel's site list highlights the pin of the row under the mouse (null clears). */
export function highlightSite(siteId) {
  if (!viewer || viewer.isDestroyed()) return;
  const prev = listHoverId;
  listHoverId = siteId ?? null;
  for (const id of [prev, listHoverId]) {
    const e = id ? viewer.entities.getById(id) : null;
    if (e && entityKind(e) === "site") styleSitePin(e, id === selectedSiteId);
  }
  fullRate(300);
}

export function selectSite(siteId) {
  if (!viewer) return;

  if (selectedSiteId && selectedSiteId !== siteId) {
    const prev = viewer.entities.getById(selectedSiteId);
    if (prev && entityKind(prev) === "site") styleSitePin(prev, false);
  }

  selectedSiteId = siteId ?? null;
  if (!siteId) { decorate(null); return; }

  const entity = viewer.entities.getById(siteId);
  if (entity && entityKind(entity) === "site") styleSitePin(entity, true);
  decorate(siteId);
}

// ---- Selection decoration: pulsing ring + thin leader line to the label ----

let ringImage = null;
let leaderImage = null;

function makeRingImage() {
  const c = document.createElement("canvas");
  c.width = c.height = 64;
  const g = c.getContext("2d");
  g.strokeStyle = "#ffb020";
  g.lineWidth = 3;
  g.beginPath(); g.arc(32, 32, 26, 0, Math.PI * 2); g.stroke();
  return c;
}

function makeLeaderImage() {
  const c = document.createElement("canvas");
  c.width = LEADER.w; c.height = LEADER.h;
  const g = c.getContext("2d");
  g.strokeStyle = "rgba(255,176,32,0.9)";
  g.lineWidth = 1;
  g.beginPath(); g.moveTo(1, LEADER.h - 1); g.lineTo(LEADER.w - 1, 1); g.stroke();
  return c;
}

function removeDecor() {
  for (const id of [SELECTION_RING_ID, SELECTION_LEADER_ID]) {
    const e = viewer.entities.getById(id);
    if (e) viewer.entities.remove(e);
  }
  for (const e of viewer.entities.values) {
    if ((entityKind(e) === "site" || entityKind(e) === "custom") && e.label) e.label.pixelOffset = LABEL_OFFSET;
  }
}

function decorate(entityId) {
  if (!viewer) return;
  const target = entityId ? viewer.entities.getById(entityId) : null;
  // Already decorated (state syncs call selectSite often) → keep the running pulse.
  const ring = viewer.entities.getById(SELECTION_RING_ID);
  if (target && ring && ring.properties?.target?.getValue() === entityId
      && Cesium.Cartesian2.equals(target.label?.pixelOffset?.getValue(viewer.clock.currentTime), SELECTED_LABEL_OFFSET)) {
    return;
  }
  removeDecor();
  if (!target) return;
  const position = target.position.getValue(viewer.clock.currentTime);
  ringImage ??= makeRingImage();
  leaderImage ??= makeLeaderImage();

  const t0 = performance.now();
  const phase = () => ((performance.now() - t0) % 1600) / 1600;
  viewer.entities.add({
    id: SELECTION_RING_ID,
    position,
    properties: { kind: "decor", target: entityId },
    billboard: {
      image: ringImage,
      width: 32, height: 32,
      heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
      scale: reducedMotion ? 1.0 : new Cesium.CallbackProperty(() => 0.7 + 0.9 * phase(), false),
      color: reducedMotion ? Cesium.Color.WHITE.withAlpha(0.8)
        : new Cesium.CallbackProperty(() => Cesium.Color.WHITE.withAlpha(0.95 * (1 - phase())), false),
      disableDepthTestDistance: Number.POSITIVE_INFINITY,
    },
  });
  viewer.entities.add({
    id: SELECTION_LEADER_ID,
    position,
    properties: { kind: "decor" },
    billboard: {
      image: leaderImage,
      width: LEADER.w, height: LEADER.h,
      horizontalOrigin: Cesium.HorizontalOrigin.LEFT,
      verticalOrigin: Cesium.VerticalOrigin.BOTTOM,
      heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
      pixelOffset: new Cesium.Cartesian2(2, -2),
      disableDepthTestDistance: Number.POSITIVE_INFINITY,
    },
  });
  if (target.label) target.label.pixelOffset = SELECTED_LABEL_OFFSET;
}

// ---- Candidate landing sites overlay (Wueller et al. 2026, scored by the pipeline) ----

const CANDIDATE_LOW = Cesium.Color.fromCssColorString("#1c4a5e");
const CANDIDATE_HIGH = Cesium.Color.fromCssColorString("#4fc3f7");
const CANDIDATE_REJECTED = Cesium.Color.fromCssColorString("#5b6670");

/** points: [{ id, lat, lon, sunPct (0..100 or null), rejected, label }] */
export function showCandidates(points) {
  if (!viewer) return;
  clearCandidates();
  for (const p of points ?? []) {
    const color = p.rejected || p.sunPct == null
      ? CANDIDATE_REJECTED
      : Cesium.Color.lerp(CANDIDATE_LOW, CANDIDATE_HIGH, Math.max(0, Math.min(1, p.sunPct / 100)), new Cesium.Color());
    viewer.entities.add({
      id: `cand-${p.id}`,
      position: Cesium.Cartesian3.fromDegrees(p.lon, p.lat, 0, Cesium.Ellipsoid.MOON),
      properties: { kind: "candidate" },
      point: {
        pixelSize: 6, color,
        outlineColor: Cesium.Color.BLACK, outlineWidth: 1,
        heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
        disableDepthTestDistance: Number.POSITIVE_INFINITY,
      },
      label: {
        text: p.label, show: false,
        font: '11px "IBM Plex Mono", monospace',
        fillColor: Cesium.Color.WHITE,
        showBackground: true, backgroundColor: LABEL_BG_COLOR,
        pixelOffset: new Cesium.Cartesian2(8, -8),
        heightReference: Cesium.HeightReference.CLAMP_TO_GROUND,
        horizontalOrigin: Cesium.HorizontalOrigin.LEFT, verticalOrigin: Cesium.VerticalOrigin.BOTTOM,
        disableDepthTestDistance: Number.POSITIVE_INFINITY,
      },
    });
  }
}

export function clearCandidates() {
  if (!viewer) return;
  for (const e of viewer.entities.values.filter((x) => entityKind(x) === "candidate")) viewer.entities.remove(e);
}

// ---- Science layer: permanently shadowed regions near the selected site (NASA LOLA, Mazarico et al. 2011) ----

const PSR_FILL = Cesium.Color.fromCssColorString("#5c6bc0").withAlpha(0.4);
const PSR_EDGE = Cesium.Color.fromCssColorString("#b3bcff");

/** polys: [{ rings: [[[lon, lat], …]], areaKm2, distKm }] — outer ring only, already simplified by the pipeline. */
export function showPsr(polys) {
  if (!viewer || viewer.isDestroyed()) return;
  clearPsr();
  (polys ?? []).forEach((p, i) => {
    const ring = p.rings?.[0];
    if (!ring || ring.length < 4) return;
    const positions = Cesium.Cartesian3.fromDegreesArray(ring.flat(), Cesium.Ellipsoid.MOON);
    viewer.entities.add({
      id: `psr-${i}`,
      properties: { kind: "psr" },
      polygon: { hierarchy: positions, material: PSR_FILL, classificationType: Cesium.ClassificationType.BOTH },
    });
    viewer.entities.add({
      id: `psr-edge-${i}`,
      properties: { kind: "psr" },
      polyline: { positions, clampToGround: true, width: 1.5, material: PSR_EDGE,
                  classificationType: Cesium.ClassificationType.BOTH },
    });
  });
  fullRate(1500);   // ground primitives are built asynchronously over the next frames
}

export function clearPsr() {
  if (!viewer || viewer.isDestroyed()) return;
  for (const e of viewer.entities.values.filter((x) => entityKind(x) === "psr")) viewer.entities.remove(e);
  fullRate(300);
}

/** Test hook: number of PSR outlines on the globe. */
window.__selenePsrCount = () => (viewer && !viewer.isDestroyed()
  ? viewer.entities.values.filter((e) => entityKind(e) === "psr" && e.polygon).length : null);

export function flyToSite(siteId) {
  if (!viewer) return;
  exitSurfaceView();
  releaseCamera();
  const entity = viewer.entities.getById(siteId);
  if (!entity) return;
  // Fly to an offset above the site — flying to the surface point puts the camera into the ground.
  flying++;
  viewer.flyTo(entity, {
    duration: FLY_DURATION_S,
    offset: new Cesium.HeadingPitchRange(0, Cesium.Math.toRadians(-50), 90000),
  }).finally(() => { flying = Math.max(0, flying - 1); });
}

// ---- Landing animation (isolated module) ----

/** Play the landing animation; resolves with { status, fps, simplified }. */
export async function playLanding(options, hudHost) {
  exitSurfaceView();
  if (!viewer) return { status: "unavailable" };
  fullRate(2000);   // until the module reports isAnimating()
  const m = await landing();
  return m.play(viewer, { ...options, hudHost, sunAzDeg: lastSunAzDeg });
}

/** The landing module, loaded once; lander-model load states are forwarded to the HUD (MoonPanel.OnModelStatus). */
async function landing() {
  if (!landingModule) {
    landingModule = await import("./landingAnimation.js");
    landingModule.onModelStatus((uri, status) => dotNetRef?.invokeMethodAsync("OnModelStatus", uri, status).catch(() => {}));
  }
  return landingModule;
}

/**
 * Heading of the lander in flight or on the surface (degrees clockwise from north; null = unknown),
 * from the mission analysis. A no-op when the optional landing module was never loaded.
 */
export function setLanderHeading(headingDeg, auto, reducedMotion) {
  if (!viewer || !landingModule) return;
  fullRate(3000);   // the landed marker turns to the new heading (its own animation frames)
  landingModule.setHeading(viewer, headingDeg, auto, reducedMotion);
}

// Test hook (like __seleneRenderErrors): heading state of the landed / descending lander.
window.__seleneLanderPose = () => landingModule?.getPose() ?? null;

/**
 * Load the animation module and sample the site's ground height ahead of CONFIRM LANDING SITE.
 * The selected lander's model (GLB, ~1 MB) is fetched a moment later so it never competes with
 * the site's terrain tiles.
 */
export async function prefetchLanding(lat, lon, model) {
  if (!viewer) return;
  const m = await landing();
  if (model?.uri) setTimeout(() => m.prefetchModel(viewer, model), 1500);
  await m.prefetchGround(viewer, lat, lon);
}

/**
 * Background preload of every lander model after start-up, one after another in the given order
 * (the QUICK DEMO lander first), so the first landing of any lander does not wait for a download.
 */
export async function preloadModels(models, delayMs) {
  if (!viewer || !models?.length) return;
  await new Promise((r) => setTimeout(r, delayMs ?? 0));
  if (!viewer) return;
  const m = await landing();
  await m.preloadModels(viewer, models);
}

// ---- Sunlight from the precomputed ephemeris ----

let sunLightKey = null;

/**
 * Scene light = the Sun as seen from the site at the chosen moment (azimuth/elevation from
 * illumination/{siteId}.json), converted from the site's east-north-up frame to the Moon-fixed frame. Soft shadows on.
 * If the Sun is hidden (sunFraction 0) a weak, cool fill light from above keeps models visible.
 */
let lastSun = null;
let lastEarth = null;    // [az, el] of the Earth at the shown moment (deg)
let baseLightIntensity = null;   // intensity set by setSunLight (the replay fades relative to it)

export function setSunLight(lat, lon, azDeg, elDeg, sunPct, earthAzDeg, earthElDeg) {
  if (!viewer || viewer.isDestroyed()) return;
  if (earthAzDeg !== undefined) { lastEarth = [earthAzDeg, earthElDeg]; }
  sky.update(viewer, lat, lon, azDeg, elDeg, lastEarth?.[0] ?? NaN, lastEarth?.[1] ?? NaN);
  lastSun = [lat, lon, azDeg, elDeg, sunPct];
  lastSunAzDeg = azDeg;
  const key = `${lat.toFixed(4)},${lon.toFixed(4)},${azDeg.toFixed(1)},${elDeg.toFixed(1)},${sunPct > 0},${enhance}`;
  if (key === sunLightKey) return;
  sunLightKey = key;

  const site = Cesium.Cartesian3.fromDegrees(lon, lat, 0, Cesium.Ellipsoid.MOON);
  const rot = Cesium.Matrix4.getMatrix3(Cesium.Transforms.eastNorthUpToFixedFrame(site), new Cesium.Matrix3());
  const az = Cesium.Math.toRadians(azDeg), el = Cesium.Math.toRadians(elDeg);
  const toSunEnu = new Cesium.Cartesian3(Math.cos(el) * Math.sin(az), Math.cos(el) * Math.cos(az), Math.sin(el));
  const toSun = Cesium.Cartesian3.normalize(Cesium.Matrix3.multiplyByVector(rot, toSunEnu, new Cesium.Cartesian3()), new Cesium.Cartesian3());
  const up = Cesium.Matrix3.multiplyByVector(rot, Cesium.Cartesian3.UNIT_Z, new Cesium.Cartesian3());

  if (sunPct > 0) {
    viewer.scene.light = new Cesium.DirectionalLight({
      direction: Cesium.Cartesian3.negate(toSun, new Cesium.Cartesian3()),   // light travels from the Sun
      intensity: enhance ? 3.0 : 2.2,
    });
    baseLightIntensity = viewer.scene.light.intensity;
  } else {
    // Sun hidden: weak fill light, mostly from overhead with a hint of the Sun's bearing.
    const fill = Cesium.Cartesian3.normalize(Cesium.Cartesian3.add(
      Cesium.Cartesian3.multiplyByScalar(up, 0.8, new Cesium.Cartesian3()),
      Cesium.Cartesian3.multiplyByScalar(toSun, 0.2, new Cesium.Cartesian3()), new Cesium.Cartesian3()), new Cesium.Cartesian3());
    viewer.scene.light = new Cesium.DirectionalLight({
      direction: Cesium.Cartesian3.negate(fill, new Cesium.Cartesian3()),
      intensity: enhance ? 0.9 : 0.6,
      color: Cesium.Color.fromCssColorString("#a9c6e0"),
    });
    baseLightIntensity = viewer.scene.light.intensity;
  }
  if (terrainTileset && !terrainTileset.isDestroyed()) {
    // ENHANCE: more ambient (regolith bounce light) so the grazing-lit terrain reads on a screen.
    terrainTileset.imageBasedLighting.sphericalHarmonicCoefficients = ambientCoefficients((sunPct > 0 ? 0.55 : 0.35) + (enhance ? 0.25 : 0));
  }
  viewer.shadows = true;
  viewer.shadowMap.softShadows = true;
  viewer.shadowMap.darkness = sunPct > 0 ? 0.35 : 0.6;
}

// ---- Surface view: stand next to the lander and look at the sky (Earth, Sun, stars) ----

let surface = null;   // { pos, heading, pitch, timer, saved, handlers }
const SURFACE_EYE_M = 3;
const SURFACE_PITCH = [-10, 30];

function applySurfaceCamera() {
  viewer.camera.setView({
    destination: surface.pos,
    orientation: { heading: surface.heading, pitch: Cesium.Math.toRadians(surface.pitch), roll: 0 },
  });
  fullRate(200);
}

/** Keep the eye SURFACE_EYE_M above the rendered terrain (tiles refine by tens of metres after arriving). */
function keepAboveTerrain() {
  if (!surface || !viewer || viewer.isDestroyed()) return;
  const carto = Cesium.Cartographic.fromCartesian(surface.pos, Cesium.Ellipsoid.MOON);
  let ground = null;
  if (viewer.scene.sampleHeightSupported) {
    try { ground = viewer.scene.sampleHeight(carto, landingModule?.markerObjects?.() ?? []); } catch { ground = null; }
  }
  if (!Number.isFinite(ground)) ground = viewer.scene.globe.getHeight(carto) ?? surface.ground;
  if (!Number.isFinite(ground)) return;
  surface.ground = ground;
  // The lander on the same refined ground as the eye (a late +68 m tile jump would bury it).
  const info = landingModule?.landedInfo?.();
  if (info && !info.lift && viewer.scene.sampleHeightSupported) {
    const bc = Cesium.Cartographic.fromCartesian(info.base, Cesium.Ellipsoid.MOON);
    let g = null;
    try { g = viewer.scene.sampleHeight(bc, landingModule.markerObjects?.() ?? []); } catch { g = null; }
    if (Number.isFinite(g)) landingModule.regroundMarker?.(viewer, g);
  }
  if (Math.abs(carto.height - (ground + SURFACE_EYE_M)) > 0.5) {
    carto.height = ground + SURFACE_EYE_M;
    surface.pos = Cesium.Cartographic.toCartesian(carto, Cesium.Ellipsoid.MOON);
    applySurfaceCamera();
  }
}

/**
 * Surface view: the camera 3 m above the terrain beside the lander (or at the site), looking level towards the
 * Earth. Drag turns around the vertical and tilts −10…+30°; the eye never goes below the terrain.
 */
export function enterSurfaceView(lat, lon, withLabels = false) {
  if (!viewer || viewer.isDestroyed() || !terrainTileset) return false;
  viewer.camera.cancelFlight();
  releaseCamera();
  const site = Cesium.Cartesian3.fromDegrees(lon, lat, 0, Cesium.Ellipsoid.MOON);
  const earthAz = lastEarth?.[0] ?? lastSunAzDeg ?? 0;
  const az = Cesium.Math.toRadians(earthAz);
  // Stand behind-left of the lander as seen towards the Earth, so it is in the foreground but not in the way.
  const info = landingModule?.landedInfo?.();
  const size = info?.sizeM ?? 0;
  const back = size ? size * 3.5 + 8 : 0, side = size ? size * 0.5 + 1 : 0;   // lander in the lower middle, below the Earth
  const east = -Math.sin(az) * back + Math.cos(az) * -side, north = -Math.cos(az) * back - Math.sin(az) * -side;
  const enu = Cesium.Transforms.eastNorthUpToFixedFrame(info?.base ?? site);
  const offset = Cesium.Matrix4.multiplyByPoint(enu, new Cesium.Cartesian3(east, north, 0), new Cesium.Cartesian3());
  const carto = Cesium.Cartographic.fromCartesian(offset, Cesium.Ellipsoid.MOON);
  const siteCarto = Cesium.Cartographic.fromCartesian(info?.base ?? site, Cesium.Ellipsoid.MOON);
  carto.height = (siteCarto?.height ?? 0) + SURFACE_EYE_M;
  const ssc = viewer.scene.screenSpaceCameraController;
  const saved = surface?.saved ?? { rotate: ssc.enableRotate, translate: ssc.enableTranslate, zoom: ssc.enableZoom, tilt: ssc.enableTilt, look: ssc.enableLook };
  ssc.enableRotate = ssc.enableTranslate = ssc.enableZoom = ssc.enableTilt = ssc.enableLook = false;
  if (surface?.timer) clearInterval(surface.timer);
  surface?.handlers?.forEach(([t, f]) => viewer.canvas.removeEventListener(t, f));
  // Half the Earth's elevation: the Earth above the middle of the view, the lander in the foreground below it.
  const startPitch = Math.max(SURFACE_PITCH[0], Math.min(SURFACE_PITCH[1], (lastEarth?.[1] ?? 4) * 0.5));
  surface = { pos: Cesium.Cartographic.toCartesian(carto, Cesium.Ellipsoid.MOON), heading: az, pitch: startPitch, saved, ground: carto.height - SURFACE_EYE_M };
  let drag = null;
  const down = (e) => { drag = { x: e.clientX, y: e.clientY }; };
  const move = (e) => {
    if (!drag || !surface) return;
    surface.heading -= Cesium.Math.toRadians((e.clientX - drag.x) * 0.15);
    surface.pitch = Math.max(SURFACE_PITCH[0], Math.min(SURFACE_PITCH[1], surface.pitch + (e.clientY - drag.y) * 0.12));
    drag = { x: e.clientX, y: e.clientY };
    applySurfaceCamera();
  };
  const up = () => { drag = null; };
  surface.handlers = [["pointerdown", down], ["pointermove", move], ["pointerup", up], ["pointerleave", up]];
  surface.handlers.forEach(([t, f]) => viewer.canvas.addEventListener(t, f));
  surface.timer = setInterval(keepAboveTerrain, 400);
  // Map pins draw through the terrain (no depth test): from the surface they would float in the sky.
  surface.hidden = viewer.entities.values.filter((e) => ["site", "custom"].includes(entityKind(e)) || e.id === SELECTION_RING_ID || e.id === SELECTION_LEADER_ID);
  surface.hidden.forEach((e) => { e.show = false; });
  sky.setShown(viewer, true, withLabels, reducedMotion);
  if (viewer.scene.skyBox) viewer.scene.skyBox.show = true;   // stars
  applySurfaceCamera();
  keepAboveTerrain();
  dotNetRef?.invokeMethodAsync("OnSurfaceViewChanged", true).catch(() => {});
  return true;
}

/** Leave Surface view: mouse controls back, sky objects hidden. The caller moves the camera (orbit / site). */
export function exitSurfaceView() {
  if (!surface || !viewer || viewer.isDestroyed()) return;
  const ssc = viewer.scene.screenSpaceCameraController;
  ssc.enableRotate = surface.saved.rotate; ssc.enableTranslate = surface.saved.translate; ssc.enableZoom = surface.saved.zoom;
  ssc.enableTilt = surface.saved.tilt; ssc.enableLook = surface.saved.look;
  clearInterval(surface.timer);
  surface.handlers.forEach(([t, f]) => viewer.canvas.removeEventListener(t, f));
  surface.hidden?.forEach((e) => { e.show = true; });
  surface = null;
  sky.setShown(viewer, false);
  dotNetRef?.invokeMethodAsync("OnSurfaceViewChanged", false).catch(() => {});
}

/** Labels on the Earth / Sun while watching the mission from the surface. */
export function setSkyLabels(on) { if (surface) sky.setShown(viewer, true, on, reducedMotion); }

/** Test hooks: Surface view state and where the sky bodies are drawn (az/el from the site). */
window.__seleneSurface = () => {
  if (!viewer || viewer.isDestroyed()) return null;
  const c = Cesium.Cartographic.fromCartesian(viewer.camera.positionWC, Cesium.Ellipsoid.MOON);
  let ground = null;
  try { ground = viewer.scene.sampleHeight(c, landingModule?.markerObjects?.() ?? []); } catch { /* ignore */ }
  const info = landingModule?.landedInfo?.();
  const toLander = info ? Cesium.Cartesian3.distance(viewer.camera.positionWC, info.base) : null;
  const lb = info ? Cesium.Cartographic.fromCartesian(info.base, Cesium.Ellipsoid.MOON).height : null;
  let landerPx = null;
  if (info) { const w = Cesium.SceneTransforms.worldToWindowCoordinates(viewer.scene, Cesium.Cartesian3.add(info.base, Cesium.Cartesian3.multiplyByScalar(info.up, 0.5 * (info.heightM ?? 2), new Cesium.Cartesian3()), new Cesium.Cartesian3())); landerPx = w ? [Math.round(w.x), Math.round(w.y)] : null; }
  return { active: !!surface, toLander, landerSize: info?.sizeM ?? null, landerBase: lb, landerPx, height: c.height, ground: Number.isFinite(ground) ? ground : null, pitch: Cesium.Math.toDegrees(viewer.camera.pitch), heading: Cesium.Math.toDegrees(viewer.camera.heading) };
};
window.__seleneSky = (lat, lon) => sky.bodyAzEl(lat, lon);
/** Test hooks (E2E): show the sky for any hour of the data, and stand on the surface at a site. */
window.__seleneSetSky = (lat, lon, sunAz, sunEl, sunPct, earthAz, earthEl) => { sunLightKey = null; setSunLight(lat, lon, sunAz, sunEl, sunPct, earthAz, earthEl); };
window.__seleneEnterSurface = (lat, lon) => enterSurfaceView(lat, lon, true);
/** Is the Earth sphere the first thing the camera sees at its centre (i.e. not hidden by terrain)? */
window.__seleneEarthVisible = () => {
  const lab = viewer?.entities.getById("sky-earth-label");
  if (!lab || !window.__seleneSky(0, 0)?.shown) return null;
  const p = lab.position.getValue(Cesium.JulianDate.now());
  const w = Cesium.SceneTransforms.worldToWindowCoordinates(viewer.scene, p);
  if (!w || w.x < 0 || w.y < 0 || w.x > viewer.canvas.clientWidth || w.y > viewer.canvas.clientHeight) return { onScreen: false };
  const picked = viewer.scene.pick(w);
  return { onScreen: true, visible: !!picked && picked.id === "sky-earth", x: w.x, y: w.y };
};

// ---- Mission replay (isolated module, loaded on first use) ----

let replayModule = null;
let replayData = null;

/** Play the mission back on the landed lander (see missionReplay.js). */
export async function playReplay(data, hudHost) {
  if (!viewer) return;
  const landingMod = await landing();
  replayModule ??= await import("./missionReplay.js");
  replayData = data;
  replayModule.play(viewer, data, hudHost, {
    setSun: (az, el, pct, eAz, eEl) => setSunLight(data.lat, data.lon, az, el, pct, eAz, eEl),
    surfaceActive: () => !!surface,
    watchFromSurface: () => enterSurfaceView(data.lat, data.lon, true),
    scaleLight: (k) => {
      if (viewer?.scene?.light && baseLightIntensity != null) viewer.scene.light.intensity = baseLightIntensity * k;
    },
    onStep: (step) => dotNetRef?.invokeMethodAsync("OnReplayStep", step).catch(() => {}),
    onEnd: (outcome) => dotNetRef?.invokeMethodAsync("OnReplayEnded", outcome).catch(() => {}),
    landing: landingMod,
  });
}

/** Stop the replay and go back to the landing moment (lander on the ground, light of the first hour). */
export function stopReplay() {
  replayModule?.stop(true);
  if (replayData && viewer && !viewer.isDestroyed()) {
    sunLightKey = null;
    setSunLight(replayData.lat, replayData.lon, replayData.sunAz[0], replayData.sunEl[0], replayData.sunPct[0],
      replayData.earthAz?.[0], replayData.earthEl?.[0]);
  }
  replayData = null;
  setSkyLabels(false);
}

export function cancelLanding() {
  landingModule?.cancel();
}

export function clearLandingMarker() {
  replayModule?.stop(true);
  if (!landingModule) return;
  landingModule.cancel();
  landingModule.clearMarker(viewer);
}

export function dispose() {
  if (frameRateTimer) clearInterval(frameRateTimer);
  frameRateTimer = 0;
  clearLandingMarker();
  document.removeEventListener("keydown", onKeyDown);
  if (clickTimer) clearTimeout(clickTimer);
  if (hoverFrame) cancelAnimationFrame(hoverFrame);
  hoverFrame = 0;
  pendingHoverPos = null;
  hoveredId = null;
  if (viewer && !viewer.isDestroyed()) {
    viewer.destroy();
  }
  viewer = null;
  dotNetRef = null;
  selectedSiteId = null;
}
