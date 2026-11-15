// SELENE OPS — one-off optimisation of the lander GLB models (see README.md in this folder).
//
//   cd tools/optimize-models && npm ci && node optimize.mjs [id ...]
//
// Reads  SeleneFrontend/wwwroot/models/source/{id}.glb   (originals, not published, gitignored)
// Writes SeleneFrontend/wwwroot/models/{id}.glb          (published)
//
// Pipeline (glTF Transform SDK 4.5 — the same functions as `npx @gltf-transform/cli optimize`):
//   dedup → weld → simplify → prune → WebP textures ≤ 1024 px → meshopt (reorder + quantize + EXT_meshopt_compression)
// The one custom step is simplify: meshoptimizer's simplifyWithAttributes, so normals and UVs count in
// the error metric. The TRELLIS meshes have many UV seams (≈ as many vertices as triangles); seam edges
// stay locked, so the simplifier stops at the 2 % error limit before the triangle target on the busiest
// models (Nova-C ~93 k, Griffin ~116 k). Tried and rejected (2026-11-14, close-up renders): the
// "Permissive" flag (collapses across seams) reaches the targets but smears the textures (dark/golden
// patches on Blue Ghost's solar panel, spots on Starship), even with a 64× UV weight.
// Experiments: FLAGS=Permissive UV_WEIGHT=16 MAX_ERROR=0.01 TEXTURE_PX=2048 NO_SIMPLIFY=1 node optimize.mjs …
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import { dedup, weld, prune, textureCompress, meshopt, compactPrimitive, getBounds } from "@gltf-transform/functions";
import { MeshoptEncoder, MeshoptDecoder, MeshoptSimplifier } from "meshoptimizer";
import sharp from "sharp";
import { statSync, existsSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const MODELS = join(ROOT, "SeleneFrontend", "wwwroot", "models");

// Target triangle counts (upper bounds; see above). The HLS landers fill more of the screen, so they keep more.
const TARGETS = {
  nova_c: 40000,
  blue_ghost: 40000,
  griffin: 45000,
  starship_hls: 70000,
  blue_moon_mk2: 70000,
};
const MAX_ERROR = +(process.env.MAX_ERROR ?? 0.02);   // simplifier error limit, fraction of the mesh extent
const UV_WEIGHT = +(process.env.UV_WEIGHT ?? 1);     // UV error weight (higher = fewer texture-smearing collapses)
const FLAGS = (process.env.FLAGS ?? "").split(",").filter(Boolean);   // meshoptimizer SimplifierFlags
const TEXTURE_PX = +(process.env.TEXTURE_PX ?? 1024);
const MAX_BYTES = 3 * 1024 * 1024;
const REDACTIONS = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "redactions.json"), "utf8"));

// Space Apps "Use of Super Intelligence": AI-generated assets carry the disclosure in their metadata too.
const AI_GENERATOR = "SELENE OPS · AI-generated: concept image Meshy AI, 3D Microsoft TRELLIS.2, optimised with gltf-transform";
const AI_EXTRAS = {
  aiGenerated: true,
  pipeline: ["concept image: Meshy AI", "image-to-3D: Microsoft TRELLIS.2 (MIT)", "optimised: glTF Transform + meshoptimizer (tools/optimize-models)"],
  note: "Stylised visualisation, not manufacturer CAD. Logos, flags and insignia painted out (tools/optimize-models/redactions.json).",
};

/** glTF Transform writes its own asset.generator: set ours in the GLB's JSON chunk afterwards (re-padded to 4 bytes). */
function setGlbGenerator(path, generator) {
  const glb = readFileSync(path);
  const jsonLen = glb.readUInt32LE(12);
  const json = JSON.parse(glb.subarray(20, 20 + jsonLen).toString("utf8"));
  json.asset.generator = generator;
  let text = Buffer.from(JSON.stringify(json), "utf8");
  if (text.length % 4) text = Buffer.concat([text, Buffer.alloc(4 - (text.length % 4), 0x20)]);
  const rest = glb.subarray(20 + jsonLen);
  const out = Buffer.alloc(12 + 8 + text.length + rest.length);
  glb.copy(out, 0, 0, 12);
  out.writeUInt32LE(out.length, 8);
  out.writeUInt32LE(text.length, 12);
  out.writeUInt32LE(0x4e4f534a, 16);   // "JSON"
  text.copy(out, 20);
  rest.copy(out, 20 + text.length);
  writeFileSync(path, out);
}

