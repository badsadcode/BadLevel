"""Room programs: what a room is *for* (facility levels, the Half-Life / Abiotic Factor tradition).

A campaign level's rooms have gameplay roles (start, fight, key, arena...). A facility's rooms
also have a job: an office, a lab, a server room, a warehouse, a reactor hall. The job decides
what fills the room, as blockout furniture an artist can find by name and swap for real props
(``room:5/desks``, ``room:9/server racks``):

* **Sectors.** Each lock zone of the mission is a sector of the complex (admin, research,
  storage, maintenance, reactor): the keycard doors are the checkpoints between them. The first
  zone is the admin sector (the way in), the last the reactor when there are three or more.
* **Programs.** Each room gets a program from its sector and size: the start is the lobby,
  the key room a security office, the exit the transit station, the sector's biggest room its
  hall (atrium, test chamber, warehouse, boiler hall, reactor: one each), the rest the sector's
  everyday rooms that fit their size.
* **Furniture.** Rows and grids of blocks, never closer than 2 cells (one player box) apart, with
  a 2-cell walkway round the walls and breaks every few cells so every aisle has a way across;
  doorways keep their clear zone. Low pieces (desks, benches, tables: 32-48 units) are cover
  you can see over; tall ones (racks, shelving, tanks, the reactor core) are solid to the ceiling.

``cfg.programs`` turns it on (the facility presets); off, nothing changes.
"""
from __future__ import annotations

import random
from typing import List

from .level import Level, Room

SECTORS = ("admin", "research", "storage", "maintenance", "reactor")
SECTOR_PROGRAMS = {          # everyday rooms (weighted by repetition), the big hall
    "admin": (["office", "office", "conference", "cafeteria"], "atrium"),
    "research": (["lab", "lab", "server", "office"], "test_chamber"),
    "storage": (["storage", "storage", "loading", "office"], "warehouse"),
    "maintenance": (["pump", "workshop", "server"], "boiler"),
    "reactor": (["control", "pump", "lab"], "reactor"),
}
MIN_SIDE = {"conference": 9, "cafeteria": 10, "storage": 9, "loading": 10, "server": 8, "lab": 8,
            "workshop": 8, "pump": 9, "control": 8, "office": 7}
ROOM_KINDS = ("start", "combat", "arena", "hub", "supply", "key", "secret", "exit")

# dungeons (cfg.program_set = "dungeon"): the keep above, the prison, the catacombs, the deep
DUNGEON_PROGRAMS = {
    "keep": (["guardroom", "armory", "library", "storeroom"], "great hall"),
    "prison": (["cells", "cells", "guardroom", "storeroom"], "pit hall"),
    "catacombs": (["crypt", "crypt", "shrine", "storeroom"], "catacomb hall"),
    "deep": (["shrine", "crypt", "cells"], "throne room"),
}
# the wizard's tower (cfg.program_set = "wizard"): the apprentices below, the study, the sanctum
WIZARD_PROGRAMS = {
    "apprentice": (["library", "storeroom", "alchemy lab", "dormitory"], "lecture hall"),
    "study": (["library", "alchemy lab", "scriptorium", "study"], "great library"),
    "sanctum": (["summoning chamber", "study", "alchemy lab"], "summoning hall"),
}
WIZARD_FIXED = {"start": "entrance hall", "exit": "observatory", "key": "reliquary", "secret": "hidden study"}
DUNGEON_FIXED = {"start": "gatehouse", "exit": "stair down", "key": "vault", "secret": "hidden cache"}
FIXED = {"start": "lobby", "exit": "transit", "key": "security", "secret": "closet"}
MIN_SIDE.update({"scriptorium": 8, "alchemy lab": 8, "summoning chamber": 9, "dormitory": 8, "study": 6,
                 "library": 8, "armory": 8, "cells": 9, "crypt": 8, "storeroom": 7, "guardroom": 7, "shrine": 8})


def sectors_for(zones: int, seed) -> List[str]:
    rng = random.Random(f"{seed}/sectors")
    middle = ["research", "storage", "maintenance"]
    rng.shuffle(middle)
    if zones <= 1:
        return ["research"]
    if zones == 2:
        return ["admin", rng.choice(["research", "reactor"])]
    return (["admin"] + middle)[:zones - 1] + ["reactor"]


def dungeon_sectors(zones: int, seed) -> List[str]:
    if zones <= 1:
        return ["catacombs"]
    if zones == 2:
        return ["keep", random.Random(f"{seed}/sectors").choice(["prison", "catacombs"])]
    return (["keep", "prison", "catacombs"])[:zones - 1] + ["deep"]


