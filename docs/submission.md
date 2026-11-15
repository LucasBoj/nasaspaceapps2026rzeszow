# Space Apps 2026 — project page text (copy-paste)

Each section below is one field of the project page on spaceappschallenge.org. All text is in English, as the
submission guide requires. Fill in the `[TEAM: …]` placeholders before submitting.

---

## Project name

SELENE OPS — Lunar South Pole Landing-Window Planner

## High-level project summary

SELENE OPS turns NASA lunar data into a go / no-go decision. A mission planner picks a landing site near the lunar
south pole, a lander (Nova-C, Blue Ghost, Griffin, Starship HLS or Blue Moon) and a date window. The app simulates,
hour by hour, how much of the Sun the solar panels see above the real LOLA terrain horizon, whether Earth is in view
for communication, and how the battery charges and drains, including night heaters. It then answers FEASIBLE,
MARGINAL or NOT FEASIBLE in plain language, finds the best landing window across all 9 Artemis III candidate regions,
and shows how robust that answer is (landing accuracy, load uncertainty, a Monte Carlo success probability). At the
pole the Sun skims the horizon, so a few days or a hundred metres decide whether a lander survives; SELENE OPS makes
that visible in seconds, in the browser, with no installation.

## Link to project demo

Demo deck (7 slides, Canva, view-only): https://www.canva.com/d/hlbxP_Xmy88jDgG

## Link to final project

- Live app: https://selene.byst.re/
- Source code (open source, Apache-2.0): https://github.com/LucasBoj/nasaspaceapps2026rzeszow

## Detailed project description

**What it does.** SELENE OPS is a landing-window planner for the lunar south pole. For a chosen site, lander and
date window it computes:

- **Energy** — the share of the solar disk above the local terrain horizon every hour, the power of the lander's
  fixed solar panels at that Sun angle (each panel face modelled with its own orientation, the landing heading
  optimised automatically), and the battery state of charge hour by hour, with battery losses and night heaters
  driven by LRO Diviner ground temperatures.
