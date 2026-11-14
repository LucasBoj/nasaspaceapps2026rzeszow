"""Import the published candidate landing sites of Wueller et al. (2026).

Source paper:
    Wueller, L., Berger, L. M., Christopher, H., et al. (2026). Assessing Potential
    Landing Sites With Favorable Illumination and Accessible, Potentially Volatile-Rich
    Permanently Shadowed Regions Within Artemis Candidate Landing Regions.
    JGR Planets, 131(3), e2025JE009434. doi:10.1029/2025JE009434

Data (CC-BY-4.0), the authors' georeferenced release of Table A1:
    Zenodo record 17084058, doi:10.5281/zenodo.17084058, file Landing_Sites.zip
    -> nested Landing_Sites.zip -> LandingSites.{shp,shx,dbf,prj}

The shapefile is in a south-polar stereographic projection with true scale at −70°.
Coordinates are reprojected with the .prj from the archive and cross-checked against
the Latitude/Longitude attribute columns.

Usage::

    python import_wueller_sites.py           # writes data/wueller2026_sites.csv
"""

from __future__ import annotations

import argparse
import csv
import io
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

import pyproj
import shapefile  # pyshp

HERE = Path(__file__).resolve().parent
ZENODO_URL = "https://zenodo.org/api/records/17084058/files/Landing_Sites.zip/content"
DEFAULT_CACHE = HERE / "cache" / "zenodo_17084058_Landing_Sites.zip"
DEFAULT_OUT = HERE / "data" / "wueller2026_sites.csv"
SHP_MEMBERS = ("LandingSites.shp", "LandingSites.shx", "LandingSites.dbf", "LandingSites.prj")

# Paper region codes -> SELENE region ids (NASA's 2024 list of nine Artemis III regions).
REGION_MAP = {
    "PCB": "cabeus_b",
    "HW": "haworth",
    "MMA": "malapert_massif",
    "MMP": "mons_mouton_plateau",
    "MMO": "mons_mouton",
    "NR1": "nobile_rim_1",
    "NR2": "nobile_rim_2",
    "dGR2": "degerlache_rim_2",
    "SP": "slater_plain",
}

HEADER = [
    "# Candidate landing sites (Table A1) from Wueller et al. (2026), JGR Planets 131(3), e2025JE009434,",
    "# doi:10.1029/2025JE009434. Data: Zenodo doi:10.5281/zenodo.17084058 (Landing_Sites.zip, CC-BY-4.0).",
    "# region = code used in the paper; selene_region = SELENE site id (empty = region not in NASA's 2024 list of 9).",
    "# lat/lon: planetocentric degrees, reprojected from LandingSites.shp with its .prj.",
    "# sun_days_2027/2028: the authors' SunDays27/SunDays28 attributes (see the paper for the definition).",
]
COLUMNS = ["region", "site_id", "lat", "lon", "selene_region", "sun_days_2027", "sun_days_2028"]


def download(url: str, target: Path) -> Path:
    """Download the Zenodo archive once (cached)."""
    if target.exists() and target.stat().st_size > 0:
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix(".part")
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "selene-pipeline/1.0"})
        with urllib.request.urlopen(req, timeout=180) as resp, open(tmp, "wb") as out:
            while chunk := resp.read(1 << 20):
                out.write(chunk)
    except (urllib.error.URLError, OSError) as exc:
        tmp.unlink(missing_ok=True)
        raise SystemExit(f"ERROR: could not download {url}: {exc}") from exc
    tmp.replace(target)
    return target


def read_shapefile_members(outer_zip: Path) -> dict[str, bytes]:
    """Return the LandingSites.* files from the nested zip without extracting the 0.9 GB raster."""
    with zipfile.ZipFile(outer_zip) as outer:
        inner_bytes = outer.read("Landing_Sites.zip")
    with zipfile.ZipFile(io.BytesIO(inner_bytes)) as inner:
        return {name: inner.read(name) for name in SHP_MEMBERS}


def load_sites(members: dict[str, bytes]) -> list[dict]:
    """Parse points, reproject with the shipped .prj and cross-check the attribute lat/lon."""
    reader = shapefile.Reader(shp=io.BytesIO(members["LandingSites.shp"]),
                              shx=io.BytesIO(members["LandingSites.shx"]),
                              dbf=io.BytesIO(members["LandingSites.dbf"]))
    crs = pyproj.CRS.from_wkt(members["LandingSites.prj"].decode("utf-8"))
    to_geo = pyproj.Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)

    sites = []
    for sr in reader.iterShapeRecords():
        rec = sr.record.as_dict()
        x, y = sr.shape.points[0]
        lon, lat = to_geo.transform(x, y)
        if abs(lat - rec["Latitude"]) > 1e-3 or abs(((lon - rec["Longitude"] + 180) % 360) - 180) > 1e-3:
            raise SystemExit(f"ERROR: {rec['Name']}: geometry ({lat:.4f}, {lon:.4f}) disagrees with attributes "
                             f"({rec['Latitude']}, {rec['Longitude']})")
        sites.append({
            "region": rec["Landing_Re"],
            "site_id": rec["Name"],
            "lat": round(lat, 5),
            "lon": round(lon, 5),
            "selene_region": REGION_MAP.get(rec["Landing_Re"], ""),
            "sun_days_2027": rec.get("SunDays27"),
            "sun_days_2028": rec.get("SunDays28"),
        })
    return sites


def write_csv(sites: list[dict], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as fh:
        fh.write("\n".join(HEADER) + "\n")
        writer = csv.DictWriter(fh, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(sorted(sites, key=lambda s: (s["region"], s["site_id"])))


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--zip", type=Path, help="use an already downloaded Landing_Sites.zip instead of Zenodo")
    p.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = p.parse_args(argv)

    archive = args.zip or download(ZENODO_URL, DEFAULT_CACHE)
    sites = load_sites(read_shapefile_members(archive))
    write_csv(sites, args.out)

    covered = sorted({s["selene_region"] for s in sites if s["selene_region"]})
    missing = sorted(set(REGION_MAP.values()) - set(covered))
    print(f"Wrote {len(sites)} sites from {len({s['region'] for s in sites})} regions to {args.out}")
    for code, rid in REGION_MAP.items():
        n = sum(1 for s in sites if s["region"] == code)
        print(f"  {rid:22s} ({code:4s}) {n:3d} candidate sites")
    if missing:
        print(f"Regions WITHOUT published candidates (grid fallback): {', '.join(missing)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
