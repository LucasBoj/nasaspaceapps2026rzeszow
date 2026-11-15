"""LRO Diviner surface temperatures at the landing sites: input of the heater (thermal) model.

NASA product: Diviner Lunar Radiometer Experiment, Polar Cumulative Product (PCP), diurnal,
south pole, binned by subsolar longitude:

* Williams, J.-P., Greenhagen, B. T., Paige, D. A., et al. (2019), "Seasonal polar temperatures
  on the Moon", JGR Planets 124, 2505-2521, doi:10.1029/2019JE006028.
* PDS4 bundle ``urn:nasa:pds:lro_diviner_derived1`` (doi:10.17189/wj0s-w188),
  PDS3 data set LRO-L-DLRE-5-GCP-V1.0. Files:
  https://pds-geosciences.wustl.edu/lro/urn-nasa-pds-lro_diviner_derived1/data_derived_pcp/diurnal/slon/pols/

``pcp_avg_tbol_pols_{sum|win}_slon{NN}_240.tab`` (NN = 01..96) hold the average bolometric
brightness temperature (K) of all nadir observations from 2009-07-05 to 2019-02-17 in 240 m bins,
for subsolar longitude (NN-1)*3.75 ... NN*3.75 deg east (label DLRE_SUNLON_MIN/MAX) and subsolar
latitude below zero (``sum``, southern summer) or above zero (``win``). Each row is
``x, y, clon, clat, tbol`` in a fixed 59-byte ASCII record after a 59-byte header line, sorted by
x and then y; x = r sin(lon), y = r cos(lon), r = 2 tan(colat / 2) (polar stereographic, distances
divided by 1737.4 km). Empty bins are left out.

Each file is ~190 MB (192 files, ~36 GB), so ``--refresh`` reads only what it needs with
multi-range HTTP requests: 350 evenly spaced records, then 35 records inside the bracket of each
target, then the ~260-record block around it (about 6 requests and 0.5 MB per file). The 240 m grid
is the same in every file, so the grid columns around each point are found once. The result, the
3x3 bins around each point for every subsolar-longitude bin and both seasons, is stored in
``data/diviner/pcp_sites.json``; everything else (``python diviner_temperatures.py``, the tests)
runs offline.

Output for the app: ``SeleneFrontend/wwwroot/data/thermal/diviner.json`` (format in README, Thermal).
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

import build

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
PDS_DIR = ("https://pds-geosciences.wustl.edu/lro/urn-nasa-pds-lro_diviner_derived1/"
           "data_derived_pcp/diurnal/slon/pols/")
CITATION = ("Williams, J.-P., et al. (2019), Seasonal polar temperatures on the Moon, "
            "JGR Planets 124, 2505-2521, doi:10.1029/2019JE006028")
DATASET = "LRO Diviner Polar Cumulative Product (PCP), PDS4 urn:nasa:pds:lro_diviner_derived1, doi:10.17189/wj0s-w188"
SNAPSHOT = HERE / "data" / "diviner" / "pcp_sites.json"
FRONTEND_JSON = REPO / "SeleneFrontend" / "wwwroot" / "data" / "thermal" / "diviner.json"

RECORD = 59                 # bytes per record (and of the header line)
BINS = 96                   # subsolar-longitude bins of 3.75 deg
BIN_DEG = 360.0 / BINS
SEASONS = ("sum", "win")    # sum: subsolar latitude < 0 (southern summer); win: > 0
R_MOON_M = 1737400.0
SAMPLES_GLOBAL = 350        # records per request stay well under the server's header limit
SAMPLES_BRACKET = 35

# IM-2 Athena (March 2025): planned and actual site, same points as case_study_im2.py.
REFERENCE_POINTS = [
    ("im2_target", "IM-2 planned site", -84.78, 29.13),
    ("im2_actual", "IM-2 actual site (Athena)", -84.7906, 29.1957),
]


def file_name(season: str, bin_no: int) -> str:
    return f"pcp_avg_tbol_pols_{season}_slon{bin_no:02d}_240.tab"


def stereo_xy(lat: float, lon: float) -> tuple[float, float]:
    """Normalised south polar stereographic coordinates as used by the PCP tables."""
    r = 2.0 * math.tan(math.radians(90.0 + lat) / 2.0)
    return r * math.sin(math.radians(lon)), r * math.cos(math.radians(lon))


def parse_record(text: str) -> tuple[float, float, float, float, float]:
    x, y, clon, clat, tbol = (float(v) for v in text.split(","))
    return x, y, clon, clat, tbol


# ----------------------------------------------------------------------------- HTTP

def _request(url: str, ranges: list[tuple[int, int]]) -> dict[int, bytes]:
    """Fetch byte ranges (inclusive) in one request; returns {start offset: bytes}."""
    spec = ",".join(f"{a}-{b}" for a, b in ranges)
    req = urllib.request.Request(url, headers={"User-Agent": "selene-ops-pipeline", "Range": f"bytes={spec}"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        if resp.status != 206:
            raise RuntimeError(f"{url}: server ignored the Range request (HTTP {resp.status})")
        ctype = resp.headers.get("Content-Type", "")
        body = resp.read()
        if "multipart/byteranges" not in ctype:
            m = re.match(r"bytes (\d+)-(\d+)/(\d+)", resp.headers.get("Content-Range", ""))
            if not m:
                raise RuntimeError(f"{url}: no Content-Range in a single-range reply")
            return {int(m.group(1)): body}
        boundary = re.search(r"boundary=(.+)$", ctype).group(1).strip().strip('"')
        return parse_multipart(body, boundary)


def parse_multipart(body: bytes, boundary: str) -> dict[int, bytes]:
    """{start offset: part bytes} of a multipart/byteranges body."""
    out: dict[int, bytes] = {}
    for part in body.split(b"--" + boundary.encode("ascii")):
        if b"Content-Range" not in part:
            continue
        head, _, data = part.partition(b"\r\n\r\n")
        m = re.search(rb"Content-Range: bytes (\d+)-(\d+)/(\d+)", head)
        start, end = int(m.group(1)), int(m.group(2))
        out[start] = data[:end - start + 1]
    return out


def file_size(url: str) -> int:
    req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "selene-ops-pipeline"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return int(resp.headers["Content-Length"])


# ----------------------------------------------------------------------------- remote sorted table

class RemoteTable:
    """A fixed-record PCP table read with range requests; records sorted by (x, y)."""

    def __init__(self, url: str, size: int):
        self.url = url
        self.count = (size - RECORD) // RECORD
        self._samples: list[tuple[int, float, float]] | None = None

    def _offset(self, k: int) -> int:
        return RECORD * (k + 1)

    def read_records(self, indices: list[int]) -> dict[int, tuple]:
        out = {}
        for chunk in _chunks(sorted(set(indices)), SAMPLES_GLOBAL):
            got = _request(self.url, [(self._offset(k), self._offset(k) + RECORD - 1) for k in chunk])
            for k in chunk:
                out[k] = parse_record(got[self._offset(k)].decode("ascii"))
        return out

    def read_blocks(self, blocks: list[tuple[int, int]]) -> list[list[tuple]]:
        """Records [a, b) for each block, in one multi-range request."""
        ranges = [(self._offset(a), self._offset(b) - 1) for a, b in blocks]
        got = _request(self.url, ranges)
        out = []
        for (a, b), (start, _) in zip(blocks, ranges):
            text = got[start].decode("ascii")
            out.append([parse_record(text[i * RECORD:(i + 1) * RECORD]) for i in range(b - a)])
        return out

    def samples(self) -> list[tuple[int, float, float]]:
        if self._samples is None:
            idx = [round(i * (self.count - 1) / (SAMPLES_GLOBAL - 1)) for i in range(SAMPLES_GLOBAL)]
            recs = self.read_records(idx)
            self._samples = [(k, recs[k][0], recs[k][1]) for k in idx]
        return self._samples

    def blocks_around(self, keys: list[tuple[float, float]], col_tol: float) -> list[list[tuple]]:
        """For each (x, y) key: the block of records around its position in the (x, y) order."""
        def less(rx: float, ry: float, key: tuple[float, float]) -> bool:
            kx, ky = key
            if abs(rx - kx) > col_tol:
                return rx < kx
            return ry < ky

        def bracket(points: list[tuple[int, float, float]], key) -> tuple[int, int]:
            lo, hi = 0, len(points) - 1
            if not less(points[lo][1], points[lo][2], key):
                return points[lo][0], points[lo][0] + 1
            if less(points[hi][1], points[hi][2], key):
                return points[hi][0], points[hi][0] + 1
            while hi - lo > 1:
                mid = (lo + hi) // 2
                if less(points[mid][1], points[mid][2], key):
                    lo = mid
                else:
                    hi = mid
            return points[lo][0], points[hi][0]

        coarse = [bracket(self.samples(), key) for key in keys]
        wanted = []
        for a, b in coarse:
            step = max(1, (b - a) // SAMPLES_BRACKET)
            wanted.extend(range(a, b + 1, step))
            wanted.append(b)
        recs = self.read_records(wanted)
        fine = []
        for (a, b), key in zip(coarse, keys):
            step = max(1, (b - a) // SAMPLES_BRACKET)
            pts = [(k, recs[k][0], recs[k][1]) for k in sorted(set(list(range(a, b + 1, step)) + [b]))]
            lo, hi = bracket(pts, key)
            fine.append((max(0, lo - 2), min(self.count, hi + 3)))
        return self.read_blocks(fine)


def _chunks(items: list, n: int):
    for i in range(0, len(items), n):
        yield items[i:i + n]


# ----------------------------------------------------------------------------- 3x3 bins

# Bin centres sit at integer multiples of this step in x and y (fitted on 350 records of
# sum_slon01: phase coherence 0.9999 for x and y, offset < 0.0001 step; = 240.04 m). Bins with no
# observations are missing from a file, so the grid, not the nearest record, defines a point's bin.
GRID_STEP = 0.000138158


@dataclass
class GridCell:
    """The bin containing a point and its 8 neighbours (same grid in every file)."""
    point_id: str
    columns: list[float]     # x of the column left of, at and right of the point's bin
    y: float                 # y of the point's bin centre
    step: float              # grid step (normalised)
    lat: float               # centre of the point's bin
    lon: float


def grid_cell(pid: str, lat: float, lon: float, step: float = GRID_STEP) -> GridCell:
    x, y = stereo_xy(lat, lon)
    xc, yc = round(x / step) * step, round(y / step) * step
    r = math.hypot(xc, yc)
    blat = -90.0 + math.degrees(2.0 * math.atan(r / 2.0))
    blon = math.degrees(math.atan2(xc, yc))
    return GridCell(pid, [xc - step, xc, xc + step], yc, step, blat, blon)


def window_3x3(block_rows: list[list[tuple]], cell: GridCell) -> list[float | None]:
    """Temperatures of the 3x3 bins (row-major: column left..right, y low..high); None = empty bin."""
    out: list[float | None] = []
    for rows, xc in zip(block_rows, cell.columns):
        for dy in (-1, 0, 1):
            yc = cell.y + dy * cell.step
            hit = [r for r in rows if abs(r[0] - xc) < cell.step / 3 and abs(r[1] - yc) < cell.step / 3]
            out.append(round(hit[0][4], 2) if hit else None)
    return out


def points() -> list[tuple[str, str, float, float]]:
    sites = json.loads(build.DEFAULT_SITES.read_text(encoding="utf-8"))
    return [(s["id"], s["name"], s["lat"], s["lon"]) for s in sites] + REFERENCE_POINTS


def refresh(workers: int = 8) -> dict:
    pts = points()
    cells = {pid: grid_cell(pid, lat, lon) for pid, _, lat, lon in pts}
    for pid, name, lat, lon in pts:
        c = cells[pid]
        d = _distance_m(lat, lon, c.lat, c.lon)
        print(f"  {pid:22s} bin centre {c.lat:.4f}, {c.lon:.4f}  ({d:.0f} m from the point)")

    def one(job: tuple[str, int]) -> tuple[str, int, dict]:
        season, b = job
        url = PDS_DIR + file_name(season, b)
        t = RemoteTable(url, file_size(url))
        keys = [(x, cells[pid].y) for pid, *_ in pts for x in cells[pid].columns]
        tol = min(c.step for c in cells.values()) / 3
        blocks = t.blocks_around(keys, col_tol=tol)
        res = {}
        for i, (pid, *_) in enumerate(pts):
            res[pid] = window_3x3(blocks[3 * i:3 * i + 3], cells[pid])
        return season, b, res

    jobs = [(s, b) for s in SEASONS for b in range(1, BINS + 1)]
    values = {pid: {s: [None] * BINS for s in SEASONS} for pid, *_ in pts}
    with ThreadPoolExecutor(max_workers=workers) as pool:
        for n, (season, b, res) in enumerate(pool.map(one, jobs), 1):
            for pid, win in res.items():
                values[pid][season][b - 1] = win
            if n % 24 == 0:
                print(f"  {n}/{len(jobs)} files")

    snapshot = {
        "fetched": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%MZ"),
        "source": PDS_DIR, "dataset": DATASET, "citation": CITATION,
        "binDeg": BIN_DEG, "seasons": {"sum": "subsolar latitude < 0", "win": "subsolar latitude > 0"},
        "window": "3x3 bins of 240 m, row-major: column x-1..x+1, y-1..y+1; index 4 = the bin containing the point",
        "points": [{"id": pid, "name": name, "lat": lat, "lon": lon,
                    "binLat": round(cells[pid].lat, 5), "binLon": round(cells[pid].lon, 5),
                    "binDistanceM": round(_distance_m(lat, lon, cells[pid].lat, cells[pid].lon)),
                    "sum": values[pid]["sum"], "win": values[pid]["win"]} for pid, name, lat, lon in pts],
    }
    SNAPSHOT.parent.mkdir(parents=True, exist_ok=True)
    SNAPSHOT.write_text(json.dumps(snapshot, indent=1), encoding="utf-8")
    print(f"wrote {SNAPSHOT.relative_to(REPO)}")
    return snapshot


def _distance_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dl = math.radians(lon2 - lon1)
    c = math.sin(p1) * math.sin(p2) + math.cos(p1) * math.cos(p2) * math.cos(dl)
    return R_MOON_M * math.acos(max(-1.0, min(1.0, c)))


# ----------------------------------------------------------------------------- app file

def fill_gaps(values: list[float | None]) -> tuple[list[float], int]:
    """Fill empty bins by linear interpolation around the (cyclic) subsolar-longitude circle."""
    n = len(values)
    known = [i for i, v in enumerate(values) if v is not None]
    if not known:
        raise ValueError("no data in any bin")
    out = list(values)
    filled = 0
    for i in range(n):
        if out[i] is not None:
            continue
        prev = max((k for k in known if k < i), default=known[-1] - n)
        nxt = min((k for k in known if k > i), default=known[0] + n)
        vp, vn = values[prev % n], values[nxt % n]
        out[i] = vp + (vn - vp) * (i - prev) / (nxt - prev)
        filled += 1
    return [round(float(v), 1) for v in out], filled


def season_curve(windows: list[list[float | None] | None]) -> tuple[list[float], dict]:
    """One season's curve: the point's bin, else the mean of its 8 neighbours, else interpolation along longitude."""
    values: list[float | None] = []
    counts = {"centre": 0, "neighbours": 0, "interpolated": 0}
    for w in windows:
        if w and w[4] is not None:
            values.append(w[4]); counts["centre"] += 1
        elif w and any(v is not None for v in w):
            nb = [v for v in w if v is not None]
            values.append(sum(nb) / len(nb)); counts["neighbours"] += 1
        else:
            values.append(None)
    curve, counts["interpolated"] = fill_gaps(values)
    return curve, counts


def _range_3x3(windows, pick) -> list[float | None]:
    return [pick(v for v in w if v is not None) if w and any(v is not None for v in w) else None for w in windows]


def subsolar_point(lat: float, lon: float, el_deg: np.ndarray, az_deg: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Subsolar latitude and east longitude (0..360) seen from a site: 90° - elevation along the Sun's azimuth."""
    phi = math.radians(lat)
    delta = np.radians(90.0 - el_deg)
    az = np.radians(az_deg)
    sin_lat = math.sin(phi) * np.cos(delta) + math.cos(phi) * np.sin(delta) * np.cos(az)
    slat = np.arcsin(np.clip(sin_lat, -1, 1))
    dlon = np.arctan2(np.sin(az) * np.sin(delta) * math.cos(phi), np.cos(delta) - math.sin(phi) * sin_lat)
    return np.degrees(slat), (lon + np.degrees(dlon)) % 360.0


