# SELENE illumination pipeline

One-off generator of per-site **sunlight** and **Earth-visibility** time series for the
SeleneFrontend app. For each candidate landing site it computes a terrain horizon from
NASA LOLA topography, then evaluates the Sun and Earth against that horizon every hour
from 2027-01-01 to 2028-12-31.

Output goes to `SeleneFrontend/wwwroot/data/illumination/`.

## Setup

Python 3.11+. (`pyshp` is only needed by `import_wueller_sites.py`.)

```bash
cd pipeline
python -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r requirements.txt
python download_kernels.py         # SPICE kernels → pipeline/kernels/ (~19 MB)
```

`download_kernels.py` fetches the four kernels used in Skyfield's *Planetary reference
frames* guide from NAIF `generic_kernels`: `de421.bsp`, `moon_080317.tf`, `pck00008.tpc`,
and `moon_pa_de421_1900-2050.bpc`. NAIF keeps `de421.bsp` and `pck00008.tpc` in
`a_old_versions/` folders, and the script uses those official URLs. If a download fails,
the script names the file that failed and exits with an error. There are no fallback
mirrors.

## Terrain model (download manually)

Use a **LOLA south-polar DEM in polar stereographic projection, 60–120 m/px**, from the
PDS Geosciences Node:

<https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/data/lola_gdr/polar/float_img/>

Recommended file: **`ldem_75s_120m_float.img`** together with its label
**`ldem_75s_120m_float.lbl`** (233 MB, 75°S–90°S, 120 m/px). Download both into the
same folder and **pass the `.lbl` file** to `--dem`. GDAL opens PDS3 products through
their label.

Why 75°S: horizon rays are 150 km long, which is about 5° of latitude. A site at 84°S
therefore needs terrain coverage down to about 79°S. An 80°S product would cut those
rays short. The finer `ldem_75s_60m_float` also works, but it is 4× larger and slower.

The DEM loader reads CRS and geotransform from the file and hard-codes neither. Some
LOLA conventions are handled automatically:

* Heights in the `float` products are in **kilometres**. Units are auto-detected (values
  smaller than ±30 can only be km). Use `--dem-units m|km` to force the units.
* The label `OFFSET = 1737.4` is the reference-sphere radius, not a height offset. Only
  the scale factor is applied.

A GeoTIFF in metres (for example a PGDA high-resolution product) works too.

## Run

Quick test, one site (~1 s once the DEM is loaded):

```bash
python build.py --dem /path/to/ldem_75s_120m_float.lbl --site malapert_massif
```

All sites in `SeleneFrontend/wwwroot/data/sites.json`:

```bash
python build.py --dem /path/to/ldem_75s_120m_float.lbl
```

Other options: `--start/--end` (UTC, default 2027-01-01T00:00Z … 2028-12-31T23:00Z),
`--sites`, `--out`, `--observer-height` (m, default 2), `--max-distance-km` (default 150),
`--kernels`, `--dem-units`. When you run a single site, only that site's entry in
`index.json` is updated.

Tests:

```bash
python -m pytest -q                                          # synthetic DEM + ephemeris
SELENE_DEM=/path/to/ldem_75s_120m_float.lbl python -m pytest -q   # + real DEM check
```

## Landing-site selection

The nine regions are NASA's October 2024 list of Artemis III candidate regions. They are
~15 km wide. The specific landing points come from peer-reviewed work:

```bash
python import_wueller_sites.py                 # Zenodo → data/wueller2026_sites.csv (130 sites, 16 regions)
python select_sites.py --dem /path/to/ldem_75s_120m_float.lbl   # best site per region + rebuild
```

