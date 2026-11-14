#!/usr/bin/env node
// Static server for a published build, like nginx on Mikrus: serves the pre-compressed .gz next to a file
// (gzip_static) and the right MIME types. For local profiling only.
//
//   node tools/startup-profile/serve.mjs out/publish/wwwroot [port=5290]

import { createServer } from 'node:http';
import { createReadStream, statSync, existsSync } from 'node:fs';
import { join, extname, normalize, resolve } from 'node:path';

const root = resolve(process.argv[2] ?? 'out/publish/wwwroot');
const port = Number(process.argv[3] ?? 5290);
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
  '.json': 'application/json', '.wasm': 'application/wasm', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.jpg': 'image/jpeg', '.webp': 'image/webp', '.woff2': 'font/woff2', '.glb': 'model/gltf-binary',
  '.dat': 'application/octet-stream', '.txt': 'text/plain', '.ico': 'image/x-icon', '.xml': 'application/xml',
};

createServer((req, res) => {
  let path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (path.endsWith('/')) path += 'index.html';
  const file = normalize(join(root, path));
  if (!file.startsWith(root)) { res.writeHead(403).end(); return; }
  const target = existsSync(file) && statSync(file).isFile() ? file : join(root, 'index.html');   // SPA fallback
  const headers = { 'Content-Type': types[extname(target)] ?? 'application/octet-stream', 'Cache-Control': 'no-cache' };
  const gz = `${target}.gz`;
  const useGz = /\bgzip\b/.test(req.headers['accept-encoding'] ?? '') && existsSync(gz);
  if (useGz) { headers['Content-Encoding'] = 'gzip'; headers['Vary'] = 'Accept-Encoding'; }
  res.writeHead(200, headers);
  createReadStream(useGz ? gz : target).pipe(res);
}).listen(port, '127.0.0.1', () => console.log(`serving ${root} on http://127.0.0.1:${port}/`));