- **Communication** — whether Earth is above the terrain horizon, and the longest gap without contact.
- **Verdict** — FEASIBLE / MARGINAL / NOT FEASIBLE with a one-sentence reason ("the battery runs out after an 11-hour
  darkness on day 4"), the best landing date, and a heatmap of every landing day in every region.

**How it works.**

1. A one-off Python pipeline reads the NASA LRO LOLA polar terrain model (120 m/pixel), computes the 360° horizon
   for each site (rays up to 150 km), and combines it with Sun and Earth positions from JPL DE421 and NAIF lunar
   orientation kernels (Skyfield). It writes hourly series for November 2026 – December 2035 as static JSON.
2. The landing sites are the best-lit, flattest candidates in each of the 9 Artemis III regions, selected from the
   130 candidates published by Wueller et al. (2026).
3. The web app (Blazor WebAssembly + CesiumJS 3D Moon) runs the mission simulation, the window optimiser and the
   uncertainty analysis entirely in the browser. There is no backend; the whole app is static files.
4. Any point between 80° and 90° S can also be analysed: the browser computes its horizon from a 240 m LOLA grid
   and a compact ephemeris file.

**Why it can be trusted (validation).**

- Sun and Earth directions match JPL Horizons to ≤ 0.005°.
- Sunlit time matches the NASA LOLA illumination maps (Mazarico et al. 2011) within 0.1–4.1 percentage points per
  site when compared like for like (same DEM resolution, surface observer, NASA's own pixel centres).
- Every lander number has a source link (FCC filings, manufacturer pages, NASA documents) or is labelled
  ASSUMPTION / DERIVED in the app.
- The verdict comes with its robustness: re-runs over the landing ellipse (each point with its own horizon),
  ±20 % load, panel height, and a 400-run Monte Carlo P(FEASIBLE).
- Case study: the IM-2 Athena landing (March 2025) — planned site vs actual site.
- 100+ unit tests (xUnit, pytest) and browser tests in Chromium, Firefox and WebKit run before every deploy.

**Benefits.** Polar landers live or die by sunlight and Earth contact, and both change within days and within a
hundred metres. SELENE OPS lets mission planners, students and the public see that trade-off and compare landers,
sites and dates in seconds, instead of reading illumination maps by hand. A science layer shows the distance to
permanently shadowed craters (possible water ice), so the "sun vs ice" trade-off is visible too.

**Our goal.** A transparent, sourced, open tool that goes from NASA data to a decision: where and when a lander can
land and survive at the lunar south pole.

**Tools and languages.** C# / .NET 10 (Blazor WebAssembly), JavaScript, CesiumJS 1.120, Python 3.11 (Skyfield,
rasterio, pyproj, NumPy), xUnit, pytest, Playwright, glTF Transform + meshoptimizer, nginx, GitHub Actions.

## NASA data (and space agency partner data)

- **NASA LRO LOLA polar DEM** `LDEM_75S_120M` (PDS Geosciences Node) — terrain horizons for every site; 240 m and
  60 m variants for custom points and validation.
- **NASA LOLA permanently shadowed regions** `LPSR_75S_120M_201608` (PDS) — distance to possible water-ice craters.
- **NASA LOLA illumination maps** (Mazarico et al. 2011, PGDA / PDS) — validation of our sunlit time.
- **NASA LRO Diviner** surface temperatures (PDS) — ground temperature for the night-heater model.
- **NASA/JPL Moon Trek** LRO WAC mosaic — imagery on the 3D Moon.
- **JPL DE421 planetary ephemeris and NAIF lunar orientation kernels** — Sun and Earth positions.
- **JPL Horizons** — independent check of Sun and Earth directions.
- **NASA Artemis III candidate landing regions** — the 9 regions the app covers.
- **NASA CLPS mission information** (IM-1, IM-2, Blue Ghost M1) — lander context and the IM-2 case study.

Partner agency data: not used in this version (JAXA Kaguya terrain is on our roadmap).

## References / resources

Data:

- Wueller, L. et al. (2026), JGR Planets, doi:10.1029/2025JE009434; data: Zenodo doi:10.5281/zenodo.17084058
  (CC-BY-4.0) — landing-site candidates.
- Mazarico, E. et al. (2011), Icarus 211 — illumination conditions at the lunar poles (LOLA illumination maps).
- Cesium Moon Terrain (Cesium ion asset 2684829) — optional 3D terrain.
- Lander specifications: FCC filings (Intuitive Machines IM-1), Firefly, Astrobotic Lunar Landers Payload User's
  Guide, NASA Artemis documents; each value is linked in the app (SPECS table).
- LROC releases 1401 / 1408 and NASA release 25-025 — IM-2 case study.

Software and assets (all open source or used under their terms):

- CesiumJS 1.120 (Apache-2.0), self-hosted.
- IBM Plex Sans / Mono (SIL Open Font License 1.1), self-hosted.
- .NET 10 / Blazor WebAssembly (MIT).
- Skyfield, rasterio, pyproj, NumPy (MIT / BSD).
- glTF Transform 4.5 and meshoptimizer (MIT).
- Microsoft TRELLIS.2 (MIT) and Meshy AI — 3D lander models (AI-generated, see below).
- All icons and lander silhouettes are our own SVGs. No NASA logos or manufacturer graphics are used.

The app is not affiliated with or endorsed by NASA.

## Use of Artificial Intelligence (Super Intelligence)

The idea, the problem definition and the product decisions are the team's. AI tools helped us build it:

- **Code, tests and documentation** — Claude Code (Anthropic, Claude Opus 5.5) wrote and reviewed most of the
  frontend, the Python pipeline, the tests and the documentation under the team's direction.
  [TEAM: name the AI tool used for the first app skeleton, commits 323edd4 / a2f267d.]
- **3D lander models** — concept images from Meshy AI, turned into 3D by Microsoft TRELLIS.2, optimised by us.
  Logos, flags and insignia that the AI painted on textures were removed.
- **No scientific number is AI-generated.** All sunlight, Earth-visibility and battery values are computed
  deterministically from NASA LOLA terrain and JPL ephemerides; lander specs come from cited sources.

Where it is marked: a visible "AI-generated 3D model" badge in the app whenever a model is shown; a
"Contains AI-generated 3D models" watermark on screenshots and the demo video; `<meta name="ai-assistance">` in the
page; `asset.generator` / `asset.extras` in every model file; a `provenance` note in every data file; the full
session log in `docs/AI_USAGE.md`.

---

## Before you click "Submit for Judging"

- [ ] All required fields filled, all in English.
- [ ] Demo link and project link open in a private window without login.
- [ ] Video ≤ 30 s with English subtitles and the AI watermark, or ≤ 7 slides with team name, member names and the
      challenge name.
- [ ] No images of anyone under 18 in the demo.
- [ ] Every team member is confirmed at the Rzeszów local event.
- [ ] Tick "Terms and Conditions" and "Confirm All Work is Original", then **Submit for Judging** before the local
      deadline (Rzeszów: 13:00 on 15 Nov 2026; global: 23:59).