const hue = { red: (r, g, b) => r > 140 && r > g * 1.8 && r > b * 1.8, blue: (r, g, b) => b > 70 && b > r * 1.5 && b > g * 1.2 };

/** Paint the listed marks out of every base-colour texture (decoded with sharp, re-encoded as PNG before WebP). */
function redact(id) {
  const spec = REDACTIONS[id];
  return async (doc) => {
    if (!spec) return;
    const textures = new Set(doc.getRoot().listMaterials().map((m) => m.getBaseColorTexture()).filter(Boolean));
    for (const tex of textures) {
      const { data, info } = await sharp(Buffer.from(tex.getImage())).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
      const { width: w, height: h } = info;
      const mask = new Uint8Array(w * h);
      for (const [u, v, r] of spec.circles ?? []) {
        const cx = u * w, cy = v * h, rr = r * w;
        for (let y = Math.max(0, Math.floor(cy - rr)); y < Math.min(h, cy + rr); y++)
          for (let x = Math.max(0, Math.floor(cx - rr)); x < Math.min(w, cx + rr); x++)
            if ((x - cx) ** 2 + (y - cy) ** 2 <= rr * rr) mask[y * w + x] = 1;
      }
      for (const c of spec.colors ?? []) {
        for (let i = 0; i < w * h; i++) if (hue[c](data[4 * i], data[4 * i + 1], data[4 * i + 2])) mask[i] = 1;
      }
      // Grow the mask by 2 px, then fill inwards from the unmasked surroundings (repeated 7×7 averages).
      for (let pass = 0; pass < 2; pass++) {
        const grown = mask.slice();
        for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) if (mask[y * w + x]) { grown[y * w + x - 1] = grown[y * w + x + 1] = grown[(y - 1) * w + x] = grown[(y + 1) * w + x] = 1; }
        mask.set(grown);
      }
      let left = mask.reduce((a, b) => a + b, 0), painted = left;
      for (let iter = 0; left > 0 && iter < 400; iter++) {
        const fill = [];
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
          if (!mask[y * w + x]) continue;
          let n = 0, sr = 0, sg = 0, sb = 0;
          for (let dy = -3; dy <= 3; dy++) for (let dx = -3; dx <= 3; dx++) {
            const xx = x + dx, yy = y + dy;
            if (xx < 0 || yy < 0 || xx >= w || yy >= h || mask[yy * w + xx]) continue;
            const j = 4 * (yy * w + xx); n++; sr += data[j]; sg += data[j + 1]; sb += data[j + 2];
          }
          if (n >= 3) fill.push([y * w + x, sr / n, sg / n, sb / n]);
        }
        for (const [i, r, g, b] of fill) { data[4 * i] = r; data[4 * i + 1] = g; data[4 * i + 2] = b; mask[i] = 0; }
        left -= fill.length;
        if (!fill.length) break;
      }
      tex.setImage(new Uint8Array(await sharp(data, { raw: { width: w, height: h, channels: 4 } }).png().toBuffer())).setMimeType("image/png");
      console.log(`    redact: ${painted} px painted out (${(100 * painted / (w * h)).toFixed(2)} % of ${w}×${h})`);
    }
  };
}

await Promise.all([MeshoptEncoder.ready, MeshoptDecoder.ready, MeshoptSimplifier.ready]);
const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ "meshopt.encoder": MeshoptEncoder, "meshopt.decoder": MeshoptDecoder });

const triangles = (doc) => doc.getRoot().listMeshes()
  .flatMap((m) => m.listPrimitives())
  .reduce((n, p) => n + (p.getIndices()?.getCount() ?? p.getAttribute("POSITION").getCount()) / 3, 0);

