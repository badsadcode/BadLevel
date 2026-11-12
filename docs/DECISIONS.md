# BadLevel: architectural decisions

Why BadLevel is built the way it is. Each entry: the decision, why, and what it
costs. Newest last. This is the reasoning to carry into NoorLevel later; the code
itself is the prototype.

---

## D1. The world is a grid of columns with stacked open spans

**Decision.** A level is a 2D grid of 32-unit cells. Each cell holds a sorted list of
*open spans* `(floor, ceiling)`; everything else is solid. 45-degree walls are
half-solid cells (`level.diag`).

**Why.** It makes the hard guarantees cheap: brushes are the solid complement of the
spans, so output is **sealed by construction**; walkability is a graph over spans;
line of sight is a voxel lookup. Stacking spans gives room-over-room, storeys,
balconies, galleries and windows without a different model.

**Cost.** No true slopes (height changes are steps), no curves beyond 45 degrees, and
32 units is coarse for detailed terrain. Fine for blockouts, which artists rework.

## D2. Python prototype now, NoorLevel (C#) later; no exact replication

**Decision.** BadLevel proves the concepts. The NoorMD bench will be a native C# port
written from a design document, not a line-by-line translation, and it does not need
to reproduce Python's levels seed for seed.

**Why.** NoorMD's convention is native, deterministic C#. Matching levels across
languages would have required a hand-rolled RNG and frozen algorithms for little
benefit. The JSON IR is the contract worth keeping.

## D3. Every level is validated, failures are re-rolled

**Decision.** The generator retries until the validator passes: sealed, every room
reachable, keys gate the exit, no softlocks.

**Why.** Procedural output that is *usually* fine is not usable. Retries are cheap
(about 1 in 4 levels needs a second attempt).

## D4. Movement is directed; softlocks are checked

**Decision.** Step up at most 18 units, drop down up to 288; lifts and ladders are
explicit links with semantics (direction, interaction, accessibility). From every
reachable spot the next goal must still be reachable.

**Why.** One-way drops and lifts make a connected room graph meaningless as a test of
completability.

## D5. Analysis is optional, separate and machine-readable

**Decision.** `badlevel.analysis` (numpy) produces plain JSON. The HTML preview only
draws it. The generator core has no dependencies.

**Why.** Exporters, engine plugins and the generator itself (candidate gate) can all
reuse the same results. Visualisation choices never leak into the data.

## D6. The candidate gate rejects, it does not optimise

**Decision.** `--candidates N` keeps the *first* variant without `problem` flags.

**Why.** Optimising every level toward target numbers converges on sameness. Variety,
pacing and deliberate vulnerability are worth more than a score.

## D7. Seeing is not walking

**Decision.** Firing slits are 48 units tall (a player is 56), so they are visual-only
by construction. Glass is a set of cells that is solid for movement and gunfire and
open for visual connectivity. The analysis keeps a visibility graph next to the
walking graph.

**Why.** Windows, overlooks and landmarks are only safe if they can never become
unintended paths (bypassing a locked door, for example).

## D8. Storeys are planning guides; connectors are rooms with ports

**Decision.** Rooms get a storey (256 units apart) but may span more. Stairwells, lifts
and ladders are connector rooms with a lower and an upper *port* where corridors
attach.

**Why.** It keeps vertical connections as first-class, editable spaces with their own
geometry and semantics, instead of special cases inside corridors.

## D9. Long corridors: fix the cause, then add beats

**Decision.** Loops are chosen *after* placement (close in space, apart in the graph),
level loops may cross other corridors at the same height (junctions), and long
corridors get beats (chambers, alcoves, vaults). A quarter of loops may be long on
purpose.

**Why.** Long corridors came from loops picked before rooms had positions. Removing
long corridors altogether would remove a legitimate design tool (flank runs, sniper
alleys); monotony is the problem, not length.

## D10. Merged halls keep their rooms as regions

**Decision.** Rooms merged into one hall stay separate rooms in the data
(`room.meta["hall"]`, `level.halls`).

**Why.** Encounters, pacing, diagnostics and editors need "the arena part" and "the
antechamber" even when they read as one space. Mega-rooms will build on this.

## D11. Recipes: design dials compile into settings; overrides pin identity

