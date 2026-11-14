"""Sun and Earth positions as seen from a point on the lunar surface.

Follows Skyfield's "Planetary reference frames" recipe: the observer is a
latitude/longitude on the Moon in the Mean-Earth / polar-axis frame
(``MOON_ME_DE421`` from ``moon_080317.tf``), and ``altaz()`` returns the
altitude above the local horizontal plane and the azimuth clockwise from
lunar north. The Moon has no atmosphere, so no refraction is applied.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from skyfield.api import Loader, PlanetaryConstants

KERNEL_FILES = ("de421.bsp", "moon_080317.tf", "pck00008.tpc", "moon_pa_de421_1900-2050.bpc")
MOON_FRAME = "MOON_ME_DE421"
DEFAULT_KERNEL_DIR = Path(__file__).resolve().parent / "kernels"


@dataclass
class MoonSky:
    """Loaded ephemeris, planetary constants and the lunar body-fixed frame."""

    timescale: object
    ephemeris: object
    constants: PlanetaryConstants
    frame: object

    def times(self, start: datetime, end: datetime, step_hours: float = 1.0):
        """Skyfield Time array from ``start`` to ``end`` inclusive (UTC)."""
        total_h = (end - start).total_seconds() / 3600.0
        count = int(np.floor(total_h / step_hours + 1e-9)) + 1
        hours = np.arange(count) * step_hours
        return self.timescale.utc(start.year, start.month, start.day,
                                  start.hour + hours, start.minute, start.second)

    def altaz(self, lat_deg: float, lon_deg: float, times, body: str) -> tuple[np.ndarray, np.ndarray]:
        """Apparent (altitude, azimuth) in degrees of ``body`` ('sun' / 'earth') for each time."""
        site = self.ephemeris["moon"] + self.constants.build_latlon_degrees(self.frame, lat_deg, lon_deg)
        apparent = site.at(times).observe(self.ephemeris[body]).apparent()
        alt, az, _ = apparent.altaz()
        return alt.degrees, az.degrees


def load_moon_sky(kernel_dir: str | Path = DEFAULT_KERNEL_DIR) -> MoonSky:
    """Load kernels from ``kernel_dir`` (never downloads).

    Raises:
        FileNotFoundError: listing missing kernels and how to fetch them.
    """
    kernel_dir = Path(kernel_dir)
    missing = [k for k in KERNEL_FILES if not (kernel_dir / k).exists()]
    if missing:
        raise FileNotFoundError(
            f"missing SPICE kernels in {kernel_dir}: {', '.join(missing)} — run: python download_kernels.py"
        )

    load = Loader(str(kernel_dir), verbose=False)
    pc = PlanetaryConstants()
    pc.read_text(load("moon_080317.tf"))
    pc.read_text(load("pck00008.tpc"))
    pc.read_binary(load("moon_pa_de421_1900-2050.bpc"))

    return MoonSky(
        timescale=load.timescale(builtin=True),
        ephemeris=load("de421.bsp"),
        constants=pc,
        frame=pc.build_frame_named(MOON_FRAME),
    )


def parse_utc(text: str) -> datetime:
    """Parse '2027-01-01T00:00:00Z' (or without Z / seconds) into an aware UTC datetime."""
    dt = datetime.fromisoformat(text.strip().replace("Z", "+00:00"))
    return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt.astimezone(timezone.utc)
