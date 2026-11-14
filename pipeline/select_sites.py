"""Pick the best landing site in each Artemis III region and update the frontend data.

For every SELENE region:

1. Candidates come from Wueller et al. (2026), Table A1 (``data/wueller2026_sites.csv``,
   produced by ``import_wueller_sites.py``). A region without published candidates
   falls back to a grid search (500 m spacing, 15 × 15 km) around its approximate
   centre, and its result is marked ``approximate``.
2. Each candidate's slope comes from the DEM (3 × 3 px, Horn). Candidates steeper than
   ``--max-slope`` are rejected.
3. The remaining candidates are scored hourly over the build window with the same method
   as ``build.py``: % of time lit (sunFraction ≥ 50), % of time with Earth above the
   horizon, longest darkness, and longest continuous light.
4. Ranking: highest lit % first. Within 1 percentage point of the best, the highest Earth
   % wins, then the shortest longest-darkness.

Outputs:

* ``SeleneFrontend/wwwroot/data/sites.json``: best site per region plus provenance and
  the top-3 alternatives
* ``SeleneFrontend/wwwroot/data/illumination/candidates/{regionId}.json``: every
  evaluated point
* then re-runs ``build.py`` for the new coordinates (skip with ``--no-build``)

Usage::

    python select_sites.py --dem /path/ldem_75s_120m_float.lbl
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
import time
from pathlib import Path

import numpy as np
import pyproj
from tqdm import tqdm

import build
import ephemeris
import horizon
import illumination

HERE = Path(__file__).resolve().parent
DEFAULT_CANDIDATES = HERE / "data" / "wueller2026_sites.csv"
DEFAULT_CANDIDATE_OUT = build.DEFAULT_OUT / "candidates"
PAPER_DOI = "doi:10.1029/2025JE009434"
PAPER_REF = f"Wueller et al. (2026), JGR Planets, {PAPER_DOI}"
GRID_SOURCE = "grid search around approximate region center (no published coordinates)"
TIE_TOLERANCE_PCT = 1.0


def read_candidates(path: Path) -> dict[str, list[dict]]:
    """CSV rows grouped by SELENE region id (comment lines starting with # are skipped)."""
    groups: dict[str, list[dict]] = {}
    if not path.exists():
        return groups
    with open(path, encoding="utf-8") as fh:
        rows = csv.DictReader(line for line in fh if not line.startswith("#"))
        for r in rows:
            if r.get("selene_region"):
                groups.setdefault(r["selene_region"], []).append(
                    {"candidateId": r["site_id"], "lat": float(r["lat"]), "lon": float(r["lon"])})
    return groups


def grid_candidates(dem: horizon.Dem, lat: float, lon: float, size_km: float, spacing_m: float) -> list[dict]:
    """Square grid (in the DEM's projected plane) centred on lat/lon."""
    cx, cy = dem.project(np.array([lon]), np.array([lat]))
    half = size_km * 500.0
    offsets = np.arange(-half, half + spacing_m / 2, spacing_m)
    gx, gy = np.meshgrid(cx[0] + offsets, cy[0] + offsets)
    to_geo = pyproj.Transformer.from_crs(dem.crs, dem.crs.geodetic_crs, always_xy=True)
    lons, lats = to_geo.transform(gx.ravel(), gy.ravel())
    return [{"candidateId": f"GRID_{i:04d}", "lat": float(a), "lon": float(o)} for i, (a, o) in enumerate(zip(lats, lons))]


def evaluate(cand: dict, dem: horizon.Dem, sky: ephemeris.MoonSky, times, max_slope: float,
             observer_height_m: float, max_distance_m: float) -> dict:
    """Score one candidate (or mark it rejected with a reason)."""
    out = {"candidateId": cand["candidateId"], "lat": round(cand["lat"], 5), "lon": round(cand["lon"], 5),
           "slopeDeg": None, "sunPct": None, "earthPct": None, "longestDarkH": None, "longestLightH": None,
           "rejected": False, "reason": ""}
    slope = dem.slope_at(cand["lat"], cand["lon"])
    if not np.isfinite(slope):
        return {**out, "rejected": True, "reason": "no DEM coverage for slope"}
    out["slopeDeg"] = round(slope, 2)
    if slope > max_slope:
        return {**out, "rejected": True, "reason": f"slope {slope:.1f}° > {max_slope:g}°"}
    try:
        s = build.compute_series(dem, sky, times, cand["lat"], cand["lon"], observer_height_m, max_distance_m)
    except ValueError as exc:
        return {**out, "rejected": True, "reason": str(exc)}
    out.update(illumination.site_metrics(s["sun_pct"], s["earth_vis"], build.STEP_HOURS))
    return out


def rank(accepted: list[dict]) -> list[dict]:
    """Best first: max sun %, then (within ±1 pt of best) max Earth %, then shortest longest-darkness."""
    if not accepted:
        return []
    best_sun = max(c["sunPct"] for c in accepted)
    contenders = [c for c in accepted if c["sunPct"] >= best_sun - TIE_TOLERANCE_PCT]
    rest = [c for c in accepted if c["sunPct"] < best_sun - TIE_TOLERANCE_PCT]
    contenders.sort(key=lambda c: (-c["earthPct"], c["longestDarkH"], -c["sunPct"]))
    rest.sort(key=lambda c: (-c["sunPct"], -c["earthPct"], c["longestDarkH"]))
    return contenders + rest


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path, help="LOLA polar DEM (GeoTIFF or PDS3 .lbl)")
    p.add_argument("--sites", type=Path, default=build.DEFAULT_SITES, help="sites.json to update")
    p.add_argument("--candidates", type=Path, default=DEFAULT_CANDIDATES, help="published candidate sites CSV")
    p.add_argument("--candidates-out", type=Path, default=DEFAULT_CANDIDATE_OUT)
    p.add_argument("--max-slope", type=float, default=10.0, help="reject steeper candidates (deg, default 10)")
    p.add_argument("--grid-spacing-m", type=float, default=500.0)
    p.add_argument("--grid-size-km", type=float, default=15.0)
    p.add_argument("--start", default=build.DEFAULT_START)
    p.add_argument("--end", default=build.DEFAULT_END)
    p.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR)
    p.add_argument("--observer-height", type=float, default=horizon.DEFAULT_OBSERVER_HEIGHT_M)
    p.add_argument("--max-distance-km", type=float, default=horizon.DEFAULT_MAX_DISTANCE_M / 1000)
    p.add_argument("--dem-units", choices=["auto", "m", "km"], default="auto")
    p.add_argument("--no-build", action="store_true", help="do not re-run build.py afterwards")
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    t0 = time.perf_counter()

    sites = json.loads(args.sites.read_text(encoding="utf-8"))
    published = read_candidates(args.candidates)
    dem = horizon.load_dem(args.dem, args.dem_units)
    sky = ephemeris.load_moon_sky(args.kernels)
    start, end = ephemeris.parse_utc(args.start), ephemeris.parse_utc(args.end)
    times = sky.times(start, end, build.STEP_HOURS)
    max_distance_m = args.max_distance_km * 1000
    args.candidates_out.mkdir(parents=True, exist_ok=True)
    print(f"DEM {dem.name} ({dem.resolution_m:g} m/px), {len(times)} hourly steps, max slope {args.max_slope:g}°")

    updated = []
    for site in sites:
        rid = site["id"]
        # The region centre is the original approximate coordinate; keep it stable across re-runs.
        center = site.get("regionCenter") or {"lat": site["lat"], "lon": site["lon"]}
        cands = published.get(rid, [])
        method = "published"
        if not cands:
            method = "grid"
            cands = grid_candidates(dem, center["lat"], center["lon"], args.grid_size_km, args.grid_spacing_m)

        results = [evaluate(c, dem, sky, times, args.max_slope, args.observer_height, max_distance_m)
                   for c in tqdm(cands, desc=f"{rid:20s}", unit="pt", leave=False)]
        accepted = rank([r for r in results if not r["rejected"]])

        if not accepted and method == "published":
            tqdm.write(f"  ! {rid}: all {len(results)} published candidates rejected — falling back to grid search")
            method = "grid"
            cands = grid_candidates(dem, center["lat"], center["lon"], args.grid_size_km, args.grid_spacing_m)
            results = [evaluate(c, dem, sky, times, args.max_slope, args.observer_height, max_distance_m) for c in cands]
            accepted = rank([r for r in results if not r["rejected"]])
        if not accepted:
            tqdm.write(f"  ✗ {rid}: no acceptable point found — entry left unchanged")
            updated.append(site)
            continue

        best = accepted[0]
        n = len(results)
        source = (f"{PAPER_REF} — best of {n} candidate sites by SELENE illumination score"
                  if method == "published" else f"{GRID_SOURCE} — best of {n} grid points by SELENE illumination score")
        updated.append({
            "id": rid,
            "name": site["name"],
            "lat": best["lat"],
            "lon": best["lon"],
            "approximate": method != "published",
            "source": source,
            "candidateId": best["candidateId"] if method == "published" else None,
            "regionCenter": center,
            "candidatesEvaluated": n,
            "alternatives": [{"candidateId": a["candidateId"], "lat": a["lat"], "lon": a["lon"],
                              "sunPct": a["sunPct"], "earthPct": a["earthPct"]} for a in accepted[1:4]],
        })

        (args.candidates_out / f"{rid}.json").write_text(json.dumps({
            "regionId": rid, "method": method,
            "source": PAPER_REF if method == "published" else GRID_SOURCE,
            "maxSlopeDeg": args.max_slope, "observerHeightM": args.observer_height,
            "start": build.iso_z(start), "end": build.iso_z(end), "litThresholdPct": illumination.LIT_THRESHOLD_PCT,
            "best": best["candidateId"],
            "candidates": sorted(results, key=lambda r: (r["rejected"], -(r["sunPct"] or 0))),
        }, indent=1) + "\n", encoding="utf-8")

        rejected = sum(r["rejected"] for r in results)
        tqdm.write(f"  ✓ {rid:20s} {best['candidateId']:12s} ({best['lat']:.4f}, {best['lon']:.4f}) "
                   f"lit {best['sunPct']:5.1f}%  earth {best['earthPct']:5.1f}%  longest light {best['longestLightH']:6.0f} h  "
                   f"[{n} evaluated, {rejected} rejected, {method}]")

    args.sites.write_text(json.dumps(updated, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Updated {args.sites} in {time.perf_counter() - t0:.1f} s")

    if args.no_build:
        return 0
    print("\nRe-running build.py for the selected sites …")
    return build.main(["--dem", str(args.dem), "--sites", str(args.sites), "--start", args.start, "--end", args.end,
                       "--kernels", str(args.kernels), "--observer-height", str(args.observer_height),
                       "--max-distance-km", str(args.max_distance_km), "--dem-units", args.dem_units])


if __name__ == "__main__":
    sys.exit(main())
