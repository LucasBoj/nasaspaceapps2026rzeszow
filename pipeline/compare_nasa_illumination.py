"""Compare our illumination model with NASA's LOLA polar illumination maps.

NASA product: LOLA "average visibility" maps, release 2016, described in
Mazarico, E., Neumann, G. A., Smith, D. E., Zuber, M. T., Torrence, M. H. (2011),
"Illumination conditions of the lunar polar regions using LOLA topography",
Icarus 211, 1066-1081, doi:10.1016/j.icarus.2010.10.030.
Product page: https://pgda.gsfc.nasa.gov/products/69 . Files (PDS Geosciences Node):
https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/extras/illumination/img/

* ``AVGVISIB_85S_060M_201608`` (60 m/px, 85°S-90°S) and ``AVGVISIB_75S_120M_201608``
  (120 m/px, 75°S-90°S), plus the ``_EARTH`` variants.
* Pixel value (label): "the percentage of timesteps where each pixel was sunlit (by
  any fraction of the solar disc)", 0..1 = DN x 0.00004. Earth maps: "average Earth
  disc visibility". The PGDA page describes the Sun maps as hourly over a full lunar
  nutation cycle (~18.6 years). Neither page states the observer height or the exact dates.
* Grid: polar stereographic (spherical, R = 1737.4 km), true scale at the pole,
  Mean-Earth/polar-axis frame of DE421 (the same frame as ``ephemeris.MOON_FRAME``).

How we fetch it: the PDS labels name the image ``AVGVISIB_*.IMG`` in upper case, but
the server stores lower-case names, so GDAL cannot open the remote label/image pair.
``--refresh`` therefore (1) downloads only the small ``.lbl``, (2) lets GDAL read the
map geometry from it next to an empty local placeholder of the right size, and
(3) fetches the 3x3 pixels around each point with HTTP Range requests (~30 KB per
point and map). The samples are stored in ``data/nasa_illumination/samples.csv``,
so everything else, including the tests, runs offline.

Our side, for the 9 landing sites in ``sites.json``: the stored 360° horizon mask
(``illumination/{siteId}/{year}.json -> horizonDeg``, observer 2 m above the 120 m LOLA DEM)
combined with fresh Sun/Earth positions from ``ephemeris.MoonSky``:

* ``ours18`` - share of hours with **any** part of the solar disc above the horizon
  (``sunFraction > 0``, the NASA definition) over 18.6 years starting at the label's
  START_TIME (2009-07-13). NASA's exact window is not published on the product pages;
  a whole nutation cycle averages out the start date.
* ``ours2027`` - the same metric over 2027-2028, i.e. ``index.json -> pctSunlit`` as
  used by the app.
* Earth: ``earthAny`` = any part of Earth's disc above the horizon (NASA definition,
  angular radius from the actual distance) and ``earthApp`` = Earth's centre more than
  0.5° above the horizon (the app's comms criterion).

Usage (from ``pipeline/``)::

    python compare_nasa_illumination.py            # offline: snapshot + our model -> table, JSON, chart
    python compare_nasa_illumination.py --refresh  # re-sample NASA maps over HTTP (public URLs only)
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
import tempfile
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np

import build
import ephemeris
import illumination

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
PDS_DIR = "https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/extras/illumination/img/"
PGDA_URL = "https://pgda.gsfc.nasa.gov/products/69"
CITATION = ("Mazarico, E., et al. (2011), Illumination conditions of the lunar polar regions using LOLA "
            "topography, Icarus 211, 1066-1081, doi:10.1016/j.icarus.2010.10.030")
SNAPSHOT_DIR = HERE / "data" / "nasa_illumination"
SAMPLES_CSV = SNAPSHOT_DIR / "samples.csv"
COMPARISON_JSON = SNAPSHOT_DIR / "comparison.json"
CHART_PNG = HERE / "docs" / "nasa_vs_ours.png"
FRONTEND_JSON = REPO / "SeleneFrontend" / "wwwroot" / "data" / "validation" / "nasa_illumination.json"
ILLUMINATION_DIR = build.DEFAULT_OUT

# (product key, file stem, latitude limit of full coverage): the 85S map is used only south of -85°.
PRODUCTS = [
    ("sun60", "avgvisib_85s_060m_201608", -85.0),
    ("sun120", "avgvisib_75s_120m_201608", -75.0),
    ("earth60", "avgvisib_85s_060m_201608_earth", -85.0),
    ("earth120", "avgvisib_75s_120m_201608_earth", -75.0),
]
DN_SCALE = 0.00004

# Reference points that check the sampling and the map orientation (not compared with our model):
# two permanently shadowed crater floors must read 0 (their mirror images across lon 0 do not),
# plus our sunniest ridge point from pipeline/README.md for information.
REFERENCE_POINTS = [
    ("ref_shackleton_floor", "Shackleton crater floor (PSR)", -89.67, 129.8),
    ("ref_faustini_floor", "Faustini crater floor (PSR)", -87.3, 77.0),
    ("ref_ridge", "Shackleton-de Gerlache ridge (our 2027-28 maximum)", -89.438, -137.28),
]

PERIOD_START = datetime(2009, 7, 13, tzinfo=timezone.utc)   # label START_TIME (date)
PERIOD_DAYS = 18.6 * 365.25
EARTH_RADIUS_KM = 6371.0


# ----------------------------------------------------------------------------- NASA maps

@dataclass
class MapGeometry:
    crs: object
    transform: object
    width: int
    height: int
    record_bytes: int


def _http_get(url: str, byte_range: tuple[int, int] | None = None) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "selene-ops-pipeline"})
    if byte_range is not None:
        req.add_header("Range", f"bytes={byte_range[0]}-{byte_range[1]}")
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = resp.read()
        if byte_range is not None and resp.status != 206:
            raise RuntimeError(f"{url}: server ignored the Range request (HTTP {resp.status})")
        return data


def map_geometry(stem: str) -> MapGeometry:
    """Map geometry as GDAL reads it from the PDS label (no image download)."""
    import rasterio

    label = _http_get(PDS_DIR + stem + ".lbl").decode("ascii", errors="replace")
    fields = {}
    for key in ("^IMAGE", "FILE_RECORDS", "RECORD_BYTES"):
        line = next(l for l in label.splitlines() if l.strip().startswith(key))
        fields[key] = line.split("=", 1)[1].strip().strip('"')
    with tempfile.TemporaryDirectory() as tmp:
        lbl = Path(tmp) / (stem.upper() + ".LBL")
        lbl.write_text(label, encoding="ascii")
        img = Path(tmp) / fields["^IMAGE"]
        with open(img, "wb") as f:   # sparse placeholder so GDAL accepts the label
            f.truncate(int(fields["FILE_RECORDS"]) * int(fields["RECORD_BYTES"]))
        with rasterio.open(lbl) as ds:
            return MapGeometry(ds.crs, ds.transform, ds.width, ds.height, int(fields["RECORD_BYTES"]))


def pixel_of(geom: MapGeometry, lat: float, lon: float) -> tuple[int, int]:
    """(row, col) of the pixel containing a planetocentric lat/lon (east longitude)."""
    from pyproj import CRS, Transformer

    crs = CRS.from_user_input(geom.crs.to_wkt())
    to_map = Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True)
    x, y = to_map.transform(lon, lat)
    col, row = ~geom.transform @ (x, y)
    return int(math.floor(row)), int(math.floor(col))


def window_3x3(stem: str, geom: MapGeometry, row: int, col: int) -> np.ndarray:
    """3x3 block of visibility values (0..1) centred on (row, col), via HTTP Range."""
    if not (1 <= row < geom.height - 1 and 1 <= col < geom.width - 1):
        raise ValueError(f"{stem}: pixel ({row}, {col}) is outside the map")
    start = (row - 1) * geom.record_bytes
    data = _http_get(PDS_DIR + stem + ".img", (start, start + 3 * geom.record_bytes - 1))
    rows = np.frombuffer(data, dtype="<i2").reshape(3, geom.record_bytes // 2)
    return rows[:, col - 1:col + 2].astype(np.float64) * DN_SCALE


def points() -> list[tuple[str, str, float, float]]:
    sites = json.loads(build.DEFAULT_SITES.read_text(encoding="utf-8"))
    return [(s["id"], s["name"], s["lat"], s["lon"]) for s in sites] + REFERENCE_POINTS


def refresh_samples() -> None:
    SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    geoms = {key: map_geometry(stem) for key, stem, _ in PRODUCTS}
    rows = []
    for pid, name, lat, lon in points():
        for key, stem, lat_limit in PRODUCTS:
            if lat > lat_limit:
                continue
            r, c = pixel_of(geoms[key], lat, lon)
            w = window_3x3(stem, geoms[key], r, c)
            rows.append({"pointId": pid, "name": name, "lat": lat, "lon": lon, "product": key, "file": stem + ".img",
                         "row": r, "col": c, "center": round(float(w[1, 1]), 5),
                         "min3x3": round(float(w.min()), 5), "max3x3": round(float(w.max()), 5)})
            print(f"  {pid:22s} {key:8s} {w[1, 1]:.4f}  (3x3 {w.min():.4f}..{w.max():.4f})")
    fetched = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%MZ")
    with open(SAMPLES_CSV, "w", newline="", encoding="utf-8") as f:
        f.write(f"# NASA LOLA average visibility maps (release 2016), sampled {fetched}\n")
        f.write(f"# Source: {PDS_DIR}  (product page {PGDA_URL})\n")
        f.write(f"# Citation: {CITATION}\n")
        f.write("# Values 0..1 = share of hourly timesteps with any part of the Sun's (or Earth's) disc visible.\n")
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"wrote {SAMPLES_CSV.relative_to(REPO)} ({len(rows)} samples)")


def read_samples(path: Path = SAMPLES_CSV) -> list[dict]:
    with open(path, encoding="utf-8") as f:
        lines = [l for l in f if not l.startswith("#")]
    rows = list(csv.DictReader(lines))
    for r in rows:
        for k in ("lat", "lon", "center", "min3x3", "max3x3"):
            r[k] = float(r[k])
    return rows


# ----------------------------------------------------------------------------- our model

def our_visibility(sky: ephemeris.MoonSky, horizon_deg: np.ndarray, lat: float, lon: float,
                   start: datetime, days: float) -> dict:
    """Shares (0..1) of hours with any Sun / any Earth / Earth > horizon + 0.5° over the period."""
    end = start + timedelta(days=days) - timedelta(hours=1)
    times = sky.times(start, end, 1.0)
    sun_el, sun_az = sky.altaz(lat, lon, times, "sun")
    sun_any = illumination.sun_fraction(sun_el, sun_az, horizon_deg) > 0

    site = sky.ephemeris["moon"] + sky.constants.build_latlon_degrees(sky.frame, lat, lon)
    earth_alt, earth_az, earth_dist = site.at(times).observe(sky.ephemeris["earth"]).apparent().altaz()
    e_el, e_az = earth_alt.degrees, earth_az.degrees
    e_radius = np.degrees(np.arcsin(EARTH_RADIUS_KM / earth_dist.km))
    h = illumination.horizon_at(horizon_deg, e_az)
    return {
        "sunAny": float(sun_any.mean()),
        "earthAny": float((e_el + e_radius > h).mean()),
        "earthApp": float(illumination.earth_visible(e_el, e_az, horizon_deg).mean()),
        "hours": int(len(sun_any)),
    }


def compare(sky: ephemeris.MoonSky | None = None) -> dict:
    sky = sky or ephemeris.load_moon_sky()
    samples = read_samples()
    index = json.loads((ILLUMINATION_DIR / "index.json").read_text(encoding="utf-8"))
    by_site = {s["siteId"]: s for s in index["sites"]}
    sites = []
    for site in json.loads(build.DEFAULT_SITES.read_text(encoding="utf-8")):
        sid = site["id"]
        any_year = next(iter(by_site[sid]["files"].values()))   # horizonDeg is the same in every yearly file
        doc = json.loads((ILLUMINATION_DIR / any_year).read_text(encoding="utf-8"))
        horizon_deg = np.asarray(doc["horizonDeg"], dtype=np.float64)
        ours18 = our_visibility(sky, horizon_deg, site["lat"], site["lon"], PERIOD_START, PERIOD_DAYS)
        ours27 = our_visibility(sky, horizon_deg, site["lat"], site["lon"],
                                datetime(2027, 1, 1, tzinfo=timezone.utc), 731)
        nasa = {r["product"]: r for r in samples if r["pointId"] == sid}
        # Like-for-like: the NASA 120 m map uses the same DEM resolution as our horizons (LDEM 120 m).
        # The 60 m map (south of -85° only) shows how much finer terrain changes the answer.
        entry = {
            "siteId": sid, "name": site["name"], "lat": site["lat"], "lon": site["lon"],
            "nasaSunPct": pct(nasa["sun120"]["center"]),
            "nasaSunRangePct": [pct(nasa["sun120"]["min3x3"]), pct(nasa["sun120"]["max3x3"])],
            "nasaSunPct60": pct(nasa["sun60"]["center"]) if "sun60" in nasa else None,
            "oursSunPct18y": pct(ours18["sunAny"]),
            "oursSunPct2027": pct(ours27["sunAny"]),
            "nasaEarthPct": pct(nasa["earth120"]["center"]),
            "nasaEarthPct60": pct(nasa["earth60"]["center"]) if "earth60" in nasa else None,
            "oursEarthAnyPct18y": pct(ours18["earthAny"]),
            "oursEarthAppPct18y": pct(ours18["earthApp"]),
        }
        entry["deltaSunPct"] = round(entry["oursSunPct18y"] - entry["nasaSunPct"], 1)
        entry["deltaSunPct60"] = (round(entry["oursSunPct18y"] - entry["nasaSunPct60"], 1)
                                  if entry["nasaSunPct60"] is not None else None)
        entry["deltaEarthPct"] = round(entry["oursEarthAnyPct18y"] - entry["nasaEarthPct"], 1)
        sites.append(entry)
        print(f"  {sid:20s} NASA 120 m {entry['nasaSunPct']:5.1f}%  ours(18.6 y) {entry['oursSunPct18y']:5.1f}%  "
              f"d {entry['deltaSunPct']:+5.1f} pp | Earth NASA {entry['nasaEarthPct']:5.1f}% ours {entry['oursEarthAnyPct18y']:5.1f}%")
    refs = [{"pointId": r["pointId"], "name": r["name"], "lat": r["lat"], "lon": r["lon"], "product": r["product"],
             "pct": pct(r["center"])} for r in samples if r["pointId"].startswith("ref_")]
    return {
        "source": "NASA LOLA polar illumination maps (average visibility, release 2016)",
        "citation": CITATION,
        "url": PGDA_URL,
        "files": PDS_DIR,
        "definition": "share of hourly timesteps with any part of the Sun's (Earth's) disc above the local horizon",
        "nasaPeriod": "hourly over a full lunar nutation cycle (~18.6 years), exact dates not published on the product pages",
        "oursPeriod18y": f"{PERIOD_START:%Y-%m-%d} + 18.6 years, hourly",
        "oursPeriod2027": "2027-01-01 .. 2028-12-31, hourly (as in the app)",
        "oursModel": "LOLA LDEM 120 m horizon (150 km rays), observer 2 m above terrain, JPL DE421 via Skyfield",
        "sites": sites,
        "referencePoints": refs,
    }


def pct(v: float) -> float:
    return round(100.0 * float(v), 1)


# ----------------------------------------------------------------------------- outputs

def markdown_table(result: dict) -> str:
    lines = ["| Site | NASA 120 m | NASA 3×3 px range | Ours 18.6 y | Δ vs NASA 120 m | Ours 2027–28 | NASA 60 m | Δ vs NASA 60 m |",
             "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for s in result["sites"]:
        n60 = f"{s['nasaSunPct60']:.1f} %" if s["nasaSunPct60"] is not None else "—"
        d60 = f"{s['deltaSunPct60']:+.1f} pp" if s["deltaSunPct60"] is not None else "—"
        lines.append(f"| {s['name']} | {s['nasaSunPct']:.1f} % | {s['nasaSunRangePct'][0]:.1f}–{s['nasaSunRangePct'][1]:.1f} % "
                     f"| {s['oursSunPct18y']:.1f} % | **{s['deltaSunPct']:+.1f} pp** | {s['oursSunPct2027']:.1f} % | {n60} | {d60} |")
    lines += ["", "| Site | Earth NASA 120 m (any part of disc) | Earth ours 18.6 y (any part of disc) | Δ | Earth ours, app criterion (centre > horizon + 0.5°) |",
              "|---|---:|---:|---:|---:|"]
    for s in result["sites"]:
        lines.append(f"| {s['name']} | {s['nasaEarthPct']:.1f} % | {s['oursEarthAnyPct18y']:.1f} % | {s['deltaEarthPct']:+.1f} pp "
                     f"| {s['oursEarthAppPct18y']:.1f} % |")
    lines += ["", "| Reference point | Map | NASA value |", "|---|---|---:|"]
    for r in result["referencePoints"]:
        lines.append(f"| {r['name']} ({r['lat']}, {r['lon']}) | {r['product']} | {r['pct']:.1f} % |")
    return "\n".join(lines)


def chart(result: dict, path: Path = CHART_PNG) -> None:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    sites = result["sites"]
    names = [s["name"] for s in sites]
    x = np.arange(len(sites))
    nasa = np.array([s["nasaSunPct"] for s in sites])
    lo = nasa - np.array([s["nasaSunRangePct"][0] for s in sites])
    hi = np.array([s["nasaSunRangePct"][1] for s in sites]) - nasa
    ours = np.array([s["oursSunPct18y"] for s in sites])
    ours27 = np.array([s["oursSunPct2027"] for s in sites])

    nasa60 = np.array([s["nasaSunPct60"] if s["nasaSunPct60"] is not None else np.nan for s in sites])

    fig, ax = plt.subplots(figsize=(10, 5.6), dpi=150)
    w = 0.27
    ax.bar(x - w, nasa, w, yerr=[lo, hi], capsize=2,
           label="NASA LOLA map 120 m (Mazarico et al. 2011), whiskers: 3×3 px range", color="#4fc3f7")
    ax.bar(x, ours, w, label="SELENE model, 18.6-year average (120 m DEM, 2 m observer)", color="#ffb020")
    ax.bar(x + w, ours27, w, label="SELENE model, 2027–28 (used in the app)", color="#ffb020", alpha=0.45)
    ax.scatter(x - w, nasa60, marker="D", s=22, color="#7a3fb0", edgecolors="white", linewidths=0.6, zorder=3,
               label="NASA LOLA map 60 m (only south of −85°)")
    for i, s in enumerate(sites):
        ax.text(x[i], max(nasa[i] + hi[i], ours[i], ours27[i]) + 1.5, f"{s['deltaSunPct']:+.1f}",
                ha="center", fontsize=8)
    ax.set_xticks(x, names, rotation=25, ha="right", fontsize=8)
    ax.set_ylabel("time with any sunlight [%]")
    ax.set_ylim(0, 105)
    ax.set_title("Sunlit time at the 9 landing sites: NASA LOLA illumination map vs SELENE model\n"
                 "(numbers: ours 18.6 y − NASA 120 m, percentage points)", fontsize=10)
    ax.legend(fontsize=7, loc="upper center", bbox_to_anchor=(0.5, -0.30), ncol=2, frameon=False)
    ax.grid(axis="y", alpha=0.3)
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path)
    plt.close(fig)


def write_outputs(result: dict) -> None:
    SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    COMPARISON_JSON.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    FRONTEND_JSON.parent.mkdir(parents=True, exist_ok=True)
    FRONTEND_JSON.write_text(json.dumps(result, separators=(",", ":"), ensure_ascii=False) + "\n", encoding="utf-8")
    chart(result)
    print(f"wrote {COMPARISON_JSON.relative_to(REPO)}, {FRONTEND_JSON.relative_to(REPO)}, {CHART_PNG.relative_to(REPO)}")


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--refresh", action="store_true", help="re-sample the NASA maps over HTTP (public URLs only)")
    args = p.parse_args(argv)
    if args.refresh or not SAMPLES_CSV.exists():
        print("sampling NASA LOLA illumination maps ...")
        refresh_samples()
    print("computing our model (18.6 years and 2027-28) ...")
    result = compare()
    write_outputs(result)
    print()
    print(markdown_table(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
