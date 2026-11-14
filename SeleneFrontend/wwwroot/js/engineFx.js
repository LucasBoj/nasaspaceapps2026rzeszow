// Engine plume and surface dust for the landing animation (and the HLS ascent in the mission replay).
// Everything is a Cesium primitive owned by one EngineFx instance; destroy() removes all of it.
//
// Plume  — ParticleSystem at the nozzle, cone pointing down along the local vertical, plus a bright core
//          billboard. Throttle 0…1 sets emission, core brightness and plume length.
// Dust   — ParticleSystem on the ground under the lander: particles start at the surface and move
//          radially outwards with an upward component (≥ tan 15°) so they stay above sloped terrain
//          (the 9 sites are < 4° steep; candidate sites above 10° are rejected), instead of the old flat
//          ring at a fixed height that disappeared under the slope.
// Sizes and speeds scale with the lander (`sizeM` = its largest dimension): Starship ≫ Blue Ghost.

let glowImage = null;
let dustImage = null;

function radialImage(stops) {
  const c = document.createElement("canvas");
  c.width = c.height = 64;
  const g = c.getContext("2d");
  const grad = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  for (const [t, color] of stops) grad.addColorStop(t, color);
  g.fillStyle = grad;
  g.fillRect(0, 0, 64, 64);
  return c;
}

const glow = () => (glowImage ??= radialImage([[0, "rgba(255,255,255,1)"], [0.35, "rgba(255,255,255,0.75)"], [1, "rgba(255,255,255,0)"]]));
const dust = () => (dustImage ??= radialImage([[0, "rgba(255,255,255,0.9)"], [0.6, "rgba(255,255,255,0.35)"], [1, "rgba(255,255,255,0)"]]));

const DUST_START = new Cesium.Color(0.82, 0.80, 0.76, 0.45);
const DUST_END = new Cesium.Color(0.62, 0.60, 0.57, 0.0);
const DOWN = Cesium.Matrix4.fromRotationTranslation(Cesium.Matrix3.fromRotationX(Math.PI), Cesium.Cartesian3.ZERO);

/**
 * opts: { sizeM, up: () => Cartesian3 (unit local vertical) }
 * update(frame): { nozzle: Cartesian3 | null, ground: Cartesian3 | null, throttle 0..1, dust 0..1, burst: bool }
 */
export class EngineFx {
  constructor(viewer, opts) {
    this.viewer = viewer;
    this.size = Math.max(1.5, opts.sizeM ?? 4);
    this.up = opts.up;
    const s = this.size;
    this.scratch = new Cesium.Cartesian3();
    this.groundCenter = new Cesium.Cartesian3();
    this.prims = [];

    this.plume = new Cesium.ParticleSystem({
      image: glow(),
      startColor: new Cesium.Color(1.0, 0.9, 0.7, 1.0),
      endColor: new Cesium.Color(1.0, 0.45, 0.12, 0.0),
      startScale: 0.9, endScale: 3.2,
      minimumParticleLife: 0.25, maximumParticleLife: 0.45,
      minimumSpeed: 5 * s, maximumSpeed: 7 * s,                // plume length ≈ 2–3 × lander size at full throttle
      imageSize: new Cesium.Cartesian2(0.4 * s, 0.4 * s),
      sizeInMeters: true,
      emissionRate: 0,
      emitter: new Cesium.ConeEmitter(Cesium.Math.toRadians(10)),
      emitterModelMatrix: DOWN,
      lifetime: Number.MAX_VALUE,
    });
    this.dust = new Cesium.ParticleSystem({
      image: dust(),
      startColor: DUST_START,
      endColor: DUST_END,
      startScale: 1.0, endScale: 4.0,
      minimumParticleLife: 0.8, maximumParticleLife: 1.6,
      minimumSpeed: 0.1, maximumSpeed: 0.2,                   // real velocity is set in updateCallback
      imageSize: new Cesium.Cartesian2(0.35 * s, 0.35 * s),
      sizeInMeters: true,
      emissionRate: 0,
      emitter: new Cesium.CircleEmitter(0.35 * s),
      lifetime: Number.MAX_VALUE,
      updateCallback: (p) => this.radial(p),
    });
    this.billboards = new Cesium.BillboardCollection({ scene: viewer.scene });
    this.core = this.billboards.add({
      image: glow(), sizeInMeters: true, width: 0.5 * s, height: 0.5 * s,
      color: new Cesium.Color(1, 0.93, 0.8, 0), show: false,
    });
    for (const p of [this.plume, this.dust, this.billboards]) {
      viewer.scene.primitives.add(p);
      this.prims.push(p);
    }
    // ParticleSystem advances with viewer.clock; the app's clock normally stands still (nothing else
    // depends on it: the Sun light comes from the precomputed data). Run it while effects exist.
    clockUsers++;
    viewer.clock.shouldAnimate = true;
  }

