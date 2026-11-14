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
                    if spans and spans[-1][1] < r.ceil and c not in lv.sky \
                            and not (lv.owner_at(c, spans[-1][0]) or "").startswith("window:"):
                        f, top = spans[-1]                 # (a slit cut through a jamb is a window)
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
                            if nb != c and nb in lv.spans and not r.contains(*nb):
                                # spaces beside it at the same height (a roofscape 64+ above is not)
                                mine = [sp for sp in lv.spans[c] if r.slot[0] <= sp[0] and sp[1] <= r.ceil] or lv.spans[c]
                                lo = min(sp[0] for sp in mine) - 16       # (the recess itself, not the
                                hi = max(sp[1] for sp in mine) + 16       # roofscape above it)
                                owners = {o[2] for o in lv.owners.get(nb, []) if o[0] < hi and lo < o[1]}
                                assert owners <= {f"room:{r.id}"} or any(o.startswith("window:") for o in owners), \
                                    (lv.seed, r.id, c, nb, owners)
            assert validate(lv)["ok"]


def test_walls_off_switch():
    cfg = compile_recipe(PRESETS["industrial"], 2)
    cfg.arch_walls = False
    lv = generate(cfg)
    assert not any("walls" in r.meta for r in lv.rooms)


# ----------------------------------------------------------------------
# portals and framed reveals (step 3)
# ----------------------------------------------------------------------
def test_portals_frame_openings(styled):
    from badlevel.architecture import rank_tiers
    for name, levels in styled.items():
        heads = jambs = 0
        for lv in levels:
            tiers = rank_tiers(lv)
            for r in lv.rooms:
                for p in r.meta.get("portals", []):
                    jambs += p["jambs"]
                    if p["head"]:
                        heads += 1
                        assert tiers[r.id] in ("grand", "major")
        assert jambs > 0, name
        if name != "bunker":
            assert heads > 0, name


