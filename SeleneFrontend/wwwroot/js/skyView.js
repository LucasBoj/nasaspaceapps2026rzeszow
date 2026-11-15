// The sky seen from the surface: the Earth and the Sun in the directions of the illumination data (azimuth /
// elevation at the site, the same numbers the analysis uses), plus Cesium's star sky box.
//
// Both bodies are placed DIST from the site along their direction and sized to their real angular diameter
// (Earth ≈ 1.9°, Sun ≈ 0.53°). They are ordinary depth-tested scene objects, so the 3D terrain hides them when
// the data says they are behind a ridge. The Earth is a textured sphere lit by the scene light (= the Sun), so its
// phase follows the Sun. Shown only in Surface view (from orbit a 13 km ball 400 km away would only confuse).
const DIST = 400_000;                 // m from the site: beyond the nearby ridges, inside the 150 km+ horizon reach
const EARTH_ANG_RADIUS_DEG = 0.95;    // Earth seen from the Moon: ~1.9° across
const SUN_ANG_RADIUS_DEG = 0.267;
const SUN_GLOW = 5;                   // glow image size in Sun diameters (1 = no glow, reduced motion)

let earth = null, sun = null;   // earth: { prim (sphere, moved by modelMatrix — no rebuild per frame), label entity }
let shown = false, labels = false, reduced = false;

const enuDir = (lat, lon, azDeg, elDeg) => {
  const site = Cesium.Cartesian3.fromDegrees(lon, lat, 0, Cesium.Ellipsoid.MOON);
  const rot = Cesium.Matrix4.getMatrix3(Cesium.Transforms.eastNorthUpToFixedFrame(site), new Cesium.Matrix3());
  const az = Cesium.Math.toRadians(azDeg), el = Cesium.Math.toRadians(elDeg);
  const enu = new Cesium.Cartesian3(Math.cos(el) * Math.sin(az), Math.cos(el) * Math.cos(az), Math.sin(el));
  return { site, dir: Cesium.Cartesian3.normalize(Cesium.Matrix3.multiplyByVector(rot, enu, new Cesium.Cartesian3()), new Cesium.Cartesian3()) };
};

function sunImage(glow) {
  const n = 128, c = document.createElement("canvas");
  c.width = c.height = n;
  const g = c.getContext("2d");
  const core = n / 2 / glow;
  if (glow > 1) {
    const halo = g.createRadialGradient(n / 2, n / 2, core * 0.8, n / 2, n / 2, n / 2);
    halo.addColorStop(0, "rgba(255,244,214,0.55)");
    halo.addColorStop(0.35, "rgba(255,226,170,0.16)");
    halo.addColorStop(1, "rgba(255,210,140,0)");
    g.fillStyle = halo; g.fillRect(0, 0, n, n);
  }
  g.fillStyle = "#fffaf0";
  g.beginPath(); g.arc(n / 2, n / 2, core, 0, Math.PI * 2); g.fill();
  return c;
}

const labelGraphics = (text) => ({
  text, show: false,
  font: '600 13px "IBM Plex Sans", sans-serif',
  fillColor: Cesium.Color.fromCssColorString("#d7e2ec"),
  style: Cesium.LabelStyle.FILL_AND_OUTLINE, outlineColor: Cesium.Color.BLACK, outlineWidth: 3,
  showBackground: true, backgroundColor: new Cesium.Color(0.04, 0.055, 0.075, 0.75),
  verticalOrigin: Cesium.VerticalOrigin.BOTTOM, pixelOffset: new Cesium.Cartesian2(0, -18),
  // Depth-tested like the bodies: a label disappears when its body is behind the terrain.
});

function earthMaterial() {
  // Lit by the scene light (the Sun) → real phase; a little emission so the night side still reads as a disc.
  return new Cesium.Material({
    fabric: {
      uniforms: { image: "img/earth-blue-marble.jpg" },
      source: `czm_material czm_getMaterial(czm_materialInput materialInput) {
        czm_material m = czm_getDefaultMaterial(materialInput);
        vec3 c = texture(image, materialInput.st).rgb;
        m.diffuse = c;
        m.emission = c * 0.18;
        return m;
      }`,
    },
  });
}

function ensure(viewer) {
  if (earth && viewer.entities.contains(earth.label)) return;
  const r = DIST * Math.tan(Cesium.Math.toRadians(EARTH_ANG_RADIUS_DEG));
  const prim = viewer.scene.primitives.add(new Cesium.Primitive({
    geometryInstances: new Cesium.GeometryInstance({
      geometry: new Cesium.EllipsoidGeometry({ radii: new Cesium.Cartesian3(r, r, r), vertexFormat: Cesium.MaterialAppearance.MaterialSupport.TEXTURED.vertexFormat, slicePartitions: 64, stackPartitions: 32 }),
      id: "sky-earth",
    }),
    appearance: new Cesium.MaterialAppearance({ material: earthMaterial(), materialSupport: Cesium.MaterialAppearance.MaterialSupport.TEXTURED, closed: true }),
    asynchronous: false,
    modelMatrix: Cesium.Matrix4.IDENTITY,
    show: false,
  }));
  const label = viewer.entities.add({ id: "sky-earth-label", show: true, properties: { kind: "decor" }, position: Cesium.Cartesian3.ZERO, label: labelGraphics("Earth") });
  earth = { prim, label, pos: null };
  const sunR = DIST * Math.tan(Cesium.Math.toRadians(SUN_ANG_RADIUS_DEG));
  const glow = reduced ? 1 : SUN_GLOW;
  sun = viewer.entities.add({
    id: "sky-sun", show: false, properties: { kind: "decor" },
    position: Cesium.Cartesian3.ZERO,
    billboard: { image: sunImage(glow), sizeInMeters: true, width: 2 * sunR * glow, height: 2 * sunR * glow },
    label: labelGraphics("Sun"),
  });
}