def wizard_sectors(zones: int, seed) -> List[str]:
    return ["apprentice", "study", "sanctum"][:max(1, zones)] if zones <= 3 else \
        ["apprentice"] + ["study"] * (zones - 2) + ["sanctum"]


def assign(level: Level):
    """Every ordinary room gets a sector (from its lock zone) and a program."""
    cfg = level.config
    g = level.graph
    zones = max(n.zone for n in g.nodes) + 1
    pset = getattr(cfg, "program_set", "facility")
    dungeon = pset in ("dungeon", "wizard")                 # (their own fallbacks, not the facility's)
    table, fixed = {"dungeon": (DUNGEON_PROGRAMS, DUNGEON_FIXED), "wizard": (WIZARD_PROGRAMS, WIZARD_FIXED)}.get(
        pset, (SECTOR_PROGRAMS, FIXED))
    names = {"dungeon": dungeon_sectors, "wizard": wizard_sectors}.get(pset, sectors_for)(zones, cfg.seed)
    rng = random.Random(f"{cfg.seed}/{cfg.variant}/programs")
    halls = set()                                    # one big hall per sector, the biggest room first
    for room in sorted(level.rooms, key=lambda r: (r.kind != "arena", -r.w * r.h)):
        if room.is_connector or room.kind not in ("arena", "hub", "combat", "supply"):
            continue
        sector = names[min(g.nodes[room.node].zone, len(names) - 1)]
        if sector not in halls and (room.kind == "arena" or room.w * room.h >= 196):
            halls.add(sector)
            room.meta["sector_hall"] = True
    for room in level.rooms:
        node = g.nodes[room.node]
        sector = names[min(node.zone, len(names) - 1)]
        room.meta["sector"] = sector
        if room.is_connector or room.kind not in ROOM_KINDS:
            continue
        side = min(room.w, room.h)
        if room.kind in fixed:
            prog = fixed[room.kind]
            room.meta.pop("sector_hall", None)
        elif room.meta.pop("sector_hall", False):
            prog = table[sector][1]
        else:
            opts = [p for p in table[sector][0] if side >= MIN_SIDE.get(p, 6)] or [table[sector][0][-1] if dungeon else "office"]
            prog = rng.choice(opts)
        room.meta["program"] = prog


# ----------------------------------------------------------------------------------------------
# furniture
# ----------------------------------------------------------------------------------------------
def _place(ctx, cells, name: str, height=None) -> int:
    """Raise cells by ``height`` (a block you can see over) or make them solid (height None)."""
    level, r = ctx.level, ctx.room
    n = 0
    for c in cells:
        if height is None:
            ctx.make_solid(c)
            level.part_solid[c] = f"room:{r.id}/{name}"
        else:
            ctx.set_floor(c, r.floor + height)
            level.part_cells[c] = f"room:{r.id}/{name}"
        n += 1
    return n


def _ok(ctx, c) -> bool:
    """A cell furniture may take: in the room, off the doorway zones, nothing there yet."""
    return ctx.room.contains(*c) and c not in _clear(ctx) and c not in ctx.used and c not in ctx.level.diag


def _clear(ctx):
    """Doorway zones for furniture: 2 cells round each door cell (a player box's width in front
    of every way in), smaller than the features' 3, so a small office still gets its desks."""
    if not hasattr(ctx, "prog_clear"):
        ctx.prog_clear = {(x + dx, y + dy) for (x, y) in ctx.doors for dx in range(-2, 3) for dy in range(-2, 3)}
    return ctx.prog_clear


def _ring_ok(ctx, cells) -> bool:
    """Nothing built hard against the piece (a pier, a wall member, other furniture): no slits.
    A doorway's clear zone may touch it: that zone stays open anyway."""
    cs = set(cells)
    for (x, y) in cs:
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                n = (x + dx, y + dy)
                if n in cs:
                    continue
                if not ctx.room.contains(*n) or n in ctx.used or n in ctx.level.diag:
                    return False
    return True


def _put(ctx, cells, name, height) -> int:
    if not cells or not _ring_ok(ctx, cells):
        return 0
    n = _place(ctx, cells, name, height)
    for (x, y) in cells:
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                ctx.used.add((x + dx, y + dy))
    return n


