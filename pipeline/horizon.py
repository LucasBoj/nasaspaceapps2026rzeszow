"""Terrain horizon mask from a LOLA polar-stereographic DEM.

For a surface point (lat, lon) we cast 360 rays (1° apart, azimuth measured
clockwise from lunar north), sample the DEM along each ray out to a maximum
distance and keep the highest elevation angle of the terrain as seen from the
observer. Earth/Sun are visible only above that angle.

Geometry
--------
* Ray points are generated on the lunar sphere (R = 1 737 400 m) with the
  great-circle "destination point" formula, then projected into the DEM's own
  CRS with pyproj. This keeps azimuths true (not grid azimuths), which matters
  near the pole where grid north and true north diverge strongly.
* Elevation angle of a terrain sample at ground distance ``d`` with height
  difference ``dh`` (sample − observer), with the drop of the curved surface:
  ``elev = atan2(dh − d² / (2R), d)``.
* Heights are bilinearly interpolated; the sampling step equals the DEM
  resolution.

DEM handling
------------
CRS and affine transform are read from the file (nothing hard-coded).
PDS3 LOLA products are opened through their ``.lbl`` label. Their label
``OFFSET`` (1737.4 km) is the reference-sphere radius, *not* an additive
height offset, so only the scale factor is applied. Float LOLA products store
heights in **kilometres**; units are auto-detected (or forced with ``units``).
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pyproj
import rasterio

MOON_RADIUS_M = 1_737_400.0
DEFAULT_OBSERVER_HEIGHT_M = 2.0
DEFAULT_MAX_DISTANCE_M = 150_000.0
N_AZIMUTHS = 360

# Lunar relief spans roughly −9 … +11 km. Values this small can only be km.
_KM_DETECTION_LIMIT = 30.0


@dataclass
class Dem:
    """A height grid in metres plus the georeferencing needed to sample it."""

    heights_m: np.ndarray            # 2-D float32, NaN where no data
    transform: rasterio.Affine       # pixel (col,row) -> projected (x,y), corner-based
    crs: pyproj.CRS
    resolution_m: float
    name: str
    units_detected: str

    def __post_init__(self) -> None:
        self._inverse = ~self.transform
        self._to_dem = pyproj.Transformer.from_crs(self.crs.geodetic_crs, self.crs, always_xy=True)

    def project(self, lon_deg: np.ndarray, lat_deg: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """Planetocentric lon/lat (deg) -> DEM projected x, y (m)."""
        return self._to_dem.transform(lon_deg, lat_deg)

    def sample(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """Bilinear interpolation of height (m) at projected coordinates; NaN outside / no data."""
        x = np.asarray(x, dtype=np.float64)
        y = np.asarray(y, dtype=np.float64)
        inv = self._inverse
        # Pixel centres sit at +0.5 in the corner-based transform.
        c = inv.a * x + inv.b * y + inv.c - 0.5
        r = inv.d * x + inv.e * y + inv.f - 0.5
        c0 = np.floor(c).astype(np.int64)
        r0 = np.floor(r).astype(np.int64)
        fc = c - c0
        fr = r - r0

        rows, cols = self.heights_m.shape
        inside = (c0 >= 0) & (r0 >= 0) & (c0 + 1 < cols) & (r0 + 1 < rows)
        out = np.full(c.shape, np.nan, dtype=np.float64)
        if not inside.any():
            return out

        ci, ri, fci, fri = c0[inside], r0[inside], fc[inside], fr[inside]
        h = self.heights_m
        top = h[ri, ci] * (1 - fci) + h[ri, ci + 1] * fci
        bottom = h[ri + 1, ci] * (1 - fci) + h[ri + 1, ci + 1] * fci
        out[inside] = top * (1 - fri) + bottom * fri  # NaN neighbours propagate
        return out

    def height_at(self, lat_deg: float, lon_deg: float) -> float:
        """Terrain height (m) at a single lat/lon."""
        x, y = self.project(np.array([lon_deg]), np.array([lat_deg]))
        return float(self.sample(x, y)[0])


def load_dem(path: str | Path, units: str = "auto") -> Dem:
    """Load a DEM (GeoTIFF, or PDS3 IMG via its .lbl) into metres.

    Args:
        path: DEM file. For PDS3 LOLA products pass the ``.lbl`` label.
        units: ``"auto"`` (default), ``"m"`` or ``"km"``.
    """
    path = Path(path)
    with rasterio.open(path) as src:
        band = src.read(1, masked=True).astype(np.float32)
        scale = src.scales[0] if src.scales and src.scales[0] else 1.0
        declared = (src.units[0] or "").strip().lower() if src.units else ""
        crs = pyproj.CRS.from_wkt(src.crs.to_wkt())
        transform = src.transform
        res = float(abs(transform.a))

    heights = np.ma.filled(band, np.nan) * np.float32(scale)
    # Guard against huge sentinel values that were not flagged as nodata.
    heights[~np.isfinite(heights) | (np.abs(heights) > 1e7)] = np.nan

    if units == "auto":
        if declared in {"km", "kilometer", "kilometers", "kilometre", "kilometres"}:
            units = "km"
        elif declared in {"m", "meter", "meters", "metre", "metres"}:
            units = "m"
        else:
            units = "km" if np.nanmax(np.abs(heights)) < _KM_DETECTION_LIMIT else "m"
    if units == "km":
        heights *= 1000.0
    elif units != "m":
        raise ValueError(f"units must be 'auto', 'm' or 'km', got {units!r}")

    if not crs.is_projected:
        raise ValueError(f"{path.name}: expected a projected (polar stereographic) DEM, got {crs.name}")

    return Dem(heights, transform, crs, res, path.name, units)


def destination(lat_deg: np.ndarray, lon_deg: np.ndarray, bearing_deg: np.ndarray,
                distance_m: np.ndarray, radius_m: float = MOON_RADIUS_M) -> tuple[np.ndarray, np.ndarray]:
    """Great-circle destination point on a sphere (all inputs broadcast)."""
    lat1 = np.radians(lat_deg)
    lon1 = np.radians(lon_deg)
    brg = np.radians(bearing_deg)
    delta = np.asarray(distance_m) / radius_m

    sin_lat2 = np.sin(lat1) * np.cos(delta) + np.cos(lat1) * np.sin(delta) * np.cos(brg)
    lat2 = np.arcsin(np.clip(sin_lat2, -1.0, 1.0))
    lon2 = lon1 + np.arctan2(np.sin(brg) * np.sin(delta) * np.cos(lat1),
                             np.cos(delta) - np.sin(lat1) * sin_lat2)
    return np.degrees(lat2), (np.degrees(lon2) + 540.0) % 360.0 - 180.0


def horizon_profile(dem: Dem, lat_deg: float, lon_deg: float,
                    observer_height_m: float = DEFAULT_OBSERVER_HEIGHT_M,
                    max_distance_m: float = DEFAULT_MAX_DISTANCE_M,
                    step_m: float | None = None) -> np.ndarray:
    """Horizon elevation angle (deg) for azimuths 0..359° (clockwise from north).

    Rays that leave the DEM keep the maximum found inside it. A ray with no
    valid sample at all falls back to the geometric horizon of a bare sphere.

    Raises:
        ValueError: if the site itself has no DEM coverage.
    """
    ground = dem.height_at(lat_deg, lon_deg)
    if not np.isfinite(ground):
        raise ValueError(f"site ({lat_deg:.3f}, {lon_deg:.3f}) is outside the DEM or on a no-data cell")
    observer = ground + observer_height_m

    step = step_m or dem.resolution_m
    distances = np.arange(step, max_distance_m + step / 2, step)            # (D,)
    azimuths = np.arange(N_AZIMUTHS, dtype=np.float64)                       # (A,)

    lat, lon = destination(lat_deg, lon_deg, azimuths[:, None], distances[None, :])
    x, y = dem.project(lon.ravel(), lat.ravel())
    heights = dem.sample(x, y).reshape(lat.shape)                           # (A, D)

    dh = heights - observer
    elev = np.degrees(np.arctan2(dh - distances**2 / (2 * MOON_RADIUS_M), distances))

    no_data = np.all(np.isnan(elev), axis=1)
    profile = np.where(no_data, -np.inf, np.nanmax(np.where(np.isnan(elev), -np.inf, elev), axis=1))
    bare_sphere = -math.degrees(math.acos(MOON_RADIUS_M / (MOON_RADIUS_M + observer_height_m)))
    return np.where(np.isfinite(profile), profile, bare_sphere)


def ray_coverage(dem: Dem, lat_deg: float, lon_deg: float,
                 max_distance_m: float = DEFAULT_MAX_DISTANCE_M) -> float:
    """Fraction (0..1) of the ray samples that fall on valid DEM cells — a data-quality hint."""
    distances = np.arange(dem.resolution_m, max_distance_m + dem.resolution_m / 2, dem.resolution_m)
    lat, lon = destination(lat_deg, lon_deg, np.arange(N_AZIMUTHS)[:, None], distances[None, :])
    x, y = dem.project(lon.ravel(), lat.ravel())
    return float(np.isfinite(dem.sample(x, y)).mean())
