"""Diviner PCP lookup: grid, gap filling and the subsolar-point geometry (offline)."""

import json
import math

import numpy as np
import pytest

import diviner_temperatures as d


def test_stereo_xy_matches_a_pcp_record():
    # sum_slon01 record: x=0.003730, y=0.137329, clon=1.555831, clat=-82.141071
    x, y = d.stereo_xy(-82.141071, 1.555831)
    assert x == pytest.approx(0.003730, abs=2e-6)
    assert y == pytest.approx(0.137329, abs=2e-6)


def test_grid_cell_contains_the_point():
    for lat, lon in [(-85.99192, 2.0564), (-89.4, -137.3), (-83.36914, -67.6646)]:
        c = d.grid_cell("p", lat, lon)
        assert d._distance_m(lat, lon, c.lat, c.lon) < 240 / math.sqrt(2) + 1
        assert c.columns[1] - c.columns[0] == pytest.approx(d.GRID_STEP)


def test_window_3x3_picks_rows_on_the_grid_and_marks_empty_bins():
    c = d.grid_cell("p", -86.0, 10.0)
    s = c.step
    rows = [[(c.columns[i], c.y + dy * s, 0, 0, 100 + 10 * i + dy) for dy in (-1, 0, 1) if not (i == 2 and dy == 1)]
            for i in range(3)]
    w = d.window_3x3(rows, c)
    assert w[4] == 110          # centre
    assert w[0] == 99 and w[8] is None


def test_season_curve_prefers_centre_then_neighbours_then_interpolation():
    full = [None] * 4 + [150.0] + [None] * 4
    nbr = [120.0, None, 130.0] + [None] * 6
    curve, counts = d.season_curve([full, nbr, None, full])
    assert curve[0] == 150 and curve[1] == 125 and curve[3] == 150
    assert curve[2] == pytest.approx(137.5)      # halfway between bins 1 and 3
    assert counts == {"centre": 2, "neighbours": 1, "interpolated": 1}


def test_subsolar_point_geometry():
    lat, lon = d.subsolar_point(-85.0, 30.0, np.array([5.0, 5.0]), np.array([0.0, 180.0]))
    assert lat == pytest.approx([0.0, -10.0], abs=1e-9)
    assert lon == pytest.approx([30.0, 210.0], abs=1e-9)


def test_app_file_lit_hours_are_warmer_than_dark_hours():
    app = json.loads(d.FRONTEND_JSON.read_text(encoding="utf-8"))
    sites = [p for p in app["points"] if "check" in p]
    assert len(sites) == 9
    for p in sites:
        assert len(p["summerK"]) == len(p["winterK"]) == 96
        assert p["check"]["litMeanK"] > p["check"]["darkMeanK"] + 40, p["id"]
