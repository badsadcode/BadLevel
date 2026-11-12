# BadLevel

Procedural **Quake-style FPS level blockouts**: real height, stairs, balconies,
locked doors, loops and secrets. Not just Wolfenstein mazes. Engine agnostic:
one generator, many exports. Artists get a clean, grid-snapped greybox to build on.

```
seed + config
   │
   ▼
1. mission graph     start → fights → arena → exit, key branches, locked doors,
                     loops (only inside the same lock zone), secret room
2. spatial layout    rooms on several floor levels, shaped footprints (rect,
                     chamfer, octagon, L, T, U, plus, ring) with real 45° walls,
                     A* corridors 3/5/7 cells wide, some running at 45°,
                     with real stairs (16-unit steps on straight runs)
3. room features     pillars · raised platforms · pits · balconies with space
                     underneath (room-over-room) · key pedestals
4. gameplay markup   player start, enemies (difficulty ramps with depth),
                     pickups, high-ground rewards, lights, doors, exit trigger
5. validation        sealed? every room walkable? does the key really gate
                     the exit? failed attempts are re-rolled automatically
6. analysis          3D line of sight, exposure, cover, firing lanes, high
   (optional)        ground, chokepoints, pacing -> flags + candidate gate
   │
   ▼
 Level IR (JSON) ──► .map (Quake/TrenchBroom) · .glb · .html preview · .analysis.json
```

## Quick start

The generator has no dependencies, just Python 3.9+. Combat analysis needs `numpy`.

```bash
python3 -m badlevel generate --seed 42 --out out
```

```bash
python3 -m badlevel gallery --seeds 1-12 --out out/gallery
```

Open `out/gallery/index.html` and click a map for the 3D preview
(orbit view, or **Fly** mode with WASD from the player start).

Options: `--main-rooms`, `--side-rooms`, `--loops`, `--keys 0..2`, `--no-secret`,
`--formats map,glb,json,html`, `--config my.json` (any `GenConfig` field).

From Python:

```python
from badlevel import GenConfig, generate
from badlevel.export import to_map, to_glb

level = generate(GenConfig(seed=7, main_rooms=8, keys=2))
open("e1m1.map", "w").write(to_map(level))
```

## Outputs

| File | Use it for |
|------|------------|
| `.map` | Quake 1 Standard format. Opens in **TrenchBroom** (Quake config), compiles with ericw-tools `qbsp`/`light`/`vis`. Importers: **Godot** (func_godot / Qodot), **Unity** and **Unreal** (.map importers). Textures are named `bl_floor`, `bl_wall`, `bl_ceiling`, `bl_door` so you can bulk-replace them. |
| `.glb` | Any engine or DCC (Blender, Godot, Unity, Unreal). Y-up, 1 cell = 32 units = 1 m. Separate materials for floor/wall/ceiling, world-aligned UVs. Every entity is an empty node `kind_N` with `extras` (key colour, enemy tier...), ready to swap for prefabs. |
| `.json` | Engine-neutral IR: graph, rooms, corridors, entities, merged brushes, raw column spans. For writing your own engine plugin. |
| `.html` | Self-contained preview with top-down map and mission graph overlay. |

Entity kinds → Quake classnames: `player_start` → `info_player_start`,
`enemy` tier 1/2/3 → `monster_army`/`monster_knight`/`monster_ogre`,
`key` red/blue → `item_key2`/`item_key1`, `door` → `func_door` (key-locked),
`secret_door` → `func_door_secret`, `exit` → `trigger_changelevel`, plus items and lights.

## Combat analysis

`badlevel.analysis` (needs `numpy`; the generator does not) looks at a level the
way a player would. It samples standing positions on all reachable floors,
including balconies and the floor under them, and casts ~150-200k line-of-sight
rays through the real 3D occupancy grid. Walls, pillars, crates, balcony decks,
ledges and exact 45° walls all block. It takes about 0.5 s per level.

| Metric | Meaning |
|---|---|
| **exposure** | m² of floor from which an enemy's eye (floor+46) sees your upper body (floor+48). Green < 80, yellow < 200, red ≥ 200 |
| **crouch cover** | share of those viewers that lose sight of you when you crouch (floor+22), so low walls and crates count |
| **overlook** | m² of floor you can see |
| **high ground** | mean height above the positions you see |
| **firing lanes** | long mutual sightlines (≥ 32 m), deduplicated |
| **per room** | mutual visibility, exposure mix, cover spots, height levels, entrances, flank pairs, loop membership, high-ground perch |
| **level** | exposure mix, loops, mandatory chokepoints (bridges in the room graph), pacing along the critical path |