/** Attribute-aware simplification of every triangle primitive towards `targetTris` in total. */
function simplify(targetTris) {
  return (doc) => {
    const prims = doc.getRoot().listMeshes().flatMap((m) => m.listPrimitives()).filter((p) => p.getMode() === 4);
    const total = triangles(doc);
    for (const prim of prims) {
      const pos = prim.getAttribute("POSITION");
      const nrm = prim.getAttribute("NORMAL");
      const uv = prim.getAttribute("TEXCOORD_0");
      const idx = prim.getIndices();
      if (!pos || !idx) continue;
      const n = pos.getCount();
      const positions = new Float32Array(pos.getArray());
      const attrs = new Float32Array(n * 5);
      const tmp = [0, 0, 0];
      for (let i = 0; i < n; i++) {
        if (nrm) { nrm.getElement(i, tmp); attrs.set(tmp, i * 5); }
        if (uv) { uv.getElement(i, tmp); attrs[i * 5 + 3] = tmp[0]; attrs[i * 5 + 4] = tmp[1]; }
      }
      const indices = new Uint32Array(idx.getArray());
      const target = Math.max(3, Math.round((indices.length * targetTris) / total / 3) * 3);
      const [out, err] = MeshoptSimplifier.simplifyWithAttributes(
        indices, positions, 3, attrs, 5, [0.5, 0.5, 0.5, UV_WEIGHT, UV_WEIGHT], null, target, MAX_ERROR, FLAGS);
      idx.setArray(out);
      compactPrimitive(prim);
      console.log(`    simplify: ${indices.length / 3} → ${out.length / 3} triangles (error ${(err * 100).toFixed(2)} % of extent)`);
    }
  };
}

const ids = process.argv.slice(2).length ? process.argv.slice(2) : Object.keys(TARGETS);
const rows = [];
for (const id of ids) {
  const src = join(MODELS, "source", `${id}.glb`);
  const dst = join(MODELS, `${id}.glb`);
  if (!TARGETS[id]) { console.error(`unknown model id: ${id}`); process.exitCode = 1; continue; }
  if (!existsSync(src)) { console.error(`missing original: ${src}`); process.exitCode = 1; continue; }
  console.log(`${id}`);
  const doc = await io.read(src);
  const before = { bytes: statSync(src).size, tris: triangles(doc), bounds: getBounds(doc.getRoot().listScenes()[0]) };
  await doc.transform(
    dedup(),
    weld(),
    ...(process.env.NO_SIMPLIFY ? [] : [simplify(TARGETS[id])]),
    prune(),
    redact(id),
    textureCompress({ encoder: sharp, targetFormat: "webp", resize: [TEXTURE_PX, TEXTURE_PX] }),
    meshopt({ encoder: MeshoptEncoder, level: "medium" }),
  );
  doc.getRoot().getAsset().generator = AI_GENERATOR;
  doc.getRoot().getAsset().extras = { ...(doc.getRoot().getAsset().extras ?? {}), ...AI_EXTRAS };
  await io.write(dst, doc);
  setGlbGenerator(dst, AI_GENERATOR);
  const after = { bytes: statSync(dst).size, tris: triangles(doc), bounds: getBounds(doc.getRoot().listScenes()[0]) };
  const ext = (b) => b.max.map((v, i) => v - b.min[i]);
  const drift = Math.max(...ext(before.bounds).map((v, i) => Math.abs(ext(after.bounds)[i] / v - 1)));
  rows.push({ id, before, after, drift });
  if (after.bytes > MAX_BYTES) { console.error(`  ${id}: ${(after.bytes / 1e6).toFixed(2)} MB is over the 3 MB budget`); process.exitCode = 1; }
  if (drift > 0.02) { console.error(`  ${id}: bounding box changed by ${(drift * 100).toFixed(1)} %`); process.exitCode = 1; }
}

const mb = (b) => (b / 1048576).toFixed(2);
const k = (t) => `${Math.round(t / 1000)} k`;
console.log("\n| model | before MB | after MB | before triangles | after triangles | bbox change |");
console.log("|---|---:|---:|---:|---:|---:|");
for (const r of rows) {
  console.log(`| ${r.id} | ${mb(r.before.bytes)} | ${mb(r.after.bytes)} | ${k(r.before.tris)} | ${k(r.after.tris)} | ${(r.drift * 100).toFixed(1)} % |`);
}
