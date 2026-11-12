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
                     A* corridors with real stairs (16-unit steps on straight runs)
3. room features     pillars · raised platforms · pits · balconies with space
                     underneath (room-over-room) · key pedestals
4. gameplay markup   player start, enemies (difficulty ramps with depth),
                     pickups, high-ground rewards, lights, doors, exit trigger
5. validation        sealed? every room walkable? does the key really gate
                     the exit? failed attempts are re-rolled automatically
   │
   ▼
 Level IR (JSON) ──► .map (Quake/TrenchBroom) · .glb · .html preview
```

## Quick start

No dependencies, just Python 3.9+.

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

Covers: 25 seeds valid and 100% reachable, determinism, sealed brushes,
outward-facing `.map` planes (qbsp convention), valid `.glb` buffers,
key gating, CLI.

## Roadmap ideas

- Engine plugins: Godot `@tool` importer for the JSON, Unity editor window, Unreal Python/Editor Utility
- Hand-made room templates (Noor's!) mixed with generated rooms
- Corridors passing over/under each other, bridges, windows between rooms, lifts
- Themes and difficulty curves, combat-space metrics (sightlines, cover density)
- Valve 220 `.map` format and a generated dev-texture WAD
