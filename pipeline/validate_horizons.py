"""Cross-check the pipeline's Sun/Earth geometry against NASA/JPL Horizons.

For every landing site in ``SeleneFrontend/wwwroot/data/sites.json`` and a fixed
set of epochs in 2027–2028, this script compares the apparent azimuth/elevation
of the Sun (Horizons id 10) and Earth (399) from ``ephemeris.MoonSky.altaz``
with the same quantity computed independently by Horizons.

Usage (from ``pipeline/``)::

    python validate_horizons.py            # offline: read snapshots in data/horizons/
    python validate_horizons.py --refresh  # re-query the Horizons API, overwrite snapshots

Only the public site coordinates (from ``sites.json``) and epochs are sent to
the API. The raw text responses are stored unmodified in ``data/horizons/`` so
``tests/test_horizons.py`` and the demo work offline.

What Horizons computes (established from its response headers, see any
snapshot file):

* **Site frame — Mean-Earth (ME), not Principal-Axis (PA).** With
  ``CENTER='coord@301'`` and ``COORD_TYPE='GEODETIC'`` the header reports
  ``Center pole/equ : MEAN_ME (high precision)``, i.e. ``SITE_COORD`` is read as
  east-longitude / latitude in the lunar Mean-Earth/polar-axis frame realised
  with the high-precision DE44x lunar orientation. That is the same convention as
  ``MOON_ME_DE421`` used by ``ephemeris.py`` and by LOLA/LRO products. We verify
  it empirically too: the script also evaluates the pipeline with the site in
  ``MOON_PA_DE421``. ME residuals are ~1e-5°, PA residuals ~0.02° (PA and ME
  differ by a rotation of that size, which tilts a south-polar observer's local
  horizon), so Horizons is unambiguously ME.
* **Reference surface — sphere R = 1737.4 km** (``Center radii`` line), so geodetic
  and planetocentric latitude coincide and the local zenith is radial, as in
  Skyfield's ``build_latlon_degrees``. We send altitude 0 km; the pipeline's 2 m
  observer height changes these angles by < 1e-5° and is ignored.
* **Azimuth convention** — "measured clockwise from north: North(0) → East(90) →
  South(180) → West(270)", identical to Skyfield's ``altaz()`` and to the
  ``sunAzCentiDeg`` / ``earthAzCentiDeg`` fields. No conversion needed.
* **Elevation** — "with respect to a plane perpendicular to the reference surface
  local zenith", airless (``Atmos refraction: NO (AIRLESS)``), apparent: light
  time, stellar aberration and gravitational deflection by Sun and Earth.
  Skyfield's ``.observe().apparent()`` applies light time, aberration and
  gravitational deflection as well, so the two models compute the same quantity.
* **Time scale** — ``TIME_TYPE='UT'``: epochs are UTC, as ``ts.utc()`` in Skyfield.
  Both use the last known leap second for the future, so they agree.
* **Ephemeris** — Horizons uses DE441 (``{source: DE441}``) and its own lunar
  orientation; the pipeline uses DE421 + ``moon_pa_de421_1900-2050.bpc``. The
  residuals therefore measure DE421-vs-DE441 differences in Moon position and
  lunar orientation plus any modelling difference.

Longitudes are sent normalised to [0, 360) east, which Horizons echoes back in
the ``Center geodetic`` line; each snapshot's site and epochs are checked
against the current ``sites.json`` before use, so a stale snapshot fails loudly.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np

import ephemeris

ROOT = Path(__file__).resolve().parent
SITES_JSON = ROOT.parent / "SeleneFrontend" / "wwwroot" / "data" / "sites.json"
SNAPSHOT_DIR = ROOT / "data" / "horizons"
API_URL = "https://ssd.jpl.nasa.gov/api/horizons.api"

BODIES = {"sun": "10", "earth": "399"}

# Seven dates spread over the shipped range 2026-11 … 2035 (different seasons and libration phases),
# six hourly samples each → 42 epochs per site and body.
DATES = ("2026-11-20", "2027-02-01", "2027-08-15", "2028-03-20", "2028-10-05", "2031-06-15", "2035-12-15")
HOURS = range(6)

# Above this elevation azimuth becomes ill-conditioned (it is undefined at the
# zenith). At our south-polar sites Sun and Earth stay below ~12°, so this mask
# excludes nothing today; it only matters if a lower-latitude site is added.
AZ_MAX_ELEV_DEG = 80.0

ILLUMINATION_DIR = ROOT.parent / "SeleneFrontend" / "wwwroot" / "data" / "illumination"
JSON_FIELDS = {"sun": ("sunElevCentiDeg", "sunAzCentiDeg"), "earth": ("earthElevCentiDeg", "earthAzCentiDeg")}


def epochs() -> list[datetime]:
    """All comparison epochs (UTC, aware)."""
    out = []
    for d in DATES:
        base = datetime.fromisoformat(d).replace(tzinfo=timezone.utc)
        out.extend(base + timedelta(hours=h) for h in HOURS)
    return out


def load_sites(path: Path = SITES_JSON) -> list[dict]:
    """Sites as ``[{'id', 'lat', 'lon'}, ...]`` from ``sites.json``."""
    data = json.loads(path.read_text(encoding="utf-8"))
    return [{"id": s["id"], "lat": float(s["lat"]), "lon": float(s["lon"])} for s in data]


def snapshot_path(site_id: str, body: str) -> Path:
    return SNAPSHOT_DIR / f"{site_id}_{body}.txt"


# --------------------------------------------------------------------------- Horizons I/O

def fetch(site: dict, body: str, times: list[datetime]) -> str:
    """Query Horizons for one site and body; returns the raw text response."""
    tlist = " ".join(f"'{t:%Y-%m-%d %H:%M}'" for t in times)
    params = {
        "format": "text",
        "COMMAND": f"'{BODIES[body]}'",
        "OBJ_DATA": "'NO'",
        "MAKE_EPHEM": "'YES'",
        "EPHEM_TYPE": "'OBSERVER'",
        "CENTER": "'coord@301'",
        "COORD_TYPE": "'GEODETIC'",
        "SITE_COORD": f"'{site['lon'] % 360.0:.5f},{site['lat']:.5f},0'",
        "TLIST_TYPE": "'CAL'",
        "TIME_TYPE": "'UT'",
        "TLIST": tlist,
        "QUANTITIES": "'4'",
        "EXTRA_PREC": "'YES'",
        "CSV_FORMAT": "'NO'",
    }
    url = f"{API_URL}?{urllib.parse.urlencode(params)}"
    req = urllib.request.Request(url, headers={"User-Agent": "selene-pipeline/1.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        text = resp.read().decode("utf-8")
    if "$$SOE" not in text:
        raise RuntimeError(f"Horizons returned no ephemeris for {site['id']}/{body}:\n{text[:2000]}")
    return text


_FLOAT = re.compile(r"^-?\d+\.\d+$")
_GEODETIC = re.compile(r"^Center geodetic\s*:\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)", re.M)


@dataclass
class HorizonsTable:
    site_lon: float          # as echoed by Horizons (east, 0..360)
    site_lat: float
    times: list[datetime]    # UTC
    az: np.ndarray           # deg, clockwise from north
    el: np.ndarray           # deg


def parse(text: str) -> HorizonsTable:
    """Parse a Horizons QUANTITIES='4' observer table (azimuth, elevation)."""
    m = _GEODETIC.search(text)
    if not m:
        raise ValueError("no 'Center geodetic' line in Horizons response")
    body = text.split("$$SOE", 1)[1].split("$$EOE", 1)[0]
    times, az, el = [], [], []
    for line in body.strip().splitlines():
        # "<date> <time> [solar/interferer markers like '*', '*i', 'i'] <az> <el>"
        tok = line.split()
        if len(tok) < 4 or not (_FLOAT.match(tok[-2]) and _FLOAT.match(tok[-1])):
            raise ValueError(f"unparsable Horizons row: {line!r}")
        clock = tok[1]
        fmt = "%H:%M:%S.%f" if "." in clock else ("%H:%M:%S" if clock.count(":") == 2 else "%H:%M")
        times.append(datetime.strptime(f"{tok[0]} {clock}", f"%Y-%b-%d {fmt}").replace(tzinfo=timezone.utc))
        az.append(float(tok[-2]))
        el.append(float(tok[-1]))
    return HorizonsTable(float(m.group(1)), float(m.group(2)), times, np.array(az), np.array(el))


def load_snapshot(site: dict, body: str, times: list[datetime]) -> HorizonsTable:
    """Read and validate one snapshot against the current site and epoch list."""
    path = snapshot_path(site["id"], body)
    if not path.exists():
        raise FileNotFoundError(f"missing Horizons snapshot {path.name} — run: python validate_horizons.py --refresh")
    table = parse(path.read_text(encoding="utf-8"))
    lon_diff = (table.site_lon - site["lon"] + 180.0) % 360.0 - 180.0
    if abs(lon_diff) > 1e-4 or abs(table.site_lat - site["lat"]) > 1e-4:
        raise ValueError(f"{path.name} is for ({table.site_lat}, {table.site_lon}), sites.json has "
                         f"({site['lat']}, {site['lon']}) — run: python validate_horizons.py --refresh")
    if table.times != times:
        raise ValueError(f"{path.name} epochs differ from DATES/HOURS — run: python validate_horizons.py --refresh")
    return table


# --------------------------------------------------------------------------- comparison

@dataclass
class Residuals:
    body: str
    site_ids: list[str]      # one per sample
    el_pipeline: np.ndarray
    d_el: np.ndarray         # pipeline − Horizons, deg
    d_az: np.ndarray         # pipeline − Horizons, wrapped to (−180, 180], deg
    az_ok: np.ndarray        # mask: azimuth well-conditioned (el < AZ_MAX_ELEV_DEG)


def compare(sky: ephemeris.MoonSky, sites: list[dict], body: str,
            tables: dict[str, HorizonsTable], times: list[datetime]) -> Residuals:
    """Pipeline (``sky``) minus Horizons for every site × epoch of ``body``."""
    t = sky.timescale.from_datetimes(times)
    ids, el_p, d_el, d_az = [], [], [], []
    for site in sites:
        alt, az = sky.altaz(site["lat"], site["lon"], t, body)
        ref = tables[site["id"]]
        ids += [site["id"]] * len(times)
        el_p.append(alt)
        d_el.append(alt - ref.el)
        d_az.append((az - ref.az + 180.0) % 360.0 - 180.0)
    el_p = np.concatenate(el_p)
    return Residuals(body, ids, el_p, np.concatenate(d_el), np.concatenate(d_az), el_p < AZ_MAX_ELEV_DEG)


def compare_shipped(sites: list[dict], body: str, tables: dict[str, HorizonsTable],
                    times: list[datetime], illum_dir: Path = ILLUMINATION_DIR) -> Residuals | None:
    """Shipped ``illumination/{siteId}/{year}.json`` (centi-degrees) minus Horizons.

    Returns None when a file is missing or was generated for other coordinates
    than the current ``sites.json`` (then it cannot be compared like-for-like).
    """
    el_key, az_key = JSON_FIELDS[body]
    ids, el_p, d_el, d_az = [], [], [], []
    for site in sites:
        years: dict[int, dict] = {}
        el, az = [], []
        for t in times:                                   # one file per calendar year (contract 2026-11-14)
            if t.year not in years:
                path = illum_dir / site["id"] / f"{t.year}.json"
                if not path.exists():
                    return None
                years[t.year] = json.loads(path.read_text(encoding="utf-8"))
            data = years[t.year]
            if abs(data["lat"] - site["lat"]) > 1e-6 or abs(data["lon"] - site["lon"]) > 1e-6 or az_key not in data:
                return None
            i = round((t - ephemeris.parse_utc(data["start"])).total_seconds() / 3600.0 / data["stepHours"])
            el.append(data[el_key][i] / 100.0)
            az.append(data[az_key][i] / 100.0)
        el, az = np.array(el), np.array(az)
        ref = tables[site["id"]]
        ids += [site["id"]] * len(times)
        el_p.append(el)
        d_el.append(el - ref.el)
        d_az.append((az - ref.az + 180.0) % 360.0 - 180.0)
    el_p = np.concatenate(el_p)
    return Residuals(body, ids, el_p, np.concatenate(d_el), np.concatenate(d_az), el_p < AZ_MAX_ELEV_DEG)


def stats(r: Residuals) -> dict:
    """Max / mean absolute residuals (deg) and sample counts."""
    return {
        "n": int(r.d_el.size),
        "max_el": float(np.max(np.abs(r.d_el))),
        "mean_el": float(np.mean(np.abs(r.d_el))),
        "n_az": int(r.az_ok.sum()),
        "max_az": float(np.max(np.abs(r.d_az[r.az_ok]))),
        "mean_az": float(np.mean(np.abs(r.d_az[r.az_ok]))),
        "el_range": (float(r.el_pipeline.min()), float(r.el_pipeline.max())),
    }


def load_all(sites: list[dict], times: list[datetime]) -> dict[str, dict[str, HorizonsTable]]:
    return {body: {s["id"]: load_snapshot(s, body, times) for s in sites} for body in BODIES}


def refresh(sites: list[dict], times: list[datetime]) -> None:
    SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    for site in sites:
        for body in BODIES:
            text = fetch(site, body, times)
            parse(text)  # fail before writing anything unparsable
            snapshot_path(site["id"], body).write_text(text, encoding="utf-8", newline="\n")
            print(f"  fetched {site['id']}/{body}")
            time.sleep(1.0)  # be polite to the public API


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--refresh", action="store_true", help="re-query Horizons and overwrite snapshots")
    parser.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR)
    args = parser.parse_args()

    sites, times = load_sites(), epochs()
    if args.refresh:
        print(f"Querying Horizons for {len(sites)} sites × {len(BODIES)} bodies × {len(times)} epochs")
        refresh(sites, times)

    sky_me = ephemeris.load_moon_sky(args.kernels)
    sky_pa = dataclasses.replace(sky_me, frame=sky_me.constants.build_frame_named("MOON_PA_DE421"))
    tables = load_all(sites, times)

    def row(label: str, r: Residuals) -> str:
        s = stats(r)
        lo, hi = s["el_range"]
        cells = [f"{s[k]:.6f}° ({s[k] * 3600:.3f}″)" for k in ("max_el", "mean_el", "max_az", "mean_az")]
        return f"| {label} | {s['n']} | {lo:.1f}° … {hi:.1f}° | " + " | ".join(cells) + " |"

    header = ("| Comparison | Samples | Elevation range | max \\|Δel\\| | mean \\|Δel\\| | max \\|Δaz\\| | mean \\|Δaz\\| |\n"
              "|---|---:|---:|---:|---:|---:|---:|")
    print(f"\n{len(sites)} sites × {len(times)} epochs ({', '.join(DATES)}, {len(HOURS)} h each), minus Horizons\n")
    print(header)
    for body in BODIES:
        print(row(f"{body.capitalize()}: pipeline (DE421, MOON_ME_DE421)", compare(sky_me, sites, body, tables[body], times)))
    for body in BODIES:
        shipped = compare_shipped(sites, body, tables[body], times)
        if shipped is None:
            print(f"| {body.capitalize()}: shipped JSON | — | skipped: illumination files missing or for other coordinates | | | | |")
        else:
            print(row(f"{body.capitalize()}: shipped JSON (0.01° steps)", shipped))
    for body in BODIES:
        print(row(f"{body.capitalize()}: site in MOON_PA_DE421 (wrong frame)", compare(sky_pa, sites, body, tables[body], times)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
