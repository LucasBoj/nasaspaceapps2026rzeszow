"""Science layer (``build_psr.py``): PSR distances and the sun-vs-ice front.

The shipped ``data/science/psr.json`` is checked offline; the checks on the NASA map itself need
``lpsr_75s_120m_201608.lbl`` (set ``SELENE_LPSR`` or put it in ``pipeline/cache``) and are skipped otherwise.
"""

from __future__ import annotations

import json
import math
import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import build_psr  # noqa: E402

DOC = json.loads(build_psr.OUT.read_text(encoding="utf-8")) if build_psr.OUT.exists() else None
LPSR = Path(os.environ.get("SELENE_LPSR", Path(__file__).resolve().parents[1] / "cache" / "lpsr_75s_120m_201608.lbl"))


@pytest.mark.skipif(DOC is None, reason="run build_psr.py first")
def test_every_site_has_ordered_distances_and_outlines():
    assert len(DOC["sites"]) == 9
    for s in DOC["sites"]:
        assert 0 <= s["dAnyKm"] <= s["d1Km"] <= s["d10Km"], s["siteId"]
        assert s["psrKm2Within2"] <= s["psrKm2Within5"] <= s["psrKm2Within10"]
        assert s["psrKm2Within10"] <= math.pi * 10 ** 2
        assert not s["onPsr"], f"{s['siteId']}: a landing site cannot be in permanent shadow"
        for o in s["outlines"]:
            assert o["distKm"] <= DOC["outlineRadiusKm"] and o["areaKm2"] >= DOC["outlineMinKm2"] * 0.5
            ring = o["rings"][0]
            assert ring[0] == ring[-1] and len(ring) >= 4
            assert all(-90 <= lat <= -75 for _, lat in ring)


@pytest.mark.skipif(DOC is None, reason="run build_psr.py first")
def test_pareto_front_is_a_real_trade_off():
    for r in DOC["regions"]:
        cands = r["candidates"]
        front = sorted((c for c in cands if c["pareto"]), key=lambda c: -c["sunPct"])
        assert front, r["siteId"]
        assert max(c["sunPct"] for c in front) == max(c["sunPct"] for c in cands)     # the sunniest point is on it
        assert min(c["d1Km"] for c in front) == min(c["d1Km"] for c in cands)         # and the one closest to ice
        # Along the front, less sun must buy a closer PSR.
        for a, b in zip(front, front[1:]):
            assert b["sunPct"] < a["sunPct"] and b["d1Km"] < a["d1Km"], (r["siteId"], a, b)
        assert sum(c["isSite"] for c in cands) == 1


@pytest.mark.skipif(not LPSR.exists(), reason="NASA LPSR map not available")
def test_known_permanently_shadowed_floors_and_a_lit_summit():
    m = build_psr.PsrMap(LPSR)
    assert m.at(-89.67, 129.8)["onPsr"]          # Shackleton crater floor
    assert m.at(-87.3, 77.0)["onPsr"]            # Faustini crater floor
    assert not m.at(-85.99192, 2.0564)["onPsr"]  # Malapert Massif site (sunlit ~80 % of the time)
    assert 15_000 < m.total_km2 < 40_000         # 75–90°S; 27 702 km² in the 2016 release
