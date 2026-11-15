# SELENE OPS — project context

**What:** a landing-window planner for the lunar south pole. Entry for **NASA Space Apps Challenge 2026**, challenge *"Lunar Landing Site Navigator / CLPS Lunar Mission Browser"*, local event **Rzeszów**.
**Deadline:** submission by **Sunday 15.11.2026, 23:59 local time**. The local jury may close earlier, so check with the Local Lead.
**Goal:** 1st place locally and a Global Award nomination. Judging criteria (1–5 each): Impact, Creativity, Validity, Relevance (NASA data as the core), Presentation.

**Product:** for a chosen site, lander and date window, compute illumination (energy), Earth comms and hour-by-hour battery balance. The output is a verdict, **"feasible / not feasible"**, plus the best window. From data to a decision.

## Stack

| Part | What | Where |
|---|---|---|
| Frontend | Blazor WebAssembly standalone, **.NET 10**. CesiumJS **1.120** (self-hosted in `wwwroot/lib/cesium-1.120`) via JS interop; IBM Plex self-hosted in `wwwroot/fonts` | `SeleneFrontend/` (`wwwroot/js/moonViewer.js`, `cardGrid.js`) |
| Data | Python 3.11+ (Skyfield, rasterio, pyproj, numpy). A **one-off** precompute into JSON | `pipeline/` → `SeleneFrontend/wwwroot/data/illumination/` |
| Runtime | **No backend.** Static files only | — |
| Hosting | Static files on Mikrus (nginx), single public domain **https://selene.byst.re/** (= stable root). GitHub Actions: push `main` → stable (live). **Deploy and Cesium are finished and verified (`85d36e0`): do not change the workflow, `deploy/*`, secrets or the token.** Preview :8080 is unused (no public address) | `deploy/` (README in Polish), `.github/workflows/deploy.yml` |

## Data sources