**Decision.** A recipe is eight dials (0..1: scale, openness, verticality,
claustrophobia, combat, exploration, loopiness, regularity) plus raw overrides.
`compile_recipe` maps dials monotonically onto GenConfig; overrides always win. All
style constants that used to live in code (size mix, shapes, room features, crates,
enemy and pickup density, corridor widths, ceiling heights, footprints) became
config levers with the old values as the 1.0 baseline. The recipe is saved beside
every export.

**Why.** Users express intent in design terms, not 40 numbers. Overrides let a preset
fix what makes it recognisable (bunker: no glass, ladders and lifts; fortress:
stairwells and slits). Keeping the old values as the baseline means "no recipe" still
generates exactly what it did.

**Non-combat.** `combat = 0` means zero enemies. The analysis then reports
`purpose: exploration` and skips combat-only diagnostics.

**Cost.** The dial mapping is hand-tuned; it is documented in `recipes.py` and should
be revisited as new space types arrive.

## D12. Identity is measured, and the measurement exposed a limit

**Decision.** `fingerprint.py` describes a level as ~17 features (room size and
variation, ceiling height, corridor width, storeys, vertical spread, regularity,
halls, windows and glass, combat density, loops, connector mix). A test requires that
presets are recognisable from fingerprints alone (nearest-centroid, leave-one-out).

**Result.** 98% on the first measured batch. But side-by-side renders showed that from
far away every preset still has the **same silhouette**: a branching tree of rooms and
corridors. Recipes change proportions, scale, density, heights and materials of the
layout, not its macro shape, which comes from the layout algorithm itself.

**Consequence.** Instant recognisability needs the next steps: space-type modules,
open areas, mega-rooms and, above all, **composition** (macro structures such as
hub and spokes, central space with satellites, stacked tower, compound). The
fingerprint test stays as a guard; a silhouette-level measure should be added with
composition.

## D13. Space types are modules behind one interface

**Decision.** Every kind of space implements `SpaceType` (`badlevel/spaces/`):
`propose` (size, heights, meta), `assign_shape`, `build` (write spans), `attachment`
(ports), `decorate` (features), `populate` (contents) and `regions`. A registry maps
room kinds to space types. Rooms (all eight gameplay roles) and connectors
(stairwell, lift, ladder) are the first modules. Corridors (style, beats, contents)
and merged halls are relationships rather than room types and got their own
modules. Placement, carving, decoration and the ports lookup dispatch through the
registry; `Room.is_connector` asks it too.

**Why.** Open areas and mega-rooms must be *additions*, not more special cases in
the core. A test registers a new space type from outside the package, generates
with it and removes it again.

**How it was verified.** A pure refactor: full `.map` + `.json` output of 38 levels
(defaults, three storeys, six presets) hashed before and after, byte for byte
identical. Keeping the random sequence stable meant the base class still performs
the feature roll for spaces that never get features.

**Cost.** Some helpers (feature placement, entity spots) still live in `decorate.py`
and are imported by the room module; they can move when a second space type needs
them in a different form.

## D14. Open areas: a plug-in space type with a sky

**Decision.** Courtyards / plazas are `OpenAreaSpace` (`spaces/open_area.py`), the first
space type added through the D13 interface. A few hubs, arenas or fight rooms become
courtyards when `open_areas > 0` (recipes: openness; compound 2, fortress 1 as its
bailey, industrial 1 as a loading yard; never in a bunker). The column is open up to
sky height (nominally 10 m, clamped to 9-14 m whatever the recipe's ceiling scale)
and those cells are in `level.sky`: the solid above them gets a sky face (`sky1` in
the `.map`, `bl_sky` in the `.glb`, `"sky"` in the IR).

**Outdoors is not a big room.** The perimeter gets pilasters in a regular rhythm so the
walls read as building fronts. Contents are one weighted choice including *nothing*
(breathing room), a kiosk/monument, a colonnade, a sunken plaza, a raised terrace or
an arcade with a walkway. Indoor pillars run floor to ceiling; outdoor columns stop
at 5-7 m because there is no ceiling to hold up (the first render showed 20 m
columns, which read as towers). Lamps hang at most 7 m up instead of at the sky.

**Default levels unchanged.** With `open_areas = 0` the graph step is skipped; the
`.map` output of the 38 golden levels stayed byte-identical. The IR gained an empty
`"sky"` list.

**Cost / next.** The kiosk is a solid block; a building you can enter belongs to
mega-rooms (sub-generation). Yards don't yet shape the macro layout (a compound
should be *organised around* its yard): that is composition.

