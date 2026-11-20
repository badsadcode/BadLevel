"""The wizard's tower (tower.py): a helix round a void, flights across it, rooms off its landings."""
import math

import pytest

from badlevel import generate
from badlevel import tower as T
from badlevel.recipes import PRESETS, compile_recipe


@pytest.fixture(scope="module")
def levels():
    return {seed: generate(compile_recipe(PRESETS["wizard"], seed)) for seed in (2, 5, 6)}


def _tower(lv):
    return next(r for r in lv.rooms if r.kind == "tower")


def test_wizard_levels(levels):
    for lv in levels.values():
        assert lv.report["ok"] and lv.report["softlock_spots"] == 0
        tw = _tower(lv)
        S = tw.meta["storeys"]
        storeys = {lv.graph.nodes[r.node].storey for r in lv.rooms if r.kind != "tower"}
        assert storeys == set(range(S))                        # rooms off every landing
        kinds = {r.kind: lv.graph.nodes[r.node].storey for r in lv.rooms}
        assert kinds["start"] == 0 and kinds["exit"] == S - 1   # in at the bottom, out at the top
        progs = {r.meta.get("program") for r in lv.rooms if r.meta.get("program")}
        assert {"entrance hall", "observatory"} <= progs


def test_helix_is_gentle_and_lands_on_every_storey():
    hx = T.Helix(9, 4, 256, 0.0)
    for k in range(4):
        assert hx.height(k * hx.per) == k * 256                  # a flat landing at each storey
    prev = hx.height(0.0)
    phi = 0.0
    while phi < 3 * hx.per:
        phi += 1.0 / (9 - T.RING / 2)                            # about a cell along the helix
        h = hx.height(phi)
        assert 0 <= h - prev <= 16                               # never more than a step per cell
        prev = h


def test_the_void_and_the_flights(levels):
    for lv in levels.values():
        tw = _tower(lv)
        flights = tw.meta["flights"]
        assert len(flights) >= 2
        assert len({f["dir"] for f in flights}) == len(flights)    # each a new direction: they cross
        D, cells = T._geom(tw.meta["R"])
        void = [(tw.x + u, tw.y + v) for (u, v), (d, a) in cells.items() if d < tw.meta["R"] - T.RING]
        for c in void:                                           # fall in, land on the ground floor
            assert lv.spans[c][0][0] == tw.floor
            for a, b in zip(lv.spans[c], lv.spans[c][1:]):      # headroom under every flight
                assert a[1] - a[0] >= lv.config.player_height
        assert all(c in lv.sky for c in void)                    # the sky at the top


def test_landings_face_different_ways(levels):
    tw = _tower(levels[2])
    ports = T.ports(tw)
    assert len(ports) == tw.meta["storeys"]
    centres = []
    for cells, h in ports:
        assert cells
        x = sum(c[0] for c in cells) / len(cells) - (tw.x + tw.w / 2)
        y = sum(c[1] for c in cells) / len(cells) - (tw.y + tw.h / 2)
        centres.append(round(math.degrees(math.atan2(y, x))) % 360)
    assert len(set(round(a / 45) for a in centres)) >= 3          # doors round the tower, not stacked


def test_no_tower_elsewhere():
    lv = generate(compile_recipe(PRESETS["classic"], 1))
    assert not any(r.kind == "tower" for r in lv.rooms)
