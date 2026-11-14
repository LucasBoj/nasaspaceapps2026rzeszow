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
  spans and DEM resolutions.

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

Results (pipeline or shipped JSON minus Horizons, absolute values, 216 samples per row):

| Comparison | Elevation range | max \|Δel\| | mean \|Δel\| | max \|Δaz\| | mean \|Δaz\| |
|---|---:|---:|---:|---:|---:|
| Sun: pipeline (DE421, `MOON_ME_DE421`) | −7.6° … 6.2° | 0.000010° (0.036″) | 0.000005° (0.018″) | 0.000008° (0.028″) | 0.000004° (0.013″) |
| Earth: pipeline (DE421, `MOON_ME_DE421`) | −8.5° … 11.4° | 0.000010° (0.035″) | 0.000007° (0.024″) | 0.000009° (0.032″) | 0.000005° (0.017″) |
| Sun: shipped `illumination/*.json` (0.01° steps) | −7.6° … 6.2° | 0.0050° | 0.0025° | 0.0050° | 0.0025° |
| Earth: shipped `illumination/*.json` (0.01° steps) | −8.4° … 11.4° | 0.0050° | 0.0027° | 0.0050° | 0.0024° |
| *Sun: site in `MOON_PA_DE421` (wrong frame)* | | *0.0227°* | *0.0138°* | *0.0211°* | *0.0190°* |
| *Earth: site in `MOON_PA_DE421` (wrong frame)* | | *0.0236°* | *0.0219°* | *0.0211°* | *0.0185°* |

The pipeline agrees with Horizons to about 0.04″. The shipped files agree to within their
0.01° rounding. The test uses a tolerance of **0.001°** for the pipeline. That is about
100× the observed residual, which leaves headroom if Horizons moves to a newer ephemeris.
It is still 20× tighter than the ME↔PA offset, so a frame or convention error fails the
test. The tolerance for the shipped JSON is **0.006°**: half a rounding step plus the
pipeline tolerance. The reasoning is written out in the test's docstring. For scale, the
horizon mask uses 1° azimuth steps and the solar radius is 0.267°.

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

## Output format

`illumination/{siteId}.json` (one file per site):

```jsonc
{
  "siteId": "malapert_massif", "lat": -85.8, "lon": -3.5, "observerHeightM": 2,
  "start": "2027-01-01T00:00:00Z", "stepHours": 1, "count": 17544,
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

## Data sources

* Candidate landing sites: Wueller, L., Berger, L. M., Christopher, H., et al. (2026),
  *JGR Planets* 131(3), e2025JE009434, doi:10.1029/2025JE009434. Data:
  doi:10.5281/zenodo.17084058 (CC-BY-4.0).
* LOLA gridded topography: Smith et al., LRO LOLA RDR/GDR, NASA PDS Geosciences Node.
* Ephemeris and lunar orientation kernels: NASA/JPL NAIF generic kernels.
* Skyfield: Rhodes, B., *Skyfield: High precision research-grade positions for planets
  and Earth satellites*.