## D15. Mega-rooms run their own generation inside their footprint

**Decision.** `MegaRoomSpace` (`spaces/mega.py`) is a huge (24-32 cells, capped at 40),
tall (12-18 m) space whose `decorate` hook runs a small generator: sub-areas joined by
open transitions instead of rooms and corridors. Vocabulary: **platforms** (raised,
with stairs), **decks** (on stilts, walkable underneath), **catwalks** (bridges between
decks at one height, floor open below), **structures** (solid masses) and **tiers**
(terraces rising towards a wall, two stairs each). Every sub-area is a semantic region
(`room.meta["regions"]`, cells in `region_cells`).

**Characters, because a mega-room is not necessarily an arena.** *hangar* (sparse: a
few big structures, a loading dock), *works* (dense: decks on one shared level so
catwalks line up, platforms, clutter), *terraces* (an amphitheatre / great hall).
Recipes weight them (`mega_styles`): industrial -> works, compound -> hangar,
fortress -> terraces, explore -> terraces/works with no enemies at all.

**Reachability by construction.** Each piece is placed together with its stairs, and
undone if it would split the base floor into islands; stair landings are reserved so
later clutter cannot block them. A test requires every non-solid region to be
reachable from the player start.

**What the tests caught.** (1) Terraces were first built inverted (the lowest tier
against the wall, higher ones in front), which buried the lower tier's stairs and made
it unreachable; tiers now rise towards the wall and are built front to back. (2) The
first "works" halls almost never had catwalks, because decks rarely shared a height and
faced each other; one deck level per hall plus aligned placement fixed it (15 catwalks
in 20 halls instead of 1 in 30).

**Cost / next.** The interiors are functional and legible but not yet memorable; they
need iteration with an artist's eye. Mega-rooms are rectangular outlines; structures are
solid (no enterable buildings yet). Composition should decide *where* a mega-room sits
(central space with satellites, compression before it).

## D16. Composition: the macro structure, and pacing by contrast

**Decision.** `GenConfig.composition` picks the level's macro structure, and it acts on
**both** the mission graph and the placement (if only one of them changed, corridors
would just wrap around everything):

| composition | graph | placement |
|---|---|---|
| `tree` (default) | as before | as before (random sides) |
| `hub` | side rooms and key branches hang off a centre node; loops come back to it | the centre is placed first; its children go round its four sides in turn, further rooms keep heading outwards |
| `central` | as hub, plus two extra loops tying satellites back to the centre | as hub, with satellites in a tight ring around the big space |
| `linear` | side rooms attach only to the critical path | the critical path follows one axis with rooms centred on it (a spine), side rooms branch off perpendicular (ribs), merged halls follow the axis, no deliberately long loops |

Recipes choose it from what the level contains (big spaces -> `central`) unless a
preset pins it: compact and bunker are spines, industrial and explore hubs.

**Pacing by contrast.** With `pacing_contrast`, corridors into big spaces (mega-rooms,
courtyards, rooms 9 m+ tall) are 3 cells wide and 3.5 m high, so arriving is a
release. The analysis reports every such transition (`transitions`: corridor vs room
cross-section) and flags the strongest reveal.

**Measured.** Three silhouette features (elongation of the room layout, centrality of
the best-connected room, share of dead ends) recognise the composition from the
silhouette **alone** 70% of the time (chance: 25%); linear levels are clearly
elongated, hub/central clearly centralised. In side-by-side top-down renders hubs read
as crosses, central levels as a mass with satellites, linear ones as fishbones (5 of
6 seeds; one wraps around through ordinary loops). All presets recognisable: 100% on
the measured batch.

**This answers D12** in part: presets no longer share one silhouette. What remains:
hub and central look alike from far above (they differ in what sits at the centre, not
in shape), and compositions are still four hand-made strategies rather than a grammar.

**Bugs found on the way.** (1) Merged halls ignored the composition and broke spines.
(2) Recipes chose the composition before applying a preset's overrides, so a fortress
whose courtyard and mega-room come from overrides was composed as a plain tree; the
composition is now derived after overrides.

