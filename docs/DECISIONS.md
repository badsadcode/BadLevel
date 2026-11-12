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
