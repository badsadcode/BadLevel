"""Architecture: hall cross-sections give spaces structure without breaking gameplay."""
import random

import pytest

from badlevel import GenConfig, generate
from badlevel import architecture as A
from badlevel.export import to_map
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import validate

PRESET_STYLE = {"fortress": "gothic", "industrial": "industrial", "bunker": "bunker", "compound": "compound"}


@pytest.fixture(scope="module")
def styled():
    return {name: [generate(compile_recipe(PRESETS[name], s)) for s in range(1, 6)] for name in PRESET_STYLE}


def shaped(lv):
    return [r for r in lv.rooms if "arch" in r.meta]


def test_off_by_default():
    lv = generate(seed=3)
    assert not shaped(lv)
    # an empty style is the same level, byte for byte
    assert to_map(generate(GenConfig(seed=3, arch_style=""))) == to_map(lv)


def test_presets_carry_their_style(styled):
    for name, style in PRESET_STYLE.items():
        assert compile_recipe(PRESETS[name], 1).arch_style == style
        for lv in styled[name]:
            assert validate(lv)["ok"]
            rooms = shaped(lv)
            assert rooms, f"{name} seed {lv.seed}: no room got a cross-section"
            assert all(r.meta["arch"]["style"] == style for r in rooms)
            # the largest space is always treated as the grand one
            assert sum(1 for r in rooms if r.meta["arch"]["tier"] == "grand") >= 1


def test_headroom_and_openings(styled):
    """Ceilings are only ever lowered, never below a walkable surface plus HEAD."""
    for levels in styled.values():
        for lv in levels:
            for r in shaped(lv):
                for c in r.cells():
                    spans = lv.room_spans(c, r)
                    if spans and spans[-1][1] < r.ceil and c not in lv.sky:
                        f, top = spans[-1]
                        assert top - f >= A.HEAD, (lv.seed, r.id, c, spans)


def test_skips_open_areas_and_connectors(styled):
    for levels in styled.values():
        for lv in levels:
            for r in lv.rooms:
                if r.kind == "courtyard" or r.is_connector:
                    assert "arch" not in r.meta


def test_variety_within_a_style(styled):
    """A palette, not a template: several profiles per style, and not one profile per level."""
    for name, levels in styled.items():
        profiles = [r.meta["arch"]["profile"] for lv in levels for r in shaped(lv)]
        assert len(set(profiles)) >= 3, (name, set(profiles))
        for lv in levels:
            ps = [r.meta["arch"]["profile"] for r in shaped(lv)]
            if len(ps) >= 4:
                assert len(set(ps)) >= 2, (name, lv.seed, ps)


def test_grand_halls_get_structure(styled):
    """Mega-rooms in the gothic and industrial presets get a real section: lowered ceilings, and
    across the seeds at least some piers (arcades, columns, pilasters)."""
    for name in ("fortress", "industrial"):
        piers = 0
        for lv in styled[name]:
            mega = next(r for r in lv.rooms if r.kind == "megaroom")
            a = mega.meta["arch"]
            assert a["tier"] == "grand" and a["shaped_cells"] > 50
            piers += a["piers"]
        assert piers > 0


def test_proportions_drive_the_structure():
    """Taller halls get wider bays and thicker piers; every profile keeps its headroom."""
    assert A.bay_size(576, 60, 0.5, 3, 12) > A.bay_size(256, 60, 0.5, 3, 12)
    assert A.pier_size(512) > A.pier_size(256)
    for name, fn in A.PROFILES.items():
        for (w, h, height) in [(9, 12, 192), (14, 20, 320), (30, 38, 512)]:
            from badlevel.level import Room
            room = Room(0, 0, "arena", 0, 0, w, h, 0, height)
            sec = fn(A.Frame(room), random.Random(7))
            if sec is None:
                continue
            assert min(sec.ceil.values()) >= A.HEAD, (name, w, h, height)
            assert max(sec.ceil.values()) <= height, (name, w, h, height)
            for group in sec.piers:
                assert all(0 <= u < sec.frame.L and 0 <= v < sec.frame.W for u, v in group), name


def test_undercroft_stays_low():
    from badlevel.level import Room
    tall = Room(0, 0, "megaroom", 0, 0, 30, 30, 0, 512)
    assert A.undercroft(A.Frame(tall), random.Random(1)) is None


def test_weights_steer_the_palette():
    cfg = compile_recipe(PRESETS["fortress"], 2)
    cfg.arch_weights = {"vault": 0.0, "single-aisle": 0.0, "undercroft": 0.0}
    lv = generate(cfg)
    mega = next(r for r in lv.rooms if r.kind == "megaroom")
    assert mega.meta["arch"]["profile"] == "basilica"
    assert not any(r.meta["arch"]["profile"] == "vault" for r in shaped(lv))