## D17. Distinctness is evaluated, published and guarded

**Decision.** `badlevel/evaluate.py` generates many seeds per preset (or composition),
fingerprints every level and reports: leave-one-out recognition and a confusion
matrix, the closest pairs (drift warning), **variety inside each label vs distance
between labels** (separation), a z-score profile per label ("what makes it itself"),
and space metrics answering the modularity note (biggest space, intimate vs expansive
rooms, side rooms, secrets, brush count). `badlevel evaluate presets|compositions`
writes `evaluation.json` and an HTML page; the live site publishes both on every push.

**Why variety matters as much as accuracy.** A preset that always made the same level
would be perfectly recognisable and still a failure. Tests require every preset to
have variety and the separation to stay above 1 (levels of different presets further
apart than levels of the same one).

**First results.** Presets: 100% recognised on 6 seeds each, separation 1.6x; the
closest pair is fortress <-> industrial (both tall, multi-storey, organised around a big
space), the next compact <-> classic. Compositions: 71% from the silhouette alone
(chance 25%) but only 50% from the full fingerprint, because compositions differ only
in macro shape and the other features are noise for that question; silhouette
separation is reported separately.

**Next.** Fortress and industrial need more distance (stairwell towers vs lift
shafts already differ; their footprints and halls should too). Hub vs central remains
the weakest composition pair.

## D18. Architecture starts with hall cross-sections

**Problem.** The architectural showcase (12 levels from the four themed presets, seeds
1-3) showed that BadLevel made big spaces and real verticality but not architecture:
every room was a box with a flat ceiling and sheer walls, so a fortress, a factory
and a compound differed by plan only. Gothic and underground weren't convincing at all.

**Decision.** `badlevel/architecture.py` gives roofed rooms a *cross-section*: a
profile of ceiling heights swept along the room's long axis, plus piers, columns and
pilasters standing in it. Profiles per style (D18 table in the README). It's an
additive pass, in two halves around a room's features: piers before (so features
avoid them), ceilings after (so they can respect platforms, decks and stairs).

**Why sections first.** The ceiling and the structure that carries it change the
silhouette of the biggest spaces most, with no new space types and no new routing. A
section is also easy to read and replace as greybox: a nave is a nave in any engine.

**Noor's constraints, and how they're met.**
1. *Variety within a style*: palettes, not templates; open variants (vault, clearspan)
   and asymmetric ones (single-aisle, shed); each use of a profile halves its weight
   for the rest of the level.
2. *A hierarchy across a level*: rooms ranked by volume into grand / major / minor
   tiers with their own palettes; small rooms get haunches or nothing.
3. *Proportions*: bay spacing grows with the height (clamped; at least three bays),
   piers thicken from 12 m, ribs and trusses deepen with the span; an undercroft is only
   offered to low rooms (in a 18 m hall it turned into a hypostyle, seen in a test render).
4. *Genuinely open space*: every grand palette keeps a pierless option.
5. *Framed reveals*: not in this step (step 3 of the plan).
6. *Useful as greybox*: plain blocks only, on the 32-unit grid and 16-unit steps;
   `room.meta["arch"]` records profile, bay, aisle widths, spring heights.

