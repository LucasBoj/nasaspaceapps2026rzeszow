# startup-profile

Measures how smooth SELENE OPS is while it starts and lands, in a headless Chrome/Edge driven over the
DevTools protocol. No dependencies (Node ≥ 22).

```bash
# 1. a Release build, served like nginx on Mikrus (pre-compressed .gz)
./deploy/deploy.sh stable --build-only
node tools/startup-profile/serve.mjs out/publish/wwwroot 5290

# 2. startup profile (real GPU), then the same with a QUICK DEMO click at 9 s
node tools/startup-profile/profile.mjs http://127.0.0.1:5290/ --gpu
node tools/startup-profile/profile.mjs http://127.0.0.1:5290/ --gpu --no-profile --click "Quick demo" --click-at 9000 --seconds 24

# 3. a 4x slower CPU (weak laptop), CPU profile saved for DevTools → Performance → Load profile
node tools/startup-profile/profile.mjs http://127.0.0.1:5290/ --gpu --cpu 4 --save startup.cpuprofile
```

What it prints:

- **Milestones**: Cesium.js and Blazor executed, globe canvas, first verdict, heatmap, 3D model ready, the click.
- **Main thread blocked > 50 ms**: every timer gap, with the scripts that ran in it (from the CPU profile):
  `Cesium.js`, `.NET (wasm)`, `browser (layout, paint, parse)`… With `--click`, split before / after the click.
- **Cesium renders**: globe frames per second (idle should be 24, see `moonViewer.js → IDLE_FPS`).
- **CPU self time** per script.

Other options: `--seconds`, `--cpu <rate>`, `--headed`, `--dump` (page text and console errors),
`--init "<js>"` (runs before the page, e.g. `localStorage.setItem('selene.analystMode.v1','1')`),
`--sample "<js expr>"` (evaluated every 500 ms, printed when it changes, e.g. `window.__seleneFrameRate()`),
`--browser <path>`.

Caveats:

- Do not measure in a hidden or covered browser window: Chrome throttles its timers to ~1 Hz and every
  measurement looks like 1-second freezes. Headless is not throttled.
- Close games, builds and other heavy programs first: the numbers are wall-clock times on a shared CPU and GPU.
  Compare builds with several runs each; single runs vary by tens of percent.
- `--no-profile` for timings: the sampling profiler itself slows WebAssembly down. Use the profile only to see
  *what* runs, not how long the page takes.
- Without `--gpu` Chrome renders WebGL in software (SwiftShader) and Cesium looks far more expensive than it is;
  the report names the WebGL renderer.