def temp_at(summer: list[float], winter: list[float], slat: np.ndarray, slon: np.ndarray) -> np.ndarray:
    """Curve value per sample (same rule as the frontend's DivinerPoint.TempK)."""
    n = len(summer)
    pos = slon / (360.0 / n) - 0.5
    k = np.floor(pos).astype(int)
    f = pos - k
    out = np.empty(len(slat))
    for curve, mask in ((np.array(summer), slat < 0), (np.array(winter), slat >= 0)):
        a, b = curve[k % n], curve[(k + 1) % n]
        out[mask] = (a + (b - a) * f)[mask]
    return out


def lit_vs_dark(site_id: str, lat: float, lon: float, summer: list[float], winter: list[float],
                years: tuple[int, ...] = (2027, 2028)) -> dict | None:
    """Independent check: Diviner ground temperature when our LOLA horizon model says the site is lit vs dark."""
    el, az, sf = [], [], []
    for y in years:
        f = build.DEFAULT_OUT / site_id / f"{y}.json"
        if not f.exists():
            return None
        d = json.loads(f.read_text(encoding="utf-8"))
        el += d["sunElevCentiDeg"]; az += d["sunAzCentiDeg"]; sf += d["sunFraction"]
    el_a, az_a, sf_a = np.array(el) / 100.0, np.array(az) / 100.0, np.array(sf)
    slat, slon = subsolar_point(lat, lon, el_a, az_a)
    t = temp_at(summer, winter, slat, slon)
    lit, dark = t[sf_a >= 50], t[sf_a < 50]
    if len(dark) == 0 or len(lit) == 0:
        return None
    return {"years": f"{years[0]}-{years[-1]}", "litMeanK": round(float(lit.mean()), 1),
            "darkMeanK": round(float(dark.mean()), 1), "darkP90K": round(float(np.percentile(dark, 90)), 1),
            "darkHours": int(len(dark)), "litHours": int(len(lit))}