def rows(ctx, rng: random.Random, name: str, thick: int, gap: int, seg: int, brk: int = 2,
         height=None, margin: int = 2) -> int:
    """Rows along the room's long axis: ``thick`` cells deep, ``gap`` between rows, cut into
    pieces ``seg`` long with ``brk`` cells between them. Where a piece meets a doorway zone or
    other structure it is cut back to its longest free run (at least 2 cells, or 1 if thick)."""
    r = ctx.room
    along_x = r.w >= r.h
    L, D = (r.w, r.h) if along_x else (r.h, r.w)
    to_w = (lambda a, b: (r.x + a, r.y + b)) if along_x else (lambda a, b: (r.x + b, r.y + a))
    placed = 0
    v = margin
    while v + thick <= D - margin:
        u = margin + rng.randint(0, 1)
        while u + 1 <= L - margin:
            span = range(u, min(u + seg, L - margin))
            # the longest run of columns whose whole depth is free
            best, run = [], []
            for a in span:
                if all(_ok(ctx, to_w(a, v + j)) for j in range(thick)):
                    run.append(a)
                    if len(run) > len(best):
                        best = list(run)
                else:
                    run = []
            if len(best) >= (1 if thick > 1 else 2):
                placed += _put(ctx, [to_w(a, v + j) for a in best for j in range(thick)], name, height)
            u += seg + brk
        v += thick + gap
    return placed


def block(ctx, name: str, w: int, h: int, height=None) -> int:
    """One piece near the middle of the room (a reception desk, a conference table, a core):
    the free spot nearest the centre."""
    r = ctx.room
    cx, cy = r.x + (r.w - w) / 2.0, r.y + (r.h - h) / 2.0
    spots = sorted(((x, y) for x in range(r.x + 2, r.x + r.w - 1 - w) for y in range(r.y + 2, r.y + r.h - 1 - h)),
                   key=lambda p: (p[0] - cx) ** 2 + (p[1] - cy) ** 2)
    for x0, y0 in spots[:40]:
        cells = [(x, y) for x in range(x0, x0 + w) for y in range(y0, y0 + h)]
        if all(_ok(ctx, c) for c in cells):
            n = _put(ctx, cells, name, height)
            if n:
                return n
    return 0


FALLBACK = {"office": ("desks", 32), "lab": ("benches", 40), "control": ("consoles", 40), "workshop": ("workbenches", 40),
            "server": ("server racks", None), "storage": ("shelving", None), "pump": ("tanks", None),
            "security": ("counter", 48), "cafeteria": ("tables", 32),
            "crypt": ("sarcophagi", 40), "library": ("bookshelves", None), "armory": ("weapon racks", 64),
            "storeroom": ("barrels", 48), "cells": ("cell walls", None),
            "alchemy lab": ("workbenches", 40), "scriptorium": ("writing desks", 32), "study": ("bookshelves", None)}


def _chasm(ctx, rng) -> bool:
    from .decorate import f_chasm
    return f_chasm(ctx, rng)


