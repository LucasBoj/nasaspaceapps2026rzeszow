# SELENE OPS — project context

**What:** a landing-window planner for the lunar south pole. Entry for **NASA Space Apps Challenge 2026**, challenge *"Lunar Landing Site Navigator / CLPS Lunar Mission Browser"*, local event **Rzeszów**.
**Deadline:** submission by **Sunday 15.11.2026, 23:59 local time**. The local jury may close earlier, so check with the Local Lead.
**Goal:** 1st place locally and a Global Award nomination. Judging criteria (1–5 each): Impact, Creativity, Validity, Relevance (NASA data as the core), Presentation.

**Product:** for a chosen site, lander and date window, compute illumination (energy), Earth comms and hour-by-hour battery balance. The output is a verdict, **"feasible / not feasible"**, plus the best window. From data to a decision.

## Stack

| Part | What | Where |
|---|---|---|
| Frontend | Blazor WebAssembly standalone, **.NET 10**. CesiumJS **1.120** via JS interop | `SeleneFrontend/` (`wwwroot/js/moonViewer.js`, `cardGrid.js`) |
| Data | Python 3.11+ (Skyfield, rasterio, pyproj, numpy). A **one-off** precompute into JSON | `pipeline/` → `SeleneFrontend/wwwroot/data/illumination/` |
| Runtime | **No backend.** Static files only | — |
| Hosting (planned) | Static files on Mikrus (nginx), mirrored on GitHub Pages | not set up yet |

## Data sources