**Flags, not a fun score.** `diagnostics.py` turns metrics into `info` / `warn` /
`problem` flags: single-approach arenas, rooms with no reason to move, overexposed
fights, dominant perches, sniper lanes, mandatory chokes, flat pacing, flat levels.
Thresholds are in `RULES`.

**Candidate gate (variety-safe).** `--candidates N` generates up to N variants of a
seed and keeps the *first* one without `problem` flags. It never optimizes toward
target numbers, so open/closed rhythm and deliberate vulnerability survive. Use
`generate_selected(cfg, candidates, gate=...)` for custom gates.

The preview draws the analysis but does not compute it: heatmap modes (exposure,
crouch cover, overlook, high ground, longest sightline), firing lanes, chokepoints,
a pacing chart, clickable flags and rooms, and click-to-inspect on any floor tile.

```bash
python3 -m badlevel generate --seed 7 --candidates 3 --formats html,map,analysis
```

## Room shapes

Rooms are not just boxes. Each one gets a footprint picked by its size and role
(arenas like octagons, rings and crosses; secret rooms stay simple):

| shape | what it is |
|-------|------------|
| `rect` / `chamfer` | box, optionally with 45° cut corners |
| `octagon` | all four corners cut at 45° |
| `L`, `T`, `U`, `plus` | notched footprints, more corners for cover and flanking |
| `ring` | courtyard around a solid central block with 45° corners (circle-strafe heaven) |

45° walls are *half cells*: the cell is split corner to corner into a solid and
an open triangle. In the `.map` they become **triangular prism brushes** (still
convex, so qbsp is happy), in the `.glb` real diagonal faces, in the JSON
`"diagonal": [[x, y, corner], ...]`.

## Corridors

Each corridor gets its own width and may run diagonally:

- **Width:** 3, 5 or 7 cells (96/160/224 units), with taller ceilings for wider
  halls (128/160/192). Approaches to arenas and hubs lean wide, side branches
  narrow, secret passages always 3. Width is clamped to fit the smaller room.
- **Diagonals:** the router moves in 8 directions (no corner cutting, never
  diagonal next to a doorway, so entrances stay straight). Diagonal stretches are a
  band of full cells edged with the same 45° half cells as shaped rooms, sized to
  match the straight width. Inner corners of bends get chamfered too.
- If a wide or diagonal route doesn't fit, the corridor falls back to a plain
  3-wide one instead of failing the level.

## How the geometry works

The world is a grid of 32-unit columns. Each column stores the **empty spans**
`(floor, ceiling)` in it; everything else is solid. Two spans in one column =
room-over-room (e.g. walking under a balcony). Brushes are the solid complement
of those spans, greedily merged into big boxes, so the output is **sealed by
construction** (no qbsp leaks). The tests check this exactly.

## Tests

```bash
pip install pytest
```

```bash
python3 -m pytest -q
```

Covers: 25 seeds valid and walkable, determinism, sealed brushes, convex
outward-facing `.map` planes incl. 45° prisms (qbsp convention), valid `.glb`
buffers, key gating, CLI, and line of sight on hand-built geometry (walls,
crates vs crouching, balcony decks, 45° half cells), analysis consistency and the
candidate gate.

**Not yet verified:** opening a `.map` in TrenchBroom and compiling it with
ericw-tools. Export working is not the same as import working.

## Roadmap ideas

- Verify in TrenchBroom + compile with ericw-tools (qbsp/vis/light)
- Windows, open room connections, merged halls, interlocking structures
- TrenchBroom groups per room/corridor in the `.map`
- Engine plugins: Godot `@tool` importer for the JSON, Unity editor window, Unreal Python/Editor Utility
- Hand-made room templates (Noor's!) mixed with generated rooms
- Corridors passing over/under each other, bridges, lifts
- Straight diagonal stair risers (currently stepped across diagonal stretches)
- Themes and difficulty curves, combat-space metrics (sightlines, cover density)
- Valve 220 `.map` format and a generated dev-texture WAD
