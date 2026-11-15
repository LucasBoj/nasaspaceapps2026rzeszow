# e2e

Browser tests for the juror path, run against a Release build in Chromium, Firefox and WebKit (Playwright).

```bash
# 1. a Release build, served like nginx on Mikrus
./deploy/deploy.sh stable --build-only
node tools/startup-profile/serve.mjs out/publish/wwwroot 5298

# 2. once: install Playwright and its browsers
cd tools/e2e && npm install && npx playwright install chromium firefox webkit

# 3. run (BR = chromium | firefox | webkit; URL defaults to http://localhost:5298/)
BR=chromium node stage2.mjs                     # summary card, HUD tooltips, Result step, juror path at 1366×768 and 1920×1080
SHOTS=../../docs/screenshots BR=chromium node stage2.mjs   # also saves the "after" screenshots
BR=chromium node replay.mjs                     # Play mission: endings, HUD timeline, compass, scrolling to the Moon
```

Each script prints `PASS` / `FAIL` lines and exits non-zero on a failure or a console error. Without
`appsettings.Development.json` (no Cesium token) the build runs in 2D mosaic mode, and the 3D model checks
in `replay.mjs` (lift-off height) need the 3D terrain.

| Script | What it checks |
|---|---|
| `stage2.mjs` | Descent panel clear of the HUD buttons; descent / button / chip tooltips cover no control (bounding boxes); verdict card fits 1366×768 with its buttons clickable; differentiating verdict numbers; Result step after touchdown; the mission summary folds on ⌂ View, Esc, camera drag and wheel and opens again from “Mission summary”; compass hidden under the card; Try another date → heatmap; best-date chips with the year; New landing; custom point → Land & analyze; QUICK DEMO replay HUD and summary cover no control; no console errors. |
| `replay.mjs` | Mission replay: light moves, HUD mini timeline and cursor, compass follows replay time, the Moon is scrolled into view, timeline cursor sync, CLPS / HLS / failure endings, skip, cleanup. |