# ----------------------------------------------------------------------
# smooth vaults and roofs
# ----------------------------------------------------------------------
def test_smooth_vaults_are_planar_and_conservative(styled):
    """Sloped ceilings: four corners in one plane, never below the span gameplay sees,
    never above the room."""
    n = 0
    for levels in styled.values():
        for lv in levels:
            for c, zs in lv.ceil_slope.items():
                f, top = lv.spans[c][-1]
                assert abs((zs[0] + zs[2]) - (zs[1] + zs[3])) <= 2, (c, zs)
                assert min(zs) >= top and top - f >= A.HEAD
                n += 1
    assert n > 1000          # fortress and compound vault and roof their big halls


def test_stepped_option():
    cfg = compile_recipe(PRESETS["fortress"], 1)
    cfg.arch_smooth = False
    lv = generate(cfg)
    assert not lv.ceil_slope and shaped(lv) and validate(lv)["ok"]


def test_sloped_brushes_in_the_map(styled):
    from badlevel.brushes import build_brushes
    lv = styled["fortress"][0]
    bs = [b for b in build_brushes(lv) if b.slope]
    assert bs
    for b in bs:   # convex: the sloped bottom stays below the flat top everywhere
        assert max(b.slope) < b.maxs[2] and b.mins[2] == min(b.slope)
    text = to_map(lv)
    # a sloped plane has three points at different heights on its face line
    assert any(len({int(p.split()[2]) for p in line.split(") (")}) == 3
               for line in text.splitlines() if line.startswith("(") and "ceiling" in line)


def test_sloped_side_faces_close_the_gap():
    """A sloped cell beside a lower flat one: the side faces exactly fill the gap between them."""
    from badlevel.export.mesh import build_mesh
    from badlevel.level import Level
    lv = Level(seed=0, cell=32, width=8, height=8, config=GenConfig())
    lv.add_span((3, 3), (0, 200))
    lv.add_span((4, 3), (0, 160))
    lv.ceil_slope[(3, 3)] = (200, 240, 240, 200)     # rises towards +x, where the lower cell is
    mb = build_mesh(lv)

    def area(mat, x_plane):
        g = mb.groups.get(mat, {"pos": [], "idx": []})
        tot = 0.0
        for i in range(0, len(g["idx"]), 3):
            a, b, c = (g["pos"][g["idx"][i + k]] for k in range(3))
            if all(abs(p[0] - x_plane) < 1e-6 for p in (a, b, c)):
                u = (b[1] - a[1], b[2] - a[2])
                v = (c[1] - a[1], c[2] - a[2])
                tot += abs(u[0] * v[1] - u[1] * v[0]) / 2
        return tot
    # on the shared face x = 128: from the low cell's ceiling (160) up to the slope's edge (240), 32 wide
    assert abs(area("ceiling", 128) + area("wall", 128) - 32 * (240 - 160)) < 1e-6


# ----------------------------------------------------------------------
# walls: members and recesses (step 2)
# ----------------------------------------------------------------------
def test_walls_break_up_long_flat_walls(styled):
    """With wall structure, the longest flat wall per room drops clearly (same layouts)."""
    from badlevel.walls import flat_wall_runs
    for name in PRESET_STYLE:
        before, after = [], []
        for seed in (1, 2, 3):
            cfg = compile_recipe(PRESETS[name], seed)
            cfg.arch_walls = False
            plain = generate(cfg)
            lv = next(lv for lv in styled[name] if lv.seed == seed)
            rooms = lambda l: [r for r in l.rooms if not r.is_connector and r.kind != "courtyard"]  # noqa: E731
            before += [flat_wall_runs(plain, r) for r in rooms(plain)]
            after += [flat_wall_runs(lv, r) for r in rooms(lv)]
        assert sum(after) / len(after) < 0.8 * sum(before) / len(before), name


def test_recesses_keep_a_solid_wall(styled):
    """A recess cell belongs to its room and never touches another space directly."""
    for levels in styled.values():
        for lv in levels:
            n = 0
            for r in lv.rooms:
                for c in lv.spans:
                    if r.contains(*c) or not any(o[2] == f"room:{r.id}" for o in lv.owners.get(c, [])):
                        continue
                    if not (r.x - 2 <= c[0] < r.x + r.w + 2 and r.y - 2 <= c[1] < r.y + r.h + 2):
                        continue
                    n += 1
                    for dx in (-1, 0, 1):
                        for dy in (-1, 0, 1):
                            nb = (c[0] + dx, c[1] + dy)
                            if nb in lv.spans and not r.contains(*nb):
                                owners = {o[2] for o in lv.owners.get(nb, [])}
                                assert owners <= {f"room:{r.id}"} or any(o.startswith("window:") for o in owners), \
                                    (lv.seed, r.id, c, nb, owners)
            assert validate(lv)["ok"]


def test_walls_off_switch():
    cfg = compile_recipe(PRESETS["industrial"], 2)
    cfg.arch_walls = False
    lv = generate(cfg)
    assert not any("walls" in r.meta for r in lv.rooms)
