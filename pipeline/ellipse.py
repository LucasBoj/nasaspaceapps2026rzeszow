"""Landing ellipse: terrain horizons for a grid of points around a landing site.

A lander does not touch down exactly on the target. To ask "how much does the verdict depend on
where inside the landing ellipse we end up?", the frontend re-runs the mission for every point of a
grid around the site. It needs only one horizon mask per point:

* The Sun and the Earth are so far away that their direction is the same for every point within a
  few hundred metres. The only things that change are the local terrain horizon and the local frame
  (north and the vertical turn slightly as you move over the sphere).
* So each point's horizon is computed with the same ray casting as the site (``horizon.py``) and then
  expressed **in the site's own azimuth/elevation frame**. A direction that the site sees at
  (azimuth a, elevation 0) is seen from the point at (a_p, e_p); the point sees the body when the
  body's site-frame elevation exceeds ``h_p(a_p) − e_p``. That value is the site-frame horizon.
* The frontend then rebuilds sunFraction / earthVisible for every point from the site's hourly Sun
  and Earth series with the same formulas as ``illumination.py``.

Near the pole the frame correction matters: 480 m from a site at −89.4° the local north turns by
~1.5°, which is several solar diameters at the horizon. The elevation term (≤ 0.016° at 480 m) is
small but included.

``reconstruct`` (used by tests and ``build_ellipse.py --validate``) and the frontend implement the
same reconstruction, so the validation covers both.
"""

from __future__ import annotations

import math

import numpy as np

import horizon
import illumination

GRID_SPACING_M = 60.0   # half a pixel of the 120 m DEM (bilinear between pixels)
GRID_RADIUS_M = 480.0   # covers the largest per-lander landing radius in landers.json (Nova-C 400 m)


def grid_offsets(spacing_m: float = GRID_SPACING_M, radius_m: float = GRID_RADIUS_M) -> np.ndarray:
    """(east, north) offsets in metres of all grid points within ``radius_m``, row-major from the north-west."""
    n = int(math.floor(radius_m / spacing_m + 1e-9))
    steps = np.arange(-n, n + 1) * spacing_m
    out = [(e, nn) for nn in steps[::-1] for e in steps if math.hypot(e, nn) <= radius_m + 1e-6]
    return np.array(out, dtype=np.float64)


def offset_point(lat_deg: float, lon_deg: float, east_m: float, north_m: float) -> tuple[float, float]:
    """Point reached from the site by walking ``east_m`` / ``north_m`` along the sphere (great circle)."""
    dist = math.hypot(east_m, north_m)
    if dist == 0:
        return lat_deg, lon_deg
    bearing = math.degrees(math.atan2(east_m, north_m))
    lat, lon = horizon.destination(lat_deg, lon_deg, bearing, dist)
    return float(lat), float(lon)


def enu_basis(lat_deg: float, lon_deg: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """East, north and up unit vectors of the local frame on a sphere (body-fixed coordinates)."""
    la, lo = math.radians(lat_deg), math.radians(lon_deg)
    east = np.array([-math.sin(lo), math.cos(lo), 0.0])
    north = np.array([-math.sin(la) * math.cos(lo), -math.sin(la) * math.sin(lo), math.cos(la)])
    up = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)])
    return east, north, up


def frame_mapping(site_lat: float, site_lon: float, point_lat: float, point_lon: float,
                  azimuths_deg: np.ndarray | None = None) -> tuple[np.ndarray, np.ndarray]:
    """For distant directions at site-frame (azimuth, elevation 0): their azimuth and elevation at the point (deg)."""
    az = np.arange(horizon.N_AZIMUTHS, dtype=np.float64) if azimuths_deg is None else np.asarray(azimuths_deg, float)
    e_s, n_s, _ = enu_basis(site_lat, site_lon)
    e_p, n_p, u_p = enu_basis(point_lat, point_lon)
    a = np.radians(az)
    d = np.cos(a)[:, None] * n_s[None, :] + np.sin(a)[:, None] * e_s[None, :]      # unit vectors, (A, 3)
    az_p = np.degrees(np.arctan2(d @ e_p, d @ n_p)) % 360.0
    el_p = np.degrees(np.arcsin(np.clip(d @ u_p, -1.0, 1.0)))
    return az_p, el_p