def app_file(snapshot: dict) -> dict:
    """Compact per-point curves for the frontend (bin containing the point; 3x3 min and max for the robustness note)."""
    out_points = []
    for p in snapshot["points"]:
        entry = {"id": p["id"], "name": p["name"], "binLat": p["binLat"], "binLon": p["binLon"],
                 "binDistanceM": p["binDistanceM"]}
        for season, key in (("sum", "summerK"), ("win", "winterK")):
            entry[key], entry[key + "Bins"] = season_curve(p[season])
            entry[key + "Filled"] = entry[key + "Bins"]["interpolated"]
            entry[key + "Min3x3"], _ = fill_gaps(_range_3x3(p[season], min))
            entry[key + "Max3x3"], _ = fill_gaps(_range_3x3(p[season], max))
        all_k = entry["summerK"] + entry["winterK"]
        entry["minK"], entry["maxK"] = min(all_k), max(all_k)
        entry["meanK"] = round(sum(all_k) / len(all_k), 1)
        check = lit_vs_dark(p["id"], p["lat"], p["lon"], entry["summerK"], entry["winterK"])
        if check:
            entry["check"] = check
        out_points.append(entry)
    return {
        "source": snapshot["source"], "dataset": snapshot["dataset"], "citation": snapshot["citation"],
        "fetched": snapshot["fetched"], "binDeg": snapshot["binDeg"],
        "note": ("Average bolometric surface temperature (K) of the 240 m Diviner bin containing each point, per "
                 "3.75 deg bin of subsolar longitude (east, bin i covers i*3.75 .. (i+1)*3.75 deg) and season "
                 "(summer: subsolar latitude < 0), from 2009-2019 nadir observations. Bins without an observation "
                 "take the mean of the 8 neighbouring 240 m bins, else linear interpolation along the subsolar "
                 "longitude (*Bins counts). Min3x3/Max3x3: range of the 3x3 bins (+-240 m). check: Diviner "
                 "temperature in hours our LOLA horizon model calls lit (sunFraction >= 50) vs dark."),
        "points": out_points,
    }


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--refresh", action="store_true", help="re-read the Diviner tables over HTTP")
    p.add_argument("--workers", type=int, default=8)
    args = p.parse_args(argv)
    snapshot = refresh(args.workers) if args.refresh else json.loads(SNAPSHOT.read_text(encoding="utf-8"))
    app = app_file(snapshot)
    FRONTEND_JSON.parent.mkdir(parents=True, exist_ok=True)
    app = {**app, "provenance": build.PROVENANCE} if isinstance(app, dict) else app
    FRONTEND_JSON.write_text(json.dumps(app, separators=(",", ":")), encoding="utf-8")
    print(f"wrote {FRONTEND_JSON.relative_to(REPO)}")
    for e in app["points"]:
        c = e.get("check")
        chk = f"  lit {c['litMeanK']:5.1f} K / dark {c['darkMeanK']:5.1f} K (p90 {c['darkP90K']:5.1f})" if c else ""
        print(f"  {e['id']:22s} min {e['minK']:6.1f} K  mean {e['meanK']:6.1f} K  max {e['maxK']:6.1f} K  "
              f"interp {e['summerKFilled']}+{e['winterKFilled']}{chk}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
