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
import { statSync, existsSync } from "node:fs";
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
    textureCompress({ encoder: sharp, targetFormat: "webp", resize: [TEXTURE_PX, TEXTURE_PX] }),
    meshopt({ encoder: MeshoptEncoder, level: "medium" }),
  );
  await io.write(dst, doc);
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