1. **Candidates.** The 130 candidate landing sites (Table A1) come from Wueller et al.
   (2026), *JGR Planets* 131(3), e2025JE009434, using the authors' georeferenced release
   on Zenodo ([doi:10.5281/zenodo.17084058](https://doi.org/10.5281/zenodo.17084058),
   CC-BY-4.0). The shapefile uses a south-polar stereographic projection with true scale
   at **−70°**. It is reprojected with the `.prj` shipped in the archive and
   cross-checked against the attribute lat/lon. All nine of our regions are covered,
   with 78 candidates in total, so the grid fallback (500 m spacing, 15 × 15 km around
   the old approximate centre, marked `approximate: true`) was **not** needed.
2. **Slope filter.** Slope is computed from the DEM (3 × 3 px window, Horn's method).
   Candidates above `--max-slope` (default **10°**) are rejected. **10° is our
   assumption, not a NASA requirement.** Note that 120 m pixels smooth out local
   slopes. No published candidate was rejected: all have ≤ 4° at 120 m/px, which is
   consistent with the authors' own ≤ 8° HLS-area filter.
3. **Score.** Scoring uses 2027-01-01 … 2028-12-31, hourly, with the same horizon and
   ephemeris code as `build.py` (`build.compute_series`). The metrics are:
   * **lit %**: hours with `sunFraction ≥ 50`
   * **Earth %**: hours with Earth > horizon + 0.5°
   * **longest darkness**: longest run with `sunFraction < 50`
   * **longest continuous light**
4. **Ranking.** Highest lit % first. Among candidates within 1 percentage point of the
   best, the highest Earth % wins, then the shortest longest-darkness.

`sites.json` gets the best point per region with these fields: `approximate: false`,
`source` (DOI + "best of N candidate sites by SELENE illumination score"),
`candidateId`, `regionCenter` (the previous approximate coordinate, kept stable across
re-runs), `candidatesEvaluated`, and `alternatives` (next three). Every evaluated point
is written to `illumination/candidates/{regionId}.json`, which the map overlay can use.

`index.json → pctSunlit` keeps its original definition (any sunlight, `sunFraction > 0`).
It is therefore higher than the selection's **lit %** (`≥ 50`).

## Method

| Step | File | What it does |
|---|---|---|
| Horizon mask | `horizon.py` | Casts 360 rays at 1° steps out to 150 km, sampled at the DEM resolution with bilinear heights. Ray points are generated on the lunar sphere (R = 1 737 400 m) by great-circle bearing, then projected into the DEM, so azimuths are **true** (from lunar north) and not grid azimuths. Each sample's elevation angle is `atan2(dh − d²/2R, d)` (curvature drop included), and each ray keeps the maximum. The observer stands at terrain + 2 m. |
| Sun / Earth positions | `ephemeris.py` | Skyfield with DE421 and the `MOON_ME_DE421` frame (`moon_080317.tf` + `moon_pa_de421_1900-2050.bpc`). Gives topocentric apparent altitude/azimuth, hourly. No refraction, because the Moon has no atmosphere. |
| Visibility | `illumination.py` | `sunFraction = clamp((sunElev − h + r) / 2r, 0, 1)` with r = 0.267°, where h is the horizon interpolated linearly at the Sun's azimuth. `earthVisible = earthElev > h + 0.5°`. |

**`sunFraction` is a linear approximation.** It ramps linearly while the disk centre moves
from one solar radius below the local horizon to one radius above it. The exact visible
area of a disk cut by a straight line is an S-curve with the same endpoints and the same
midpoint (0.5). The largest difference is 5.8 percentage points, at about 0.6 radius from
the horizon. Statistics based on "any sunlight" (`sunFraction > 0`) are unaffected.

Other assumptions:

* The Moon is treated as a sphere of 1737.4 km, the same reference that LOLA heights use.
* The horizon line between neighbouring 1° azimuths is linear.
* Terrain beyond 150 km, or outside the DEM, is ignored.
* `index.json → horizonCoveragePct` reports how much of each site's ray length had valid
  DEM data.

### Validation

* **Sun geometry.** Peak solar elevation at −85.8° comes out at 5.78°. The analytic
  value is (90° − 85.8°) + 1.54° = 5.74°. `tests/` checks this bound at several
  latitudes.
* **Azimuth convention.** A synthetic DEM with a mountain placed at true bearing 135°
  produces its horizon peak at 135°.
* **Known sunniest spot.** A search at DEM resolution along the Shackleton–de Gerlache
  connecting ridge finds a maximum of **89.7 % sunlit time over 2027–28 at
  (−89.438°, −137.28°)**. The literature reports ~89–92 % for the best ridge points
  (Mazarico et al. 2011, Icarus 211; Gläser et al. 2014), computed over different time
  spans and DEM resolutions. **This is not a like-for-like check.** NASA's own LOLA
  map at that exact point reads only 71.0 % (120 m) and 65.2 % (60 m) over 18.6 years
  (see [Validation vs NASA LOLA illumination maps](#validation-vs-nasa-lola-illumination-maps)).
  A narrow ridge is where our 120 m DEM and 2 m observer are most optimistic. The point
  comparison at the 9 landing sites below is the stronger validation.

## Validation vs JPL Horizons

`validate_horizons.py` checks the Sun and Earth positions against
[NASA/JPL Horizons](https://ssd.jpl.nasa.gov/horizons/), which is an independent
implementation running a newer ephemeris (DE441). The check covers all 9 sites at 24
epochs (2027-02-01, 2027-08-15, 2028-03-20 and 2028-10-05, 00–05 UTC hourly). Each
query asks for the apparent azimuth and elevation (`QUANTITIES='4'`) as seen from
`CENTER='coord@301'`, `COORD_TYPE='GEODETIC'`, `SITE_COORD='lon,lat,0'`. The raw
responses are stored in `data/horizons/`, so the check and `tests/test_horizons.py` run
offline.

```bash
python validate_horizons.py            # compare using the stored snapshots
python validate_horizons.py --refresh  # re-query the API (sends only public site coordinates)
```

**Conventions.** These were read from the Horizons response headers and confirmed by the
numbers below.

* The site coordinates are east longitude and latitude in the lunar **Mean-Earth (ME)**
  frame (`Center pole/equ : MEAN_ME (high precision)`). That is the same frame as our
  `MOON_ME_DE421`.
* The reference surface is a sphere with R = 1737.4 km, as in our model.
* Azimuth is measured clockwise from north. Elevation is airless (no refraction) and
  apparent.
* Times are UTC.

No conversion is needed. If the site is placed in the Principal-Axis frame instead, the
residuals grow by a factor of about 2000.

Results (pipeline or shipped JSON minus Horizons, absolute values, 378 samples per row: 7 dates
2026-11-20 … 2035-12-15, 6 hours each; re-run 2026-11-15 for the 2026–2035 data):

| Comparison | Elevation range | max \|Δel\| | mean \|Δel\| | max \|Δaz\| | mean \|Δaz\| |
|---|---:|---:|---:|---:|---:|
| Sun: pipeline (DE421, `MOON_ME_DE421`) | −7.6° … 7.9° | 0.000014° (0.052″) | 0.000006° (0.021″) | 0.000008° (0.028″) | 0.000003° (0.009″) |
| Earth: pipeline (DE421, `MOON_ME_DE421`) | −8.5° … 11.4° | 0.000014° (0.052″) | 0.000009° (0.033″) | 0.000009° (0.032″) | 0.000004° (0.013″) |
| Sun: shipped `illumination/*/*.json` (0.01° steps) | −7.6° … 7.9° | 0.0050° | 0.0024° | 0.0050° | 0.0026° |
| Earth: shipped `illumination/*/*.json` (0.01° steps) | −8.4° … 11.4° | 0.0050° | 0.0026° | 0.0050° | 0.0024° |
| *Sun: site in `MOON_PA_DE421` (wrong frame)* | | *0.0235°* | *0.0143°* | *0.0211°* | *0.0191°* |
| *Earth: site in `MOON_PA_DE421` (wrong frame)* | | *0.0236°* | *0.0219°* | *0.0214°* | *0.0186°* |

The pipeline agrees with Horizons to about 0.05″ over the whole range, up to the end of 2035. The shipped files agree to within their
0.01° rounding. The test uses a tolerance of **0.001°** for the pipeline. That is about
100× the observed residual, which leaves headroom if Horizons moves to a newer ephemeris.
It is still 20× tighter than the ME↔PA offset, so a frame or convention error fails the
test. The tolerance for the shipped JSON is **0.006°**: half a rounding step plus the
pipeline tolerance. The reasoning is written out in the test's docstring. For scale, the
horizon mask uses 1° azimuth steps and the solar radius is 0.267°.

## Validation vs NASA LOLA illumination maps

`compare_nasa_illumination.py` compares our sunlight and Earth visibility at the 9
landing sites with NASA's published LOLA polar illumination maps:

* **Source:** Mazarico, E., et al. (2011), *Illumination conditions of the lunar polar
  regions using LOLA topography*, Icarus 211, 1066–1081, doi:10.1016/j.icarus.2010.10.030.
  Maps are release 2016 on the PDS Geosciences Node
  ([files](https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/extras/illumination/img/),
  [PGDA product page](https://pgda.gsfc.nasa.gov/products/69)):
  * `AVGVISIB_75S_120M_201608`: 120 m/px, the same resolution as our DEM
  * `AVGVISIB_85S_060M_201608`: 60 m/px, south of −85° only
  * the `_EARTH` variants of both
* **What a pixel means:** per the PDS label, the share of hourly timesteps when the pixel was
  sunlit "by any fraction of the solar disc". The Earth maps use any part of Earth's disc.
  PGDA says the Sun maps cover a full lunar nutation cycle (~18.6 years). Neither page gives
  the exact dates or the observer height, which is most likely the surface.
* **Our side:**
  * The definition is matched: `sunFraction > 0`, the same as `index.json → pctSunlit`.
  * We use each site's stored horizon (120 m DEM, observer 2 m) with fresh JPL DE421
    positions over 18.6 years from 2009-07-13, the label's START_TIME.
  * We also report 2027–28, the period the app uses. That recomputation reproduces
    `index.json` to within 0.2 pp, which is tested.
  * For Earth, "any part of the disc" uses the angular radius from the actual Earth
    distance. The app's own comms criterion (centre > horizon + 0.5°) is shown alongside.
* **How the maps are read:** the labels name `*.IMG` in upper case, but the server stores
  lower case, so GDAL cannot open the remote pair. `--refresh` instead:
  1. downloads only the `.lbl`;
  2. lets GDAL read the map geometry from it;
  3. fetches the 3 × 3 pixels around each point with HTTP Range requests (~30 KB each).

  The samples are kept in `data/nasa_illumination/samples.csv`, so the script and
  `tests/test_nasa_comparison.py` run offline.
* **Orientation check:** the floors of Shackleton (−89.67°, 129.8°E) and Faustini
  (−87.3°, 77°E) read exactly 0 % in all four maps, as permanently shadowed regions should.
  Their mirror images across longitude 0 do not.

```bash
python compare_nasa_illumination.py            # offline: table, data/nasa_illumination/comparison.json, docs/nasa_vs_ours.png
python compare_nasa_illumination.py --refresh  # re-sample the NASA maps (public URLs only)
```

![Sunlit time: NASA LOLA map vs SELENE model](docs/nasa_vs_ours.png)

**Sunlight** (% of hours with any part of the solar disc visible):

| Site | NASA 120 m | NASA 3×3 px range | Ours 18.6 y | Δ vs NASA 120 m | Ours 2027–28 | NASA 60 m | Δ vs NASA 60 m |
|---|---:|---:|---:|---:|---:|---:|---:|
| Peak near Cabeus B | 52.5 % | 39.0–55.0 % | 54.3 % | **+1.8 pp** | 53.8 % | — | — |
| Haworth | 66.6 % | 47.7–67.7 % | 70.6 % | **+4.0 pp** | 70.4 % | 62.1 % | +8.5 pp |
| Malapert Massif | 69.8 % | 42.7–74.3 % | 81.2 % | **+11.4 pp** | 81.9 % | 64.0 % | +17.2 pp |
| Mons Mouton Plateau | 42.7 % | 41.4–42.8 % | 43.0 % | **+0.3 pp** | 42.7 % | — | — |
| Mons Mouton | 74.3 % | 37.3–78.4 % | 79.1 % | **+4.8 pp** | 79.5 % | 56.8 % | +22.3 pp |
| Nobile Rim 1 | 83.5 % | 52.5–83.5 % | 83.8 % | **+0.3 pp** | 84.3 % | 76.8 % | +7.0 pp |
| Nobile Rim 2 | 52.4 % | 29.3–52.4 % | 53.4 % | **+1.0 pp** | 53.8 % | — | — |
| de Gerlache Rim 2 | 78.5 % | 48.9–78.5 % | 79.4 % | **+0.9 pp** | 79.6 % | 72.5 % | +6.9 pp |
| Slater Plain | 58.3 % | 50.1–59.1 % | 60.1 % | **+1.8 pp** | 60.8 % | 52.9 % | +7.2 pp |

**Earth visibility** (% of hours):

| Site | NASA 120 m (any part of disc) | Ours 18.6 y (any part of disc) | Δ | Ours, app criterion (centre > horizon + 0.5°) |
|---|---:|---:|---:|---:|
| Peak near Cabeus B | 72.6 % | 83.5 % | +10.9 pp | 71.9 % |
| Haworth | 60.2 % | 61.1 % | +0.9 pp | 54.0 % |
| Malapert Massif | 65.2 % | 75.4 % | +10.2 pp | 66.7 % |
| Mons Mouton Plateau | 84.8 % | 84.9 % | +0.1 pp | 73.5 % |
| Mons Mouton | 64.4 % | 79.6 % | +15.2 pp | 69.9 % |
| Nobile Rim 1 | 64.2 % | 70.2 % | +6.0 pp | 62.3 % |
| Nobile Rim 2 | 66.3 % | 66.5 % | +0.2 pp | 58.9 % |
| de Gerlache Rim 2 | 61.3 % | 61.8 % | +0.5 pp | 54.6 % |
| Slater Plain | 40.2 % | 41.0 % | +0.8 pp | 33.5 % |

**What it shows:**

* **Like-for-like (NASA 120 m vs ours, both 120 m terrain):** 6 of 9 sites agree within 2 pp
  and 8 of 9 within 5 pp (median +1.8 pp). Malapert Massif is the outlier at +11.4 pp.
  Ours is never darker.
* **Sources of the remaining excess:**
  * Our observer stands 2 m above the terrain; NASA's height is not stated.
  * Our horizon rays stop at 150 km.
  * Our sites were picked as the sunniest candidate in each region on the same 120 m
    DEM, which favours pixels where that DEM is optimistic.
* **Finer terrain lowers the numbers.** Against the 60 m map ours is 7–22 pp higher.
  Peaks and massifs such as Mons Mouton and Malapert are affected most. The 3 × 3 px
  ranges show how steep the gradients are around those points: one pixel (120 m)
  can change sunlit time by 20–30 pp. Our numbers are therefore an upper bound, the same
  conclusion as the "120 m DEM optimism" caveat below.
* **Earth agrees within 1 pp at 5 sites.** Three of the four outliers (+10–15 pp) are
  summits or massifs (Cabeus B, Malapert, Mons Mouton), where a 2 m observer and smoothed
  terrain matter most for a target only a few degrees above the horizon. Nobile Rim 1 is
  +6.0 pp.
* **Period effect is small:** at all 9 sites 2027–28 is within 1 pp of the 18.6-year
  average.

## Validation vs NASA statement

NASA's 2022 release states that *"All 13 regions contain sites that provide continuous
access to sunlight throughout a 6.5-day period – the planned duration of the Artemis III
surface mission"*
([nasa.gov](https://www.nasa.gov/news-release/nasa-identifies-candidate-regions-for-landing-next-americans-on-moon/)).
The statement covers the 2022 list of 13 regions. Only **5 of our 9** are on that list.
Mons Mouton, Mons Mouton Plateau, Peak near Cabeus B and Slater Plain were added in the
October 2024 update. For those four the same 6.5-day test is **our planning criterion,
not a NASA claim**.

Test (`tests/test_site_selection.py`): for the selected site of each region, 2027–28 must
contain at least one uninterrupted window of **≥ 156 h** with `sunFraction ≥ 50`.

| Region | Selected site (lat, lon) | Lit % (≥ 50) | Earth % | Longest continuous light | ≥ 6.5 d | Basis |
|---|---|---:|---:|---:|:---:|---|
| Peak near Cabeus B | `PCB04` (-83.3691, -67.6646) | 53.0 | 70.2 | 412 h (17.2 d) | ✅ yes | our criterion (region added 2024) |
| Haworth | `HW_K2023` (-86.9895, -24.2545) | 65.2 | 55.3 | 696 h (29.0 d) | ✅ yes | NASA 2022 statement |
| Malapert Massif | `MMA_K2023` (-85.9919, 2.0564) | 78.2 | 67.4 | 4,061 h (169.2 d) | ✅ yes | NASA 2022 statement |
| Mons Mouton Plateau | `MMP10` (-84.1383, 25.6422) | 41.5 | 73.9 | 358 h (14.9 d) | ✅ yes | our criterion (region added 2024) |
| Mons Mouton | `MMO02` (-85.4810, 29.5494) | 75.2 | 70.8 | 1,403 h (58.5 d) | ✅ yes | our criterion (region added 2024) |
| Nobile Rim 1 | `NR103` (-85.5456, 37.5572) | 78.9 | 62.4 | 3,883 h (161.8 d) | ✅ yes | NASA 2022 statement |
| Nobile Rim 2 | `NR205` (-83.6185, 59.0568) | 52.2 | 60.6 | 427 h (17.8 d) | ✅ yes | NASA 2022 statement |
| de Gerlache Rim 2 | `dGR2_K2023` (-88.4500, -67.8591) | 76.5 | 54.9 | 662 h (27.6 d) | ✅ yes | NASA 2022 statement |
| Slater Plain | `SP09` (-87.1838, 133.3445) | 59.0 | 33.7 | 468 h (19.5 d) | ✅ yes | our criterion (region added 2024) |

**All nine regions pass.** The weakest is Mons Mouton Plateau, at 14.9 days of continuous
light: 2.3× the requirement.

Caveats that could lower these numbers:

* The DEM is 120 m/px. Small-scale relief such as boulders, small craters and local
  ridges is smoothed out, which tends to overestimate sunlight.
* The observer stands 2 m above the terrain (panel height). Lower would mean more
  shadowing.
* The time span is 2027–28, not a full 18.6-year nutation cycle.
* The solar disk uses the linear approximation described above.

### Consistency with Wueller et al. (2026)

The source shapefile also carries the authors' own illumination counts per year
(`SunDays27`, `SunDays28`). They are copied into `data/wueller2026_sites.csv`. Their
values never exceed 32. That suggests the counts cover a fixed "midsummer" window,
which matches the dataset description ("illumination conditions … for the midsummers"),
so check the exact definition in the paper. Our number is the longest continuous light
across the **whole** year, so we expect ours ≥ theirs.

For the 9 selected sites × 2 years, this holds in **15 of 18** cases. The three
exceptions differ by 0.5–2.3 days (HW_K2023 2027, SP09 2027, NR205 2028), which is
plausible given their much finer DEMs. This is a consistency check, not a
like-for-like validation.

## Landing ellipse

Landers miss their target. Blue Ghost Mission 1 touched down within its 100 m target, Nova-C IM-2 more than
400 m off (NASA), and NASA requires Artemis landers to land within 100 m of a target. Near the pole one
120 m pixel can decide between a lit ridge and a shadow, so the app re-checks the verdict over the whole
landing area instead of a single point.

```bash
python build_ellipse.py --dem /path/to/ldem_75s_120m_float.lbl              # all sites, ~30 s per site
python build_ellipse.py --dem /path/to/ldem_75s_120m_float.lbl --validate   # + the checks below
```

`build_ellipse.py` writes `illumination/{siteId}/ellipse.json`: a 60 m grid (half a DEM pixel, bilinear
heights) of 197 points within 480 m of each site, the largest landing radius in `landers.json`. Each point
stores its own horizon mask and three frame numbers; no hourly series. The frontend rebuilds sunFraction and
earthVisible at every point from the **site's** Sun and Earth series and re-runs the shown mission there
(`LandingEllipse.cs`, same heading, date and duration).

Why that is enough (`ellipse.py`): the Sun and the Earth are so far away that their direction is the same
480 m away. What changes is the terrain horizon and the local frame. Near the pole, local north turns by
up to ~0.6° over 480 m (meridian convergence) and the vertical tilts by d/R = 0.016°. The point's mask stays
in its own 1° grid and the site's azimuth is shifted into it (`azShiftDeg`), and the elevation gets the tilt
term (`tiltDeg`, `tiltAzDeg`). An earlier version resampled each mask into the site frame; that second
interpolation smoothed sharp horizon features and cost up to 15 pp in single hours.

`--validate` recomputes 5 points per site (0, 480 m E, 480 m S, 424 m NW, 170 m NE) directly, with their own
ephemeris and horizon, over 2027–28 (17,544 h), and compares with the reconstruction from the shipped site
series (`docs/ellipse_validation.json`). It also compares our sunlit range over the 3×3 points 120 m apart
(2027–28, any Sun) with the NASA LOLA map's 3×3-pixel range (18.6 years):

| Site | Max Δ sunFraction | Hours > 1 pp | Earth hours differing | Ours ±120 m | NASA 3×3 px |
|---|---|---|---|---|---|
| Peak near Cabeus B | 2 pp | 0.1% | 5 | 49.1–56.0% | 39.0–55.0% |
| Haworth | 2 pp | 0.61% | 7 | 53.8–70.5% | 47.7–67.7% |
| Malapert Massif | 2 pp | 0.34% | 6 | 53.5–83.7% | 42.7–74.3% |
| Mons Mouton Plateau | 2 pp | 0.15% | 3 | 41.5–43.1% | 41.4–42.8% |
| Mons Mouton | 2 pp | 0.54% | 4 | 56.9–81.6% | 37.3–78.4% |
| Nobile Rim 1 | 2 pp | 0.39% | 4 | 54.9–84.2% | 52.5–83.5% |
| Nobile Rim 2 | 2 pp | 0.15% | 5 | 45.4–53.8% | 29.3–52.4% |
| de Gerlache Rim 2 | 2 pp | 0.39% | 4 | 48.9–79.7% | 48.9–78.5% |
| Slater Plain | 2 pp | 0.2% | 4 | 50.7–65.0% | 50.1–59.1% |

* The reconstruction error (≤ 2 pp, a few Earth hours out of 17,544) is the same as at the site itself:
  it comes from the centi-degree rounding of the shipped files, not from the method.
* Against NASA, the upper ends agree within 0–9 pp, and Nobile Rim 1, de Gerlache Rim 2, Mons Mouton
  Plateau and Haworth match closely. Our lower ends are often higher than NASA's (Mons Mouton 57% vs
  37%). The NASA pixels are not the same points (map grid vs our east/north grid), the period differs
  (18.6 years vs 2027–28), and the 120 m DEM smooths small ridges. Read the ellipse as "at least this
  sensitive", not as an upper bound on the risk.
* Slopes come from the 120 m DEM (Horn, 3×3 pixels) and are smoother than the real surface.

## Output format

`illumination/{siteId}/{year}.json` (one file per site and calendar year, 2026-11 … 2035; same fields in every file):

```jsonc
{
  "siteId": "malapert_massif", "lat": -85.8, "lon": -3.5, "observerHeightM": 2,
  "start": "2027-01-01T00:00:00Z", "stepHours": 1, "count": 8760,   // one calendar year; 2026 starts 2026-11-01
  "sunFraction": [0..100],            // % of solar disk visible (int)
  "sunElevCentiDeg": [int],           // Sun elevation × 100
  "sunAzCentiDeg": [int],             // Sun azimuth × 100, clockwise from north
  "earthVisible": [0|1],
  "earthElevCentiDeg": [int],
  "earthAzCentiDeg": [int],           // Earth azimuth × 100 (added 2026-11-14)
  "horizonDeg": [360 floats],         // horizon elevation at azimuth 0..359°
  "generatedWith": { "dem": "...", "kernels": [...], "method": "linear-disk-approx" }
}
```

`illumination/index.json` lists every site with `pctSunlit` (share of hours with
`sunFraction > 0`), `pctComms`, `longestDarkHours`, and `horizonCoveragePct`.

`illumination/{siteId}/ellipse.json` (`build_ellipse.py`, optional for the app):

```jsonc
{
  "siteId": "malapert_massif", "lat": -85.99, "lon": 2.06, "observerHeightM": 2,
  "spacingM": 60, "radiusM": 480,
  "points": [{
    "e": -120, "n": 60,                      // offset east / north of the site (m)
    "lat": ..., "lon": ..., "groundM": ..., "slopeDeg": 3.1,
    "azShiftDeg": -0.06, "tiltDeg": 0.0045, "tiltAzDeg": 296.6,   // site frame → point frame
    "pctSunlit": 61.2, "pctComms": 74.0,     // whole data range, like index.json
    "horizonCentiDeg": [360 ints]            // the point's own horizon × 100, azimuth in its own frame
  }],
  "generatedWith": { "dem": "...", "method": "own-frame horizon + azShift/tilt (pipeline/ellipse.py)" }
}
```

## Data sources

* Candidate landing sites: Wueller, L., Berger, L. M., Christopher, H., et al. (2026),
  *JGR Planets* 131(3), e2025JE009434, doi:10.1029/2025JE009434. Data:
  doi:10.5281/zenodo.17084058 (CC-BY-4.0).
* LOLA gridded topography: Smith et al., LRO LOLA RDR/GDR, NASA PDS Geosciences Node.
* Ephemeris and lunar orientation kernels: NASA/JPL NAIF generic kernels.
* Skyfield: Rhodes, B., *Skyfield: High precision research-grade positions for planets
  and Earth satellites*.
