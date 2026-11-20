"""Dungeons (after Noor's concept art): chasm bridges over spikes, torches, grates, traps, and
dungeon rooms with a job (crypts, cells, libraries...)."""
import pytest

from badlevel import generate
from badlevel.decorate import CHASM_DEPTH
from badlevel.export import to_map
from badlevel.programs import DUNGEON_PROGRAMS
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import _link_edges, _node_at, flood


@pytest.fixture(scope="module")
def levels():
    return {seed: generate(compile_recipe(PRESETS["dungeon"], seed)) for seed in (3, 4, 6)}


def test_dungeon_rooms(levels):
    known = {p for progs, hall in DUNGEON_PROGRAMS.values() for p in progs + [hall]} | \
            {"gatehouse", "stair down", "vault", "hidden cache"}
    for lv in levels.values():
        assert lv.report["ok"]
        progs = {r.meta.get("program") for r in lv.rooms if r.meta.get("program")}
        assert progs <= known and len(progs) >= 6
        assert {r.meta["sector"] for r in lv.rooms} <= set(DUNGEON_PROGRAMS)


def test_chasms_are_death_not_traps(levels):
    for lv in levels.values():
        chasms = [r for r in lv.rooms if "chasm" in r.meta]
        assert chasms
        start = next(e for e in lv.entities if e.kind == "player_start")
        reach = flood(lv, _node_at(lv, start.pos), set(), _link_edges(lv))
        for r in chasms:
            pit = [c for c, p in lv.part_cells.items() if p == f"room:{r.id}/chasm"]
            bridge = [c for c, p in lv.part_cells.items() if p == f"room:{r.id}/bridge"]
            assert len(pit) >= 20 and bridge
            for c in pit:
                assert lv.spans[c][0][0] == r.floor - CHASM_DEPTH and lv.hazard[c] == "spikes"
                assert not any((c[0], c[1], i) in reach for i in range(len(lv.spans[c])))   # never walked into
            assert CHASM_DEPTH > lv.config.max_drop
            for c in bridge:                                    # the bridge stays at the room's floor
                assert lv.spans[c][0][0] == r.floor


def test_torches_grates_traps(levels):
    for lv in levels.values():
        kinds = [e.kind for e in lv.entities]
        assert kinds.count("torch") >= 10
        traps = [e for e in lv.entities if e.kind == "trap"]
        assert traps and all(t.props["type"] in ("blade", "darts", "spikes") for t in traps)
    grates = [c for lv in levels.values() for c, p in lv.part_ceiling.items() if p.endswith("/grate")]
    assert grates and all(c in lv.sky for lv in levels.values() for c, p in lv.part_ceiling.items()
                          if p.endswith("/grate"))


def test_dungeon_export(levels):
    lv = levels[6]
    q1, q2 = to_map(lv, "quake"), to_map(lv, "quake2")
    assert '"classname" "light_torch_small_walltorch"' in q1
    assert '"classname" "trigger_hurt"' in q1 and '"classname" "trigger_hurt"' in q2
    assert "room:" in q1 and "/bridge" in q1 or "/chasm" in q1


def test_off_elsewhere():
    cfg = compile_recipe(PRESETS["classic"], 1)
    assert not cfg.torches and cfg.chasms == 0 and cfg.traps == 0 and cfg.skylights == 0
    lv = generate(cfg)
    assert not any(e.kind in ("torch", "trap") for e in lv.entities)
    assert "spikes" not in lv.hazard.values()


def test_dart_traps_are_real_in_quake(levels):
    """A darts marker becomes a tripwire firing a shooter (Q1 trap_spikeshooter, Q2 target_blaster)."""
    for lv in levels.values():
        darts = [e for e in lv.entities if e.kind == "trap" and e.props["type"] == "darts"]
        q1, q2 = to_map(lv, "quake"), to_map(lv, "quake2")
        assert q1.count('"classname" "trap_spikeshooter"') == len(darts)
        assert q2.count('"classname" "target_blaster"') == len(darts)
        assert q1.count('"_badlevel_kind" "tripwire"') == len(darts)
