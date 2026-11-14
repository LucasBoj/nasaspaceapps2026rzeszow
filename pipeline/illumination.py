"""Per-timestep visibility of the Sun and Earth against a horizon mask."""

from __future__ import annotations

import numpy as np

SUN_ANGULAR_RADIUS_DEG = 0.267
EARTH_MARGIN_DEG = 0.5


def horizon_at(horizon_deg: np.ndarray, azimuth_deg: np.ndarray) -> np.ndarray:
    """Horizon elevation linearly interpolated at arbitrary azimuths (wraps at 360°)."""
    az = np.mod(np.asarray(azimuth_deg, dtype=np.float64), 360.0)
    grid = np.arange(len(horizon_deg) + 1, dtype=np.float64) * (360.0 / len(horizon_deg))
    return np.interp(az, grid, np.append(horizon_deg, horizon_deg[0]))


def sun_fraction(sun_elev_deg: np.ndarray, sun_az_deg: np.ndarray, horizon_deg: np.ndarray,
                 radius_deg: float = SUN_ANGULAR_RADIUS_DEG) -> np.ndarray:
    """Visible fraction (0..1) of the solar disk.

    Linear approximation: the fraction grows linearly while the disk centre
    moves from one radius below the local horizon to one radius above it,
    ``clamp((elev − h + r) / 2r, 0, 1)``. The exact segment area of a disk is
    an S-curve with the same endpoints and the same value (0.5) at the
    midpoint; the largest absolute difference is 0.058 (5.8 percentage
    points), when the disk centre is ~0.62 radius from the horizon.
    """
    h = horizon_at(horizon_deg, sun_az_deg)
    return np.clip((np.asarray(sun_elev_deg) - h + radius_deg) / (2 * radius_deg), 0.0, 1.0)


def earth_visible(earth_elev_deg: np.ndarray, earth_az_deg: np.ndarray, horizon_deg: np.ndarray,
                  margin_deg: float = EARTH_MARGIN_DEG) -> np.ndarray:
    """1 where Earth's centre is more than ``margin_deg`` above the local horizon, else 0."""
    h = horizon_at(horizon_deg, earth_az_deg)
    return (np.asarray(earth_elev_deg) > h + margin_deg).astype(np.int8)


def longest_run(mask: np.ndarray) -> int:
    """Length of the longest run of True values in a 1-D boolean array."""
    padded = np.concatenate(([0], np.asarray(mask, dtype=np.int8), [0]))
    edges = np.flatnonzero(np.diff(padded))
    return int((edges[1::2] - edges[::2]).max()) if edges.size else 0


def summarize(sun_fraction_pct: np.ndarray, earth_vis: np.ndarray, step_hours: float) -> dict:
    """Headline statistics used by the frontend's site index."""
    lit = np.asarray(sun_fraction_pct) > 0
    return {
        "pctSunlit": round(float(lit.mean() * 100), 1),
        "pctComms": round(float(np.asarray(earth_vis).mean() * 100), 1),
        "longestDarkHours": round(longest_run(~lit) * step_hours, 1),
    }