  /** Dust: radial outward along the local horizontal plus an upward component (stays above slopes). */
  radial(particle) {
    const up = this.up();
    const off = Cesium.Cartesian3.subtract(particle.position, this.groundCenter, this.scratch);
    const vertical = Cesium.Cartesian3.dot(off, up);
    const horiz = Cesium.Cartesian3.subtract(off, Cesium.Cartesian3.multiplyByScalar(up, vertical, new Cesium.Cartesian3()), off);
    const len = Cesium.Cartesian3.magnitude(horiz);
    if (len < 1e-6) return;
    if (!particle._seleneSpeed) particle._seleneSpeed = (2.2 + Math.random() * 1.8) * this.size;
    const hs = particle._seleneSpeed;
    const v = Cesium.Cartesian3.multiplyByScalar(horiz, hs / len, new Cesium.Cartesian3());
    Cesium.Cartesian3.add(v, Cesium.Cartesian3.multiplyByScalar(up, hs * 0.3, new Cesium.Cartesian3()), v);
    particle.velocity = v;
  }

  update(f) {
    if (this.destroyed) return;
    const s = this.size;
    const t = Math.max(0, Math.min(1, f.throttle ?? 0));
    if (f.nozzle) {
      const m = Cesium.Transforms.eastNorthUpToFixedFrame(f.nozzle);
      this.plume.modelMatrix = m;
      this.core.position = f.nozzle;
    }
    this.plume.emissionRate = t > 0.02 ? 60 + 140 * t : 0;
    this.plume.minimumSpeed = (2 + 3 * t) * s;
    this.plume.maximumSpeed = (3 + 4 * t) * s;
    this.core.show = t > 0.02 && !!f.nozzle;
    this.core.color = new Cesium.Color(1, 0.93, 0.8, 0.35 + 0.6 * t);
    this.core.width = this.core.height = (0.5 + 0.5 * t) * s;

    // Warm-up: the first frames emit a few invisible dust particles, so the dust billboards' shaders are
    // compiled while the lander is still high (otherwise the first dust frame stalls and renders dark).
    if (this.warm === undefined) this.warm = 12;
    if (this.warm > 0) {
      this.warm--;
      this.dust.startColor = this.dust.endColor = this.warm > 0 ? new Cesium.Color(1, 1, 1, 0) : DUST_END;
      if (this.warm === 0) this.dust.startColor = DUST_START;
    }
    const d = Math.max(0, Math.min(1, f.dust ?? 0));
    if (f.ground) {
      Cesium.Cartesian3.clone(f.ground, this.groundCenter);
      this.dust.modelMatrix = Cesium.Transforms.eastNorthUpToFixedFrame(f.ground);
    }
    this.dust.emissionRate = f.burst ? 220 : d > 0.02 ? 25 + 120 * d : this.warm > 0 ? 20 : 0;
    if (window.__seleneFxOnly === "plume") this.dust.emissionRate = 0;                       // test hooks
    if (window.__seleneFxOnly === "dust") { this.plume.emissionRate = 0; this.core.show = false; }
  }

  destroy() {
    if (this.destroyed) return;
    this.destroyed = true;
    const v = this.viewer;
    for (const p of this.prims) {
      if (v && !v.isDestroyed() && v.scene.primitives.contains(p)) v.scene.primitives.remove(p);   // remove() destroys it
    }
    this.prims = [];
    clockUsers = Math.max(0, clockUsers - 1);
    if (clockUsers === 0 && v && !v.isDestroyed()) v.clock.shouldAnimate = false;
  }
}

let clockUsers = 0;

/** Number of live effect primitives this module added to a scene (tests: nothing may be left after cancel). */
export function countFx(viewer) {
  let n = 0;
  const prims = viewer.scene.primitives;
  for (let i = 0; i < prims.length; i++) {
    const p = prims.get(i);
    if (p instanceof Cesium.ParticleSystem || p instanceof Cesium.BillboardCollection) n++;
  }
  return n;
}
