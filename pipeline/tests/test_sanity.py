"""Sanity tests for the illumination pipeline.

Run from the ``pipeline`` directory::

    python -m pytest -q

Ephemeris tests need the SPICE kernels (``python download_kernels.py``) and
are skipped otherwise. Horizon tests use a small synthetic DEM, so no real
LOLA file is required; set ``SELENE_DEM=/path/to/ldem.lbl`` to additionally
check a real DEM.
"""

from __future__ import annotations

import os
import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pyproj
import pytest
import rasterio
from rasterio.transform import from_origin

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import ephemeris  # noqa: E402
import horizon  # noqa: E402
import illumination  # noqa: E402

# Lunar south-polar stereographic CRS, used only to build synthetic test DEMs.
SOUTH_POLAR_STEREO = "+proj=stere +lat_0=-90 +lat_ts=-90 +lon_0=0 +x_0=0 +y_0=0 +R=1737400 +units=m +no_defs"


# --------------------------------------------------------------------------- fixtures

@pytest.fixture(scope="module")
def sky() -> ephemeris.MoonSky:
    try:
        return ephemeris.load_moon_sky()
    except FileNotFoundError as exc:
        pytest.skip(str(exc))


def make_dem(tmp_path: Path, site_lat: float, site_lon: float, bump=None,
             half_size_m: float = 170_000, res_m: float = 250.0) -> Path:
    """Write a GeoTIFF (heights in metres) centred on a site; optional Gaussian bump.

    ``bump`` = (lat, lon, height_m, sigma_m).
    """
    crs = pyproj.CRS.from_proj4(SOUTH_POLAR_STEREO)
    to_xy = pyproj.Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True)
    cx, cy = to_xy.transform(site_lon, site_lat)

    n = int(2 * half_size_m / res_m)
    xs = cx - half_size_m + (np.arange(n) + 0.5) * res_m
    ys = cy + half_size_m - (np.arange(n) + 0.5) * res_m
    gx, gy = np.meshgrid(xs, ys)
    heights = np.zeros((n, n), dtype=np.float32)
    if bump is not None:
        blat, blon, bh, sigma = bump
        bx, by = to_xy.transform(blon, blat)
        heights += (bh * np.exp(-((gx - bx) ** 2 + (gy - by) ** 2) / (2 * sigma**2))).astype(np.float32)

    path = tmp_path / "synthetic_dem.tif"
    with rasterio.open(path, "w", driver="GTiff", width=n, height=n, count=1, dtype="float32",
                       crs=crs.to_wkt(), transform=from_origin(cx - half_size_m, cy + half_size_m, res_m, res_m)) as dst:
        dst.write(heights, 1)
        dst.update_tags(1, units="m")
    return path


# --------------------------------------------------------------------------- ephemeris

TWO_YEARS = (datetime(2027, 1, 1, tzinfo=timezone.utc), datetime(2028, 12, 31, 23, tzinfo=timezone.utc))


@pytest.mark.parametrize("lat", [-88.6, -89.2, -89.9])
@pytest.mark.parametrize("lon", [0.0, 120.0, -100.0])
def test_sun_stays_within_3deg_near_pole(sky, lat, lon):
    """|sun elevation| < 3° close to the pole.

    The bound (90 − |lat|) + 1.54° drops below 3° only poleward of −88.54°
    (at −88.5° the Sun really reaches 3.07°), hence −88.6° as the outer case.
    """
    alt, az = sky.altaz(lat, lon, sky.times(*TWO_YEARS, 6), "sun")
    assert np.all(np.abs(alt) < 3.0), f"max |sun elevation| {np.abs(alt).max():.2f}°"
    assert np.all((az >= 0) & (az < 360))


@pytest.mark.parametrize("lat", [-88.0, -88.5, -89.5])
def test_sun_elevation_geometric_bound(sky, lat):
    """|sun elevation| never exceeds (90 − |lat|) + 1.54° (+0.1° for parallax and solar-disk effects)."""
    alt, _ = sky.altaz(lat, 45.0, sky.times(*TWO_YEARS, 1), "sun")
    assert np.abs(alt).max() < (90 - abs(lat)) + 1.54 + 0.1