**Safety.** Ceilings are only lowered, never below the top walkable surface + 128
(a base corridor's height) or below an opening beside the cell; galleries and sky are
skipped. Piers keep 2 cells from doorway cells (features keep 3, but arcades with gaps
looked broken), stand on plain floor, keep a walkable ring, and are undone if the base
floor would split. Each room uses its own random stream, so unstyled levels are
byte-identical (golden check: 26 of 26 unchanged; only the 12 styled preset levels in
the set changed). The validator remains mandatory.

**Known limits.** Arches and vaults are stepped (1 cell x 16 units), "voxel arches";
smooth slopes would need sloped brushes in the exporter. Rooms on a multi-storey level
are often 6 m high, which leaves 64 units to shape: there the effect is subtle. Shaped
rooms (rings, octagons) take the profile of their bounding box.

## D19. Smooth vaults, and character for small rooms

**Smooth vaults.** Stepped vaults read as arches from across a hall but as an upside-down
staircase from below. Profiles that vary along one axis (vault, barrel, basilica,
gable, shed, multispan, sawtooth, haunches, tray) now also give a continuous surface;
at a cell's four corners it yields a plane, kept in `level.ceil_slope`. The column span
keeps the **lowest corner**, so validation, LOS and analysis stay conservative and
unchanged. Exporters draw the true surface:
- `.map`: the solid above a sloped cell is a box with a sloped bottom plane (convex, so
  qbsp-safe); cells with the same corners merge along the axis the slope doesn't change
  on, so the brush count barely moves.
- mesh / glTF / preview: a sloped ceiling quad, and side faces between cells whose
  bounds are lines along the edge: the edge is split where bounds cross and each piece
  becomes a quad. Checked with a magenta-background render (no pixel of background
  shows through from inside) and a unit test that the side faces exactly fill a gap.
- IR: `ceiling_slopes` and per-brush `slope`.
Ribs, trusses, drop panels, coffers and groin vaults stay crisp steps: they are
members, not surfaces. A slope is only used where nothing is stacked above the room in
that column. `arch_smooth: false` restores the strict 16-unit grid.

**Small rooms.** On multi-storey levels most rooms are 6 m high, and with 128 units of
headroom there was almost nothing left to shape: 28 of 64 fortress rooms and ~45 of 78
industrial rooms stayed plain boxes. Changes:
- HEAD is now 112, the height of the low pacing corridors the generator already makes,
  so no structure is lower than a corridor a player already walks through. Profile
  thresholds are relative to HEAD.
- With smooth surfaces a 48-64 unit vault or gable reads, so low vaults and gables are
  allowed; new low-room profiles **tray** (raised middle, coved edges) and **coffered**
  (a waffle slab in a bunker).
- "plain" is weighted lower in the minor tier and damped like any other profile.
Result over seeds 1-5: plain rooms (secrets excluded) fortress 6/64, industrial 13/78,
compound 10/63, bunker 21/54. The bunker's remaining plain rooms are 128 units high
(corridor height): nothing fits under them, and their character has to come from walls.

## D20. Structure along the walls

**Problem.** Noor's note on the showcase: "enormous uninterrupted wall surfaces". After
step 1 the ceilings had structure, the walls didn't: the longest flat wall per room
averaged 11-15 m, up to 38 m in the big halls.

**Decision.** `badlevel/walls.py` gives every eligible room of a styled level a rhythm
along its walls, aligned with its section's bays where it has them:
- *inward members* on the bay lines of all four walls (shafts, frame pilasters,
  counterforts 2 wide in bunkers), placed with the pier rules before the features;
  skipped where the section already stands members against the walls;
- *outward recesses* between the members: gothic lancet niches with pointed (sloped)
  heads, industrial service bays in every other bay, bunker shelters (96 high, so they
  fit 128-high rooms), compound window bays with a 32-unit sill;
- *one end feature* in grand and major halls: an apse niche 2 deep with a pointed head,
  a loading door, a blast door, a bay window;
- *crane corbels* along the long walls of tall industrial halls (forced stepped cells
  laid over the section).

**Why recesses go outward.** Cutting into the solid around a room changes the wall's
depth without taking floor from the room, and it is where a wall would have niches.
It is only safe with rules: the recess cell must be solid rock with no owner, inside
the border, and every 8-neighbour that isn't the room or the recess itself must be solid
too, so one cell of wall always remains between it and any other space. All or nothing
per recess, never at a doorway (2 cells). A recess belongs to its room (owner), so
windows found later may open from it, which looks right.

**Measured** (seeds 1-3, the same layouts with and without walls): mean longest flat
wall per room fortress 11.5 -> 5.9 m, industrial 13.0 -> 7.0 m, compound 14.5 -> 5.9 m,
bunker 7.1 -> 5.4 m; the worst wall per level in fortress, industrial and compound
19-38 m -> 8-26 m (bunker 9-13 -> 7-9 m). A test requires a clear
drop for every style. `arch_walls: false` turns the pass off; unstyled levels are
byte-identical.

**Known limits.** Members are full-height boxes; there are no stepped buttresses or
string courses yet. Rooms with shaped outlines (octagons, rings) get members only where
their bounding box meets a wall. Small gothic rooms can look busy with shafts every
3 cells; the minor tier only thins the recesses.
