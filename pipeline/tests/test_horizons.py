"""Cross-validation of the ephemeris against NASA/JPL Horizons (offline).

Uses the raw Horizons responses stored in ``data/horizons/`` (refresh with
``python validate_horizons.py --refresh``); no network access is needed.
Needs the SPICE kernels (``python download_kernels.py``), skipped otherwise.

Tolerances. The first run (2026-11-14, 9 sites × 24 epochs × Sun/Earth) gave
max |Δel| = 0.036″ and max |Δaz| = 0.032″ (≈1e-5°) between the pipeline
(DE421 + MOON_ME_DE421) and Horizons (DE441 + high-precision MEAN_ME). That is
the whole DE421-vs-DE441 difference in Moon position and lunar orientation for
this geometry. The requested starting points (0.05° el, 0.2° az) are ~5000×
looser than that and would even pass with the site in the wrong (PA) lunar
frame, which is off by 0.021–0.024°. We therefore use:

* ``TOL_PIPELINE_DEG = 0.001°`` (3.6″) for elevation and azimuth: ~100× the
  observed residual, so a future Horizons switch to a newer DE or orientation
  model will not make the test flaky, yet 20× tighter than the ME↔PA offset, so a
  frame mix-up, wrong azimuth convention or time-scale bug fails.
* ``TOL_SHIPPED_DEG = 0.005° + 0.001°`` for the shipped ``illumination/*.json``,
  which store centi-degrees: half a quantisation step plus the model tolerance.

Even 0.001° is far below anything the product resolves: the horizon mask has
1° azimuth steps, the solar radius is 0.267° and ``earthVisible`` uses a 0.5°
margin. Azimuth samples above ``AZ_MAX_ELEV_DEG`` (80°) would be skipped as
ill-conditioned; at these south-polar sites Sun and Earth stay below ~12°.
"""

from __future__ import annotations

import dataclasses
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import ephemeris  # noqa: E402
import validate_horizons as vh  # noqa: E402

TOL_PIPELINE_DEG = 0.001
TOL_SHIPPED_DEG = 0.005 + TOL_PIPELINE_DEG
PA_MIN_OFFSET_DEG = 0.01  # the wrong frame must be clearly distinguishable


@pytest.fixture(scope="module")
def sky() -> ephemeris.MoonSky:
    try:
        return ephemeris.load_moon_sky()
    except FileNotFoundError as exc:
        pytest.skip(str(exc))


@pytest.fixture(scope="module")
def sites() -> list[dict]:
    return vh.load_sites()


@pytest.fixture(scope="module")
def times():
    return vh.epochs()


@pytest.fixture(scope="module")
def tables(sites, times):
    return vh.load_all(sites, times)


def test_snapshots_cover_all_sites_and_epochs(sites, times, tables):
    assert len(sites) == 9
    for body in vh.BODIES:
        assert set(tables[body]) == {s["id"] for s in sites}
        for table in tables[body].values():
            assert table.times == times
            assert np.all((table.az >= 0) & (table.az < 360))
            assert np.all(np.abs(table.el) <= 90)


def test_horizons_uses_mean_earth_sphere_and_clockwise_azimuth():
    """The conventions the comparison relies on, as stated in the raw responses."""
    for path in sorted(vh.SNAPSHOT_DIR.glob("*.txt")):
        text = path.read_text(encoding="utf-8")
        assert "Center pole/equ : MEAN_ME" in text, path.name
        assert "Center radii    : 1737.4, 1737.4, 1737.4 km" in text, path.name
        assert "Atmos refraction: NO (AIRLESS)" in text, path.name
        assert "Azimuth is measured clockwise from north" in text, path.name


@pytest.mark.parametrize("body", list(vh.BODIES))
def test_pipeline_matches_horizons(sky, sites, times, tables, body):
    r = vh.compare(sky, sites, body, tables[body], times)
    worst_el = int(np.argmax(np.abs(r.d_el)))
    assert np.max(np.abs(r.d_el)) < TOL_PIPELINE_DEG, f"{r.site_ids[worst_el]}: Δel = {r.d_el[worst_el]:.6f}°"
    d_az = np.where(r.az_ok, r.d_az, 0.0)
    worst_az = int(np.argmax(np.abs(d_az)))
    assert np.max(np.abs(d_az)) < TOL_PIPELINE_DEG, f"{r.site_ids[worst_az]}: Δaz = {d_az[worst_az]:.6f}°"


@pytest.mark.parametrize("body", list(vh.BODIES))
def test_wrong_lunar_frame_would_be_detected(sky, sites, times, tables, body):
    """Sanity check of the test itself: a PA-frame site must fail the tolerance."""
    sky_pa = dataclasses.replace(sky, frame=sky.constants.build_frame_named("MOON_PA_DE421"))
    r = vh.compare(sky_pa, sites, body, tables[body], times)
    assert np.max(np.abs(r.d_el)) > PA_MIN_OFFSET_DEG


@pytest.mark.parametrize("body", list(vh.BODIES))
def test_shipped_illumination_matches_horizons(sites, times, tables, body):
    r = vh.compare_shipped(sites, body, tables[body], times)
    if r is None:
        pytest.skip("illumination JSON missing or generated for other coordinates than sites.json")
    assert np.max(np.abs(r.d_el)) < TOL_SHIPPED_DEG
    assert np.max(np.abs(r.d_az[r.az_ok])) < TOL_SHIPPED_DEG


def test_parse_handles_marker_columns():
    """Rows with and without '*'/'i' markers, including 3-digit azimuths."""
    text = (
        "Center geodetic : 335.74547, -86.98951, 0.0          {E-lon(deg),Lat(deg),Alt(km)}\n"
        "$$SOE\n"
        " 2027-Feb-01 00:00:00.000 *   270.413147300   0.249627173\n"
        " 2027-Aug-15 00:00:00.000 *i   50.995780786   2.191474151\n"
        " 2028-Mar-20 00:00:00.000  i  276.650940164  -1.020838067\n"
        " 2028-Mar-20 01:00:00.000     276.143660509  -1.047929692\n"
        "$$EOE\n"
    )
    t = vh.parse(text)
    assert t.site_lon == pytest.approx(335.74547) and t.site_lat == pytest.approx(-86.98951)
    assert list(t.az) == [270.4131473, 50.995780786, 276.650940164, 276.143660509]
    assert list(t.el) == [0.249627173, 2.191474151, -1.020838067, -1.047929692]
    assert [x.hour for x in t.times] == [0, 0, 0, 1]