/** Directions from the data at the site (deg); call for every new moment (selection, timeline hover, replay frame). */
export function update(viewer, lat, lon, sunAz, sunEl, earthAz, earthEl) {
  if (!viewer || viewer.isDestroyed()) return;
  ensure(viewer);
  const s = enuDir(lat, lon, sunAz, sunEl);
  sun.position = Cesium.Cartesian3.add(s.site, Cesium.Cartesian3.multiplyByScalar(s.dir, DIST, new Cesium.Cartesian3()), new Cesium.Cartesian3());
  if (Number.isFinite(earthAz) && Number.isFinite(earthEl)) {
    const e = enuDir(lat, lon, earthAz, earthEl);
    const pos = Cesium.Cartesian3.add(e.site, Cesium.Cartesian3.multiplyByScalar(e.dir, DIST, new Cesium.Cartesian3()), new Cesium.Cartesian3());
    earth.pos = pos;
    earth.label.position = pos;
    // Earth's north along the Moon's spin axis; the side facing the Moon faces the site (a stylised orientation).
    const z = Cesium.Cartesian3.UNIT_Z;
    const toSite = Cesium.Cartesian3.negate(e.dir, new Cesium.Cartesian3());
    const x = Cesium.Cartesian3.normalize(Cesium.Cartesian3.subtract(toSite, Cesium.Cartesian3.multiplyByScalar(z, Cesium.Cartesian3.dot(toSite, z), new Cesium.Cartesian3()), new Cesium.Cartesian3()), new Cesium.Cartesian3());
    const y = Cesium.Cartesian3.cross(z, x, new Cesium.Cartesian3());
    const m = new Cesium.Matrix3(x.x, y.x, z.x, x.y, y.y, z.y, x.z, y.z, z.z);
    earth.prim.modelMatrix = Cesium.Matrix4.fromRotationTranslation(m, pos, new Cesium.Matrix4());
  }
  earth.prim.show = shown && !!earth.pos && Number.isFinite(earthAz);
  sun.show = shown;
}

/** Show the bodies (Surface view) and optionally their name labels (Play mission). */
export function setShown(viewer, on, withLabels = false, reducedMotion = false) {
  if (!viewer || viewer.isDestroyed()) return;
  if (reducedMotion !== reduced && sun) { viewer.entities.remove(sun); sun = null; }
  reduced = reducedMotion;
  if (!sun && earth) {   // the Sun is rebuilt with / without glow; the Earth stays
    const sunR = DIST * Math.tan(Cesium.Math.toRadians(SUN_ANG_RADIUS_DEG)), glow = reduced ? 1 : SUN_GLOW;
    sun = viewer.entities.add({ id: "sky-sun", show: false, properties: { kind: "decor" }, position: Cesium.Cartesian3.ZERO,
      billboard: { image: sunImage(glow), sizeInMeters: true, width: 2 * sunR * glow, height: 2 * sunR * glow }, label: labelGraphics("Sun") });
  }
  ensure(viewer);
  shown = on; labels = on && withLabels;
  earth.prim.show = on && !!earth.pos;
  sun.show = on && !Cesium.Cartesian3.equals(sun.position.getValue(Cesium.JulianDate.now()), Cesium.Cartesian3.ZERO);
  earth.label.label.show = labels && !!earth.pos; sun.label.show = labels;
}

/** Test hook: where the bodies are drawn, as azimuth / elevation seen from the site (deg). */
export function bodyAzEl(lat, lon) {
  if (!earth || !sun || !earth.pos) return null;
  const site = Cesium.Cartesian3.fromDegrees(lon, lat, 0, Cesium.Ellipsoid.MOON);
  const inv = Cesium.Matrix4.inverseTransformation(Cesium.Transforms.eastNorthUpToFixedFrame(site), new Cesium.Matrix4());
  const azel = (e) => {
    const p = e.pos ?? e.position.getValue(Cesium.JulianDate.now());
    const v = Cesium.Matrix4.multiplyByPoint(inv, p, new Cesium.Cartesian3());
    return { az: (Cesium.Math.toDegrees(Math.atan2(v.x, v.y)) + 360) % 360, el: Cesium.Math.toDegrees(Math.atan2(v.z, Math.hypot(v.x, v.y))) };
  };
  return { earth: azel(earth), sun: azel(sun), shown };
}
