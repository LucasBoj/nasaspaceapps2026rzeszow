"""Generate per-site illumination / Earth-visibility time series for SeleneFrontend.

Example::

    python build.py --dem /data/ldem_75s_120m_float.lbl --site malapert_massif
    python build.py --dem /data/ldem_75s_120m_float.lbl            # all sites

Writes ``<out>/{siteId}.json`` for each site and (re)writes ``<out>/index.json``
with headline statistics. Running a single site updates only its entry in the
existing index.
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

import ephemeris
import horizon
import illumination

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
DEFAULT_SITES = REPO / "SeleneFrontend" / "wwwroot" / "data" / "sites.json"
DEFAULT_OUT = REPO / "SeleneFrontend" / "wwwroot" / "data" / "illumination"
DEFAULT_START = "2027-01-01T00:00:00Z"
DEFAULT_END = "2028-12-31T23:00:00Z"
STEP_HOURS = 1


def iso_z(dt: datetime) -> str:
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def centi(values: np.ndarray) -> list[int]:
    """Degrees -> integer hundredths of a degree (keeps the JSON small)."""
    return np.rint(np.asarray(values) * 100).astype(int).tolist()


def compute_series(dem: horizon.Dem, sky: ephemeris.MoonSky, times, lat: float, lon: float,
                   observer_height_m: float, max_distance_m: float) -> dict:
    """Horizon mask plus hourly Sun/Earth geometry and visibility for one point.

    Shared by ``build.py`` and ``select_sites.py`` so both use the identical method.
    """
    mask = horizon.horizon_profile(dem, lat, lon, observer_height_m, max_distance_m)
    sun_alt, sun_az = sky.altaz(lat, lon, times, "sun")
    earth_alt, earth_az = sky.altaz(lat, lon, times, "earth")
    return {
        "mask": mask,
        "sun_alt": sun_alt, "sun_az": sun_az,
        "earth_alt": earth_alt, "earth_az": earth_az,
        "sun_pct": np.rint(illumination.sun_fraction(sun_alt, sun_az, mask) * 100).astype(int),
        "earth_vis": illumination.earth_visible(earth_alt, earth_az, mask),
    }


def build_site(site: dict, dem: horizon.Dem, sky: ephemeris.MoonSky, times, start: datetime,
               observer_height_m: float, max_distance_m: float) -> dict:
    """Compute the full time series document for one site."""
    lat, lon = float(site["lat"]), float(site["lon"])

    s = compute_series(dem, sky, times, lat, lon, observer_height_m, max_distance_m)
    mask, sun_alt, sun_az, earth_alt = s["mask"], s["sun_alt"], s["sun_az"], s["earth_alt"]
    sun_pct, earth_vis = s["sun_pct"], s["earth_vis"]

    return {
        "siteId": site["id"],
        "lat": lat,
        "lon": lon,
        "observerHeightM": observer_height_m,
        "start": iso_z(start),
        "stepHours": STEP_HOURS,
        "count": int(len(sun_pct)),
        "sunFraction": sun_pct.tolist(),
        "sunElevCentiDeg": centi(sun_alt),
        "sunAzCentiDeg": centi(sun_az),
        "earthVisible": earth_vis.astype(int).tolist(),
        "earthElevCentiDeg": centi(earth_alt),
        "earthAzCentiDeg": centi(s["earth_az"]),          # added 2026-11-14 (additive; HUD compass)
        "horizonDeg": [round(float(v), 2) for v in mask],
        "generatedWith": {
            "dem": dem.name,
            "kernels": list(ephemeris.KERNEL_FILES),
            "method": "linear-disk-approx",
        },
    }


def write_json(path: Path, doc: dict) -> None:
    path.write_text(json.dumps(doc, separators=(",", ":")) + "\n", encoding="utf-8")


def update_index(out_dir: Path, entries: list[dict], meta: dict) -> Path:
    """Merge site entries into index.json (keeps sites not rebuilt in this run)."""
    index_path = out_dir / "index.json"
    existing = {}
    if index_path.exists():
        try:
            existing = {s["siteId"]: s for s in json.loads(index_path.read_text(encoding="utf-8")).get("sites", [])}
        except (json.JSONDecodeError, KeyError):
            existing = {}
    for e in entries:
        existing[e["siteId"]] = e
    doc = {**meta, "sites": sorted(existing.values(), key=lambda s: s["siteId"])}
    index_path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    return index_path


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path, help="LOLA polar DEM (GeoTIFF, or PDS3 .lbl label)")
    p.add_argument("--sites", type=Path, default=DEFAULT_SITES, help="sites.json (default: frontend data)")
    p.add_argument("--site", help="build only this site id (quick test)")
    p.add_argument("--start", default=DEFAULT_START, help=f"UTC start (default {DEFAULT_START})")
    p.add_argument("--end", default=DEFAULT_END, help=f"UTC end, inclusive (default {DEFAULT_END})")
    p.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output directory")
    p.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR, help="SPICE kernel directory")
    p.add_argument("--observer-height", type=float, default=horizon.DEFAULT_OBSERVER_HEIGHT_M,
                   help="panel/antenna height above terrain in m (default 2)")
    p.add_argument("--max-distance-km", type=float, default=horizon.DEFAULT_MAX_DISTANCE_M / 1000,
                   help="horizon ray length in km (default 150)")
    p.add_argument("--dem-units", choices=["auto", "m", "km"], default="auto",
                   help="height units of the DEM (default: auto-detect)")
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)

    sites = json.loads(args.sites.read_text(encoding="utf-8"))
    if args.site:
        sites = [s for s in sites if s["id"] == args.site]
        if not sites:
            print(f"ERROR: site '{args.site}' not found in {args.sites}", file=sys.stderr)
            return 2

    start, end = ephemeris.parse_utc(args.start), ephemeris.parse_utc(args.end)
    if end < start:
        print("ERROR: --end is before --start", file=sys.stderr)
        return 2

    t0 = time.perf_counter()
    print(f"Loading DEM {args.dem} …")
    dem = horizon.load_dem(args.dem, args.dem_units)
    print(f"  {dem.heights_m.shape[1]}×{dem.heights_m.shape[0]} px, {dem.resolution_m:g} m/px, "
          f"heights in {dem.units_detected} → m, CRS: {dem.crs.name}")

    sky = ephemeris.load_moon_sky(args.kernels)
    times = sky.times(start, end, STEP_HOURS)
    print(f"Time grid: {iso_z(start)} … {iso_z(end)}, {len(times)} steps of {STEP_HOURS} h")

    args.out.mkdir(parents=True, exist_ok=True)
    max_distance_m = args.max_distance_km * 1000
    entries, failed = [], []

    for site in tqdm(sites, desc="sites", unit="site"):
        try:
            doc = build_site(site, dem, sky, times, start, args.observer_height, max_distance_m)
        except ValueError as exc:
            tqdm.write(f"  ✗ {site['id']}: {exc}")
            failed.append(site["id"])
            continue

        write_json(args.out / f"{site['id']}.json", doc)
        stats = illumination.summarize(np.array(doc["sunFraction"]), np.array(doc["earthVisible"]), STEP_HOURS)
        coverage = horizon.ray_coverage(dem, doc["lat"], doc["lon"], max_distance_m)
        entries.append({"siteId": site["id"], "name": site.get("name", site["id"]),
                        "lat": doc["lat"], "lon": doc["lon"], "file": f"{site['id']}.json",
                        **stats, "horizonCoveragePct": round(coverage * 100, 1)})
        tqdm.write(f"  ✓ {site['id']}: sunlit {stats['pctSunlit']}%, comms {stats['pctComms']}%, "
                   f"longest dark {stats['longestDarkHours']} h, DEM coverage {coverage:.0%}")

    if entries:
        meta = {"start": iso_z(start), "end": iso_z(end), "stepHours": STEP_HOURS,
                "observerHeightM": args.observer_height, "dem": dem.name,
                "method": "linear-disk-approx",
                "generatedAt": iso_z(datetime.now(timezone.utc))}
        index_path = update_index(args.out, entries, meta)
        print(f"Wrote {len(entries)} site file(s) and {index_path}")
    print(f"Done in {time.perf_counter() - t0:.1f} s")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
