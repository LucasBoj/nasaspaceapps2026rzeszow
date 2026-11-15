"""Science layer: permanently shadowed regions (PSRs, possible water ice) near the landing sites.

Input: NASA's LOLA permanently-shadowed-region map ``LPSR_75S_120M_201608`` (Mazarico et al. 2011, PDS Geosciences
Node, release 2016): a binary map on exactly the grid of our ``ldem_75s_120m_float`` DEM (7624 x 7624 px, 120 m).

    python build_psr.py --psr /path/to/lpsr_75s_120m_201608.lbl

Writes ``SeleneFrontend/wwwroot/data/science/psr.json`` with, for the 9 sites and every Wueller et al. (2026) candidate
of their regions: the distance to the nearest PSR pixel (any size, >= 1 km², >= 10 km²), the PSR area within 2 / 5 / 10 km,
the nearest PSR >= 1 km² (point and area), and the sun-vs-ice Pareto front of each region; plus simplified PSR outlines
within 15 km of each site (lon/lat) for the 3D view. Distances are straight lines over the map plane (polar
stereographic, < 0.3 % scale error south of 80°S), not walking paths.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pyproj
import rasterio
from rasterio import features
from scipy import ndimage
import shapely
from shapely.geometry import Point, shape

import build

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "SeleneFrontend" / "wwwroot" / "data" / "science" / "psr.json"
CANDIDATES = build.DEFAULT_OUT / "candidates"
SOURCE = {
    "product": "LPSR_75S_120M_201608",
    "citation": "Mazarico, E., et al. (2011), Illumination conditions of the lunar polar regions using LOLA topography, "
                "Icarus 211, 1066-1081, doi:10.1016/j.icarus.2010.10.030",
    "url": "https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/extras/illumination/img/",
    "definition": "pixels never lit by any part of the Sun over the LOLA illumination simulation (PSR = 1)",
}
BIG_KM2 = (1.0, 10.0)
AREA_RADII_KM = (2.0, 5.0, 10.0)
OUTLINE_RADIUS_KM = 15.0
OUTLINE_MIN_KM2 = 0.05          # ignore 1–3 pixel specks in the outlines (they stay in the distances)


class PsrMap:
    """The PSR raster with connected components, areas and distance maps."""

    def __init__(self, path: Path):
        with rasterio.open(path) as src:
            dn = src.read(1)
            self.transform = src.transform
            self.crs = pyproj.CRS.from_wkt(src.crs.to_wkt())
        self.psr = dn > 0                                      # DN +20000 = PSR, -20000 = not (label)
        self.res_m = abs(self.transform.a)
        px_km2 = self.res_m ** 2 / 1e6
        self.labels, n = ndimage.label(self.psr, structure=np.ones((3, 3)))
        self.areas_km2 = ndimage.sum(self.psr, self.labels, index=np.arange(1, n + 1)) * px_km2
        self.dist_km = {0.0: self._dist(self.psr)}
        self.nearest_idx = {}
        for a in BIG_KM2:
            mask = np.isin(self.labels, np.flatnonzero(self.areas_km2 >= a) + 1)
            self.dist_km[a], self.nearest_idx[a] = self._dist(mask, indices=True)
        self._inv = ~self.transform
        self._to_xy = pyproj.Transformer.from_crs(self.crs.geodetic_crs, self.crs, always_xy=True)
        self._to_geo = pyproj.Transformer.from_crs(self.crs, self.crs.geodetic_crs, always_xy=True)

    def _dist(self, mask, indices=False):
        out = ndimage.distance_transform_edt(~mask, return_indices=indices)
        if indices:
            d, idx = out
            return d * self.res_m / 1000.0, idx
        return out * self.res_m / 1000.0

    @property
    def total_km2(self) -> float:
        return float(self.psr.sum() * self.res_m ** 2 / 1e6)

    def pixel(self, lat: float, lon: float) -> tuple[int, int]:
        x, y = self._to_xy.transform(lon, lat)
        col, row = self._inv * (x, y)
        return int(np.floor(row)), int(np.floor(col))

    def lonlat_of_pixel(self, row: float, col: float) -> tuple[float, float]:
        x, y = self.transform * (col + 0.5, row + 0.5)
        lon, lat = self._to_geo.transform(x, y)
        return float(lon), float(lat)

    def at(self, lat: float, lon: float) -> dict:
        r, c = self.pixel(lat, lon)
        k = int(np.ceil(max(AREA_RADII_KM) * 1000 / self.res_m))
        win = self.psr[r - k:r + k + 1, c - k:c + k + 1]
        yy, xx = np.mgrid[-k:k + 1, -k:k + 1]
        rr = np.hypot(yy, xx) * self.res_m / 1000.0
        out = {"onPsr": bool(self.psr[r, c]),
               "dAnyKm": round(float(self.dist_km[0.0][r, c]), 2),
               "d1Km": round(float(self.dist_km[1.0][r, c]), 2),
               "d10Km": round(float(self.dist_km[10.0][r, c]), 2)}
        for rad in AREA_RADII_KM:
            out[f"psrKm2Within{rad:g}"] = round(float((win & (rr <= rad)).sum() * self.res_m ** 2 / 1e6), 2)
        nr, nc = self.nearest_idx[1.0][0][r, c], self.nearest_idx[1.0][1][r, c]
        lon_n, lat_n = self.lonlat_of_pixel(nr, nc)
        out["nearest1"] = {"lat": round(lat_n, 5), "lon": round(lon_n, 5),
                           "areaKm2": round(float(self.areas_km2[self.labels[nr, nc] - 1]), 1)}
        return out

    def outlines(self, lat: float, lon: float, radius_km: float = OUTLINE_RADIUS_KM) -> list[dict]:
        """PSR polygons intersecting a circle around the point, simplified to ~half a pixel, as lon/lat rings."""
        r, c = self.pixel(lat, lon)
        k = int(np.ceil(radius_km * 1000 / self.res_m)) + 2
        r0, c0 = max(r - k, 0), max(c - k, 0)
        win = self.psr[r0:r + k + 1, c0:c + k + 1].astype(np.uint8)
        wt = self.transform * rasterio.Affine.translation(c0, r0)
        cx, cy = self._to_xy.transform(lon, lat)
        circle = Point(cx, cy).buffer(radius_km * 1000)
        def to_geo(xy: np.ndarray) -> np.ndarray:
            return np.column_stack(self._to_geo.transform(xy[:, 0], xy[:, 1]))
        polys = []
        for geom, value in features.shapes(win, mask=win.astype(bool), transform=wt, connectivity=8):
            if value != 1:
                continue
            g = shape(geom)
            if g.area / 1e6 < OUTLINE_MIN_KM2 or not g.intersects(circle):
                continue
            g = g.simplify(self.res_m / 2, preserve_topology=True)
            ll = shapely.transform(g, to_geo)
            rings = [[[round(x, 5), round(y, 5)] for x, y in ll.exterior.coords]]
            polys.append({"areaKm2": round(g.area / 1e6, 2),
                          "distKm": round(g.distance(Point(cx, cy)) / 1000, 2), "rings": rings})
        polys.sort(key=lambda p: p["distKm"])
        return polys


def pareto(cands: list[dict]) -> set[str]:
    """Candidates not beaten on both more sun (sunPct) and a closer PSR >= 1 km² (d1Km)."""
    front = set()
    for c in cands:
        if not any(o["sunPct"] >= c["sunPct"] and o["d1Km"] <= c["d1Km"] and (o["sunPct"] > c["sunPct"] or o["d1Km"] < c["d1Km"])
                   for o in cands):
            front.add(c["candidateId"])
    return front


def build_doc(m: PsrMap, sites: list[dict]) -> dict:
    out_sites, regions = [], []
    for s in sites:
        entry = {"siteId": s["id"], "name": s["name"], "lat": s["lat"], "lon": s["lon"], **m.at(s["lat"], s["lon"]),
                 "outlines": m.outlines(s["lat"], s["lon"])}
        out_sites.append(entry)
        cand_path = CANDIDATES / f"{s['id']}.json"
        if not cand_path.exists():
            continue
        doc = json.loads(cand_path.read_text(encoding="utf-8"))
        cands = []
        for c in doc["candidates"]:
            if c.get("rejected"):
                continue
            a = m.at(c["lat"], c["lon"])
            cands.append({"candidateId": c["candidateId"], "lat": c["lat"], "lon": c["lon"], "slopeDeg": c.get("slopeDeg"),
                          "sunPct": c["sunPct"], "earthPct": c["earthPct"], "longestDarkH": c.get("longestDarkH"),
                          "dAnyKm": a["dAnyKm"], "d1Km": a["d1Km"], "psrKm2Within5": a["psrKm2Within5"],
                          "isSite": c["candidateId"] == s.get("candidateId")})
        front = pareto(cands)
        for c in cands:
            c["pareto"] = c["candidateId"] in front
        regions.append({"siteId": s["id"], "sunPeriod": f"{doc.get('start', '')} .. {doc.get('end', '')}",
                        "sunDefinition": f"share of hours with >= {doc.get('litThresholdPct', 50)} % of the solar disc (candidates/*.json)",
                        "candidates": sorted(cands, key=lambda c: -c["sunPct"])})
    return {**SOURCE, "generatedAt": build.iso_z(datetime.now(timezone.utc)),
            "totalPsrKm2": round(m.total_km2), "regionsKm2": {f">={a:g}": int((m.areas_km2 >= a).sum()) for a in BIG_KM2},
            "outlineRadiusKm": OUTLINE_RADIUS_KM, "outlineMinKm2": OUTLINE_MIN_KM2,
            "sites": out_sites, "regions": regions}


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--psr", required=True, type=Path, help="LPSR_75S_120M_201608 label (.lbl)")
    p.add_argument("--sites", type=Path, default=build.DEFAULT_SITES)
    p.add_argument("--out", type=Path, default=OUT)
    args = p.parse_args(argv)
    t0 = time.perf_counter()
    m = PsrMap(args.psr)
    doc = build_doc(m, json.loads(args.sites.read_text(encoding="utf-8")))
    doc["provenance"] = build.PROVENANCE
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(doc, separators=(",", ":"), ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"PSR total {doc['totalPsrKm2']} km², regions {doc['regionsKm2']}")
    print("| Site | nearest PSR | PSR ≥ 1 km² | PSR ≥ 10 km² | PSR within 5 km | outlines |")
    print("|---|---:|---:|---:|---:|---:|")
    for s in doc["sites"]:
        print(f"| {s['name']} | {s['dAnyKm']:.1f} km | {s['d1Km']:.1f} km | {s['d10Km']:.1f} km | {s['psrKm2Within5']:.1f} km² | {len(s['outlines'])} |")
    for r in doc["regions"]:
        front = [c for c in r["candidates"] if c["pareto"]]
        print(f"  {r['siteId']:20s} front: " + " | ".join(f"{c['candidateId']}{'*' if c['isSite'] else ''} {c['sunPct']:.0f}% {c['d1Km']:.1f} km" for c in front))
    print(f"wrote {args.out.relative_to(REPO)} ({args.out.stat().st_size / 1024:.0f} KB) in {time.perf_counter() - t0:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