def test_doors_still_seal_their_corridors(styled):
    """A raised mouth never holds a door: every door reaches the ceiling of the cells it closes."""
    for levels in styled.values():
        for lv in levels:
            s = lv.cell
            for e in lv.entities:
                if e.kind not in ("door", "secret_door") or not e.bounds:
                    continue
                (x0, y0, z0), (x1, y1, z1) = e.bounds
                for x in range(int(x0 // s), int((x1 - 1) // s) + 1):
                    for y in range(int(y0 // s), int((y1 - 1) // s) + 1):
                        for f, top in lv.spans_in((x, y), z0, z1):
                            assert top <= z1 and (x, y) not in lv.ceil_slope, (lv.seed, e.kind, (x, y), top, z1)


def test_framed_reveals_and_surprises():
    """Some big halls put their end feature on the axis of the way in, others stay oblique."""
    from badlevel.architecture import rank_tiers
    axial = oblique = 0
    for name in ("industrial", "compound"):
        for seed in range(1, 6):
            lv = generate(compile_recipe(PRESETS[name], seed))
            tiers = rank_tiers(lv)
            for r in lv.rooms:
                w = r.meta.get("walls", {})
                if tiers.get(r.id) in ("grand", "major") and w.get("focus"):
                    if w.get("axial"):
                        axial += 1
                    elif not w.get("through"):
                        oblique += 1
    assert axial >= 5 and oblique >= 3, (axial, oblique)


def test_portals_off_switch():
    cfg = compile_recipe(PRESETS["compound"], 1)
    cfg.arch_portals = False
    lv = generate(cfg)
    assert not any("portals" in r.meta for r in lv.rooms) and validate(lv)["ok"]


# ----------------------------------------------------------------------
# underground (the bunker preset)
# ----------------------------------------------------------------------
def test_underground_goes_down_from_the_surface(styled):
    for lv in styled["bunker"]:
        assert lv.config.underground and validate(lv)["ok"]
        top = max(r.storey for r in lv.rooms)
        start = next(r for r in lv.rooms if r.kind == "start")
        exit_ = next(r for r in lv.rooms if r.kind == "exit")
        assert start.storey == top and start.meta.get("surface")
        assert any(c in lv.sky for c in start.cells())
        assert exit_.storey == 0
        # the critical path never climbs back up
        main = sorted((n for n in lv.graph.nodes if getattr(n, "main", False)), key=lambda n: n.depth)
        storeys = [n.storey for n in main]
        assert all(b <= a for a, b in zip(storeys, storeys[1:])), storeys


def test_deep_chamber(styled):
    profiles = []
    for lv in styled["bunker"]:
        mega = [r for r in lv.rooms if r.kind == "megaroom"]
        assert mega and mega[0].storey == 0
        profiles.append(mega[0].meta["arch"]["profile"])
    assert profiles.count("cavern") >= 3, profiles


def test_underground_is_opt_in():
    assert not compile_recipe(PRESETS["fortress"], 1).underground
    lv = generate(seed=4, storeys=3)
    assert not any(r.meta.get("surface") for r in lv.rooms)


def test_deep_chamber_is_seen_from_above(styled):
    """Underground: an observation booth on the storey above looks into the deep chamber through
    glass. Visual only: the booth is a dead end, and the glass blocks movement."""
    seen = 0
    for lv in styled["bunker"]:
        booth = [r for r in lv.rooms if r.kind == "overlook"]
        if not booth:
            continue
        seen += 1
        b = booth[0]
        hall = lv.rooms[b.meta["overlooks"]]
        assert b.floor - hall.floor >= 160 and validate(lv)["ok"]
        win = [e for e in lv.entities if e.kind == "window" and e.props.get("overlook")]
        assert win and not win[0].props["glazed"]          # a bunker sees through slits, never glass
        s = lv.cell
        (x0, y0, _), (x1, y1, _) = win[0].bounds
        cells = [(x, y) for x in range(int(x0 // s), int((x1 - 1) // s) + 1)
                 for y in range(int(y0 // s), int((y1 - 1) // s) + 1)]
        # nobody fits through: glass, or an opening lower than a player
        # (a corridor may pass below the slit, through the same wall: check the window's own opening)
        def window_span(c):
            return next(sp for sp in lv.spans[c] if lv.owner_of(c, sp) == "window:overlook")
        assert cells and all(c in lv.glass or (lambda sp: sp[1] - sp[0] < lv.config.player_height)(window_span(c))
                             for c in cells)
        # nothing stands in front of the window
        assert all(lv.room_spans(tuple(c), hall) for c in hall.meta["overlook"])
    assert seen >= 4, seen


# ----------------------------------------------------------------------
# compounds: buildings in the yards, roofscape around them
# ----------------------------------------------------------------------
@pytest.fixture(scope="module")
def compounds():
    return [generate(compile_recipe(PRESETS["compound"], s)) for s in range(1, 7)]


def test_yards_have_enterable_buildings(compounds):
    from badlevel.validate import _node_at, flood
    yards = with_b = 0
    for lv in compounds:
        assert validate(lv)["ok"]
        start = _node_at(lv, next(e for e in lv.entities if e.kind == "player_start").pos)
        reach = flood(lv, start, set())
        reach_cells = {(x, y) for x, y, _ in reach}
        for y in (r for r in lv.rooms if r.kind == "courtyard"):
            yards += 1
            if y.meta.get("buildings"):
                with_b += 1
            for b in y.meta.get("buildings", []):
                x0, y0, bw, bh = b["rect"]
                inside = [(x, yy) for x in range(x0 + 1, x0 + bw - 1) for yy in range(y0 + 1, y0 + bh - 1)]
                assert any(c in reach_cells for c in inside), "a building you can walk into"
                roof_nodes = [(x, yy, len(lv.spans[(x, yy)]) - 1) for x in range(x0, x0 + bw)
                              for yy in range(y0, y0 + bh) if (x, yy) in lv.spans]
                if b["ladder"]:
                    assert any(n in reach for n in roof_nodes), "the ladder reaches the roof"
    assert with_b >= 0.6 * yards, (with_b, yards)


def test_roofscape_is_scenery(compounds):
    """The roofs around a yard open the view to the sky, but nobody can walk onto them."""
    from badlevel.validate import _node_at, flood
    total = 0
    for lv in compounds:
        start = _node_at(lv, next(e for e in lv.entities if e.kind == "player_start").pos)
        reach = flood(lv, start, set())
        for (x, y, i) in reach:
            owner = lv.owner_of((x, y), lv.spans[(x, y)][i]) or ""
            assert not owner.startswith("roof:"), (lv.seed, x, y)
        roofs = [c for c in lv.spans if any((lv.owner_of(c, sp) or "").startswith("roof:") for sp in lv.spans[c])]
        assert all(c in lv.sky for c in roofs)
        total += len(roofs)
    assert total > 1000


def test_roofscape_is_opt_in():
    assert not compile_recipe(PRESETS["fortress"], 1).open_roofscape


def test_buildings_have_a_little_life_inside(compounds):
    """Windows nobody fits through, a light, and a reward you can reach by going in."""
    from badlevel.validate import _node_at, flood
    wins = rewards = 0
    for lv in compounds:
        start = _node_at(lv, next(e for e in lv.entities if e.kind == "player_start").pos)
        reach = flood(lv, start, set())
        for c, owners in lv.owners.items():
            for lo, hi, name in owners:
                if name.startswith("window:building"):
                    assert hi - lo < lv.config.player_height
                    wins += 1
        for e in lv.entities:
            if e.props.get("building") and e.kind in ("health", "ammo"):
                n = _node_at(lv, (e.pos[0], e.pos[1], e.pos[2] + 1))
                assert n in reach, (lv.seed, e.kind, e.pos)
                rewards += 1
    assert wins > 20 and rewards > 10, (wins, rewards)


def test_compound_yards_are_low_with_room_around():
    cfg = compile_recipe(PRESETS["compound"], 2)
    assert cfg.yard_height == 320 and cfg.yard_setback == 4
    lv = generate(cfg)
    for y in (r for r in lv.rooms if r.kind == "courtyard"):
        assert y.ceil - y.floor == 320


# ----------------------------------------------------------------------
# named, replaceable parts (.glb objects and .map TrenchBroom groups)
# ----------------------------------------------------------------------
def _glb_json(lv):
    import json
    import struct
    from badlevel.export.gltf import to_glb
    b = to_glb(lv)
    n = struct.unpack("<I", b[12:16])[0]
    return json.loads(b[20:20 + n])


def test_glb_objects_per_room_and_element(styled):
    lv = styled["fortress"][2]
    j = _glb_json(lv)
    names = {n["name"] for n in j["nodes"]}
    geo = next(n for n in j["nodes"] if n["name"] == "level_geometry")
    areas = [j["nodes"][i] for i in geo["children"]]
    assert any(a["name"].startswith("room_") for a in areas) and any(a["name"].startswith("corridor_") for a in areas)
    # every architectural element is its own object, with what it is in its extras
    elements = {n["extras"]["element"] for n in j["nodes"] if "mesh" in n and "element" in n.get("extras", {})}
    assert {"structure", "members"} <= elements and elements & {"vault", "basilica", "gable", "undercroft"}
    mega = next(r for r in lv.rooms if r.kind == "megaroom")
    assert f"room_{mega.id}_megaroom.{mega.meta['arch']['profile']}" in names


def test_map_groups_per_element(styled):
    import re
    from badlevel.export import to_map
    lv = styled["compound"][0]
    m = to_map(lv)
    groups = re.findall(r'"_tb_name" "([^"]+)"', m)
    assert any(": building" in g for g in groups) and any(": roofscape" in g for g in groups)
    assert m.count('"classname" "func_group"') == len(groups)
    # a level without architecture has no groups: its .map is unchanged
    assert "func_group" not in to_map(generate(seed=2))


# ----------------------------------------------------------------------
# more vocabularies: egyptian, sci-fi, castle (any recipe can use any style)
# ----------------------------------------------------------------------
@pytest.mark.parametrize("style,grand", [("egyptian", {"hypostyle", "corbelled", "battered"}),
                                         ("scifi", {"hull", "chamfered", "lightwell"}),
                                         ("castle", {"trussed", "barrel", "undercroft"})])
def test_new_styles(style, grand):
    profiles = set()
    walls = recesses = 0
    for preset in ("fortress", "classic"):
        for seed in (1, 2):
            cfg = compile_recipe(PRESETS[preset], seed)
            cfg.arch_style = style
            lv = generate(cfg)
            assert validate(lv)["ok"]
            for r in shaped(lv):
                assert r.meta["arch"]["style"] == style
                profiles.add(r.meta["arch"]["profile"])
                if r.meta["arch"]["tier"] == "grand":
                    assert r.meta["arch"]["profile"] in grand
            for r in lv.rooms:
                w = r.meta.get("walls", {})
                walls += w.get("members", 0)
                recesses += w.get("recesses", 0)
    assert len(profiles) >= 3 and walls > 0 and recesses > 0, (profiles, walls, recesses)


def test_style_from_the_command_line(tmp_path):
    from badlevel.cli import main as cli
    assert cli(["generate", "--seed", "2", "--recipe", "classic", "--style", "castle", "--out", str(tmp_path),
                "--formats", "map", "--no-analysis"]) == 0
    assert any("timber" in p.read_text() or "func_group" in p.read_text() for p in tmp_path.glob("*.map"))


@pytest.mark.parametrize("preset", ["fortress", "classic", "compound"])
def test_architecture_never_changes_the_layout(preset):
    """Any style on the same recipe and seed gives the same layout: it never makes an attempt
    invalid (a window's owner entry has to come before a room's on a cell that became solid)."""
    from badlevel.architecture import STYLES
    for seed in (1, 2):
        base = compile_recipe(PRESETS[preset], seed)
        base.arch_style = ""
        ref = generate(base)
        for st in STYLES:
            cfg = compile_recipe(PRESETS[preset], seed)
            cfg.arch_style = st
            lv = generate(cfg)
            assert lv.notes[-1] == ref.notes[-1] and len(lv.rooms) == len(ref.rooms), (preset, seed, st)


# ----------------------------------------------------------------------
# landmarks
# ----------------------------------------------------------------------
def test_one_landmark_per_styled_level(styled):
    import numpy as np
    from badlevel.analysis.occupancy import Occupancy
    from badlevel.portals import openings
    found = seen = 0
    total = 0
    for levels in styled.values():
        for lv in levels:
            total += 1
            rooms = [r for r in lv.rooms if "landmark" in r.meta]
            assert len(rooms) <= 1
            if not rooms:
                continue
            found += 1
            r = rooms[0]
            lm = r.meta["landmark"]
            assert lm["top"] > r.floor and validate(lv)["ok"]
            assert any(p.startswith(f"room:{r.id}/landmark") for p in lv.part_solid.values())
            # it can be seen from a way into its room
            occ = Occupancy(lv, glass_opaque=False)
            s = lv.cell
            lx, ly = lm["centre"]
            z = (r.floor + lm["top"]) / 2
            eyes = [((c[0] + 0.5) * s, (c[1] + 0.5) * s, r.floor + 46) for op in openings(lv, r) for c in op["cells"]]

            def face(e):        # a point on the landmark's side facing the eye (its centre is solid)
                dx, dy = e[0] - lx * s, e[1] - ly * s
                n = max(1e-6, (dx * dx + dy * dy) ** 0.5)
                off = 2 * lm.get("scale", 1) + 1.2        # just outside its footprint
                return np.array([[lx * s + dx / n * off * s, ly * s + dy / n * off * s, z]])
            if eyes and any(occ.visible(np.array([e]), face(e))[0] for e in eyes):
                seen += 1
    assert found >= 0.8 * total, (found, total)
    assert seen >= 0.7 * found, (seen, found)


def test_landmark_switch():
    cfg = compile_recipe(PRESETS["fortress"], 1)
    cfg.arch_landmark = False
    assert not any("landmark" in r.meta for r in generate(cfg).rooms)


def test_members_get_bases_and_capitals(styled):
    """Pilasters get a plinth (one walkable step) and a capital (under the ceiling), one order per room."""
    from badlevel.walls import BASE_RISE, BLOCK_RISE
    seen = set()
    for levels in styled.values():
        for lv in levels:
            for r in lv.rooms:
                d = r.meta.get("walls", {}).get("dress")
                if not d:
                    continue
                seen.add((d["base"], d["capital"]))
                for c, p in lv.part_cells.items():
                    if p == f"room:{r.id}/bases":
                        rise = lv.room_spans(c, r)[0][0] - r.floor
                        assert rise == (BLOCK_RISE if d["base"] == "block" else BASE_RISE)
                for c, p in lv.part_ceiling.items():
                    if p == f"room:{r.id}/capitals":
                        f, ce = lv.room_spans(c, r)[-1]
                        assert ce - f >= A.HEAD
    assert len(seen) >= 4
    assert any(b for b, _ in seen) and any(cap for _, cap in seen)


def test_dress_switch():
    cfg = compile_recipe(PRESETS["fortress"], 2)
    cfg.arch_dress = False
    lv = generate(cfg)
    assert not any("dress" in r.meta.get("walls", {}) for r in lv.rooms)
    assert not any(p.endswith(("/bases", "/capitals")) for p in list(lv.part_cells.values()) + list(lv.part_ceiling.values()))


def test_sloped_capitals():
    """Splayed capitals and corbels (after Tom's Blender pilasters): the ceiling slopes from low at
    the wall side up to the room's ceiling; the span keeps the low point."""
    sloped = 0
    for st in ("gothic", "castle", "egyptian"):
        for s in (1, 2):
            cfg = compile_recipe(PRESETS["fortress"], s)
            cfg.arch_style = st
            lv = generate(cfg)
            assert validate(lv)["ok"]
            for c, p in lv.part_ceiling.items():
                if p.endswith("/capitals") and c in lv.ceil_slope:
                    zs = lv.ceil_slope[c]
                    assert min(zs) == lv.spans[c][-1][1] < max(zs)
                    sloped += 1
    assert sloped
