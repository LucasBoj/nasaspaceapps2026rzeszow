"""Case study: Intuitive Machines IM-2 "Athena" (Nova-C), March 2025 — planned vs actual landing site.

Computes, with exactly the method used for the 9 regions (``build.build_site``: LOLA 120 m horizon,
150 km rays, observer 2 m, JPL DE421 via Skyfield), the hourly Sun/Earth series for

* the planned site  -84.78°, 29.13°E  — LROC image 1401 "IM-2 Landing Region" (26 Feb 2025),
  given to 0.01° (about ±150 m);
* the actual site   -84.7906°, 29.1957°E — LROC image 1408 "IM-2 Athena Seen Obliquely" (10 Mar 2025):
  the lander "ended up on its side within a 20-meter diameter crater".

Period: from 2025-03-06T17:00Z (touchdown ~17:30 UTC, NASA: "approximately 11:30 a.m." CST) for 15 days
(Nova-C design life 14 d, landers.json). Contact ended 2025-03-07 06:15 UTC (NASA: "12:15 a.m. CST
Friday"), so Athena operated ~12.75 h; that window is summarised separately.

Outputs (the illumination/* contract format, but a 2025 period and not one of the 9 regions):
``SeleneFrontend/wwwroot/data/casestudy/im2/{target,actual}.json`` plus ``case.json`` (sources, the
measured offset, window statistics, the 20 m crater geometry and, with network access, NASA's long-term
LOLA illumination at both points), and the chart ``pipeline/docs/im2_case_study.png``.

The 20 m crater is far below the 120 m DEM resolution, so the horizon cannot contain it. Instead of
assuming a crater depth (no verifiable source), we report the depth above which the floor centre of a
20 m crater sees no Sun at all during the window: depth > 10 m × tan(max Sun elevation + solar radius).

Usage (from ``pipeline/``)::

    python case_study_im2.py --dem dem/ldem_75s_120m_float.lbl [--no-nasa]
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np

import build
import ephemeris
import horizon
import illumination

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
OUT_DIR = REPO / "SeleneFrontend" / "wwwroot" / "data" / "casestudy" / "im2"
CHART_PNG = HERE / "docs" / "im2_case_study.png"
DEFAULT_DEM = HERE / "dem" / "ldem_75s_120m_float.lbl"

LROC_1401 = "https://lroc.im-ldi.com/images/1401"
LROC_1408 = "https://lroc.im-ldi.com/images/1408"
NASA_RELEASE = "https://www.nasa.gov/news-release/nasa-receives-some-data-before-intuitive-machines-ends-lunar-mission/"
WIKI_IM2 = "https://en.wikipedia.org/wiki/IM-2"

POINTS = [
    {"id": "target", "name": "Planned site (IM-2 target)", "lat": -84.78, "lon": 29.13,
     "source": "LROC image 1401 'IM-2 Landing Region' (26 Feb 2025): intended landing site (-84.78°, 29.13°E); "
               "given to 0.01° (about ±150 m)", "sourceUrl": LROC_1401},
    {"id": "actual", "name": "Actual site (Athena)", "lat": -84.7906, "lon": 29.1957,
     "source": "LROC image 1408 'IM-2 Athena Seen Obliquely' (10 Mar 2025): on its side within a 20-meter diameter "
               "crater at 84.7906° S, 29.1957° E", "sourceUrl": LROC_1408},
]

START = datetime(2025, 3, 6, 17, 0, tzinfo=timezone.utc)
DAYS = 15
LANDING = datetime(2025, 3, 6, 17, 30, tzinfo=timezone.utc)      # NASA: "approximately 11:30 a.m." CST
CONTACT_END = datetime(2025, 3, 7, 6, 15, tzinfo=timezone.utc)   # NASA: "12:15 a.m. CST Friday"
CRATER_DIAMETER_M = 20.0                                         # LROC 1408
R_MOON_M = 1737400.0


def offset_m(a: dict, b: dict) -> float:
    """Great-circle distance on the LOLA reference sphere (m)."""
    p1, p2 = math.radians(a["lat"]), math.radians(b["lat"])
    dl = math.radians(b["lon"] - a["lon"])
    h = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * R_MOON_M * math.asin(math.sqrt(h))


def window_stats(doc: dict, first: int, count: int) -> dict:
    """Statistics over samples [first, first+count) of a series document."""
    sl = slice(first, first + count)
    sun = np.asarray(doc["sunFraction"][sl])
    el = np.asarray(doc["sunElevCentiDeg"][sl]) / 100.0
    az = np.asarray(doc["sunAzCentiDeg"][sl]) / 100.0
    e_el = np.asarray(doc["earthElevCentiDeg"][sl]) / 100.0
    e_az = np.asarray(doc["earthAzCentiDeg"][sl]) / 100.0
    hz = np.asarray(doc["horizonDeg"], dtype=float)
    sun_clear = el - illumination.horizon_at(hz, az)
    earth_clear = e_el - illumination.horizon_at(hz, e_az)
    max_el = float(el.max())
    # Floor centre of a 20 m crater: the rim (radius 10 m) hides the Sun if depth > 10 m · tan(el + r).
    crater_depth = (CRATER_DIAMETER_M / 2) * math.tan(math.radians(max(0.0, max_el + illumination.SUN_ANGULAR_RADIUS_DEG)))
    return {
        "hours": int(count),
        "sunAnyPct": round(float((sun > 0).mean() * 100), 1),
        "sunLitPct": round(float((sun >= 50).mean() * 100), 1),
        "meanSunFractionPct": round(float(sun.mean()), 1),
        "earthPct": round(float(np.asarray(doc["earthVisible"][sl]).mean() * 100), 1),
        "sunElevMinDeg": round(float(el.min()), 2),
        "sunElevMaxDeg": round(max_el, 2),
        "sunClearanceMinDeg": round(float(sun_clear.min()), 2),
        "earthClearanceMinDeg": round(float(earth_clear.min()), 2),
        "sunAzimuthRangeDeg": [round(float(az.min()), 1), round(float(az.max()), 1)],
        "craterFloorDarkIfDeeperThanM": round(crater_depth, 2),
    }


def nasa_long_term(points: list[dict]) -> dict | None:
    """NASA LOLA 120 m average Sun/Earth visibility (Mazarico et al. 2011) at both points, or None offline."""
    try:
        import compare_nasa_illumination as cmp
        out = {"source": cmp.CITATION, "url": cmp.PGDA_URL, "map": "AVGVISIB_75S_120M_201608 (+ _EARTH), 120 m",
               "points": {}}
        for key, stem in (("sun", "avgvisib_75s_120m_201608"), ("earth", "avgvisib_75s_120m_201608_earth")):
            geom = cmp.map_geometry(stem)
            for p in points:
                r, c = cmp.pixel_of(geom, p["lat"], p["lon"])
                w = cmp.window_3x3(stem, geom, r, c)
                out["points"].setdefault(p["id"], {})[key] = {
                    "pct": round(float(w[1, 1]) * 100, 1),
                    "rangePct": [round(float(w.min()) * 100, 1), round(float(w.max()) * 100, 1)],
                }
        return out
    except Exception as e:   # network or map failure: the case study works without it
        print(f"  NASA map sampling skipped: {e}", file=sys.stderr)
        return None


def chart(docs: dict, window_h: float, path: Path = CHART_PNG) -> None:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    hours = 72
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(10, 5.6), dpi=150, sharex=True)
    for pid, color in (("target", "#4fc3f7"), ("actual", "#ffb020")):
        d = docs[pid]
        t = np.arange(hours)
        el = np.asarray(d["sunElevCentiDeg"][:hours]) / 100
        hz = illumination.horizon_at(np.asarray(d["horizonDeg"]), np.asarray(d["sunAzCentiDeg"][:hours]) / 100)
        ax1.plot(t, el, color=color, label=f"{d['name']}: Sun elevation")
        ax1.plot(t, hz, color=color, ls="--", lw=1, label=f"{d['name']}: terrain horizon at the Sun's azimuth")
        if pid == "actual":
            depth = (CRATER_DIAMETER_M / 2) * np.tan(np.radians(np.maximum(0, el + illumination.SUN_ANGULAR_RADIUS_DEG)))
            ax2.plot(t, depth, color=color, label="20 m crater: floor centre in shadow if deeper than this")
    for ax in (ax1, ax2):
        ax.axvspan(0.5, 0.5 + window_h, color="#7f93a6", alpha=0.15)
        ax.grid(alpha=0.3)
    ax1.set_ylabel("degrees")
    ax1.set_title("IM-2 Athena, from 2025-03-06 17:00 UTC (shaded: on the surface until contact ended, ~12.75 h)", fontsize=10)
    ax1.legend(fontsize=7, loc="center right")
    ax2.set_ylabel("crater depth [m]")
    ax2.set_xlabel("hours after 2025-03-06 17:00 UTC (LOLA 120 m horizon; the 20 m crater itself is below DEM resolution)")
    ax2.set_ylim(bottom=0)
    ax2.legend(fontsize=7, loc="upper left")
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path)
    plt.close(fig)


def markdown(case: dict) -> str:
    rows = [("Sun: any part of the disc visible", "sunAnyPct", "%"), ("Sun: ≥ 50 % of the disc (lit)", "sunLitPct", "%"),
            ("Earth visible", "earthPct", "%"), ("Sun elevation max", "sunElevMaxDeg", "°"),
            ("Lowest Sun clearance above the LOLA horizon", "sunClearanceMinDeg", "°"),
            ("20 m crater floor stays dark if deeper than", "craterFloorDarkIfDeeperThanM", " m")]
    out = ["| | Planned, on-surface window | Actual, on-surface window | Planned, 14 days | Actual, 14 days |",
           "|---|---:|---:|---:|---:|"]
    for label, key, unit in rows:
        v = [case["points"][p][w][key] for w in ("window", "days14") for p in ("target", "actual")]
        out.append(f"| {label} | {v[0]}{unit} | {v[1]}{unit} | {v[2]}{unit} | {v[3]}{unit} |")
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dem", type=Path, default=DEFAULT_DEM, help="LOLA polar DEM (.lbl)")
    p.add_argument("--kernels", type=Path, default=ephemeris.DEFAULT_KERNEL_DIR)
    p.add_argument("--no-nasa", action="store_true", help="skip sampling NASA's LOLA illumination maps (offline)")
    args = p.parse_args(argv)

    dem = horizon.load_dem(args.dem)
    sky = ephemeris.load_moon_sky(args.kernels)
    end = START + timedelta(days=DAYS) - timedelta(hours=1)
    times = sky.times(START, end, build.STEP_HOURS)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    first = int((LANDING - START).total_seconds() // 3600)
    window_h = (CONTACT_END - LANDING).total_seconds() / 3600
    window_n = math.ceil((CONTACT_END - START).total_seconds() / 3600) - first
    docs, points = {}, {}
    for pt in POINTS:
        doc = build.build_site(pt, dem, sky, times, START, horizon.DEFAULT_OBSERVER_HEIGHT_M, horizon.DEFAULT_MAX_DISTANCE_M)
        doc.update({"name": pt["name"], "source": pt["source"], "sourceUrl": pt["sourceUrl"]})
        build.write_json(OUT_DIR / f"{pt['id']}.json", doc)
        docs[pt["id"]] = doc
        points[pt["id"]] = {
            "name": pt["name"], "lat": pt["lat"], "lon": pt["lon"], "file": f"{pt['id']}.json",
            "source": pt["source"], "sourceUrl": pt["sourceUrl"],
            "window": window_stats(doc, first, window_n),
            "days14": window_stats(doc, first, 14 * 24),
        }
        print(f"  {pt['id']}: window sun {points[pt['id']]['window']['sunAnyPct']}%  14 d sun {points[pt['id']]['days14']['sunAnyPct']}%")

    case = {
        "mission": "Intuitive Machines IM-2, Nova-C lander 'Athena' (NASA CLPS)",
        "landerId": "nova_c",
        "landingUtc": "2025-03-06T17:30:00Z",
        "landingSource": "NASA: touched down 'at approximately 11:30 a.m. March 6' (CST)",
        "contactEndUtc": "2025-03-07T06:15:00Z",
        "contactEndSource": "NASA: 'an early end of mission at 12:15 a.m. CST Friday'",
        "sourceUrl": NASA_RELEASE,
        "onSurfaceHours": round(window_h, 2),
        "attitude": "on its side (NASA; LROC 1408: 'ended up on its side within a 20-meter diameter crater')",
        "secondary": ("Wikipedia (citing CNN / Intuitive Machines): 'shadowed crater', solar arrays produced "
                      "'only about 100 watts'; secondary sources, used for comparison only"),
        "secondaryUrl": WIKI_IM2,
        "offsetM": round(offset_m(POINTS[0], POINTS[1])),
        "offsetNote": ("Distance between the two LROC coordinates; the target is given to 0.01° (±~150 m). NASA's "
                       "release says 'more than 1,300 feet (400 meters)' in the text and 'about 820 feet' (~250 m) "
                       "in an image caption."),
        "craterDiameterM": CRATER_DIAMETER_M,
        "craterNote": ("The 20 m crater is below the 120 m DEM resolution, so the LOLA horizon cannot contain it. "
                       "'craterFloorDarkIfDeeperThanM' is pure geometry: the floor centre sees no Sun if the crater is "
                       "deeper than 10 m × tan(max Sun elevation + solar radius). The crater's depth is not published."),
        "period": {"start": build.iso_z(START), "days": DAYS, "stepHours": build.STEP_HOURS,
                   "landingIndex": first, "windowSamples": window_n},
        "method": "build.build_site: LOLA LDEM 120 m horizon (150 km rays), observer 2 m, JPL DE421 via Skyfield",
        "points": points,
        "nasaLongTerm": None if args.no_nasa else nasa_long_term(POINTS),
        "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    build.write_json(OUT_DIR / "case.json", case)
    chart({k: {**v, "name": POINTS[i]["name"]} for i, (k, v) in enumerate(docs.items())}, window_h)
    print(f"offset between LROC coordinates: {case['offsetM']} m")
    print(markdown(case))
    print(f"wrote {OUT_DIR.relative_to(REPO)} and {CHART_PNG.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
