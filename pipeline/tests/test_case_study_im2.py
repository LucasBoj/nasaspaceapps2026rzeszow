"""Offline checks of the IM-2 Athena case study data (case_study_im2.py output, committed in the frontend)."""

from __future__ import annotations

import json
import math

import pytest

import case_study_im2 as cs

FILES = {p["id"]: cs.OUT_DIR / f"{p['id']}.json" for p in cs.POINTS}
CASE = cs.OUT_DIR / "case.json"

pytestmark = pytest.mark.skipif(not CASE.exists(), reason="run case_study_im2.py first")


@pytest.fixture(scope="module")
def case():
    return json.loads(CASE.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def docs():
    return {k: json.loads(v.read_text(encoding="utf-8")) for k, v in FILES.items()}


def test_points_match_the_lroc_sources(docs, case):
    assert (docs["target"]["lat"], docs["target"]["lon"]) == (-84.78, 29.13)        # LROC 1401
    assert (docs["actual"]["lat"], docs["actual"]["lon"]) == (-84.7906, 29.1957)    # LROC 1408
    for d in docs.values():
        assert d["sourceUrl"].startswith("https://lroc.im-ldi.com/images/14")
    assert case["sourceUrl"].startswith("https://www.nasa.gov/")


def test_period_and_contract(docs, case):
    for d in docs.values():
        assert d["start"] == "2025-03-06T17:00:00Z"
        assert d["stepHours"] == 1 and d["count"] == cs.DAYS * 24
        for key in ("sunFraction", "sunElevCentiDeg", "sunAzCentiDeg", "earthVisible", "earthElevCentiDeg", "earthAzCentiDeg"):
            assert len(d[key]) == d["count"], key
        assert len(d["horizonDeg"]) == 360
    assert case["onSurfaceHours"] == pytest.approx(12.75, abs=0.01)
    assert case["period"]["landingIndex"] == 0


def test_sun_elevation_within_the_polar_bound(docs):
    # Max Sun elevation at latitude φ: (90° − |φ|) + 1.54° (lunar obliquity to the ecliptic) + parallax margin.
    for d in docs.values():
        bound = 90 - abs(d["lat"]) + 1.54 + 0.1
        assert max(d["sunElevCentiDeg"]) / 100 <= bound


def test_offset_between_the_two_lroc_points(case):
    # ~0.32 km poleward and ~0.18 km east → about 370 m on the 1737.4 km sphere.
    assert 300 <= case["offsetM"] <= 450


def test_crater_threshold_is_pure_geometry(case):
    for p in case["points"].values():
        for w in ("window", "days14"):
            s = p[w]
            expected = 10 * math.tan(math.radians(max(0.0, s["sunElevMaxDeg"] + 0.267)))
            assert s["craterFloorDarkIfDeeperThanM"] == pytest.approx(expected, abs=0.02)