def test_sun_peak_elevation_matches_geometry(sky):
    """At −85.8° the yearly peak should be ≈ (90 − 85.8) + 1.54° (lunar axial tilt to the ecliptic)."""
    times = sky.times(datetime(2027, 1, 1, tzinfo=timezone.utc), datetime(2027, 12, 31, 23, tzinfo=timezone.utc), 1)
    alt, _ = sky.altaz(-85.8, 0.0, times, "sun")
    assert abs(alt.max() - (90 - 85.8 + 1.54)) < 0.25


def test_time_grid_is_hourly_and_inclusive(sky):
    t = sky.times(ephemeris.parse_utc("2027-01-01T00:00:00Z"), ephemeris.parse_utc("2027-01-02T00:00:00Z"), 1)
    assert len(t) == 25


# --------------------------------------------------------------------------- horizon

def test_flat_terrain_gives_bare_sphere_horizon(tmp_path):
    """On a flat sphere the horizon is the geometric dip for a 2 m observer (≈ −0.087°)."""
    dem = horizon.load_dem(make_dem(tmp_path, -85.0, 0.0))
    mask = horizon.horizon_profile(dem, -85.0, 0.0, observer_height_m=2.0)
    assert mask.shape == (360,)
    assert not np.isnan(mask).any()
    assert np.allclose(mask, -0.0869, atol=0.01), (mask.min(), mask.max())


def test_mountain_appears_at_true_azimuth(tmp_path):
    """A peak placed due south-east (135°) must raise the horizon at 135°, not at its grid bearing."""
    site_lat, site_lon = -85.0, 30.0
    peak_lat, peak_lon = horizon.destination(site_lat, site_lon, 135.0, 20_000.0)
    dem = horizon.load_dem(make_dem(tmp_path, site_lat, site_lon, bump=(float(peak_lat), float(peak_lon), 2000.0, 2500.0)))
    mask = horizon.horizon_profile(dem, site_lat, site_lon)
    assert mask.shape == (360,)
    assert not np.isnan(mask).any()
    assert abs(int(np.argmax(mask)) - 135) <= 1, f"peak at {np.argmax(mask)}°"
    assert mask.max() > 4.0  # ~2 km high at 20 km ≈ 5.7°


def test_site_outside_dem_is_rejected(tmp_path):
    dem = horizon.load_dem(make_dem(tmp_path, -85.0, 0.0, half_size_m=20_000))
    with pytest.raises(ValueError):
        horizon.horizon_profile(dem, -80.0, 0.0)


@pytest.mark.skipif(not os.environ.get("SELENE_DEM"), reason="set SELENE_DEM to test a real LOLA DEM")
def test_real_dem_mask():
    dem = horizon.load_dem(os.environ["SELENE_DEM"])
    mask = horizon.horizon_profile(dem, -85.988, 2.06)  # Malapert Massif summit area
    assert mask.shape == (360,)
    assert not np.isnan(mask).any()
    assert -6.0 < mask.min() and mask.max() < 30.0


# --------------------------------------------------------------------------- illumination maths

def test_sun_fraction_formula():
    flat = np.zeros(360)
    r = illumination.SUN_ANGULAR_RADIUS_DEG
    f = illumination.sun_fraction(np.array([-1.0, -r, 0.0, r, 1.0]), np.zeros(5), flat)
    assert np.allclose(f, [0.0, 0.0, 0.5, 1.0, 1.0])


def test_horizon_interpolation_wraps():
    mask = np.zeros(360)
    mask[359], mask[0] = 2.0, 4.0
    assert illumination.horizon_at(mask, np.array([359.5]))[0] == pytest.approx(3.0)


def test_earth_margin():
    mask = np.zeros(360)
    vis = illumination.earth_visible(np.array([0.4, 0.6]), np.array([10.0, 10.0]), mask)
    assert vis.tolist() == [0, 1]


def test_longest_run_and_summary():
    assert illumination.longest_run(np.array([1, 1, 0, 1, 1, 1, 0], dtype=bool)) == 3
    s = illumination.summarize(np.array([0, 0, 50, 100]), np.array([1, 0, 1, 1]), 1)
    assert s == {"pctSunlit": 50.0, "pctComms": 75.0, "longestDarkHours": 2.0}
