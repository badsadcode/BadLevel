"""Arena mode: deathmatch maps (spawns, weapons, power-ups, jump pads; no keys, exit or monsters).
Every spot leads back to every other, every room has two ways out, every spawn is about as far
from its nearest gun as the others, and every jump pad lands its player on its tower."""
import math

import pytest

from badlevel import generate
from badlevel.arena import jump_launch
from badlevel.export import to_map
from badlevel.recipes import PRESETS, arena_mode, compile_recipe
from badlevel.validate import validate

CAMPAIGN_ONLY = {"enemy", "key", "exit", "door", "secret_door"}


@pytest.fixture(scope="module")
def maps():
    return {(name, seed): generate(compile_recipe(PRESETS[name], seed))
            for name in ("duel", "arena") for seed in (1, 2, 3)}


def test_arena_maps_are_deathmatch_maps(maps):
    for (name, seed), lv in maps.items():
        kinds = [e.kind for e in lv.entities]
        assert not CAMPAIGN_ONLY & set(kinds), (name, seed)
        assert kinds.count("player_start") == 1
        assert kinds.count("spawn") >= 4
        assert kinds.count("weapon") >= 3
        assert lv.arena["powerups"]
        assert lv.report["ok"] and lv.report["softlock_spots"] == 0


def test_no_dead_ends_and_fair_spawns(maps):
    for (name, seed), lv in maps.items():
        a = lv.arena
        assert a["dead_ends"] == 0, (name, seed)
        # the worst-placed spawn walks at most twice as far to a gun as the best-placed
        assert a["fairness"] <= 2.0, (name, seed, a["nearest_weapon"])
        spawns = [e.pos for e in lv.entities if e.kind == "spawn"]
        for i, p in enumerate(spawns):          # never on top of each other
            for q in spawns[i + 1:]:
                assert abs(p[0] - q[0]) + abs(p[1] - q[1]) >= 4 * lv.cell


def test_every_arena_has_jump_pads(maps):
    assert sum(lv.arena["jump_pads"] for lv in maps.values()) >= len(maps)
    for lv in maps.values():
        pads = [e for e in lv.entities if e.kind == "jumppad"]
        links = [l for l in lv.links if l["kind"] == "jumppad"]
        assert len(pads) == len(links)
        assert all(not l["two_way"] for l in links)     # up by pad, down by dropping


def _fly(lv, pad):
    """Fly a player box along the pad's launch; where does it come down?"""
    p0 = (pad.pos[0], pad.pos[1], pad.bounds[0][2] + 4)
    speed, pitch, yaw = jump_launch(p0, pad.props["target"])
    v = speed * 10
    vh = v * math.cos(math.radians(-pitch))
    vx, vy, vz = vh * math.cos(math.radians(yaw)), vh * math.sin(math.radians(yaw)), v * math.sin(math.radians(-pitch))
    x, y, z = p0
    top = pad.props["target"][2] - 24
    s = lv.cell
    for _ in range(3000):
        x, y = x + vx / 144, y + vy / 144
        vz -= 800 / 144
        z += vz / 144
        for px in (x - 15, x + 15):
            for py in (y - 15, y + 15):
                c = (int(px // s), int(py // s))
                if not any(f <= z and z + 56 <= ce for f, ce in lv.spans.get(c, ())):
                    return (x, y, z), vz < 0 and abs(z - top) < 10
    return (x, y, z), False


def test_jump_pads_land_on_their_towers(maps):
    for lv in maps.values():
        for pad in (e for e in lv.entities if e.kind == "jumppad"):
            (x, y, _), landed = _fly(lv, pad)
            tx, ty, _ = pad.props["target"]
            assert landed and math.hypot(x - tx, y - ty) < 24


def test_jump_launch_math():
    speed, pitch, yaw = jump_launch((0, 0, 0), (100, 0, 128))
    assert pitch < -45 and abs(yaw) < 1e-6 and speed > 0
    _, _, yaw = jump_launch((0, 0, 0), (0, 100, 64))
    assert yaw == 90


def test_any_recipe_in_arena_mode():
    cfg = compile_recipe(PRESETS["classic"], 2)
    arena_mode(cfg)
    lv = generate(cfg)
    assert validate(lv)["ok"]
    assert not {"enemy", "key", "exit"} & {e.kind for e in lv.entities}
    assert lv.arena["spawns"] >= 4


def test_arena_export_quake_and_quake2(maps):
    lv = maps[("arena", 1)]
    q1, q2 = to_map(lv, "quake"), to_map(lv, "quake2")
    for text in (q1, q2):
        assert '"classname" "info_player_deathmatch"' in text
        assert '"classname" "trigger_push"' in text
        assert "monster_" not in text
    assert '"item_artifact_super_damage"' in q1 and '"item_quad"' in q2


def test_campaign_is_untouched():
    for name in ("classic", "techbase"):
        cfg = compile_recipe(PRESETS[name], 1)
        assert cfg.mode == "campaign" and cfg.jump_pads == 0
