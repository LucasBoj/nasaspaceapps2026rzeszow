# SELENE illumination pipeline

One-off generator of per-site **sunlight** and **Earth-visibility** time series for the
SeleneFrontend app. For each candidate landing site it computes a terrain horizon from
NASA LOLA topography, then evaluates the Sun and Earth against that horizon every hour
from 2027-01-01 to 2028-12-31.

Output goes to `SeleneFrontend/wwwroot/data/illumination/`.

## Setup

Python 3.11+.

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
  "horizonDeg": [360 floats],         // horizon elevation at azimuth 0..359°
  "generatedWith": { "dem": "...", "kernels": [...], "method": "linear-disk-approx" }
}
```

`illumination/index.json` lists every site with `pctSunlit` (share of hours with
`sunFraction > 0`), `pctComms`, `longestDarkHours`, and `horizonCoveragePct`.

## Data sources

* LOLA gridded topography: Smith et al., LRO LOLA RDR/GDR, NASA PDS Geosciences Node.
* Ephemeris and lunar orientation kernels: NASA/JPL NAIF generic kernels.
* Skyfield: Rhodes, B., *Skyfield: High precision research-grade positions for planets
  and Earth satellites*.
