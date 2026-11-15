"""Landing-ellipse horizons for SeleneFrontend: ``illumination/{siteId}/ellipse.json`` per site.

Example::

    python build_ellipse.py --dem /data/ldem_75s_120m_float.lbl              # all sites (~25 s per site)
    python build_ellipse.py --dem /data/ldem_75s_120m_float.lbl --validate   # + check against a full recomputation

Needs the yearly site files from ``build.py`` (the per-point statistics are rebuilt from them exactly as the
frontend does). Method and accuracy: ``ellipse.py`` and README → "Landing ellipse".

``--validate`` recomputes, for 5 points per site, the full hourly series 2027–28 directly at the point (its own
ephemeris and horizon) and compares it with the reconstruction from the site's shipped series. It also compares our
±120 m range of sunlit time with the NASA LOLA map's 3×3-pixel range (``data/validation/nasa_illumination.json``).
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from tqdm import tqdm

import build
import ellipse
import ephemeris
import horizon

HERE = Path(__file__).resolve().parent
NASA_CHECK = HERE.parent / "SeleneFrontend" / "wwwroot" / "data" / "validation" / "nasa_illumination.json"
VALIDATE_OFFSETS = [(0, 0), (480, 0), (0, -480), (-300, 300), (120, 120)]
NASA_WINDOW = [(e, n) for n in (120, 0, -120) for e in (-120, 0, 120)]       # our 3×3 at the NASA pixel pitch


def load_site_series(out_dir: Path, site_id: str) -> dict:
    """The shipped yearly files of one site joined (rounded values, exactly what the frontend reads)."""
    index = json.loads((out_dir / "index.json").read_text(encoding="utf-8"))
    entry = next(s for s in index["sites"] if s["siteId"] == site_id)
    parts = [json.loads((out_dir / f).read_text(encoding="utf-8")) for _, f in sorted(entry["files"].items())]
    joined = {k: np.concatenate([np.asarray(p[k]) for p in parts]) for k in build.SERIES_KEYS}
    joined["start"] = parts[0]["start"]
    return joined


def point_stats(point: dict, series: dict, sl: slice = slice(None)) -> tuple[float, float]:
    """(pctSunlit with any Sun, pctComms) at a point over ``sl`` of the site series."""
    sun, earth = ellipse.reconstruct(point["horizon"], point["azShiftDeg"], point["tiltDeg"], point["tiltAzDeg"],
                                     series["sunElevCentiDeg"][sl] / 100, series["sunAzCentiDeg"][sl] / 100,
                                     series["earthElevCentiDeg"][sl] / 100, series["earthAzCentiDeg"][sl] / 100)
    return float((sun > 0).mean() * 100), float(earth.mean() * 100)


def build_doc(site: dict, dem: horizon.Dem, series: dict, observer_height_m: float, max_distance_m: float) -> dict:
    lat, lon = float(site["lat"]), float(site["lon"])
    pts = ellipse.ellipse_points(dem, lat, lon, observer_height_m, max_distance_m)
    out = []
    for p in pts:
        p["horizon"] = np.round(p["horizon"], 2)          # what is shipped (centi-degrees)
        sunlit, comms = point_stats(p, series)
        out.append({
            "e": round(p["e"]), "n": round(p["n"]),
            "lat": round(p["lat"], 6), "lon": round(p["lon"], 6),
            "groundM": round(p["groundM"], 1),
            "slopeDeg": None if not np.isfinite(p["slopeDeg"]) else round(p["slopeDeg"], 1),
            "azShiftDeg": round(p["azShiftDeg"], 4), "tiltDeg": round(p["tiltDeg"], 5), "tiltAzDeg": round(p["tiltAzDeg"], 2),
            "pctSunlit": round(sunlit, 1), "pctComms": round(comms, 1),
            "horizonCentiDeg": np.rint(p["horizon"] * 100).astype(int).tolist(),
        })
    return {
        "siteId": site["id"], "lat": lat, "lon": lon, "observerHeightM": observer_height_m,
        "spacingM": ellipse.GRID_SPACING_M, "radiusM": ellipse.GRID_RADIUS_M,
        "statsPeriod": "whole data range of the site files, any Sun / Earth > horizon + 0.5°",
        "points": out,
        "generatedWith": {"dem": dem.name, "method": "own-frame horizon + azShift/tilt (pipeline/ellipse.py)"},
    }


def validate(site: dict, dem: horizon.Dem, sky: ephemeris.MoonSky, series: dict, observer_height_m: float,
             max_distance_m: float, nasa: dict | None) -> dict:
    lat, lon = float(site["lat"]), float(site["lon"])
    start = ephemeris.parse_utc("2027-01-01T00:00:00Z")
    end = ephemeris.parse_utc("2028-12-31T23:00:00Z")
    times = sky.times(start, end, 1)
    first = int((start - ephemeris.parse_utc(series["start"])).total_seconds() // 3600)
    sl = slice(first, first + len(times))
    max_d, share_d, earth_h = 0, 0.0, 0
    for e, n in VALIDATE_OFFSETS:
        plat, plon = ellipse.offset_point(lat, lon, e, n)
        direct = build.compute_series(dem, sky, times, plat, plon, observer_height_m, max_distance_m)
        shift, tilt, tilt_az = ellipse.frame_params(lat, lon, plat, plon)
        sun, earth = ellipse.reconstruct(np.round(direct["mask"], 2), shift, tilt, tilt_az,
                                         series["sunElevCentiDeg"][sl] / 100, series["sunAzCentiDeg"][sl] / 100,
                                         series["earthElevCentiDeg"][sl] / 100, series["earthAzCentiDeg"][sl] / 100)
        d = np.abs(sun - direct["sun_pct"])
        max_d = max(max_d, int(d.max()))
        share_d = max(share_d, float((d > 1).mean() * 100))
        earth_h = max(earth_h, int((earth != direct["earth_vis"]).sum()))
    ours = []
    for e, n in NASA_WINDOW:
        plat, plon = ellipse.offset_point(lat, lon, e, n)
        mask = np.round(horizon.horizon_profile(dem, plat, plon, observer_height_m, max_distance_m), 2)
        shift, tilt, tilt_az = ellipse.frame_params(lat, lon, plat, plon)
        ours.append(point_stats({"horizon": mask, "azShiftDeg": shift, "tiltDeg": tilt, "tiltAzDeg": tilt_az}, series, sl)[0])
    result = {"siteId": site["id"], "maxSunDeltaPp": max_d, "hoursOver1PpPct": round(share_d, 2),
              "earthMismatchHours": earth_h, "hours": len(times),
              "ours3x3SunPct2027": [round(min(ours), 1), round(max(ours), 1)]}
    if nasa and (row := next((s for s in nasa["sites"] if s["siteId"] == site["id"]), None)):
        result["nasa3x3SunPct"] = row.get("nasaSunRangePct")
    return result


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path, help="LOLA polar DEM (GeoTIFF, or PDS3 .lbl label)")
    p.add_argument("--sites", type=Path, default=build.DEFAULT_SITES)
    p.add_argument("--site", help="only this site id")
    p.add_argument("--out", type=Path, default=build.DEFAULT_OUT, help="illumination directory (with the yearly files)")
    p.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR)
    p.add_argument("--observer-height", type=float, default=horizon.DEFAULT_OBSERVER_HEIGHT_M)
    p.add_argument("--max-distance-km", type=float, default=horizon.DEFAULT_MAX_DISTANCE_M / 1000)
    p.add_argument("--validate", action="store_true", help="compare with a direct recomputation and the NASA 3×3 range")
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    sites = json.loads(args.sites.read_text(encoding="utf-8"))
    if args.site:
        sites = [s for s in sites if s["id"] == args.site]
        if not sites:
            print(f"ERROR: site '{args.site}' not found in {args.sites}", file=sys.stderr)
            return 2

    t0 = time.perf_counter()
    dem = horizon.load_dem(args.dem)
    sky = ephemeris.load_moon_sky(args.kernels) if args.validate else None
    nasa = json.loads(NASA_CHECK.read_text(encoding="utf-8")) if NASA_CHECK.exists() else None
    max_distance_m = args.max_distance_km * 1000
    report = []
    for site in tqdm(sites, desc="sites", unit="site"):
        series = load_site_series(args.out, site["id"])
        doc = build_doc(site, dem, series, args.observer_height, max_distance_m)
        build.write_json(args.out / site["id"] / "ellipse.json", doc)
        lit = [p["pctSunlit"] for p in doc["points"]]
        tqdm.write(f"  ✓ {site['id']}: {len(doc['points'])} points, sunlit {min(lit):.1f}–{max(lit):.1f}% "
                   f"(site {doc['points'][len(doc['points']) // 2]['pctSunlit']:.1f}%)")
        if args.validate:
            v = validate(site, dem, sky, series, args.observer_height, max_distance_m, nasa)
            report.append(v)
            tqdm.write(f"    validation: max Δsun {v['maxSunDeltaPp']} pp, hours > 1 pp {v['hoursOver1PpPct']}%, "
                       f"Earth mismatch ≤ {v['earthMismatchHours']} h of {v['hours']}; ±120 m sunlit 2027–28 "
                       f"{v['ours3x3SunPct2027'][0]}–{v['ours3x3SunPct2027'][1]}% vs NASA 3×3 {v.get('nasa3x3SunPct')}")
    if report:
        (HERE / "docs").mkdir(exist_ok=True)
        path = HERE / "docs" / "ellipse_validation.json"
        path.write_text(json.dumps({"generatedAt": build.iso_z(datetime.now(timezone.utc)), "sites": report}, indent=2) + "\n",
                        encoding="utf-8")
        print(f"Validation written to {path}")
    print(f"Done in {time.perf_counter() - t0:.1f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
