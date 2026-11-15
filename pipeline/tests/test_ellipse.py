"""Landing-ellipse horizons (``ellipse.py``): grid, frame parameters and the reconstruction the frontend uses.

The reconstruction rebuilds a point's sunFraction / earthVisible from the SITE's Sun and Earth series plus the point's
own horizon and two frame numbers. On the real DEM (``build_ellipse.py --validate``, 9 sites × 5 points × 17 544 h)
it differs from a full recomputation at the point by at most 2 pp — the same as the centi-degree rounding of the shipped
files. Here the same check runs on a synthetic DEM (kernels needed, skipped otherwise).
"""

from __future__ import annotations

import math
import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import build  # noqa: E402
import ellipse  # noqa: E402
import ephemeris  # noqa: E402
import horizon  # noqa: E402
from test_sanity import make_dem  # noqa: E402


@pytest.fixture(scope="module")
def sky() -> ephemeris.MoonSky:
    try:
        return ephemeris.load_moon_sky()
    except FileNotFoundError as exc:
        pytest.skip(str(exc))


def test_grid_is_a_symmetric_disc_with_the_site_in_the_middle():
    g = ellipse.grid_offsets(60, 480)
    assert len(g) == 197                                    # integer points with i² + j² ≤ 64
    assert np.hypot(g[:, 0], g[:, 1]).max() <= 480 + 1e-9
    assert tuple(g[len(g) // 2]) == (0.0, 0.0)              # build_ellipse relies on this order
    assert {tuple(p) for p in g} == {(-e, -n) for e, n in g}


def test_offset_point_walks_the_requested_distance():
    lat, lon = ellipse.offset_point(-85.0, 30.0, 300.0, -400.0)
    e, n, _ = ellipse.enu_basis(-85.0, 30.0)
    r = horizon.MOON_RADIUS_M
    p = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)),
                  math.cos(math.radians(lat)) * math.sin(math.radians(lon)), math.sin(math.radians(lat))]) * r
    c = np.array([math.cos(math.radians(-85.0)) * math.cos(math.radians(30.0)),
                  math.cos(math.radians(-85.0)) * math.sin(math.radians(30.0)), math.sin(math.radians(-85.0))]) * r
    assert abs((p - c) @ e - 300.0) < 0.5 and abs((p - c) @ n + 400.0) < 0.5


def test_frame_params_identity_at_the_site():
    shift, tilt, _ = ellipse.frame_params(-89.4, 120.0, -89.4, 120.0)
    assert abs(shift) < 1e-9 and tilt < 1e-9


@pytest.mark.parametrize("lat", [-83.4, -87.0, -89.4])
def test_linear_frame_model_matches_exact_mapping(lat):
    """az_p = az + shift and el_p = tilt·cos(az − tiltAz) reproduce the exact vector mapping to < 1e-4°."""
    plat, plon = ellipse.offset_point(lat, 40.0, 480.0, 0.0)
    az = np.arange(360.0)
    az_p, el_p = ellipse.frame_mapping(lat, 40.0, plat, plon, az)
    shift, tilt, tilt_az = ellipse.frame_params(lat, 40.0, plat, plon)
    daz = (az_p - az - shift + 180) % 360 - 180
    assert np.abs(daz).max() < 1e-4
    assert np.abs(el_p - tilt * np.cos(np.radians(az - tilt_az))).max() < 1e-4
    assert abs(tilt - math.degrees(480.0 / horizon.MOON_RADIUS_M)) < 1e-4     # the vertical turns by d / R
    # North turns by about Δlon·sin(lat) (meridian convergence) — large near the pole.
    assert abs(abs(shift) - abs((plon - 40.0) * math.sin(math.radians(lat)))) < 0.01


def test_reconstruction_matches_direct_computation(tmp_path, sky):
    """A ridge 6 km east of a −88° site: the point 480 m east sees it differently; the site's ephemeris suffices."""
    site_lat, site_lon = -88.0, 60.0
    peak_lat, peak_lon = horizon.destination(site_lat, site_lon, 95.0, 6_000.0)
    dem = horizon.load_dem(make_dem(tmp_path, site_lat, site_lon, half_size_m=40_000, res_m=120.0,
                                    bump=(float(peak_lat), float(peak_lon), 300.0, 1_500.0)))
    times = sky.times(datetime(2027, 1, 1, tzinfo=timezone.utc), datetime(2027, 3, 31, 23, tzinfo=timezone.utc), 1)
    site = build.compute_series(dem, sky, times, site_lat, site_lon, 2.0, 35_000)
    rounded = [np.rint(np.asarray(site[k]) * 100) / 100 for k in ("sun_alt", "sun_az", "earth_alt", "earth_az")]
    for east, north in [(480.0, 0.0), (0.0, -480.0), (-300.0, 300.0)]:
        plat, plon = ellipse.offset_point(site_lat, site_lon, east, north)
        direct = build.compute_series(dem, sky, times, plat, plon, 2.0, 35_000)
        shift, tilt, tilt_az = ellipse.frame_params(site_lat, site_lon, plat, plon)
        sun, earth = ellipse.reconstruct(np.round(direct["mask"], 2), shift, tilt, tilt_az, *rounded)
        assert np.abs(sun - direct["sun_pct"]).max() <= 2
        assert (earth != direct["earth_vis"]).mean() < 0.002
        # Without the frame correction the error is clearly larger near the pole.
        naive, _ = ellipse.reconstruct(np.round(direct["mask"], 2), 0.0, 0.0, 0.0, *rounded)
        if east:
            assert np.abs(naive - direct["sun_pct"]).max() > 2
