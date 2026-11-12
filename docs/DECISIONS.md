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
