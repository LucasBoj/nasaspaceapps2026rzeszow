"""Data for the in-browser analysis of any point in the south polar cap (80–90°S).

The browser cannot run Skyfield or read the 230 MB LOLA DEM, so this script exports the two
ingredients it needs, in compact binary form:

* ``data/ephem/{year}.bin`` — for every hour 2026-11-01 … 2035-12-31 the apparent direction of the
  Sun and the Earth as seen from the Moon's centre, in the same body-fixed frame as the pipeline
  (MOON_ME_DE421): selenographic latitude/longitude of the sub-solar and sub-Earth points and the
  distances. Float32 little-endian, 6 values per hour:
  ``[sunLatDeg, sunLonDeg, sunDistKm, earthLatDeg, earthLonDeg, earthDistKm]``.
  The browser turns them into topocentric elevation/azimuth for any surface point (the observer's
  offset from the Moon's centre gives the parallax, up to 0.26° for the Earth).
* ``data/dem/south80_240m.bin`` — LOLA heights (m, Int16, ``-32768`` = no data) for 80–90°S in
  the DEM's own polar stereographic grid, averaged from 120 m to 240 m pixels (2 × 2), row 0 north,
  **delta-encoded along each row** (first value absolute, then differences) so HTTP compression
  shrinks it well. ``south80_240m.json`` holds size, transform, CRS and pyproj check points.
* ``data/ephem/index.json`` — years, start, count, step, field order and provenance.

Usage::

    python export_browser_data.py --dem /path/ldem_75s_120m_float.lbl
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

import build
import ephemeris
import horizon

HERE = Path(__file__).resolve().parent
DATA = HERE.parent / "SeleneFrontend" / "wwwroot" / "data"
START = "2026-11-01T00:00:00Z"
END = "2035-12-31T23:00:00Z"
FIELDS = ["sunLatDeg", "sunLonDeg", "sunDistKm", "earthLatDeg", "earthLonDeg", "earthDistKm"]
DEM_PIXEL_M = 240.0
DEM_HALF_EXTENT_M = 304_320.0          # covers 80°S (ρ = 304.0 km) on a whole number of 240 m pixels
NODATA = -32768


def export_ephemeris(sky: ephemeris.MoonSky, out_dir: Path) -> dict:
    start, end = ephemeris.parse_utc(START), ephemeris.parse_utc(END)
    out_dir.mkdir(parents=True, exist_ok=True)
    moon = sky.ephemeris["moon"]
    years = []
    for year in range(start.year, end.year + 1):
        y0 = max(start, datetime(year, 1, 1, tzinfo=timezone.utc))
        y1 = min(end, datetime(year, 12, 31, 23, tzinfo=timezone.utc))
        times = sky.times(y0, y1, 1)
        cols = []
        for body in ("sun", "earth"):
            app = moon.at(times).observe(sky.ephemeris[body]).apparent()
            lat, lon, dist = app.frame_latlon(sky.frame)
            cols += [lat.degrees, lon.degrees, dist.km]
        data = np.column_stack(cols).astype("<f4")
        (out_dir / f"{year}.bin").write_bytes(data.tobytes())
        years.append({"year": year, "start": build.iso_z(y0), "count": int(len(data)), "file": f"{year}.bin"})
        print(f"  ephem {year}: {len(data)} h, {data.nbytes / 1e6:.2f} MB")
    meta = {"start": START, "end": END, "stepHours": 1, "fields": FIELDS, "dtype": "float32-le", "provenance": build.PROVENANCE,
            "frame": ephemeris.MOON_FRAME, "kernels": list(ephemeris.KERNEL_FILES),
            "moonRadiusM": horizon.MOON_RADIUS_M,
            "note": "apparent positions (light time + aberration) seen from the Moon's centre, as in ephemeris.MoonSky",
            "years": years}
    (out_dir / "index.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    return meta


def export_dem(dem: horizon.Dem, out_dir: Path) -> dict:
    out_dir.mkdir(parents=True, exist_ok=True)
    t = dem.transform
    src_px = abs(t.a)
    k = int(round(DEM_PIXEL_M / src_px))
    assert abs(k * src_px - DEM_PIXEL_M) < 1e-6, "target pixel must be a multiple of the source pixel"
    c0 = int(round((-DEM_HALF_EXTENT_M - t.c) / t.a))
    r0 = int(round((DEM_HALF_EXTENT_M - t.f) / t.e))
    n = int(round(2 * DEM_HALF_EXTENT_M / DEM_PIXEL_M))
    block = dem.heights_m[r0:r0 + n * k, c0:c0 + n * k].astype(np.float64)
    assert block.shape == (n * k, n * k), f"crop outside the DEM: {block.shape}"
    mean = np.nanmean(block.reshape(n, k, n, k), axis=(1, 3))   # NaN only where all k×k are NaN
    heights = np.where(np.isfinite(mean), np.rint(mean), NODATA).astype(np.int16)
    # Delta along rows (int16 wraps safely: the decoder adds in int16 too).
    delta = heights.copy()
    delta[:, 1:] = (heights[:, 1:].astype(np.int32) - heights[:, :-1].astype(np.int32)).astype(np.int16)
    (out_dir / "south80_240m.bin").write_bytes(delta.astype("<i2").tobytes())
    # pyproj check points, so the browser's projection can be tested against the pipeline's.
    lats = np.array([-80.0, -85.0, -89.5, -83.0, -87.2])
    lons = np.array([0.0, 90.0, -45.0, 170.0, -120.0])
    xs, ys = dem.project(lons, lats)
    meta = {"width": n, "height": n, "pixelM": DEM_PIXEL_M, "originX": -DEM_HALF_EXTENT_M, "originY": DEM_HALF_EXTENT_M,
            "provenance": build.PROVENANCE,
            "crs": dem.crs.to_proj4(), "moonRadiusM": horizon.MOON_RADIUS_M, "dtype": "int16-le", "nodata": NODATA,
            "encoding": "row-delta", "heightUnit": "m", "source": dem.name,
            "method": f"mean of {k}×{k} source pixels ({src_px:g} m), rounded to 1 m",
            "checkPoints": [{"lat": float(a), "lon": float(b), "x": float(x), "y": float(y)} for a, b, x, y in zip(lats, lons, xs, ys)]}
    (out_dir / "south80_240m.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"  DEM {n}×{n} px at {DEM_PIXEL_M:g} m, {heights.nbytes / 1e6:.1f} MB raw, "
          f"{np.mean(heights == NODATA) * 100:.2f} % no data")
    return meta


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", required=True, type=Path)
    p.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR)
    p.add_argument("--out", type=Path, default=DATA)
    args = p.parse_args(argv)
    t0 = time.perf_counter()
    sky = ephemeris.load_moon_sky(args.kernels)
    export_ephemeris(sky, args.out / "ephem")
    export_dem(horizon.load_dem(args.dem), args.out / "dem")
    print(f"Done in {time.perf_counter() - t0:.1f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
