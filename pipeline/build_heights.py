"""Horizon masks of each site for several heights above the terrain: ``illumination/{siteId}/heights.json``.

The app's series use an observer (panel / antenna) 2 m above the LOLA terrain. On summits and rims the result is
sensitive to that height: the local ground falls away, so a few metres lift the horizon off nearby terrain. NASA's
illumination maps (Mazarico et al. 2011) are computed at the surface; at the surface our model reproduces them
(README → "Validation vs NASA LOLA illumination maps"). This file lets the app re-check the verdict for other panel
heights (robustness row) without new hourly series: the Sun and Earth directions are the same, only the mask changes.

    python build_heights.py --dem /path/to/ldem_75s_120m_float.lbl          # all sites, a few seconds

Needs the yearly site files from ``build.py`` (statistics are rebuilt from them as the frontend does).
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np

import build
import build_ellipse
import ellipse
import horizon

HEIGHTS_M = (0.0, 1.0, 2.0, 3.0, 5.0, 10.0)


def build_doc(site: dict, dem: horizon.Dem, series: dict, max_distance_m: float) -> dict:
    lat, lon = float(site["lat"]), float(site["lon"])
    rows = []
    for h in HEIGHTS_M:
        mask = np.round(horizon.horizon_profile(dem, lat, lon, observer_height_m=h, max_distance_m=max_distance_m), 2)
        sunlit, comms = build_ellipse.point_stats({"horizon": mask, "azShiftDeg": 0.0, "tiltDeg": 0.0, "tiltAzDeg": 0.0}, series)
        rows.append({"heightM": h, "pctSunlit": round(sunlit, 1), "pctComms": round(comms, 1),
                     "horizonCentiDeg": np.rint(mask * 100).astype(int).tolist()})
    return {"siteId": site["id"], "lat": lat, "lon": lon, "seriesObserverHeightM": horizon.DEFAULT_OBSERVER_HEIGHT_M,
            "statsPeriod": "whole data range of the site files, any Sun / Earth > horizon + 0.5°",
            "heights": rows, "generatedWith": {"dem": dem.name, "method": "horizon.horizon_profile per observer height"}}


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path)
    p.add_argument("--sites", type=Path, default=build.DEFAULT_SITES)
    p.add_argument("--out", type=Path, default=build.DEFAULT_OUT)
    p.add_argument("--max-distance-km", type=float, default=horizon.DEFAULT_MAX_DISTANCE_M / 1000)
    args = p.parse_args(argv)

    t0 = time.perf_counter()
    dem = horizon.load_dem(args.dem)
    sites = json.loads(args.sites.read_text(encoding="utf-8"))
    print(f"{'site':22s} " + " ".join(f"{h:>5g} m" for h in HEIGHTS_M) + "   (% of hours with any Sun)")
    for site in sites:
        series = build_ellipse.load_site_series(args.out, site["id"])
        doc = build_doc(site, dem, series, args.max_distance_km * 1000)
        # The 2 m mask must be the one in the yearly files (same DEM, same method).
        first_year = sorted((args.out / site["id"]).glob("20*.json"))[0]
        shipped = json.loads(first_year.read_text(encoding="utf-8"))["horizonDeg"]
        two = next(r for r in doc["heights"] if r["heightM"] == horizon.DEFAULT_OBSERVER_HEIGHT_M)
        if np.abs(np.asarray(two["horizonCentiDeg"]) / 100 - np.asarray(shipped)).max() > 0.011:
            print(f"ERROR: {site['id']}: the 2 m mask differs from the yearly files — rebuild them with the same DEM", file=sys.stderr)
            return 1
        build.write_json(args.out / site["id"] / "heights.json", doc)
        print(f"{site['id']:22s} " + " ".join(f"{r['pctSunlit']:6.1f}%" for r in doc["heights"]))
    print(f"Done in {time.perf_counter() - t0:.1f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