def furnish(ctx, prog: str, rng: random.Random) -> None:
    from .decorate import f_balcony, f_crates, f_pedestal, f_pillars, f_pit, f_platform
    r = ctx.room
    long_ = max(r.w, r.h)
    if r.kind == "key":
        f_pedestal(ctx, rng)                       # the keycard on its stand
    if prog == "lobby":
        f_balcony(ctx, rng) or f_pillars(ctx, rng)
        block(ctx, "reception desk", 4 if r.w >= r.h else 1, 1 if r.w >= r.h else 4, 40)
    elif prog == "atrium":
        f_balcony(ctx, rng)
        f_pillars(ctx, rng)
    elif prog == "office":
        rows(ctx, rng, "desks", 1, 2, 2, 2, height=32)
    elif prog == "conference":
        block(ctx, "conference table", max(2, r.w - 6) if r.w >= r.h else 2, 2 if r.w >= r.h else max(2, r.h - 6), 32)
    elif prog == "cafeteria":
        rows(ctx, rng, "tables", 2, 2, 2, 2, height=32)
    elif prog in ("lab", "control"):
        rows(ctx, rng, "benches" if prog == "lab" else "consoles", 1, 3, min(5, long_ - 6), 2, height=40)
    elif prog == "workshop":
        rows(ctx, rng, "workbenches", 1, 3, 3, 2, height=40)
        f_crates(ctx, rng)
    elif prog == "server":
        rows(ctx, rng, "server racks", 1, 2, 5, 2)
    elif prog == "storage":
        rows(ctx, rng, "shelving", 2, 3, 6, 2)
    elif prog == "warehouse":
        rows(ctx, rng, "shelving", 2, 4, 8, 3)
        f_crates(ctx, rng)
    elif prog == "loading":
        f_platform(ctx, rng)                       # the dock
        f_crates(ctx, rng)
    elif prog == "pump":
        rows(ctx, rng, "tanks", 2, 3, 2, 3)
    elif prog == "boiler":
        f_platform(ctx, rng)
        rows(ctx, rng, "boilers", 3, 4, 3, 4)
    elif prog == "reactor":
        core = 6 if min(r.w, r.h) >= 18 else 4
        block(ctx, "reactor core", core, core)
        f_balcony(ctx, rng)
    elif prog == "test_chamber":
        f_pit(ctx, rng)
        f_platform(ctx, rng)
    elif prog == "security":
        rows(ctx, rng, "counter", 1, 4, 3, 2, height=48)
    elif prog == "transit":
        f_pillars(ctx, rng)
    # --- dungeons ---------------------------------------------------------------------------
    elif prog in ("catacomb hall", "throne room", "crypt", "guardroom") and min(r.w, r.h) >= 12 and \
            rng.random() < getattr(ctx.level.config, "chasms", 0.0) * 0.25 and _chasm(ctx, rng):
        f_pillars(ctx, rng)                            # a chasm through it, and a bridge
    elif prog == "gatehouse":
        f_pillars(ctx, rng)
    elif prog == "guardroom":
        block(ctx, "table", 2, 2, 32)
        rows(ctx, rng, "weapon racks", 1, 4, 3, 3, height=64)
    elif prog == "armory":
        rows(ctx, rng, "weapon racks", 1, 3, 4, 2, height=64)
    elif prog == "library":
        rows(ctx, rng, "bookshelves", 1, 2, 5, 2)
    elif prog == "storeroom":
        rows(ctx, rng, "barrels", 2, 2, 2, 2, height=48)
        f_crates(ctx, rng)
    elif prog == "cells":
        rows(ctx, rng, "cell walls", 1, 3, 3, 2)          # stalls between solid walls: the cells
    elif prog == "crypt":
        rows(ctx, rng, "sarcophagi", 1, 2, 2, 2, height=40)
    elif prog == "shrine":
        block(ctx, "altar", 3 if r.w >= r.h else 2, 2 if r.w >= r.h else 3, 48)
        f_pillars(ctx, rng)
    elif prog == "vault":
        rows(ctx, rng, "chests", 1, 3, 2, 3, height=32)
    elif prog == "great hall":
        f_balcony(ctx, rng)                            # goblins on the balcony
        f_pillars(ctx, rng)
        block(ctx, "long table", max(2, r.w - 8) if r.w >= r.h else 2, 2 if r.w >= r.h else max(2, r.h - 8), 32)
    elif prog == "pit hall":
        from .decorate import f_chasm
        f_chasm(ctx, rng) or f_pit(ctx, rng)           # the bridge over the spikes
        f_balcony(ctx, rng)
    elif prog == "catacomb hall":
        f_pillars(ctx, rng)
        rows(ctx, rng, "sarcophagi", 1, 3, 2, 3, height=40)
    elif prog == "throne room":
        f_platform(ctx, rng)                           # the dais
        f_pillars(ctx, rng)
    # --- the wizard's tower ----------------------------------------------------------------
    elif prog == "entrance hall":
        f_pillars(ctx, rng)
    elif prog == "alchemy lab":
        rows(ctx, rng, "workbenches", 1, 3, 4, 2, height=40)
        block(ctx, "cauldron", 2, 2, 32)
    elif prog == "scriptorium":
        rows(ctx, rng, "writing desks", 1, 2, 2, 2, height=32)
    elif prog == "study":
        block(ctx, "desk", 3 if r.w >= r.h else 2, 2 if r.w >= r.h else 3, 32)
        rows(ctx, rng, "bookshelves", 1, 4, 4, 2, margin=2)
    elif prog == "dormitory":
        rows(ctx, rng, "beds", 2, 2, 1, 2, height=24)
    elif prog in ("lecture hall", "great library"):
        f_balcony(ctx, rng)
        rows(ctx, rng, "bookshelves" if prog == "great library" else "benches", 1, 3, 5, 2,
             height=None if prog == "great library" else 32)
    elif prog in ("summoning chamber", "summoning hall"):
        block(ctx, "summoning circle", 4, 4, 8)          # a raised ring to stand on (8: a step)
        f_pillars(ctx, rng)
    elif prog == "reliquary":
        rows(ctx, rng, "reliquaries", 1, 3, 2, 3, height=48)
    elif prog == "observatory":
        block(ctx, "telescope", 2, 2, None)
    # closet, hidden cache, stair down, hidden study: empty
    small = FALLBACK.get(prog)
    if small and not any(v.startswith(f"room:{r.id}/{small[0]}") for v in
                         list(ctx.level.part_cells.values()) + list(ctx.level.part_solid.values())):
        name, height = small                       # nothing fitted: smaller pieces, closer together
        rows(ctx, rng, name, 1, 2, 2, 2, height=height)
    r.features.append(prog)