- **Terrain:** NASA LRO/LOLA, `ldem_75s_120m_float` (PDS Geosciences Node, polar stereographic, 120 m/px).
- **Landing sites:** Wueller et al. (2026), JGR Planets, doi:10.1029/2025JE009434, Table A1 via Zenodo doi:10.5281/zenodo.17084058 (CC-BY-4.0, attribution required).
- **Ephemerides:** JPL DE421 plus lunar orientation kernels (NAIF), computed via Skyfield.
- **Imagery:** NASA/JPL Moon Trek, LRO WAC mosaic (tiles `trek.nasa.gov`, no token needed).
- **3D terrain (optional):** Cesium Moon Terrain, ion asset `2684829` (already added to the team's ion account; works locally with the dev token).
- **Cesium ion token:** only in `SeleneFrontend/wwwroot/appsettings.Development.json`, which is **gitignored**. **The repo is public — never commit the token.** `appsettings.json` keeps an empty value. Without a token the app falls back to 2D mosaic mode.

## Data contract — `illumination/{siteId}.json` (single source of truth)

Change it only deliberately: describe the change here, then update both the pipeline and the frontend.

```jsonc
{
  "siteId": "malapert_massif", "lat": -85.8, "lon": -3.5, "observerHeightM": 2,
  "start": "2027-01-01T00:00:00Z", "stepHours": 1, "count": 17544,
  "sunFraction": [0..100],        // int, % of the solar disk above the local horizon
  "sunElevCentiDeg": [int],       // Sun elevation × 100
  "sunAzCentiDeg": [int],         // Sun azimuth × 100, clockwise from lunar north
  "earthVisible": [0|1],          // Earth centre > horizon + 0.5°
  "earthElevCentiDeg": [int],
  "earthAzCentiDeg": [int],       // Earth azimuth × 100 — ADDED 2026-11-14 (additive, for the HUD Sun/Earth compass)
  "horizonDeg": [360 floats],     // horizon at azimuths 0..359°
  "generatedWith": { "dem": "...", "kernels": [...], "method": "linear-disk-approx" }
}
```

`illumination/index.json` holds `{start, end, stepHours, observerHeightM, dem, method, generatedAt, sites: [{siteId, name, lat, lon, file, pctSunlit, pctComms, longestDarkHours, horizonCoveragePct}]}`.

Time for sample `i` is `start + i × stepHours`.

**Contract changelog:** 2026-11-14 added `earthAzCentiDeg` (additive; readers must tolerate missing fields in older files).

## Commands

```bash
# Frontend (from SeleneFrontend/)
dotnet watch                      # http://localhost:5281
dotnet build                      # must build with 0 warnings
dotnet test ../SeleneFrontend.Tests  # simulator + optimizer unit tests (links the logic sources)

# Pipeline (from pipeline/, see pipeline/README.md)
python download_kernels.py
python build.py --dem /path/ldem_75s_120m_float.lbl [--site ID]
python -m pytest -q
```

## Hard rules

1. **No NASA logos or insignia, and no graphics from lander manufacturers.** Only our own SVGs.
2. **Do not invent lander specs or coordinates.** Mark approximate values `approximate`. Every number needs a source (`source` / `sourceUrl`).
3. **UI style is a mission console:** CSS variables from `app.css`, IBM Plex Sans/Mono, thin borders, **max 2px radius**, plus modern interactions. Accessibility is required: visible focus, WCAG AA contrast, `prefers-reduced-motion`.
4. **The demo must work offline from a laptop.** Data is precomputed ahead of time.
5. **After EVERY task:** update `ROADMAP.md` (Done with date and commit, new problems, AI usage log), then commit and push to `origin main`.

## Gotchas (learned the hard way)

- `index.html` must load `_framework/blazor.webassembly#[.{fingerprint}].js`. `blazor.web.js` gives a black screen.
- `Cesium.Ellipsoid.default` exists only from **Cesium 1.119**. On older versions the globe stays Earth-sized.
- The Cesium script has to come from `…/releases/X/Build/Cesium/Cesium.js`. The path without `Build/Cesium` returns 404.
- C# models are PascalCase and the JSON is camelCase. Deserialize with `JsonSerializerDefaults.Web`.
- `type=number` inputs: format and parse with `CultureInfo.InvariantCulture`. The browser runs with `pl` culture.
- LOLA `float_img`: open the **`.lbl`**, not the `.img`. Heights are in **km**. The label `OFFSET` is the sphere radius, not a height offset.
- A running `dotnet watch` locks `obj/`. For a second, test instance use `dotnet run --no-build --urls …`.
- Analysis logic (`MissionSimulator`, `WindowOptimizer`, `IlluminationSeries`, `LanderPreset`) must stay UI-free: `SeleneFrontend.Tests` compiles these files directly.
- Dates in WASM: the runtime uses the browser's time zone. Treat unspecified `DateTime`s as UTC (`TimeUtil.AsUtc`), never call `ToUniversalTime()` on them.
- Anything rendered per hour or per day (timeline, heatmap) must not re-render on every progress tick. Use `ShouldRender` or the canvas heatmap.
- `index.html` must keep `<script type="importmap"></script>`: .NET 10 fills it with fingerprinted file names. Without it the app works on the dev server but **not** on nginx or GitHub Pages.
- `wwwroot/appsettings.Development.json` (token) is excluded from publish in the csproj. Keep it that way: published files are public.
- CesiumJS is loaded with `defer`. Code touching `Cesium` must wait for `UiService.WhenCesiumReadyAsync()` (MoonPanel does).
- Editing anything in `wwwroot` makes a running `dotnet watch` rebuild. A second `dotnet run --no-build` instance then serves a stale manifest (MIME errors), so restart it.
- Cesium camera: call `camera.cancelFlight()` before `camera.lookAt()`. A running `flyTo` (intro, fly-to-site) plus `lookAt` drives the camera to NaN, and Cesium stops rendering with "Invalid array length". Release a `lookAt` lock with `lookAtTransform(Matrix4.IDENTITY)` before other camera moves (`releaseCamera()` in moonViewer.js).
- Never sample the clock twice for related geometry (e.g. ellipse semi-axes): compute once per frame. Invalid geometry stops the Cesium render loop. `moonViewer.js` has a render-error safety net, so check `window.__seleneRenderErrors` in tests.
- JS modules (`moonViewer.js`, `landingAnimation.js`…) are fingerprinted with `integrity` through the importmap. After editing JS, rebuild or restart the dev server, or the browser blocks the module.
- The landing animation is optional (`features.landingAnimation`). Nothing outside `landingAnimation.js` may depend on it, and `moonViewer.js` only imports it lazily.
- UI state that many components share (boot, welcome, help, compare, compass time) lives in `UiService`. Timeline hover uses its own `HoverTimeChanged` event so the whole UI does not re-render on mouse move.
- `sites.json` is **generated** by `pipeline/select_sites.py` from Wueller et al. (2026) candidates (Zenodo, CC-BY-4.0). Do not edit coordinates by hand; re-run the pipeline. Their shapefile projection has true scale at −70°, unlike the DEM (−90°), so always reproject with the shipped `.prj`.
- "Lit" has two definitions: `index.json → pctSunlit` uses `sunFraction > 0`, while site selection and the 6.5-day test use `≥ 50`.
