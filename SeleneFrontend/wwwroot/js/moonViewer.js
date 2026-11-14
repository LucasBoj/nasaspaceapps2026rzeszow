// Cesium ion asset ID of the lunar 3D terrain tileset — sprawdź w Cesium ion (Asset Depot → Moon terrain).
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
let hoveredId = null;
let pendingHoverPos = null;
let hoverFrame = 0;

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
  viewer.screenSpaceEventHandler.setInputAction(onLeftClick, Cesium.ScreenSpaceEventType.LEFT_CLICK);
  viewer.screenSpaceEventHandler.setInputAction(onMouseMove, Cesium.ScreenSpaceEventType.MOUSE_MOVE);

  return { terrainLoaded, hasToken };
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
  viewer.camera.flyTo({
    ...view,
    duration: 3.2,
    easingFunction: Cesium.EasingFunction.QUADRATIC_IN_OUT,
  });
}

function southPoleView() {
  return {
    destination: Cesium.Cartesian3.fromDegrees(0, -72, 420000, Cesium.Ellipsoid.MOON),
    orientation: { heading: Cesium.Math.toRadians(180), pitch: Cesium.Math.toRadians(-38), roll: 0 },
  };
}

/** South-pole overview. Instant by default; pass animate=true for a camera flight. */
export function resetView(animate = false) {
  if (!viewer) return;
  releaseCamera();
  const view = southPoleView();
  if (animate && !reducedMotion) {
    viewer.camera.flyTo({ ...view, duration: FLY_DURATION_S });
  } else {
    viewer.camera.setView(view);
  }
}

function isPin(entity) {
  const kind = entityKind(entity);
  return kind === "site" || kind === "custom" || kind === "candidate";
}

// Hover: pick at most once per animation frame.
function onMouseMove(movement) {
  pendingHoverPos = movement.endPosition;
  if (!hoverFrame) hoverFrame = requestAnimationFrame(updateHover);
}

function updateHover() {
  hoverFrame = 0;
  if (!viewer || viewer.isDestroyed() || !pendingHoverPos) return;

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
      text: text.toUpperCase(),
      font: '11px "IBM Plex Mono", monospace',
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
    ...pinGraphics(CUSTOM_COLOR, PIN_SIZE.custom, `CUSTOM ${lat.toFixed(2)}°, ${lon.toFixed(2)}°`),
  });
}

export function loadSites(sites) {
  if (!viewer) return;

  const stale = viewer.entities.values.filter((e) => entityKind(e) === "site");
  for (const e of stale) viewer.entities.remove(e);

  for (const s of sites ?? []) {
    viewer.entities.add({
      id: s.id,
      position: Cesium.Cartesian3.fromDegrees(s.lon, s.lat, 0, Cesium.Ellipsoid.MOON),
      properties: { kind: "site", approximate: !!s.approximate },
      ...pinGraphics(SITE_COLOR, PIN_SIZE.site, s.name ?? s.id),
    });
  }

  if (selectedSiteId) {
    const id = selectedSiteId;
    selectedSiteId = null;
    selectSite(id);
  }
}

function styleSitePin(entity, selected) {
  const color = selected ? SELECTED_COLOR : SITE_COLOR;
  entity.point.color = color;
  entity.point.pixelSize = (selected ? PIN_SIZE.selected : PIN_SIZE.site) + (entity.id === hoveredId ? HOVER_GROW_PX : 0);
  entity.label.fillColor = color;
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

export function flyToSite(siteId) {
  if (!viewer) return;
  releaseCamera();
  const entity = viewer.entities.getById(siteId);
  if (!entity) return;
  // Fly to an offset above the site — flying to the surface point puts the camera into the ground.
  viewer.flyTo(entity, {
    duration: FLY_DURATION_S,
    offset: new Cesium.HeadingPitchRange(0, Cesium.Math.toRadians(-50), 90000),
  });
}

// ---- Landing animation (isolated module) ----

/** Play the landing animation; resolves with { status, fps, simplified }. */
export async function playLanding(options, hudHost) {
  if (!viewer) return { status: "unavailable" };
  const m = await landing();
  return m.play(viewer, { ...options, hudHost });
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
export function setSunLight(lat, lon, azDeg, elDeg, sunPct) {
  if (!viewer || viewer.isDestroyed()) return;
  const key = `${lat.toFixed(4)},${lon.toFixed(4)},${azDeg.toFixed(1)},${elDeg.toFixed(1)},${sunPct > 0}`;
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
      intensity: 2.2,
    });
  } else {
    // Sun hidden: weak fill light, mostly from overhead with a hint of the Sun's bearing.
    const fill = Cesium.Cartesian3.normalize(Cesium.Cartesian3.add(
      Cesium.Cartesian3.multiplyByScalar(up, 0.8, new Cesium.Cartesian3()),
      Cesium.Cartesian3.multiplyByScalar(toSun, 0.2, new Cesium.Cartesian3()), new Cesium.Cartesian3()), new Cesium.Cartesian3());
    viewer.scene.light = new Cesium.DirectionalLight({
      direction: Cesium.Cartesian3.negate(fill, new Cesium.Cartesian3()),
      intensity: 0.6,
      color: Cesium.Color.fromCssColorString("#a9c6e0"),
    });
  }
  if (terrainTileset && !terrainTileset.isDestroyed()) {
    terrainTileset.imageBasedLighting.sphericalHarmonicCoefficients = ambientCoefficients(sunPct > 0 ? 0.55 : 0.35);
  }
  viewer.shadows = true;
  viewer.shadowMap.softShadows = true;
  viewer.shadowMap.darkness = sunPct > 0 ? 0.35 : 0.6;
}

export function cancelLanding() {
  landingModule?.cancel();
}

export function clearLandingMarker() {
  if (!landingModule) return;
  landingModule.cancel();
  landingModule.clearMarker(viewer);
}

export function dispose() {
  clearLandingMarker();
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
