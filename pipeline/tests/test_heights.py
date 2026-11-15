"""Per-height horizons (``build_heights.py``): a higher observer never sees a higher horizon, and the heights include
the values the app re-checks (1 m, 3 m) next to the series' 2 m and NASA's surface (0 m)."""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import build_heights  # noqa: E402
import horizon  # noqa: E402
from test_sanity import make_dem  # noqa: E402


def test_heights_cover_the_app_checks():
    assert {0.0, 1.0, 2.0, 3.0} <= set(build_heights.HEIGHTS_M)
    assert horizon.DEFAULT_OBSERVER_HEIGHT_M in build_heights.HEIGHTS_M


def test_raising_the_observer_lowers_the_horizon(tmp_path):
    site_lat, site_lon = -86.0, 20.0
    peak_lat, peak_lon = horizon.destination(site_lat, site_lon, 200.0, 3_000.0)
    dem = horizon.load_dem(make_dem(tmp_path, site_lat, site_lon, half_size_m=30_000, res_m=120.0,
                                    bump=(float(peak_lat), float(peak_lon), 150.0, 800.0)))
    masks = [horizon.horizon_profile(dem, site_lat, site_lon, observer_height_m=h, max_distance_m=25_000)
             for h in build_heights.HEIGHTS_M]
    for low, high in zip(masks, masks[1:]):
        assert np.all(high <= low + 1e-9)
    assert masks[0][200] - masks[-1][200] > 0.1      # the nearby hill drops by a visible amount at 10 m
