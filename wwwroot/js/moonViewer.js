let viewer = null;
let dotNetRef = null;
let sitePins = [];
let selectedPinId = null;

export async function initViewer(elementId, dotNetReference) {
  dotNetRef = dotNetReference;
  
  // Set Moon as the ellipsoid BEFORE creating viewer
  Cesium.Ellipsoid.default = Cesium.Ellipsoid.MOON;
  
  const container = document.getElementById(elementId);
  
  try {
    viewer = new Cesium.Viewer(elementId, {
      terrainProvider: Cesium.Terrain.fromWorldTerrain(),
      baseLayerPicker: false,
      geocoder: false,
      homeButton: false,
      sceneModePicker: false,
      navigationHelpButton: false,
      animation: false,
      timeline: false,
      fullscreenButton: false,
      infoBox: false,
      skyAtmosphere: false,
      skyBox: new Cesium.SkyBox({
        sources: {
          positiveX: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/px.jpg',
          negativeX: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/nx.jpg',
          positiveY: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/py.jpg',
          negativeY: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/ny.jpg',
          positiveZ: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/pz.jpg',
          negativeZ: 'https://cesium.com/downloads/cesiumjs/releases/1.116/Assets/Skybox/nz.jpg'
        }
      }),
      scene3DOnly: true
    });
    
    // Remove atmosphere
    viewer.scene.atmosphere = null;
    viewer.scene.globe.showGroundAtmosphere = false;
    
    // Black background
    viewer.scene.backgroundColor = Cesium.Color.BLACK;
    
    // Load Cesium Moon terrain - would need valid token in appsettings.json
    try {
      const moonTerrain = await Cesium.Cesium3DTileset.fromIonAssetId(2684829);
      viewer.scene.primitives.add(moonTerrain);
    } catch (e) {
      console.warn("Cesium ion token not configured. Showing ellipsoid only.");
    }
    
    // Start camera over south pole, angled view
    const southPole = Cesium.Cartesian3.fromDegrees(0, -90, 400000);
    viewer.camera.setView({
      destination: southPole,
      orientation: {
        heading: Cesium.Math.toRadians(0),
        pitch: Cesium.Math.toRadians(-45),
        roll: 0
      }
    });
    
    // Load sites
    const response = await fetch('data/sites.json');
    const sites = await response.json();
    await loadSites(sites);
    
    // Click handler for terrain
    viewer.screenSpaceEventHandler.setInputAction((click) => {
      const pickedObject = viewer.scene.pick(click.position);
      if (!Cesium.defined(pickedObject)) {
        const earthPosition = viewer.scene.pickPosition(click.position);
        if (Cesium.defined(earthPosition)) {
          const cartographic = Cesium.Cartographic.fromCartesian(earthPosition);
          const lat = Cesium.Math.toDegrees(cartographic.latitude);
          const lon = Cesium.Math.toDegrees(cartographic.longitude);
          dotNetRef.invokeMethodAsync('OnCustomSiteCreated', lat, lon);
        }
      }
    }, Cesium.ScreenSpaceEventType.LEFT_CLICK);
    
  } catch (error) {
    console.error("Error initializing viewer:", error);
    throw error;
  }
}

export async function loadSites(sites) {
  if (!viewer) return;
  
  // Clear existing pins
  sitePins.forEach(pin => {
    viewer.entities.remove(pin.entity);
  });
  sitePins = [];
  
  for (const site of sites) {
    const pin = new Cesium.Entity({
      position: Cesium.Cartesian3.fromDegrees(site.lon, site.lat),
      point: {
        pixelSize: 8,
        color: Cesium.Color.CYAN,
        outlineColor: Cesium.Color.WHITE,
        outlineWidth: 1
      },
      label: {
        text: site.name,
        font: '11px "IBM Plex Mono", monospace',
        horizontalOrigin: Cesium.HorizontalOrigin.LEFT,
        verticalOrigin: Cesium.VerticalOrigin.BOTTOM,
        pixelOffset: new Cesium.Cartesian2(10, -10),
        fillColor: Cesium.Color.CYAN,
        outlineColor: Cesium.Color.BLACK,
        outlineWidth: 1
      },
      siteId: site.id
    });
    
    viewer.entities.add(pin);
    sitePins.push({ id: site.id, entity: pin });
  }
  
  // Re-add click handler for pins
  viewer.screenSpaceEventHandler.setInputAction((click) => {
    const pickedObject = viewer.scene.pick(click.position);
    if (Cesium.defined(pickedObject) && pickedObject.id && pickedObject.id.siteId) {
      selectSite(pickedObject.id.siteId);
      dotNetRef.invokeMethodAsync('OnSiteSelected', pickedObject.id.siteId);
    }
  }, Cesium.ScreenSpaceEventType.LEFT_CLICK);
}

export function flyToSite(siteId) {
  if (!viewer) return;
  
  const pin = sitePins.find(p => p.id === siteId);
  if (pin && pin.entity.position) {
    viewer.camera.flyTo({
      destination: pin.entity.position,
      orientation: {
        heading: Cesium.Math.toRadians(0),
        pitch: Cesium.Math.toRadians(-45),
        roll: 0
      },
      duration: 1.5
    });
  }
}

export function selectSite(siteId) {
  if (!viewer) return;
  
  // Reset previous selection
  if (selectedPinId) {
    const oldPin = sitePins.find(p => p.id === selectedPinId);
    if (oldPin) {
      oldPin.entity.point.color = Cesium.Color.CYAN;
      oldPin.entity.point.pixelSize = 8;
    }
  }
  
  // Highlight new selection
  const newPin = sitePins.find(p => p.id === siteId);
  if (newPin) {
    newPin.entity.point.color = Cesium.Color.YELLOW;
    newPin.entity.point.pixelSize = 12;
    selectedPinId = siteId;
  }
}

export function setSunDirection(azimuthDeg, elevationDeg) {
  if (!viewer) return;
  // TODO: scene.light = new Cesium.DirectionalLight(...)
  console.log(`Sun direction: az=${azimuthDeg}°, el=${elevationDeg}°`);
}

export function dispose() {
  if (viewer) {
    viewer.destroy();
    viewer = null;
  }
}
