// Visible AI disclosure on images (Space Apps "Use of Super Intelligence"): stamps "Contains AI-generated 3D models"
// on screenshots that show a lander model, and writes a transparent overlay PNG for the demo video.
//
//   node watermark.mjs stamp <image.png> [...]     # in place, top-right corner, scaled to the image width
//   node watermark.mjs overlay <out.png> [width]   # transparent PNG (default 1920 px wide) to lay over the video
import sharp from "sharp";
import { readFileSync, writeFileSync } from "node:fs";

const TEXT = "Contains AI-generated 3D models";
const SUB = "Lander models: Meshy AI + Microsoft TRELLIS.2";

function label(w) {
  const fs = Math.max(14, Math.round(w / 85)), sub = Math.round(fs * 0.72), pad = Math.round(fs * 0.6);
  const bw = Math.round(fs * 18.5), bh = Math.round(fs + sub + pad * 2.4);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${bw}" height="${bh}">
    <rect x="0.5" y="0.5" width="${bw - 1}" height="${bh - 1}" rx="2" fill="rgba(10,14,19,0.72)" stroke="rgba(215,226,236,0.55)"/>
    <text x="${pad}" y="${pad + fs * 0.85}" font-family="IBM Plex Sans, Helvetica, Arial, sans-serif" font-weight="600" font-size="${fs}" fill="#ffffff">${TEXT}</text>
    <text x="${pad}" y="${pad + fs * 0.85 + sub * 1.25}" font-family="IBM Plex Sans, Helvetica, Arial, sans-serif" font-size="${sub}" fill="#d7e2ec">${SUB}</text>
  </svg>`;
  return { buf: Buffer.from(svg), bw, bh };
}

const [mode, ...args] = process.argv.slice(2);
if (mode === "stamp") {
  for (const file of args) {
    const img = sharp(readFileSync(file));
    const { width, height } = await img.metadata();
    const { buf, bw } = label(width);
    const margin = Math.round(width / 120);
    const out = await img.composite([{ input: buf, top: margin + Math.round(height * 0.075), left: width - bw - margin }]).png().toBuffer();
    writeFileSync(file, out);
    console.log("stamped", file);
  }
} else if (mode === "overlay") {
  const [out, w = "1920"] = args;
  const width = +w, height = Math.round(width * 9 / 16);
  const { buf, bw, bh } = label(width);
  const margin = Math.round(width / 60);
  await sharp({ create: { width, height, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
    .composite([{ input: buf, top: height - bh - margin, left: width - bw - margin }]).png().toFile(out);
  console.log("overlay", out, `${width}×${height}`);
} else {
  console.error("usage: node watermark.mjs stamp <png...> | overlay <out.png> [width]");
  process.exit(1);
}