- **Terrain:** NASA LRO/LOLA, `ldem_75s_120m_float` (PDS Geosciences Node, polar stereographic, 120 m/px).
- **Landing sites:** Wueller et al. (2026), JGR Planets, doi:10.1029/2025JE009434, Table A1 via Zenodo doi:10.5281/zenodo.17084058 (CC-BY-4.0, attribution required).
- **Ephemerides:** JPL DE421 plus lunar orientation kernels (NAIF), computed via Skyfield.
- **Imagery:** NASA/JPL Moon Trek, LRO WAC mosaic (tiles `trek.nasa.gov`, no token needed).
- **3D terrain (optional):** Cesium Moon Terrain, ion asset `2684829` (already added to the team's ion account; works locally with the dev token).
- **Cesium ion token:** only in `SeleneFrontend/wwwroot/appsettings.Development.json`, which is **gitignored**. **The repo is public — never commit the token.** `appsettings.json` keeps an empty value. Without a token the app falls back to 2D mosaic mode.

## Data contract — `illumination/{siteId}/{year}.json` (single source of truth)

Change it only deliberately: describe the change here, then update both the pipeline and the frontend.

```jsonc
{
  "siteId": "malapert_massif", "lat": -85.8, "lon": -3.5, "observerHeightM": 2,
  "start": "2027-01-01T00:00:00Z", "stepHours": 1, "count": 8760,   // per calendar year (2026 starts 2026-11-01)
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

`illumination/index.json` holds `{start, end, stepHours, years, observerHeightM, dem, method, generatedAt, sites: [{siteId, name, lat, lon, files: {"2027": "site/2027.json", …}, pctSunlit, pctComms, longestDarkHours, horizonCoveragePct, byYear: {"2027": {pctSunlit, pctComms, longestDarkHours}, …}}]}`. Range 2026-11-01 … 2035-12-31 (DE421 and `moon_pa_de421_1900-2050` cover it). The frontend (`IlluminationRepository`) loads only the years of the landing window (+62 days for the mission and the max-stay cap) and joins them (`IlluminationSeries.Concat`); the window is limited to 24 months and starts no earlier than today.

Time for sample `i` is `start + i × stepHours`.

**Contract changelog:** 2026-11-14 added `earthAzCentiDeg` (additive; readers must tolerate missing fields in older files). 2026-11-15 **one file per site and calendar year** (`{siteId}/{year}.json`, same fields, own `start`/`count`), range 2026-11 … 2035; `index.json` gains `years`, `files`, `byYear` and drops `file`; the single `{siteId}.json` files are removed (`build.py` deletes them). 2026-11-15 added `{siteId}/ellipse.json` (additive, separate file from `build_ellipse.py`; optional for the app): a 60 m grid of 197 points within 480 m, each with its own `horizonCentiDeg` (own azimuth frame), `azShiftDeg`, `tiltDeg`, `tiltAzDeg`, `slopeDeg`, `pctSunlit`, `pctComms`. Format in `pipeline/README.md` → Output format.

## Energy model (fixed panels, Lambert incidence)

`landers.json → panels` describes how the array is mounted: `{mode: "fixed" | "ideal", facets: [{name, azOffsetDeg, normalElevDeg, share}], kind, note, sourceUrl}`. `azOffsetDeg` is relative to the lander heading; `normalElevDeg` 0 = vertical wall panel, 90 = top deck. The current layouts are the team's description and are marked ASSUMPTION. HLS uses `ideal` because its layout is not public.

- Per step: `P = solarArrayW × sunFraction/100 × Σ shareᵢ·max(0, cos θᵢ) / K`, with `cos θᵢ = cos εᵢ·cos el·cos(az − φᵢ) + sin εᵢ·sin el` and `φᵢ = heading + azOffsetᵢ` (`Services/SolarGeometry.cs`).
- **K** is the maximum of the same sum over Sun directions az 0–359°, el 0–10°. So `solarArrayW` is read as the output at the best Sun direction for that layout, not as the sum of nameplate ratings.
- **AUTO heading** maximises the mission's solar energy. It uses per-heading prefix sums (10° grid, built once per series and layout, ~20 MB for 9 sites) and a parabolic refinement. The what-ifs are a manual heading and `IDEAL` tracking (the old model, an upper bound).
- Fixed panels need `sunElevCentiDeg` and `sunAzCentiDeg`. Files without them fall back to ideal tracking. **The data contract is unchanged.**
- At the pole the Sun's azimuth moves ~12°/day, so fixed-panel CLPS landers survive only ~7–8 days from one landing. QUICK DEMO uses 7 days (`UiService.RecommendedMissionDays`).
- Score = `35·minSoC + 20·comms + 10·light + 20·clamp(produced/used − 1, 0, 1) + 15·terrainMargin` (`WindowOptimizer.Score`). `terrainMargin = ½·m(min Sun clearance) + ½·m(min Earth clearance)` with `m(c) = 1 − e^(−max(c,0)/2°)`. Clearance is the height above the LOLA horizon, from `IlluminationSeries.SunClearanceDeg()` / `EarthClearanceDeg()`. Without this term every fully lit, fully connected window scored the same (ties across all 9 regions with ideal tracking). Do not cap it linearly: a cap brings the ties back.
- **Battery:** `landers.json → batteryRoundTripEff` (η, default 1 = ideal). Loads are served from the array first; a surplus charges at ×√η, a deficit is drawn at ÷√η; surplus with a full battery is shunted without loss (`SimulationResult.BatteryLossWh`). What-if `idealBattery` sets η = 1. Depth of discharge is the section-04 battery threshold, not a separate parameter.
- **Heatmap is shared per input signature** (`AnalysisService._heatmapTask`): it does not depend on the selected site, so a run with the same inputs (QUICK DEMO / Land & analyze after the startup run, another site) reuses it or joins the computation in flight, and phase 1 takes the site's row from it. Anything new that changes the analysis result must be part of `Signature()`, or a stale heatmap is reused.
- **Robustness row** under the verdict (`RobustnessRow.razor`): (1) `SensitivityAnalysis.ForLoad` re-runs the shown mission with day + survival loads ×0.8 / ×1.2 (heading pinned) and bisects the break-even load factor (verdict is monotone in load); (2) the NASA 120 m map's 3×3-pixel range (`nasaSunRangePct` in `data/validation/nasa_illumination.json`), POINT-SENSITIVE at ≥ 10 pp loss within 120 m; (3) **landing ellipse** (`LandingEllipse.Analyze`, `AnalysisService.Ellipse`): the shown mission (same date, duration, heading) re-run at every `ellipse.json` point within `landers.json → landingAccuracyM` (sourced per lander: Blue Ghost 100 m, Nova-C 400 m, HLS 100 m NASA requirement; Griffin 250 m ASSUMPTION). Each point's sunFraction/earthVisible is rebuilt from the SITE's Sun/Earth series with the point's own horizon: `h_p(az + azShift) − tilt·cos(az − tiltAz)`, validated ≤ 2 pp against a full recomputation (`build_ellipse.py --validate`). Do not resample the masks into the site frame (the second interpolation cost up to 15 pp). The target keeps the nominal verdict. SIMPLE shows one plain sentence (`VerdictText.EllipseSentence`).

## Commands

```bash
# Frontend (from SeleneFrontend/)
dotnet watch                      # http://localhost:5281
dotnet build                      # must build with 0 warnings
dotnet test ../SeleneFrontend.Tests  # simulator + optimizer unit tests (links the logic sources)

# Deploy (from repo root, see deploy/README.md)
git push origin main                      # CI → tests → https://selene.byst.re/ (live!); rollback: git revert <c> && push
./deploy/deploy.sh preview --build-only   # local build + checks, no upload (works in Git Bash)
./deploy/deploy.sh preview                # manual deploy (needs rsync; not in Git Bash), then: … stable
CESIUM_ION_TOKEN='<URL-restricted token>' ./deploy/deploy.sh stable   # optional 3D, never commit the token
./deploy/smoke-test.sh <site-url> out/publish/wwwroot/index.html      # check a deployed site

# Pipeline (from pipeline/, see pipeline/README.md)
python download_kernels.py
python build.py --dem /path/ldem_75s_120m_float.lbl [--site ID]
python -m pytest -q
python validate_horizons.py [--refresh]   # Sun/Earth az/el vs JPL Horizons (offline snapshots in data/horizons/)
python build_ellipse.py --dem /path/ldem_75s_120m_float.lbl [--validate]   # landing-ellipse horizons → illumination/{site}/ellipse.json
python compare_nasa_illumination.py [--refresh]   # sunlit/Earth time vs NASA LOLA maps (Mazarico 2011) → README table, docs/nasa_vs_ours.png, frontend data/validation/
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
- CesiumJS is self-hosted: `wwwroot/lib/cesium-1.120/` is `Build/Cesium` from the `cesium@1.120.0` npm package (byte-identical to the cesium.com CDN build) and must keep `Workers/`, `Assets/`, `ThirdParty/`, `Widgets/` next to `Cesium.js`. To upgrade, copy a new version into a new `lib/cesium-X.Y` folder (nginx caches that path as immutable) and change both links in `index.html`. Do not load anything from a CDN or Google Fonts: the demo must work offline. If a CDN is ever needed again, the path must be `…/releases/X/Build/Cesium/Cesium.js` (without `Build/Cesium` it is a 404).
- C# models are PascalCase and the JSON is camelCase. Deserialize with `JsonSerializerDefaults.Web`.
- `type=number` inputs: format and parse with `CultureInfo.InvariantCulture`. The browser runs with `pl` culture.
- LOLA `float_img`: open the **`.lbl`**, not the `.img`. Heights are in **km**. The label `OFFSET` is the sphere radius, not a height offset.
- A running `dotnet watch` locks `obj/`. For a second, test instance use `dotnet run --no-build --urls …`.
- Analysis logic (`MissionSimulator`, `WindowOptimizer`, `IlluminationSeries`, `LanderPreset`) must stay UI-free: `SeleneFrontend.Tests` compiles these files directly.
- Dates in WASM: the runtime uses the browser's time zone. Treat unspecified `DateTime`s as UTC (`TimeUtil.AsUtc`), never call `ToUniversalTime()` on them.
- Anything rendered per hour or per day (timeline, heatmap) must not re-render on every progress tick. Use `ShouldRender` or the canvas heatmap.
- `index.html` must keep `<script type="importmap"></script>`: .NET 10 fills it with fingerprinted file names. Without it the app works on the dev server but **not** on nginx or GitHub Pages.
- `wwwroot/appsettings.Development.json` (token) is excluded from publish in the csproj, and `deploy.sh` aborts if it ever appears. Keep it that way: published files are public. If you edit a published file (as deploy.sh does), regenerate its `.gz`, because nginx `gzip_static` serves the compressed copy.
- CesiumJS is loaded with `defer`. Code touching `Cesium` must wait for `UiService.WhenCesiumReadyAsync()` (MoonPanel does).
- Editing anything in `wwwroot` makes a running `dotnet watch` rebuild. A second `dotnet run --no-build` instance then serves a stale manifest (MIME errors), so restart it.
- Cesium camera: call `camera.cancelFlight()` before `camera.lookAt()`. A running `flyTo` (intro, fly-to-site) plus `lookAt` drives the camera to NaN, and Cesium stops rendering with "Invalid array length". Release a `lookAt` lock with `lookAtTransform(Matrix4.IDENTITY)` before other camera moves (`releaseCamera()` in moonViewer.js).
- **Idle frame-rate cap** (`moonViewer.js → IDLE_FPS = 24`): a still globe renders at 24 fps; camera moves, the landing animation (+9 s settle) and `fullRate(ms)` get every frame. Anything new that animates by itself in the 3D view (not a camera move, not the landing module) must call `fullRate(ms)`, or it runs at 24 fps. Check with `window.__seleneFrameRate()` and `tools/startup-profile` (README there; never measure in a hidden window — timers drop to 1 Hz).
- Never sample the clock twice for related geometry (e.g. ellipse semi-axes): compute once per frame. Invalid geometry stops the Cesium render loop. `moonViewer.js` has a render-error safety net, so check `window.__seleneRenderErrors` in tests.
- JS modules (`moonViewer.js`, `landingAnimation.js`…) are fingerprinted with `integrity` through the importmap. After editing JS, rebuild or restart the dev server, or the browser blocks the module.
- glTF models (`wwwroot/models/*.glb`, only via `landers.json → modelUri`, all 5 landers) are Y-up. `Cesium.Model` converts to Z-up, so use an ENU `modelMatrix`; `modelRotationDeg` (optional) only yaws about the local vertical. They are meshopt-compressed and **quantized**: POSITION is int16-normalized and the scale/offset sits on the mesh node, so bounds must be dequantized and pass through the node transforms (`glbInfo` in landingAnimation.js). Scale to `modelHeightM`, or to `modelWidthM` when `modelScaleBy: "width"` (Griffin). Never edit the published GLBs by hand: originals live in the git-ignored `models/source/` (excluded from build/publish), regenerate with `tools/optimize-models`. Models are preloaded after start and pooled (hidden, not destroyed).
- After touchdown the Cesium Moon tiles can lift the ground by ~68 m: the settle loop moves the lander **and** re-aims the locked camera (keeping the user's heading/pitch/range). Without that a small lander's close-up camera ends up under the terrain (black view).
- Terrain height near the camera changes as Cesium Moon tiles refine (~68 m jumps). Anything placed on the surface must follow `scene.sampleHeight` of the rendered tiles; a single early sample is not enough.
- The scene light is the real Sun from the illumination JSON (`setSunLight` in moonViewer.js). Polar light is grazing, so the uniform SH ambient is what keeps terrain and models readable.
- Lander heading in 3D = the analysed mission's heading (`SimulationResult.HeadingDeg`, AUTO or manual). It arrives ~1 s into the descent; `MoonPanel.SyncLanderHeadingAsync` calls `setLanderHeading` only for missions newer than the landing (`AnalysisService.MissionVersion`), and `landingAnimation.js` buffers a heading that arrives before the lander exists (`earlyHeading`). The lander yaws in the terminal phase; a landed marker turns via `freeze()` (every part has pose functions — do not shift frozen positions by hand).
- The Nova-C GLB has **no side panels** (uniform gold foil, 4-fold hull). Solar panels in 3D are our own plates drawn from `landers.json → panels` (`plateSpecs`), never read from a model. GLB front: glTF +Z → east after Cesium's axis correction (`GLB_FRONT_AZ_DEG = 90`); the model only turns about the local vertical.
- `requestAnimationFrame` stops while the Browser pane / tab is hidden: the landing and marker turns then freeze mid-way. Test hook: `window.__seleneLanderPose()` → `{headingDeg, targetDeg, plates, landed, plateAzimuths}` (azimuths measured from the rendered plates).
- The landing animation is optional (`features.landingAnimation`). Nothing outside `landingAnimation.js` may depend on it, and `moonViewer.js` only imports it lazily.
- **Two modes:** SIMPLE (default: Moon, 3-step panel, one `VerdictCard`) and ANALYST (`UiService.AnalystMode`, everything else). The verdict is shown ONCE (`VerdictCard`, inline in the results panel in Analyst). Plain language in SIMPLE: no SoC/PSR/DTE/heading/terrain margin. `UiService.Landed` locks site picking until `NewLanding()`. Every disabled action shows a visible plain-language reason tied with `aria-describedby` (main button: `UiService.LandBlockers()`); never rely on a tooltip alone. See `docs/ux-redesign-2026-11-14.md`.
- Camera guard (`guardCamera` in moonViewer.js, `scene.preRender`): ≥ 300 m over the terrain in free flight, pitch/zoom limits, NaN recovery. Never enable `Cesium3DTileset.enableCollision` (1 fps stalls) and never call `sampleHeight` every frame (it is a render pass).
- UI state that many components share (boot, welcome, help, compare, compass time) lives in `UiService`. Timeline hover uses its own `HoverTimeChanged` event so the whole UI does not re-render on mouse move.
- `sites.json` is **generated** by `pipeline/select_sites.py` from Wueller et al. (2026) candidates (Zenodo, CC-BY-4.0). Do not edit coordinates by hand; re-run the pipeline. Their shapefile projection has true scale at −70°, unlike the DEM (−90°), so always reproject with the shipped `.prj`.
- "Lit" has two definitions: `index.json → pctSunlit` uses `sunFraction > 0`, while site selection and the 6.5-day test use `≥ 50`.
- `System.Text.Json` cannot serialise `NaN`. `SimulationResult` and `HeatmapResult` use NaN for "not applicable" (no heading with ideal tracking, never lit), so map NaN to a sentinel before sending data to JS (`LandingHeatmap.Payload` uses −1).
- Lunar site coordinates in JPL Horizons are in the Mean-Earth frame, the same as `MOON_ME_DE421`. Do not convert to PA (`pipeline/validate_horizons.py`).
- NASA LOLA illumination maps (PDS `extras/illumination/img/`) have labels that name `*.IMG` in upper case, while the server stores lower case. GDAL cannot open the remote pair. `compare_nasa_illumination.py` reads the geometry from the `.lbl` and fetches pixels with HTTP Range. The PGDA COG directory returns 404. Compare like-for-like against the **120 m** map (our DEM resolution); the 60 m map is 7–22 pp darker at peaks.
- `wwwroot/data/validation/nasa_illumination.json` is generated by `compare_nasa_illumination.py` and is optional for the app (`MissionConfigState.NasaCheck`). It is not part of the `illumination/*` contract. Do not edit it by hand.
- Any new UI-free logic file used by the simulator must also be linked in `SeleneFrontend.Tests.csproj` (as `SolarGeometry.cs`, `SensitivityAnalysis.cs`, `NasaIlluminationCheck.cs`, `LandingEllipse.cs` and `LandingEllipseData.cs` are).
- `*.sh`, `*.conf` and `*.yml` are forced to LF by `.gitattributes`. With `core.autocrlf=true` a CRLF script fails on the server and in CI (`$'\r': command not found`). Clones from before `.gitattributes` keep CRLF until refreshed (`deploy/README.md`, step 2).
- On Windows `python3` is often the Microsoft Store stub. Scripts must pick the first Python that actually runs (as `deploy.sh` does) and set `PYTHONIOENCODING=utf-8`: piped Windows Python crashes on `→` otherwise.
- The published `index.html` names `blazor.webassembly` twice: the importmap alias (plain name) and the `<script src>` (fingerprinted). Checks must read the `src=` one.
- CI deploy (`.github/workflows/deploy.yml`) without the `MIKRUS_SSH_KEY` secret only tests and builds (warning "Deploy skipped"). The build is stamped with its target (`out/publish/selene-build.txt`); `deploy.sh --upload-only` refuses a build made for the other target (og:image differs). New scripts need the exec bit in git: `git update-index --chmod=+x` (`core.filemode=false` on Windows).
