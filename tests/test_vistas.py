"""Vistas (vistas.py): big windows in grand halls onto a sky and hills nobody can walk to."""
import pytest

from badlevel import generate
from badlevel.export import to_map
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import _link_edges, _node_at, flood


@pytest.fixture(scope="module")
def levels():
    return {seed: generate(compile_recipe(PRESETS["grand"], seed)) for seed in (1, 5, 6)}


def _vista_cells(lv):
    return {c for c, os_ in lv.owners.items() if any(o[2].startswith("vista:") for o in os_)}


def test_grand_halls_get_vistas(levels):
    for seed, lv in levels.items():
        wins = [e for e in lv.entities if e.kind == "window" and e.props.get("vista") is not None]
        assert wins, seed
        for w in wins:
            assert w.props["glazed"]
            (x0, y0, z0), (x1, y1, z1) = w.bounds
            assert z1 - z0 >= 160 and max(x1 - x0, y1 - y0) >= 4 * lv.cell     # big: a wall of glass
        assert lv.report["ok"]


def test_the_view_is_seen_not_walked(levels):
    for lv in levels.values():
        view = _vista_cells(lv)
        assert view and view <= lv.sky
        start = next(e for e in lv.entities if e.kind == "player_start")
        reach = flood(lv, _node_at(lv, start.pos), set(), _link_edges(lv))
        assert not any((x, y) in view for x, y, _ in reach)
        # the window cells are glass: solid to walk, open to see
        panes = {(int(x // lv.cell), int(y // lv.cell)) for e in lv.entities
                 if e.kind == "window" and e.props.get("vista") is not None
                 for x, y in [e.pos[:2]]}
        assert panes <= lv.glass


def test_hills_are_planar_slopes(levels):
    for lv in levels.values():
        for c in _vista_cells(lv):
            if c in lv.floor_slope:
                z00, z10, z11, z01 = lv.floor_slope[c]
                assert z00 + z11 == z10 + z01          # one plane: a valid brush top
                assert lv.spans[c][0][0] == max(z00, z10, z11, z01)


def test_sky_walls_face_only_the_view(levels):
    for lv in levels.values():
        view = _vista_cells(lv)
        assert lv.sky_walls
        for (x, y) in lv.sky_walls:
            assert (x, y) not in lv.spans
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    n = (x + dx, y + dy)
                    assert n in view or n not in lv.spans


def test_vista_export(levels):
    text = to_map(levels[6])
    assert '"_badlevel_part" "vista:0/terrain"' in text or "vista:0/terrain" in text


def test_off_by_default():
    cfg = compile_recipe(PRESETS["fortress"], 2)
    assert cfg.vistas == 0
    lv = generate(cfg)
    assert not lv.sky_walls and not any(e.props.get("vista") is not None for e in lv.entities if e.kind == "window")