def frame_params(site_lat: float, site_lon: float, point_lat: float, point_lon: float) -> tuple[float, float, float]:
    """The point's frame relative to the site's, as three numbers (deg).

    ``az_point = az_site + azShift`` and ``el_point = el_site + tilt · cos(az_site − tiltAz)`` for distant bodies near
    the horizon. Exact to first order in distance / radius (the neglected terms are ~(480 m / 1737 km)² rad).
    """
    az = np.arange(horizon.N_AZIMUTHS, dtype=np.float64)
    az_p, el_p = frame_mapping(site_lat, site_lon, point_lat, point_lon, az)
    shift = float(np.degrees(np.angle(np.mean(np.exp(1j * np.radians(az_p - az))))))
    a = np.radians(az)
    c, s = 2 * np.mean(el_p * np.cos(a)), 2 * np.mean(el_p * np.sin(a))   # first Fourier harmonic of el_p
    return shift, float(math.hypot(c, s)), float(math.degrees(math.atan2(s, c)) % 360.0)


def site_frame_horizon(point_mask_deg: np.ndarray, az_shift_deg: float, tilt_deg: float, tilt_az_deg: float,
                       site_azimuth_deg: np.ndarray) -> np.ndarray:
    """The point's horizon seen as a site-frame elevation limit at the given site-frame azimuths.

    The point mask is interpolated in its own frame (like ``illumination.horizon_at`` does for the site), so no
    second resampling smooths sharp horizon features.
    """
    az = np.asarray(site_azimuth_deg, dtype=np.float64)
    return (illumination.horizon_at(point_mask_deg, az + az_shift_deg)
            - tilt_deg * np.cos(np.radians(az - tilt_az_deg)))


def ellipse_points(dem: horizon.Dem, site_lat: float, site_lon: float,
                   observer_height_m: float = horizon.DEFAULT_OBSERVER_HEIGHT_M,
                   max_distance_m: float = horizon.DEFAULT_MAX_DISTANCE_M,
                   spacing_m: float = GRID_SPACING_M, radius_m: float = GRID_RADIUS_M) -> list[dict]:
    """Every grid point: offsets, position, ground height, slope, its own horizon mask (deg) and frame parameters.

    Points outside the DEM or on a no-data cell are skipped.
    """
    points = []
    for east, north in grid_offsets(spacing_m, radius_m):
        lat, lon = offset_point(site_lat, site_lon, east, north)
        try:
            mask = horizon.horizon_profile(dem, lat, lon, observer_height_m, max_distance_m)
        except ValueError:
            continue
        shift, tilt, tilt_az = frame_params(site_lat, site_lon, lat, lon)
        points.append({
            "e": float(east), "n": float(north), "lat": lat, "lon": lon,
            "groundM": dem.height_at(lat, lon),
            "slopeDeg": dem.slope_at(lat, lon),
            "azShiftDeg": shift, "tiltDeg": tilt, "tiltAzDeg": tilt_az,
            "horizon": mask,
        })
    return points


def reconstruct(point_mask_deg: np.ndarray, az_shift_deg: float, tilt_deg: float, tilt_az_deg: float,
                sun_elev_deg: np.ndarray, sun_az_deg: np.ndarray,
                earth_elev_deg: np.ndarray, earth_az_deg: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """sunFraction (int %) and earthVisible (0/1) at a point from the SITE's Sun/Earth series (rules of build.py)."""
    h_sun = site_frame_horizon(point_mask_deg, az_shift_deg, tilt_deg, tilt_az_deg, sun_az_deg)
    h_earth = site_frame_horizon(point_mask_deg, az_shift_deg, tilt_deg, tilt_az_deg, earth_az_deg)
    r = illumination.SUN_ANGULAR_RADIUS_DEG
    sun_pct = np.rint(np.clip((np.asarray(sun_elev_deg) - h_sun + r) / (2 * r), 0.0, 1.0) * 100).astype(int)
    earth = (np.asarray(earth_elev_deg) > h_earth + illumination.EARTH_MARGIN_DEG).astype(np.int8)
    return sun_pct, earth
