"""Offline checks of the NASA LOLA illumination-map comparison (compare_nasa_illumination.py).

Runs on the stored snapshot (data/nasa_illumination/samples.csv) and result
(data/nasa_illumination/comparison.json); no network, no kernels.

Tolerances, set from the first run (2026-11-14) and kept loose enough to survive a
re-sample, tight enough to catch a broken geometry or definition:

* vs the NASA 120 m map (same DEM resolution as our horizons) every site is within
  12 pp and at least 5 of 9 are within 2 pp (observed: +0.3 … +11.4 pp, median +1.8).
  A wrong lat/lon -> pixel mapping or a wrong definition (e.g. sunFraction >= 50 instead
  of > 0) moves several sites by tens of points.
* ours is never more than 1 pp darker than NASA: with the observer 2 m above the terrain
  (NASA's height is not published, most likely the surface) we expect slightly more light.
* two permanently shadowed crater floors read exactly 0 in every map, which pins the map
  orientation (their mirror images across longitude 0 are lit).
"""

from __future__ import annotations

import json
import math
import statistics

import pytest

import compare_nasa_illumination as cmp

R_MOON_M = 1737400.0


@pytest.fixture(scope="module")
def samples():
    return cmp.read_samples()


@pytest.fixture(scope="module")
def result():
    return json.loads(cmp.COMPARISON_JSON.read_text(encoding="utf-8"))


def test_snapshot_covers_all_sites_with_valid_values(samples):
    sites = json.loads(cmp.build.DEFAULT_SITES.read_text(encoding="utf-8"))
    for site in sites:
        products = {r["product"] for r in samples if r["pointId"] == site["id"]}
        assert {"sun120", "earth120"} <= products, site["id"]
        if site["lat"] <= -85:
            assert {"sun60", "earth60"} <= products, site["id"]
    for r in samples:
        assert 0.0 <= r["min3x3"] <= r["center"] <= r["max3x3"] <= 1.0, r
    header = cmp.SAMPLES_CSV.read_text(encoding="utf-8").splitlines()[:4]
    assert any(cmp.PDS_DIR in line for line in header)
    assert any("doi:10.1016/j.icarus.2010.10.030" in line for line in header)


def test_permanently_shadowed_floors_read_zero(samples):
    for pid in ("ref_shackleton_floor", "ref_faustini_floor"):
        rows = [r for r in samples if r["pointId"] == pid]
        assert len(rows) == 4, pid
        assert all(r["center"] == 0.0 for r in rows), rows


def _grid_60m():
    from affine import Affine
    from rasterio.crs import CRS

    # AVGVISIB_85S_060M: 5058 x 5058, 60 m/px, projection offset 2528.5 px, sphere R = 1737.4 km.
    return cmp.MapGeometry(
        crs=CRS.from_proj4("+proj=stere +lat_0=-90 +lat_ts=-90 +lon_0=0 +R=1737400 +units=m +no_defs"),
        transform=Affine(60.0, 0.0, -151740.0, 0.0, -60.0, 151740.0),
        width=5058, height=5058, record_bytes=10116)


@pytest.mark.parametrize("lat, lon", [(-90.0, 0.0), (-89.0, 90.0), (-89.0, 0.0), (-86.0, -135.0)])
def test_pixel_of_matches_the_polar_stereographic_formula(lat, lon):
    # South polar stereographic, true scale at the pole: rho = 2R tan(45° + lat/2), x = rho sin(lon), y = rho cos(lon).
    rho = 2 * R_MOON_M * math.tan(math.radians(45 + lat / 2))
    x, y = rho * math.sin(math.radians(lon)), rho * math.cos(math.radians(lon))
    expected = (math.floor((151740.0 - y) / 60.0), math.floor((x + 151740.0) / 60.0))
    assert cmp.pixel_of(_grid_60m(), lat, lon) == expected


def test_agreement_with_the_nasa_120m_map(result):
    deltas = [s["deltaSunPct"] for s in result["sites"]]
    assert len(deltas) == 9
    assert all(abs(d) <= 12 for d in deltas), deltas
    assert sum(abs(d) <= 2 for d in deltas) >= 5, deltas
    assert min(deltas) >= -1, deltas
    assert statistics.median(deltas) <= 3, deltas


def test_earth_visibility_agreement(result):
    deltas = [s["deltaEarthPct"] for s in result["sites"]]
    assert all(abs(d) <= 16 for d in deltas), deltas
    assert sum(abs(d) <= 2 for d in deltas) >= 5, deltas


def test_recomputed_2027_28_matches_the_app_data(result):
    # Same horizon + fresh ephemeris over 2027-28 must reproduce index.json -> pctSunlit,
    # i.e. the comparison uses exactly the model the app uses.
    index = json.loads((cmp.ILLUMINATION_DIR / "index.json").read_text(encoding="utf-8"))
    # index.json covers 2026-11 … 2035 now; 2027–28 = the two yearly figures weighted by their hours.
    app = {s["siteId"]: (s["byYear"]["2027"]["pctSunlit"] * 8760 + s["byYear"]["2028"]["pctSunlit"] * 8784) / 17544
           for s in index["sites"]}
    for s in result["sites"]:
        assert s["oursSunPct2027"] == pytest.approx(app[s["siteId"]], abs=0.2), s["siteId"]


def test_frontend_copy_matches(result):
    front = json.loads(cmp.FRONTEND_JSON.read_text(encoding="utf-8"))
    assert front == result
