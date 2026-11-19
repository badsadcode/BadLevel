"""Mirrored team maps (mirror.py): one half, its mirror image, lanes between, a flag per team."""
import pytest

from badlevel import generate
from badlevel.export import to_map
from badlevel.recipes import PRESETS, arena_mode, compile_recipe

FLIP = {"sw": "se", "se": "sw", "nw": "ne", "ne": "nw"}


@pytest.fixture(scope="module")
def maps():
    return {seed: generate(compile_recipe(PRESETS["ctf"], seed)) for seed in (2, 4, 6)}


def test_exact_mirror(maps):
    for seed, lv in maps.items():
        A = lv.arena["seam"]
        mx = lambda x: 2 * A - 1 - x                       # noqa: E731
        assert all(lv.spans.get((mx(x), y)) == sp for (x, y), sp in lv.spans.items()), seed
        assert all(lv.diag.get((mx(x), y)) == FLIP[c] for (x, y), c in lv.diag.items())
        assert all((mx(x), y) in lv.hazard for (x, y) in lv.hazard)
        for (x, y), t in lv.floor_slope.items():
            assert lv.floor_slope[(mx(x), y)] == (t[1], t[0], t[3], t[2])


def test_teams_are_mirror_images(maps):
    for lv in maps.values():
        A, s = lv.arena["seam"], lv.cell
        flags = {e.props["team"]: e.pos for e in lv.entities if e.kind == "flag"}
        assert set(flags) == {1, 2}
        assert abs(flags[1][0] + flags[2][0] - 2 * A * s) < 1e-6 and flags[1][1:] == flags[2][1:]
        spawns = [e for e in lv.entities if e.kind == "spawn"]
        t1 = sorted((round(2 * A * s - e.pos[0]), round(e.pos[1])) for e in spawns if e.props["team"] == 1)
        t2 = sorted((round(e.pos[0]), round(e.pos[1])) for e in spawns if e.props["team"] == 2)
        assert t1 == t2 and len(t1) >= 4
        assert all(e.pos[0] < A * s for e in spawns if e.props["team"] == 1)     # each team on its own side
        quads = [e for e in lv.entities if e.kind == "quad"]
        assert len(quads) == 1 and quads[0].pos[0] == A * s                       # one, on the seam
        assert sum(1 for e in lv.entities if e.kind == "player_start") == 1


def test_lanes_and_one_circuit(maps):
    for lv in maps.values():
        assert lv.report["ok"] and lv.report["softlock_spots"] == 0
        assert 2 <= lv.arena["lanes"] <= 3 and lv.arena["midfield"]
        A = lv.arena["seam"]
        # the halves meet only through the lanes, at the seam
        seam = [(x, y) for (x, y) in lv.spans if x == A - 1]
        assert seam and all(lv.part_cells.get(c, "").endswith(("/lane", "/midfield")) for c in seam)


def test_ctf_export(maps):
    for game, quad in (("quake", "item_artifact_super_damage"), ("quake2", "item_quad")):
        text = to_map(maps[4], game)
        for cls in ("info_player_team1", "info_player_team2", "item_flag_team1", "item_flag_team2",
                    "info_player_deathmatch", quad):
            assert f'"classname" "{cls}"' in text, (game, cls)
        assert text.count('"item_flag_team1"') == 1


def test_any_recipe_as_ctf():
    cfg = compile_recipe(PRESETS["classic"], 5)
    arena_mode(cfg)
    cfg.mirror = True
    lv = generate(cfg)
    assert lv.report["ok"] and lv.arena["team"]
