"""Our model at the NASA LOLA illumination maps' own pixel centres (like-for-like check of the method).

``compare_nasa_illumination.py`` compares the app's numbers (our exact landing points, observer 2 m) with the NASA map
pixel that contains each point. That mixes three things: the method, the point (a pixel centre can be 85 m away on a
steep summit) and the observer height (NASA's maps are for the surface). This script removes the last two: for the
3 × 3 pixels around every site, in both NASA maps (120 m, and 60 m south of 85°S), it computes our sunlit time at the
**pixel centre**, over the same 18.6 years, with the observer at 0 m and at 2 m, on the LOLA DEM of the same resolution.

    python compare_nasa_pixels.py --dem /path/ldem_75s_120m_float.lbl --dem60 /path/ldem_75s_60m_float.lbl
    python compare_nasa_pixels.py ... --refresh      # re-read the NASA 3x3 windows over HTTP first (public PDS files)

Writes ``data/nasa_illumination/pixels.csv`` (NASA windows, offline input), ``data/nasa_illumination/pixels_comparison.json``
and prints the table used in README → "Validation vs NASA LOLA illumination maps".
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from datetime import timedelta
from pathlib import Path

import numpy as np

import compare_nasa_illumination as cni
import ephemeris
import horizon
import illumination

PIXELS_CSV = cni.SNAPSHOT_DIR / "pixels.csv"
PIXELS_JSON = cni.SNAPSHOT_DIR / "pixels_comparison.json"
HEIGHTS_M = (0.0, 2.0)
MAPS = ("sun120", "sun60")


def refresh_windows() -> None:
    """Every pixel of the 3x3 windows already sampled by compare_nasa_illumination.py, with its centre lat/lon."""
    from pyproj import CRS, Transformer

    stems = {k: s for k, s, _ in cni.PRODUCTS}
    geoms = {k: cni.map_geometry(stems[k]) for k in MAPS}
    rows = []
    for r in cni.read_samples():
        if r["product"] not in MAPS or r["pointId"].startswith("ref_"):
            continue
        g = geoms[r["product"]]
        row, col = int(r["row"]), int(r["col"])
        w = cni.window_3x3(stems[r["product"]], g, row, col)
        crs = CRS.from_user_input(g.crs.to_wkt())
        to_geo = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)
        for i in range(3):
            for j in range(3):
                x, y = g.transform * (col - 1 + j + 0.5, row - 1 + i + 0.5)
                lon, lat = to_geo.transform(x, y)
                rows.append({"siteId": r["pointId"], "map": r["product"], "di": i - 1, "dj": j - 1,
                             "lat": round(lat, 7), "lon": round(lon, 7), "nasaSun": round(float(w[i, j]), 5)})
    with open(PIXELS_CSV, "w", newline="", encoding="utf-8") as f:
        f.write(f"# NASA LOLA average visibility (release 2016), every pixel of the 3x3 windows; source {cni.PDS_DIR}\n")
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"wrote {PIXELS_CSV.relative_to(cni.REPO)} ({len(rows)} pixels)")


def read_windows() -> list[dict]:
    with open(PIXELS_CSV, encoding="utf-8") as f:
        return list(csv.DictReader(line for line in f if not line.startswith("#")))


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path, help="LOLA 120 m DEM (for the NASA 120 m map)")
    p.add_argument("--dem60", type=Path, help="LOLA 60 m DEM (for the NASA 60 m map); without it the 60 m map is skipped")
    p.add_argument("--refresh", action="store_true", help="re-read the NASA 3x3 windows over HTTP")
    args = p.parse_args(argv)

    if args.refresh or not PIXELS_CSV.exists():
        refresh_windows()
    dems = {"sun120": horizon.load_dem(args.dem)}
    if args.dem60:
        dems["sun60"] = horizon.load_dem(args.dem60)
    sky = ephemeris.load_moon_sky()
    times = sky.times(cni.PERIOD_START, cni.PERIOD_START + timedelta(days=cni.PERIOD_DAYS) - timedelta(hours=1), 1.0)

    pixels = [r for r in read_windows() if r["map"] in dems]
    for r in pixels:
        lat, lon = float(r["lat"]), float(r["lon"])
        el, az = sky.altaz(lat, lon, times, "sun")
        for h in HEIGHTS_M:
            mask = horizon.horizon_profile(dems[r["map"]], lat, lon, observer_height_m=h)
            r[f"ours{h:g}m"] = float((illumination.sun_fraction(el, az, mask) > 0).mean())

    summary = []
    for site in dict.fromkeys(r["siteId"] for r in pixels):
        for m in MAPS:
            px = [r for r in pixels if r["siteId"] == site and r["map"] == m]
            if not px:
                continue
            nasa = np.array([float(r["nasaSun"]) for r in px]) * 100
            entry = {"siteId": site, "map": m, "pixels": len(px), "nasaCentre": round(float(nasa[4]), 1)}
            for h in HEIGHTS_M:
                ours = np.array([r[f"ours{h:g}m"] for r in px]) * 100
                d = ours - nasa
                entry[f"ours{h:g}mCentre"] = round(float(ours[4]), 1)
                entry[f"bias{h:g}m"] = round(float(d.mean()), 1)
                entry[f"meanAbs{h:g}m"] = round(float(np.abs(d).mean()), 1)
            summary.append(entry)
    all_d = {f"{m}|{h:g}": np.array([[r[f"ours{h:g}m"] * 100 - float(r["nasaSun"]) * 100] for r in pixels if r["map"] == m]).ravel()
             for m in dems for h in HEIGHTS_M}
    overall = {k: {"pixels": int(v.size), "bias": round(float(v.mean()), 1), "meanAbs": round(float(np.abs(v).mean()), 1),
                   "medianAbs": round(float(np.median(np.abs(v))), 1), "within5pp": round(float((np.abs(v) <= 5).mean() * 100), 0)}
               for k, v in all_d.items()}
    PIXELS_JSON.write_text(json.dumps({"period": f"{cni.PERIOD_START:%Y-%m-%d} + 18.6 years, hourly",
                                       "definition": "share of hours with any part of the Sun above the horizon",
                                       "dems": {k: d.name for k, d in dems.items()}, "overall": overall,
                                       "sites": summary}, indent=2) + "\n", encoding="utf-8")

    print("| Site | NASA map | NASA centre | Ours 0 m | Ours 2 m | 3×3 mean bias 0 m | 3×3 mean bias 2 m |")
    print("|---|---|---:|---:|---:|---:|---:|")
    for e in summary:
        print(f"| {e['siteId']} | {e['map'][3:]} m | {e['nasaCentre']:.1f} % | {e['ours0mCentre']:.1f} % | {e['ours2mCentre']:.1f} % "
              f"| {e['bias0m']:+.1f} pp | {e['bias2m']:+.1f} pp |")
    for k, v in overall.items():
        print(f"{k}: {v}")
    print(f"wrote {PIXELS_JSON.relative_to(cni.REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
