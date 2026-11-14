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

let viewer = null;
let dotNetRef = null;
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
      terrainLoaded = true;
    } catch (e) {
      console.warn("Lunar 3D terrain unavailable, falling back to Moon Trek mosaic:", e);
    }
  }

  resetView();
  viewer.screenSpaceEventHandler.setInputAction(onLeftClick, Cesium.ScreenSpaceEventType.LEFT_CLICK);
  viewer.screenSpaceEventHandler.setInputAction(onMouseMove, Cesium.ScreenSpaceEventType.MOUSE_MOVE);

  return { terrainLoaded, hasToken };
}

/** South-pole overview. Instant by default; pass animate=true for a camera flight. */
export function resetView(animate = false) {
  if (!viewer) return;
  const view = {
    destination: Cesium.Cartesian3.fromDegrees(0, -72, 420000, Cesium.Ellipsoid.MOON),
    orientation: {
      heading: Cesium.Math.toRadians(180),
      pitch: Cesium.Math.toRadians(-38),
      roll: 0,
    },
  };
  if (animate) {
    viewer.camera.flyTo({ ...view, duration: FLY_DURATION_S });
  } else {
    viewer.camera.setView(view);
  }
}

function isPin(entity) {
  const kind = entityKind(entity);
  return kind === "site" || kind === "custom";
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
  dotNetRef?.invokeMethodAsync("OnCustomSiteCreated", lat, lon);
}

function pinGraphics(color, pixelSize, text) {
  return {
    point: {
      pixelSize,
      color,
      outlineColor: Cesium.Color.BLACK,
      outlineWidth: 1,
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
  if (!siteId) return;

  const entity = viewer.entities.getById(siteId);
  if (entity && entityKind(entity) === "site") styleSitePin(entity, true);
}

export function flyToSite(siteId) {
  if (!viewer) return;
  const entity = viewer.entities.getById(siteId);
  if (!entity) return;
  // Fly to an offset above the site — flying to the surface point puts the camera into the ground.
  viewer.flyTo(entity, {
    duration: FLY_DURATION_S,
    offset: new Cesium.HeadingPitchRange(0, Cesium.Math.toRadians(-50), 90000),
  });
}

export function setSunDirection(azimuthDeg, elevationDeg) {
  if (!viewer) return;
  // TODO: scene.light = new Cesium.DirectionalLight(...)
  console.log(`Sun direction: az=${azimuthDeg}°, el=${elevationDeg}°`);
}

export function dispose() {
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
