# Demo deck — 7 slides (Space Apps limit: max 7 incl. title)

Rules this deck follows: English only; team name, member names and the challenge on slide 1; every image with a
lander keeps its "Contains AI-generated 3D models" stamp; no NASA logos; no photos of people under 18.
Style: dark mission-console look (#0b0f14 background, white text, cyan #4cc9f0 accents, green ✓ / amber ⚠ / red ✕),
IBM Plex Sans (headings) and IBM Plex Mono (numbers). Few words per slide, big visuals.

---

## Slide 1 — Title

**SELENE OPS**
From NASA lunar data to a landing decision.

- Team: [TEAM NAME]
- Members: [Name Surname, Name Surname, …]
- Challenge: Lunar Landing Site Navigator / CLPS Lunar Mission Browser
- NASA Space Apps Challenge 2026 · Rzeszów
- Live: selene.byst.re

Visual: `docs/screenshots/after-1920x1080-2-quick-demo.png` (lander on the Moon + FEASIBLE card), full bleed.

## Slide 2 — The problem

**At the lunar south pole, a few days or 100 metres decide if a lander survives.**

- The Sun skims the horizon a few degrees up and circles the sky once a month.
- Fixed solar panels lose it as it moves ~12° a day; mountains cast shadows for hours.
- Earth, the only radio link, also hides behind terrain.
- March 2025: IM-2 Athena tipped over in a crater; its panels got too little sunlight and it shut down after about a day.

Visual: a dark polar terrain crop (e.g. `docs/screenshots/stage3-replay-dark.png`) with one line overlaid:
"Sun 1° above the horizon".

## Slide 3 — Our solution: three steps to a verdict

**Pick a site → pick a lander → Land & analyze.**

- 9 Artemis III candidate regions (or any point in 80–90° S).
- 5 landers: Nova-C, Blue Ghost, Griffin, Starship HLS, Blue Moon — every spec sourced.
- Answer in plain language: ✓ FEASIBLE · ⚠ MARGINAL · ✕ NOT FEASIBLE, plus the best landing date.
- Runs in any browser, no installation, no backend.

Visual: `docs/screenshots/stage2-after-1920x1080-landed.png` (verdict card with "Landing area OK" and the
step panel).

## Slide 4 — How NASA data makes it possible

**Hour by hour, 2026–2035, for every landing day.**

Flow (left → right, simple boxes):
1. NASA LRO LOLA terrain (120 m) → 360° horizon of each site
2. JPL DE421 + NAIF kernels → Sun and Earth position every hour
3. Simulation → panel power (each panel face) · Earth contact · battery · night heaters (LRO Diviner)
4. Optimiser → best window across 9 regions × every day

Also used: NASA LOLA shadowed-crater map (possible water ice), NASA/JPL Moon Trek imagery, JPL Horizons,
Wueller et al. 2026 landing-site candidates.

Visual: `docs/screenshots/stage2-after-1920x1080-heatmap.png` (heatmap: 9 regions × 731 days).

## Slide 5 — Why you can trust the answer

**Validated against NASA and JPL, honest about uncertainty.**

- Sun / Earth directions = JPL Horizons within 0.005°.
- Sunlit time = NASA LOLA illumination maps within 0.1–4.1 pp per site (like for like).
- Every lander number has a source link — or is labelled ASSUMPTION.
- Each verdict is stress-tested: landing ellipse (each spot its own horizon), loads ±20 %, panel height,
  400-run Monte Carlo → "works in N of 400 simulated landings".

Visual: `pipeline/docs/nasa_vs_ours.png` (NASA map vs our model, 9 sites).

## Slide 6 — One example, one decision

**Blue Ghost at Nobile Rim 1, 7 days on the surface.**

- Land 26 Dec 2028 → ⚠ MARGINAL: an 11-hour darkness drains the battery to ~6 %.
- Land 23 Jan 2029 → ✕ MISSION LOST.
- "Show the best landing window" → ✓ FEASIBLE date in seconds.
- Same tool, same site: the date is the difference.

Visual: QUICK DEMO verdict card (take a fresh screenshot from selene.byst.re → Quick demo; the stored
`stage3-*` screenshots are from before the heater model and show older numbers).

## Slide 7 — What's next

**From a hackathon tool to a mission-planning aid.**

- Deep Space Network station visibility, not just "Earth above the horizon".
- JAXA Kaguya terrain as a second data source; finer 20 m terrain at the pole.
- Sun vs ice: pick a site that powers a rover *and* reaches water ice.
- Printable mission report for planners and classrooms.

Footer: selene.byst.re · github.com/LucasBoj/nasaspaceapps2026rzeszow · Open source (Apache-2.0)
Small print: AI helped write the code and made the 3D lander models; every science number comes from NASA/JPL data.
Not affiliated with or endorsed by NASA.
