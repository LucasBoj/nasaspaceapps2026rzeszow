"""Tests for landing-site selection and the NASA 6.5-day sunlight statement.

The NASA check reads the generated frontend data (``sites.json`` and
``illumination/{siteId}.json``); it is skipped if the data has not been built.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import horizon  # noqa: E402
import illumination  # noqa: E402
import select_sites  # noqa: E402
from test_sanity import make_dem  # noqa: E402

DATA = Path(__file__).resolve().parents[2] / "SeleneFrontend" / "wwwroot" / "data"
SIX_AND_HALF_DAYS_H = 156  # Artemis III planned surface duration

# NASA's 2022 release ("All 13 regions contain sites that provide continuous access to
# sunlight throughout a 6.5-day period") covers only these of our nine 2024 regions.
REGIONS_IN_NASA_2022_LIST = {"haworth", "malapert_massif", "nobile_rim_1", "nobile_rim_2", "degerlache_rim_2"}


def _sites() -> list[dict]:
    path = DATA / "sites.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else []


@pytest.mark.parametrize("site", _sites(), ids=lambda s: s["id"])
def test_best_site_has_continuous_sunlight_for_6_5_days(site):
    """At least one uninterrupted ≥156 h window with sunFraction ≥ 50 in 2027–28."""
    paths = [DATA / "illumination" / site["id"] / f"{y}.json" for y in (2027, 2028)]   # yearly files
    if not all(p.exists() for p in paths):
        pytest.skip(f"{site['id']} 2027/2028 not built")
    docs = [json.loads(p.read_text(encoding="utf-8")) for p in paths]
    lit = np.concatenate([np.array(d["sunFraction"]) for d in docs]) >= illumination.LIT_THRESHOLD_PCT
    longest_h = illumination.longest_run(lit) * docs[0]["stepHours"]
    scope = "NASA 2022 statement" if site["id"] in REGIONS_IN_NASA_2022_LIST else "planning criterion (region added 2024)"
    assert longest_h >= SIX_AND_HALF_DAYS_H, f"{site['id']}: longest continuous light {longest_h} h < 156 h [{scope}]"


def test_sites_have_published_provenance():
    sites = _sites()
    if not sites:
        pytest.skip("sites.json not found")
    for s in sites:
        if not s.get("approximate", True):
            assert "10.1029/2025JE009434" in s["source"], s["id"]
            assert s.get("candidateId"), s["id"]
        assert "regionCenter" in s and "alternatives" in s, s["id"]


# --------------------------------------------------------------------------- unit tests

@pytest.mark.parametrize("tilt_deg", [0.0, 5.0, 12.0])
def test_slope_of_tilted_plane(tmp_path, tilt_deg):
    """A plane tilted by a known angle must give that slope (3×3 Horn window)."""
    path = make_dem(tmp_path, -85.0, 0.0, half_size_m=5_000, res_m=120.0)
    import rasterio
    with rasterio.open(path, "r+") as ds:
        rows, cols = ds.shape
        xs = (np.arange(cols) + 0.5) * 120.0
        ds.write((np.tile(xs, (rows, 1)) * np.tan(np.radians(tilt_deg))).astype(np.float32), 1)
    dem = horizon.load_dem(path, units="m")
    assert dem.slope_at(-85.0, 0.0) == pytest.approx(tilt_deg, abs=0.05)


def _c(cid, sun, earth, dark):
    return {"candidateId": cid, "sunPct": sun, "earthPct": earth, "longestDarkH": dark}


def test_rank_prefers_sun_then_earth_within_one_point():
    ranked = select_sites.rank([
        _c("A", 80.0, 50.0, 300),
        _c("B", 79.4, 70.0, 300),   # within 1 pt of best sun -> wins on Earth visibility
        _c("C", 78.5, 99.0, 100),   # more than 1 pt behind -> not a contender
    ])
    assert [c["candidateId"] for c in ranked] == ["B", "A", "C"]


def test_rank_breaks_full_tie_on_shorter_darkness():
    ranked = select_sites.rank([_c("A", 70.0, 60.0, 400), _c("B", 70.0, 60.0, 250)])
    assert ranked[0]["candidateId"] == "B"


def test_site_metrics_threshold():
    m = illumination.site_metrics(np.array([100, 60, 49, 0, 50]), np.array([1, 1, 0, 0, 1]), 1)
    assert m == {"sunPct": 60.0, "earthPct": 60.0, "longestDarkH": 2.0, "longestLightH": 2.0}
