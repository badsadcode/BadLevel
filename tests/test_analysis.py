import json

import numpy as np
import pytest

from badlevel import GenConfig, generate
from badlevel.analysis import AnalysisConfig, analyze, generate_selected
from badlevel.analysis.occupancy import Occupancy
from badlevel.cli import main as cli_main
from badlevel.export import to_html
from badlevel.level import Level

EYE, CHEST, CROUCH = 46, 48, 22


def box_level(w=20, h=7, floor=0, ceil=192):
    """A plain empty room of w x h cells (with a solid border)."""
    lv = Level(seed=0, cell=32, width=w + 2, height=h + 2, config=GenConfig())
    for x in range(1, w + 1):
        for y in range(1, h + 1):
            lv.spans[(x, y)] = [(floor, ceil)]
    return lv


def p(cx, cy, z):
    return np.array([[(cx + 0.5) * 32, (cy + 0.5) * 32, z]], dtype=float)


def sees(occ, a, b):
    return bool(occ.visible(a, b)[0])


def test_open_room_is_visible_and_walls_block():
    lv = box_level()
    occ = Occupancy(lv)
    assert sees(occ, p(2, 3, EYE), p(18, 3, CHEST))
    for y in range(1, 8):  # full-height wall across the room
        del lv.spans[(10, y)]
    occ = Occupancy(lv)
    assert not sees(occ, p(2, 3, EYE), p(18, 3, CHEST))


def test_low_crate_hides_crouching_target_only():
    lv = box_level()
    lv.spans[(15, 3)] = [(40, 192)]  # 40-unit crate between viewer and target
    occ = Occupancy(lv)
    viewer = p(2, 3, EYE)
    assert sees(occ, viewer, p(16, 3, CHEST)), "standing target behind a low crate stays visible"
    assert not sees(occ, viewer, p(16, 3, CROUCH)), "crouching behind it hides you"


def test_tall_block_hides_both():
    lv = box_level()
    lv.spans[(15, 3)] = [(64, 192)]
    occ = Occupancy(lv)
    assert not sees(occ, p(2, 3, EYE), p(16, 3, CHEST))


def test_balcony_deck_blocks_sight_from_below_but_not_from_the_edge():
    lv = box_level(ceil=320)
    for x in range(10, 21):  # deck over the right half: floor space below, deck surface at 144
        for y in range(1, 8):
            lv.spans[(x, y)] = [(0, 128), (144, 320)]
    occ = Occupancy(lv)
    on_deck = p(18, 3, 144 + CHEST)
    assert not sees(occ, p(17, 3, EYE), on_deck), "the deck slab is between them"
    assert sees(occ, p(2, 3, EYE), p(11, 3, 144 + CHEST)), "a target at the deck edge is visible from the floor"
    assert sees(occ, p(11, 3, 144 + EYE), p(2, 3, CHEST)), "standing at the edge you can look down"
    assert not sees(occ, p(18, 3, 144 + EYE), p(2, 3, CHEST)), "from far back the deck itself is in the way"


def test_diagonal_half_cell_blocks_only_its_solid_triangle():
    lv = box_level(w=6, h=6)
    lv.diag[(3, 3)] = "sw"  # solid triangle in the cell's south-west half
    occ = Occupancy(lv)
    inside = lambda u, v: occ.is_open(np.array([[(3 + u) * 32, (3 + v) * 32, 100.0]]))[0]
    assert not inside(0.2, 0.2) and inside(0.8, 0.8)
    # rays parallel to the 45-degree wall: through the open half pass, through the solid half do not
    ray = lambda k: occ.visible(np.array([[(3 + k + 0.9) * 32, (3 - 0.9) * 32, 100.0]]),
                                np.array([[(3 + k - 0.9) * 32, (3 + 0.9) * 32, 100.0]]))[0]
    assert ray(1.35), "u + v = 1.35 inside the cell: open half"
    assert not ray(0.65), "u + v = 0.65: solid half"
    # the S and W edges are fully solid: a straight ray crossing the cell is blocked
    assert not occ.visible(np.array([[3.9 * 32, 1.5 * 32, 100.0]]), np.array([[3.9 * 32, 5.5 * 32, 100.0]]))[0]


@pytest.fixture(scope="module")
def report():
    return analyze(generate(seed=7))


def test_report_is_machine_readable_and_consistent(report):
    js = json.loads(json.dumps(report))
    assert js["format"] == "badlevel-analysis"
    fields = js["samples"]["fields"]
    data = np.array(js["samples"]["data"])
    assert data.shape == (js["samples"]["count"], len(fields)) and len(data) > 100
    col = {f: data[:, i] for i, f in enumerate(fields)}
    assert ((col["cover"] >= 0) & (col["cover"] <= 1)).all()
    assert (col["exposure"] >= 0).all() and set(np.unique(col["cls"])) <= {0, 1, 2}
    assert (col["longest"] <= AnalysisConfig().max_range * 32 * 1.5).all()
    shares = js["level"]["exposure_share"]
    assert abs(sum(shares.values()) - 1) < 0.01
    assert len(js["rooms"]) == 12 and all(0 <= r["mutual_visibility"] <= 1 for r in js["rooms"])
    assert all(f["severity"] in ("problem", "warn", "info") for f in js["flags"])
    level = generate(seed=7)
    assert js["level"]["loops"] == len(level.corridors) - len(level.rooms) + 1
    assert all(l["length"] >= AnalysisConfig().lane_min for l in js["lanes"])


def test_analysis_is_deterministic(report):
    again = analyze(generate(seed=7))
    assert again["samples"]["data"] == report["samples"]["data"]
    assert again["flags"] == report["flags"]


def test_balcony_rooms_show_high_ground():
    level = next(lv for lv in (generate(seed=s) for s in range(40))
                 if any("balcony" in r.features for r in lv.rooms))
    rep = analyze(level)
    balcony = [r for r in rep["rooms"] if "balcony" in r["features"]]
    assert balcony and any(r["high_ground"] and r["high_ground"]["height_adv"] > 32 for r in balcony)


def test_gate_rejects_and_picks_another_variant():
    rejected = []

    def gate(rep):
        if not rejected:
            rejected.append(1)
            return ["first candidate rejected"]
        return []
    level, rep = generate_selected(GenConfig(seed=3), candidates=3, gate=gate)
    assert rep["selection"]["chosen_variant"] == 1 and rep["selection"]["passed_gate"]
    assert rep["selection"]["candidates"][0]["rejected"] == ["first candidate rejected"]

    level, rep = generate_selected(GenConfig(seed=3), candidates=2, gate=lambda r: ["always"])
    assert not rep["selection"]["passed_gate"] and len(rep["selection"]["candidates"]) == 2


def test_preview_embeds_analysis(report):
    page = to_html(generate(seed=7), report)
    assert "const ANALYSIS = {" in page and '"tiles":[' in page
    assert "const ANALYSIS = null" in to_html(generate(seed=7))


def test_cli_writes_analysis(tmp_path):
    assert cli_main(["generate", "--seed", "5", "--out", str(tmp_path), "--formats", "html,analysis",
                     "--candidates", "2"]) == 0
    rep = json.loads((tmp_path / "badlevel_5.analysis.json").read_text())
    assert rep["selection"]["candidates"]
