# Porting BadLevel (Python) to NoorLevel (C#): the conventions

Several people (and agents) port modules in parallel. Code written against these rules can call
each other's functions without seeing them first: the name of every C# function follows from the
Python one.

The Python source is `badlevel/` at the repository root. Port **faithfully**: same algorithm, same
order of operations, same constants and comments (shortened is fine). Random numbers don't need
to match Python's; only the logic does.

## Files and names

| Python | C# |
|---|---|
| `badlevel/foo.py` | `NoorLevel/src/NoorLevel/Foo.cs`, `public static class Foo` (namespace `NoorLevel`) |
| `badlevel/foo_bar.py` | `FooBar.cs`, `static class FooBar` |
| `badlevel/spaces/foo.py` | `Spaces/Foo.cs`, `public static class FooSpace` for module functions (e.g. `HallSpace`, `CorridorSpace`), classes keep their names (`RoomSpace`, `ConnectorSpace`, `OpenAreaSpace`, `MegaSpace`, `TowerSpace`) |
| `def foo_bar(...)` | `public static ... FooBar(...)` |
| `def _foo(...)` (private, but imported elsewhere) | `public static ... Foo(...)` too (we don't hide helpers) |
| two functions that collide after removing `_` | the private one gets suffix `_`: `Foo_` |
| `CONSTANT = ...` | `public static readonly ... Constant` (PascalCase) |
| `class Foo:` | `public sealed class Foo` (or non-sealed if subclassed) |
| keyword / default args | C# optional parameters, same names in camelCase, same defaults |

Existing names that already exist in NoorLevel and must be kept:
`Layout` (layout.py), `Decorate` (decorate.py), `MissionGraph`/`Node`/`Edge` (graph.py; `build_graph` = `MissionGraph.Build`),
`Connectors` (connectors.py), `Shape` (shapes.py: `make_shape` = `Shape.Make`, `pick_shape` = `Shape.Pick`,
`SOLID_SIDES` = `Shape.SolidSides(corner)`, `corner_vertices` = `Shape.CornerVertices`),
`Validator` (validate.py: `validate` = `Validator.Run`, `successors`, `flood`, `_node_at` = `NodeAt`,
`_link_edges` = `LinkEdges`, `_door_cells` = `DoorCells`), `Clearance` (clearance.py: `widen_pinches` = `Clearance.Widen`,
`clearance_report` = `Clearance.Report`), `Recipes` / `Recipe` (recipes.py), `GenConfig` (config.py), `Generator`
(generator.py), `LayoutException` (`LayoutError`), `Level`, `Room`, `Corridor`, `Entity`, `Link`.
`weights.weighted(rng, options, bias, key)` = `rng.WeightedBias(options, bias, key)`.

## Types

| Python | C# |
|---|---|
| cell `(x, y)` | `Cell` (`.X`, `.Y`, `.Offset(dx, dy)`) |
| span `(floor, ceil)` | `Span` (`.Floor`, `.Ceil`, `.Height`) |
| corner `"sw" / "se" / "nw" / "ne"` | `Corner.SW` ... |
| position `(x, y, z)` | `Vec3` |
| entity `bounds` | `Entity.Bounds` = `(Vec3 Lo, Vec3 Hi)?` |
| link dict `{"kind","a","b","two_way","interaction","accessible","room"}` | `Link` |
| `room.meta["key"]` | `room.Meta["key"]` (Dictionary<string, object>; read with `room.Get<T>("key", fallback)`) |
| corner heights `(z00, z10, z11, z01)` | `(int, int, int, int)` |
| `Dict[Cell, X]`, `Set[Cell]`, `List[X]` | `Dictionary<Cell, X>`, `HashSet<Cell>`, `List<X>` |
| a tuple returned | a C# value tuple with named fields |
| `level.config` | `level.Config` (`GenConfig`, fields PascalCase of the Python names: `arch_style` = `ArchStyle`) |
| `getattr(cfg, "x", default)` | just `cfg.X` (every field exists) |
| `str(e)` of a LayoutError | `e.Message` |

Ownership strings stay exactly as in Python: `"room:3"`, `"corridor:7"`, `"window:2"`, `"roof:5"`,
part names like `"room:5/vault"`.

## Random numbers

`random.Random` = `Rng` (`NoorLevel/src/NoorLevel/Rng.cs`):

| Python | C# |
|---|---|
| `rng.random()` | `rng.Random()` |
| `rng.randint(a, b)` | `rng.Int(a, b)` |
| `rng.randrange(a, b, step)` | `rng.Range(a, b, step)` |
| `rng.uniform(a, b)` | `rng.Uniform(a, b)` |
| `rng.choice(xs)` | `rng.Choice(xs)` |
| `rng.choices(xs, ws)[0]` | `rng.Choices(xs, ws)` |
| `rng.shuffle(xs)` | `rng.Shuffle(xs)` |
| `rng.sample(xs, k)` | `rng.Sample(xs, k)` |
| `random.Random(f"{seed}/x/{id}")` (a stream from a label) | `Rng.FromLabel($"{seed}/x/{id}")` |
| `random.Random(rng.getrandbits(64))` | `rng.Derive("label")` |

Iterate sets and dicts in a **deterministic order** wherever the order changes the result (Python
dicts keep insertion order, `HashSet` doesn't: sort by `Cell` or use a `List`).

## Leave out

Monsters, weapons, items, ammo, power-up *pickups* and enemy placement (NoorLevel makes the
space; the game fills it). Arena **spawn points**, CTF **flags** and **jump pads** stay (they shape
the level). Keep the analysis/evaluation modules out of the generator path.

## Checking your work

```bash
dotnet build NoorLevel/NoorLevel.sln
dotnet run --project NoorLevel/src/NoorLevel.Cli -- sweep 3        # every preset, every format
dotnet test NoorLevel/tests/NoorLevel.Tests
```
