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
2. spatial layout    rooms on several floor levels or stacked storeys
                     (stairwells, lifts, ladders, one-way drops), shaped footprints (rect,
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

## Live preview

Every push runs the tests, generates fresh levels with the current code and publishes
them to GitHub Pages (`.github/workflows/preview.yml`, built by `scripts/build_site.py`):
all presets side by side, a single-storey and a multi-storey gallery, each level with
its 3D preview and `.map` / `.glb` downloads. Build the same site locally with
`python scripts/build_site.py site`.

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

## Recipes and presets

Say what kind of place you want in design terms; BadLevel compiles it into its
settings. A recipe is eight dials from 0 to 1 plus optional raw overrides:

| dial | turns up |
|---|---|
| `scale` | room count and room sizes |
| `openness` | merged halls, windows and glass, wide corridors |
| `verticality` | height variation, storeys, platforms, pits, balconies, tall rooms |
| `claustrophobia` | low ceilings, narrow corridors, few openings |
| `combat` | enemies and cover (0 = no combat at all) |
| `exploration` | side rooms, secrets, views ahead, pickups |
| `loopiness` | loops and alternative routes |
| `regularity` | rectangles and right angles (vs octagons, rings, 45° corridors) |

Presets: `compact`, `classic`, `bunker`, `fortress`, `industrial`, `compound`, `explore`.

```bash
python3 -m badlevel presets
```

```bash
python3 -m badlevel generate --seed 7 --recipe bunker
```

```bash
python3 -m badlevel compare --recipes all --seeds 1-3 --out out/compare
```

A recipe can also be a JSON file (`{"name", "dials": {...}, "overrides": {...}}`).
The recipe used is saved beside every export as `<name>.recipe.json`. Without
`--recipe`, BadLevel generates exactly what it did before recipes existed.

Presets are checked for **identity**: `fingerprint.py` describes a level as ~17
features, and a test requires each preset to be recognisable from its fingerprint
alone. Why things are built this way: [docs/DECISIONS.md](docs/DECISIONS.md).

## Evaluation

```bash
python3 -m badlevel evaluate presets --seeds 1-6 --out out/evaluation
```

Generates every preset (or every composition: `evaluate compositions`) for several
seeds and reports how recognisable each one is (confusion matrix), which ones drift
towards each other, how much variety each keeps inside itself versus the distance
between them, what makes each one itself (a feature profile), and space metrics
(biggest space, intimate vs expansive rooms, exploration, brush count). Published on
the live preview site as the Evaluation page.

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

## Storeys and vertical connections

`--storeys 1` (the default) is a classic single-storey level: one floor plan with
height variation. `--storeys 2..5` builds stacked floors. Storeys are a planning
grid (256 units apart), not hard limits: arenas and hubs may reach up into the
next storey, rooms on different storeys can sit directly above each other, and
corridors pass over and under rooms and other corridors.

The critical path changes storey as it goes. Each change uses one of these, with
its traversal semantics recorded in `level.links` and the IR:

| connector | how | traversal |
|---|---|---|
| **stair corridor** | a corridor with a long straight staircase | walk, two-way |
| **stairwell** | switchback tower; climbs of more than one flight pair stack cycles in the same footprint | walk, two-way |
| **lift** | shaft with a top landing over open space and a moving platform (`func_plat` in the `.map`) | ride, two-way, accessible |
| **ladder** | shaft with a ladder volume up to the landing (`func_ladder`: Quake 1 has no ladders, importers map it) | climb, two-way, not accessible |
| **drop** | a loop corridor that runs level, then falls down a 2-cell shaft | one-way |

Validation is **directed**: you can step up 18 units but drop up to 288, and lifts
and ladders are explicit links. Besides key order and exit gating, it runs a
**softlock check**: from every spot you can reach, the next key (and finally the
exit) must still be reachable. A connected room graph isn't enough.

In the preview, the **Cut** slider hides everything above a height so you can look
into lower storeys.

## Windows, galleries and visual connectivity

Seeing is not walking. BadLevel keeps the two apart:

- **Firing slits:** 48-unit openings cut through thin walls between two different
  areas. You can see and shoot through; a player (56 tall) never fits, so they are
  visual-only by construction. Crouch right behind the wall and you're hidden.
- **Glazed windows:** tall panes in `level.glass`. Walls for movement and gunfire,
  see-through for visual connectivity. In the `.map`: a `func_wall` with `alpha`
  (transparent in modern Quake engines).
- **Galleries (mezzanines):** tall rooms on multi-storey levels get a deck at the
  upper storey's height, reached by a corridor from an upper-storey room in the same
  lock zone. An overlook onto the hall below, and a one-way way down (the softlock
  check keeps it fair).

The analysis builds a **visibility graph** next to the walking graph: area pairs
that see each other although they are 3+ steps apart on foot (flagged when the
view crosses storeys or goes through glass), **landmarks** (rooms seen from many
places), and **previews**: how many rooms ahead each critical-path room can first be
seen. Levels with storeys but no cross-storey views get a `no_vertical_landmarks`
warning. In the preview: *Sight links* toggle and a Visual connectivity table.

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

## Composition and pacing

`composition` sets the level's macro structure, in both the mission graph and the
placement: `tree` (branching growth, the default), `hub` (a centre with spokes and
loops coming back to it), `central` (satellites in a ring around a big space) or
`linear` (a spine with side rooms as ribs). With `pacing_contrast`, the corridors into
big spaces are narrow and low, so arriving is a release; the analysis reports each
transition and the strongest reveal. Recipes pick both from what the level contains;
presets can pin them.

```bash
python3 -m badlevel generate --seed 7 --recipe industrial
```

## Open areas

Courtyards, plazas and yards open to the sky (`open_areas` in the config; recipes turn
it on through openness: compound 2, fortress 1, industrial 1). The top of their
columns is sky (`sky1` texture in the `.map`, `bl_sky` material in the `.glb`), the
perimeter gets pilasters so walls read as building fronts, and the contents are a
weighted choice that includes leaving the yard empty: a kiosk or monument, a low
colonnade, a sunken plaza, a raised terrace, or an arcade with a walkway on top. On
multi-storey levels, upper-storey galleries can overlook a yard.

**Compounds** (`open_buildings`, `open_roofscape`, the compound preset): yards get 1-3
free-standing buildings you can walk into, some with a ladder to a rooftop vantage
point, and the rock around the yard is carved into stepped roofs under the sky, so the
yard reads as a place between buildings instead of a pit. The roofs are scenery
(nobody can reach them; a test checks). Buildings have window openings, a light, a
counter and a small reward inside; compound yards are 10 m high (`yard_height`) and keep
other rooms 4 cells further away (`yard_setback`).

**Landmarks** (`arch_landmark`): a styled level's biggest space gets one deliberate,
unique object to navigate by, shaped by the style: a spire, a keep, an obelisk, a chimney
stack, a reactor core with floating rings, a water tower, a monolith (or a great column
where the room is too low).

## Compiling for Quake

The `.map` compiles with [ericw-tools](https://ericwa.github.io/ericw-tools/): no leaks, vis
and light run cleanly (only "texture not found" warnings: BadLevel names its textures,
`bl_wall`, `bl_floor`, ..., and ships no wad; add a `wad` key or retexture in TrenchBroom).
To check a whole range of levels:

```
python scripts/compile_check.py /path/to/ericw-tools/bin --seeds 1-2 --vis --light
```

## Editing the blockout part by part

The output is meant to be kept, textured, or changed piece by piece. In the **.glb**,
`level_geometry` holds one object per room and corridor (`room_5_megaroom`,
`corridor_3`), each with its elements as children: `.structure`, `.vault`, `.piers`,
`.members`, `.niches`, `.end feature`, `.portals`, `.building 2`, `.roofscape`... In
Blender, select `room_5_megaroom.vault`, delete it and drop in a curved vault of your own.
In the **.map**, each element is a TrenchBroom group (`func_group`, which compilers treat as
world), so the same element is one click in the editor.

## Mega-rooms

A level within the level (`mega_rooms` in the config; recipes turn it on through scale).
A huge, tall space that runs its own small generation: **platforms** with stairs,
**decks** you can walk under, **catwalks** between decks, solid **structures** and
**tiers** rising towards a wall, each a semantic region. Three characters, because a
mega-room isn't necessarily an arena: **hangar** (vast and mostly empty), **works**
(an industrial hall of decks and catwalks) and **terraces** (an amphitheatre or great
hall). Every region is reachable by construction and checked by a test.

## Architecture: hall cross-sections

With `arch_style` (gothic, industrial, bunker, compound; the fortress, industrial,
bunker and compound presets set it) roofed rooms stop being boxes. Each gets a
**cross-section**: a profile swept along its long axis, made of varied ceiling heights
plus structure standing in the room, so the silhouette changes even as plain greybox.

| Style | Profiles |
|---|---|
| gothic | **basilica** (pointed nave, low aisles, arcade piers, transverse ribs), **single-aisle**, **vault** (one ribbed pointed vault, no piers), **undercroft** (piers under groin vaults, low rooms only), **gable** |
| industrial | **portal** (pilaster frames, deep trusses, maybe a raised monitor), **sawtooth**, **multispan** (gabled spans on column rows), **clearspan** |
| bunker | **ribbed** (downstand ribs, haunches), **barrel** (low segmental vault), **mushroom** (squat piers with drop panels) |
| compound | **gable**, **shed**, **portal**, **multispan**, **clearspan** |
| egyptian | **hypostyle** (columns and a processional aisle), **lintel**, **corbelled**, **battered** |
| scifi | **hull** (chamfered with frames), **chamfered**, **lightwell** |
| castle | **trussed** (open roof with tie-beams), **timber**, barrel, undercroft |

Any recipe can use any style: `badlevel generate --recipe classic --style castle`.

Rooms are ranked by volume: the biggest space (and any mega-room) is *grand*, the next
third *major*, the rest *minor*, each with its own palette. Small rooms get something
subtle (haunches) or nothing. Bay spacing, pier thickness and rib depth follow the room's
proportions. Every palette keeps an option without piers, and each use of a profile
halves its weight for the rest of the level. `arch_weights` biases the choice
(`{"basilica": 2, "sawtooth": 0}`). The chosen section is recorded in
`room.meta["arch"]` (in the IR) so artists can see the intent and replace it.

Vaults and roofs are **smooth** (sloped planes in the `.map`, the `.glb` and the IR's
`ceiling_slopes`); beams, ribs and coffers stay crisp. `arch_smooth: false` keeps a strict
16-unit grid. Low rooms get low vaults, gables, **tray** or **coffered** ceilings.

**Walls** get structure too (`walls.py`, `arch_walls`): members on the bay lines
(shafts, frame pilasters, bunker counterforts), recesses cut into the solid wall between
them (gothic lancet niches with pointed heads, industrial service bays, bunker shelters,
compound window bays), one end feature in big halls (an apse niche, a loading door, a
blast door, a bay window) and crane corbels in tall industrial halls. A recess always
keeps one solid cell between it and any other space. The mean longest flat wall per
room roughly halves.

**Portals and framed reveals** (`portals.py`, `arch_portals`, `arch_axial`): corridor
openings get jambs, and in big rooms a raised head (pointed arch, round arch, lintel)
so the low corridor frames the hall; about two thirds of big halls put their end
feature on the axis of the way in, the rest stay oblique for surprise. Never over a door.

**Underground** (`underground`, the bunker preset): in from the surface and down. The
start room is open to the sky, the critical path steps down storey by storey to an exit
on the deepest storey, and at the bottom there's a deep chamber cut from the rock
(cavern section: rough ceiling, rock pillars, rough-cut walls). On the way down, an observation booth
on the storey above looks into the deep chamber through a glazed window (visual only: the
chamber stays behind its locked door).

Safety: piers go in before a room's features, keep 2 cells from doorways and a walkable
ring, and are undone if they would split the floor. Ceilings are applied after the
features and only lowered: never below a walkable surface plus 112 units (the lowest corridor), never below an
opening next to the cell, never over a gallery or the sky. The validator still has the
last word. Levels without a style are byte-identical to before.

## Merged halls

Not everything is rooms joined by corridors. About 30% of eligible neighbours (open
tree links on the same storey, not keys, secrets or connectors) are placed
**touching** along a wide seam (6+ cells) and become one continuous **hall**: an
L- or T-shaped space, an arena with an antechamber, up to 3 rooms. If their floors
differ by more than a step, the seam becomes **terrace steps** across its full width.

Every original room stays a **semantic region** (`room.meta["hall"]`, `level.halls`,
`"halls"` in the IR): encounters, pickups, pacing, diagnostics and the per-room
analysis still treat "the arena part" and "the antechamber" separately, and
editors can select them separately. On the map, green links mean "merged".

## Long corridors: shorter loops, and beats

Long corridors aren't boring because they're long; they're boring when nothing
changes. So BadLevel doesn't remove them, it fixes the cause and breaks them up:

- **Loops are chosen after placement**, between rooms that are 2+ steps apart in
  the graph (a triangle that bypasses a room; 3+ preferred) but close in space.
  About a quarter of loops may deliberately be long (a flank run, a sniper alley).
  Level loops may **cross** other corridors at the same height (junctions) instead of
  detouring around everything.
- **Beats** every ~16 m on long corridors, on straight level stretches with room to
  spare: a **chamber** (wider and taller, often with pillars that make cover pockets,
  and an enemy or two), **alcoves** (side niches with a pickup) or a **vault** (a much
  higher ceiling with a light).
- **Monotony analysis:** the longest stretch of each corridor where nothing changes
  (no turn, step, beat, junction or window). Over 40 m gets a `monotone_corridor`
  warning.

Single-storey levels went from a median loop corridor of ~90 m to ~26 m, and the
longest monotone stretch is under 20 m.

## Space types (for developers)

Every kind of space is a module in `badlevel/spaces/` implementing one interface:
`propose` (size, heights), `assign_shape`, `build` (write its open spans),
`attachment` (where corridors connect), `decorate` (features), `populate`
(contents) and `regions`. Rooms and the vertical connectors are the built-in types;
corridors and merged halls have their own modules. To add a new kind of space,
subclass `SpaceType` (or `RoomSpace`) and `register()` it; see
`tests/test_spaces.py` for a complete example. Background: docs/DECISIONS.md D13.

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
- Composition as a grammar instead of four strategies; hub vs central still look alike from above
  (macro structures, compression/release pacing). See docs/DECISIONS.md D12.
- Multi-storey compositions: halls spanning storeys, interlocking structures
- TrenchBroom groups per room/region in the `.map`
- Placing windows and galleries *for* previews along the critical path (now they
  are opportunistic: multi-storey levels preview far less than single-storey ones)
- Open archways between adjacent rooms
- TrenchBroom groups per room/corridor in the `.map`
- Engine plugins: Godot `@tool` importer for the JSON, Unity editor window, Unreal Python/Editor Utility
- Hand-made room templates (Noor's!) mixed with generated rooms
- Corridors passing over/under each other, bridges, lifts
- Straight diagonal stair risers (currently stepped across diagonal stretches)
- Themes and difficulty curves, combat-space metrics (sightlines, cover density)
- Valve 220 `.map` format and a generated dev-texture WAD
