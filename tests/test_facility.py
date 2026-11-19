"""Facility levels: sectors behind keycard doors, rooms with a job (programs.py) and its blockout
furniture, vents as low short-cuts."""
import collections

import pytest

from badlevel import generate
from badlevel.export import to_map
from badlevel.programs import SECTOR_PROGRAMS, sectors_for
from badlevel.recipes import PRESETS, compile_recipe

FURNITURE = ("desks", "tables", "benches", "consoles", "workbenches", "server racks", "shelving", "tanks",
             "boilers", "reactor core", "counter", "reception desk", "conference table")
HALLS = {hall for _, hall in SECTOR_PROGRAMS.values()}


@pytest.fixture(scope="module")
def levels():
    return {(name, seed): generate(compile_recipe(PRESETS[name], seed))
            for name in ("facility", "complex") for seed in (2, 4, 6)}


def test_sectors_and_programs(levels):
    for key, lv in levels.items():
        assert lv.report["ok"]
        rooms = [r for r in lv.rooms if not r.is_connector and r.kind not in ("megaroom", "courtyard", "overlook")]
        assert all(r.meta.get("program") for r in rooms), key
        by_kind = {r.kind: r.meta["program"] for r in rooms}
        assert by_kind["start"] == "lobby" and by_kind["exit"] == "transit" and by_kind["key"] == "security"
        sectors = {r.meta["sector"] for r in rooms}
        assert len(sectors) >= 2, key                   # keycard doors divide sectors
        halls = collections.Counter((r.meta["sector"], r.meta["program"]) for r in rooms if r.meta["program"] in HALLS)
        assert all(n == 1 for n in halls.values()), halls   # one big hall per sector


def test_sector_order():
    assert sectors_for(3, 1)[0] == "admin" and sectors_for(3, 1)[-1] == "reactor"
    assert sectors_for(1, 1) == ["research"]


def test_rooms_are_furnished(levels):
    want = total = 0
    for lv in levels.values():
        parts = set(lv.part_solid.values()) | set(lv.part_cells.values())
        for r in lv.rooms:
            if r.meta.get("program") in ("office", "lab", "server", "storage", "cafeteria", "pump", "security"):
                total += 1
                want += any(p.startswith(f"room:{r.id}/") and p.split("/", 1)[1] in FURNITURE for p in parts)
    assert total and want / total >= 0.7, (want, total)


def test_furniture_is_named_in_the_map(levels):
    lv = levels[("facility", 4)]
    text = to_map(lv)
    names = {p.split("/", 1)[1] for p in set(lv.part_solid.values()) | set(lv.part_cells.values()) if "/" in p}
    hit = [n for n in FURNITURE if n in names]
    assert hit
    assert any(f'/{n}"' in text for n in hit)          # a func_group an artist can find and swap


def test_vents(levels):
    cfg = compile_recipe(PRESETS["facility"], 1)
    vents = [(lv, c) for lv in levels.values() for c in lv.corridors if c.vent]
    assert vents
    for lv, c in vents:
        assert c.height == lv.config.vent_height and c.radius == 1 and not c.beats
        assert len(c.path) <= 40                         # a short-cut, not a trek
        assert any(lv.part_cells.get(p) == f"corridor:{c.id}/vent" for p in c.path)
    assert cfg.vent_height < 128


def test_campaign_has_no_programs():
    lv = generate(compile_recipe(PRESETS["classic"], 3))
    assert not any(r.meta.get("program") for r in lv.rooms)
    assert not any(c.vent for c in lv.corridors)
