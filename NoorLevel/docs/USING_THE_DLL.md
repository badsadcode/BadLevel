# Using NoorLevel.dll

NoorLevel is a plain .NET library with no dependencies. It targets **netstandard2.1** (Unity 2021+,
Godot 4 C#, .NET Framework 4.8 via netstandard, Mono) and **net8.0**. Everything in this guide is in
[`samples/HelloNoorLevel/Program.cs`](../samples/HelloNoorLevel/Program.cs), which builds and runs:

```bash
dotnet run --project samples/HelloNoorLevel
```

## Adding it to a project

- **A .NET project:** reference the project (`<ProjectReference Include="path/to/src/NoorLevel/NoorLevel.csproj" />`)
  or the compiled DLL (`dist/Library/NoorLevel.dll` after `build.cmd`):
  ```xml
  <Reference Include="NoorLevel"><HintPath>libs/NoorLevel.dll</HintPath></Reference>
  ```
- **Unity:** copy `NoorLevel.dll` (the netstandard2.1 build: `src/NoorLevel/bin/Release/netstandard2.1/`) into
  `Assets/Plugins/`.
- **Godot 4 (C#):** reference it from your `.csproj` like any .NET project.

Everything lives in the `NoorLevel` namespace; the exporters are in `NoorLevel.Export`.

## Making a level

```csharp
using NoorLevel;

// a preset and a seed: the same seed always gives the same level, on every machine
Level level = Generator.Generate("dungeon", seed: 7);

// a preset with its dials turned (each 0..1)
Level tall = Generator.Generate("classic", 3, r => { r.Verticality = 1.0; r.Combat = 0.2; });
```

The presets are `Recipes.Presets` (compact, classic, bunker, fortress, industrial, compound, techbase, runekeep,
outpost, grand, facility, complex, dungeon, wizard, market, arena, ctf, duel, explore). `Recipes.Surprise(seed)`
returns a preset picked by the seed plus extras to set on top. The ten dials (`Recipe.Dials`) are scale,
openness, verticality, claustrophobia, combat, exploration, loopiness, regularity, spaciousness and variety.

### Full control: every parameter

A preset and its dials compile into a `GenConfig` with about 85 settings. Change any of them before generating:

```csharp
GenConfig cfg = Recipes.Compile(Recipes.Presets["fortress"], seed: 12);
cfg.MapSize = 120;          // the level's longer side, in metres (one cell = one metre)
cfg.Storeys = 5;            // floors, 1 to 6
cfg.Stacking = 1.0;         // rooms over rooms: 0 = now and then, 1 = whenever it can
cfg.Pillars = false;        // no free-standing columns
cfg.ArchStyle = "egyptian";
Level fort = Generator.Generate(cfg);
```

Or by name: the spelling recipes, the command line (`--set map_size=96`) and the previewer use. Dictionaries
(weights) **merge** into the preset's own:

```csharp
Recipes.SetFields(cfg, new Dictionary<string, object>
{
    ["map_size"] = 96, ["keys"] = 2,
    ["feature_weights"] = new Dictionary<string, object> { ["balcony"] = 3.0 },
}, merge: true);
```

**Every parameter, its range and what it does: [PARAMETERS.md](PARAMETERS.md)** (generated from the code, so
it's always complete). The same list is in `ConfigDocs.All` at runtime, and `noorlevel-cli params` prints it.

A few to know:

| What you want | Set |
|---|---|
| A level about 100 m across | `MapSize = 100` (the generator fits it by changing room counts and sizes, within ~15%) |
| More floors, rooms over rooms | `Storeys = 4..6`, `Stacking = 0.5..1`, `StoreyChange` (how often the path changes floor) |
| Bigger or smaller rooms | `RoomScale`, `HeightScale`, `SizeBias` (`small` / `medium` / `large` weights) |
| More or fewer rooms | `MainRooms`, `SideRooms`, `Loops`, `Keys` (0–2), `Secret` |
| No pillars / no clutter | `Pillars = false`, `CrateScale = 0`, `FeatureWeights["pillars"] = 0` |
| A different look | `ArchStyle` (gothic, industrial, bunker, compound, castle, egyptian, scifi), `ArchWalls`, `ArchPortals`, `ArchLandmark` |
| Deathmatch | `Mode = "arena"` (spawns instead of start and exit), `Mirror = true` for CTF |

### Stations and markers

```csharp
var cfg = Recipes.Compile(Recipes.Presets["station"], seed: 7);   // a deep-sea station
cfg.Spine = "ring"; cfg.StationStyle = "derelict"; cfg.MapSize = 64;
Level st = Generator.Generate(cfg);
var props = st.Entities.Where(e => e.Kind == "prop");               // dressing markers: class, mount, footprint...
var loot  = st.Entities.Where(e => e.Kind == "loot");               // gameplay markers
```

Any preset can be dressed: `cfg.Programs = true; cfg.ProgramSet = "station"; cfg.Dressing = true;
cfg.StationEntities = true;`. What the markers mean, their fields, units and axes: the **consumer contract**
in the [README](../README.md#consumer-contract-markers-in-the-glb).

### When it can't

`Generator.Generate` builds, checks and rebuilds until the level passes the validator (up to
`cfg.MaxAttempts`). If no attempt passes it throws a `GenerationException`; try another seed or looser
settings. A returned level always has `level.Report.Ok == true`: it can be finished from start to exit,
keys come before their doors, nothing is a dead end, and a player-sized box fits everywhere it has to.

Generating takes 0.1–3 seconds depending on the size. In a game, run it on a worker thread
(`Task.Run(() => Generator.Generate(cfg))`) and build the meshes on the main thread.

## Reading a level

All positions are **map units, Z up** (Quake's convention: 32 units = one cell = 1 m, a player is 56 tall).
To get Y-up metres: `(x, y, z) -> (x / 32, z / 32, -y / 32)` (that's what the .glb and NoorEngine exports do).

```csharp
Entity start = level.Entities.First(e => e.Kind == "player_start");   // start.Pos, start.Props["angle"] (degrees)
foreach (Room room in level.Rooms)  /* room.Kind, X, Y, W, H (cells), Floor, Ceil (units), Storey, Shape */ ;
foreach (var kv in level.Spans)     /* kv.Key = Cell (x, y), kv.Value = open Spans (Floor, Ceil), bottom to top */ ;
```

| Member | What it is |
|---|---|
| `Spans` | The space itself: per grid column, the open vertical spans (everything else is solid). Two spans in a column = something over something. |
| `Rooms`, `Corridors` | What the spans were made from: rooms with kind (`start`, `combat`, `hub`, `key`, `exit`, `secret`, `courtyard`, `megaroom`, `stairwell`, `lift`, `ladder`, `spiral`, `tower`...), corridors with their paths. |
| `Entities` | Things placed in the level (table below). |
| `Links` | Ways between places that aren't walking: `lift`, `ladder`, `jumppad`. |
| `Diag` | Half cells with a 45° wall: cell → the corner its solid triangle points at. |
| `CeilSlope`, `FloorSlope` | Sloped vaults and ramps: corner heights per cell. |
| `Water`, `Hazard`, `Sky`, `Glass` | Water surface heights, slime / lava / spike floors, cells open to the sky, glass cells (solid). |
| `PartSolid`, `PartCells`, `PartCeiling` | Named parts (`room:5/vault`, `room:7/piers`) so art can replace them. |
| `Graph` | The mission graph the level was built from. |
| `Report` | The validator's findings. `Notes` says how it went. |

**Entities** (`e.Kind`, `e.Pos`, `e.Props`, and `e.Bounds` for things with a size):

| Kind | Props | Bounds |
|---|---|---|
| `player_start` | `angle` | |
| `exit` | | the trigger box |
| `key` | `color` (`red`, `blue`) | |
| `door` | `key` (the key that opens it) | the door |
| `secret_door` | | the door |
| `lift` | `low`, `high`, `travel`, `room` | the platform (at the top) |
| `ladder` | `low`, `high`, `room` | the ladder |
| `jumppad` | `target` (a Vec3), `rise`, `room` | the pad |
| `light`, `torch` | `intensity` | |
| `window` | `glazed`, `areas` | the opening (glass when glazed) |
| `trap` | `type` (`blade`, `darts`, `spikes`), `axis`, `width` | |
| `spawn` (arena) | `angle`, `team` (CTF) | |
| `flag` (CTF) | `team` | |
| `merchant` (market) | `stall`, `sells`, `room` | |

No monsters, weapons or pickups: NoorLevel makes the space, your game fills it. Arena maps list where weapons and
power-ups would go in `level.Arena`.

## Exporting

```csharp
using NoorLevel.Export;

byte[] data = Exporters.Export(level, "glb", out string fileName);      // any format by name
File.WriteAllText("level.map", MapExport.ToMap(level, MapGame.Quake2)); // or a format's own call
File.WriteAllBytes("level.glb", Gltf.ToGlb(level));
```

| Format | Call | Notes |
|---|---|---|
| `glb` | `Gltf.ToGlb(level)` | Y-up metres; a mesh per named part; entities as nodes with `extras` |
| `quake`, `quake2`, `valve` | `MapExport.ToMap(level, MapGame.X)` + `Textures` | zips with WAD2 / WAL / WAD3 textures; compile leak-free |
| `obj`, `stl` | `Obj.ToObj`, `Stl.ToStl` | |
| `json` | `LevelJson.ToIr(level)` | everything, for your own tools |
| `svg` | `Svg.ToSvg(level)` | a floor plan |
| `noorengine` | `NoorEngineExport.Project(level, cothDir)` | a NoorEngine project with a player and Coth |

`Exporters.Formats` lists them (id, label, extension, description).

### A mesh for your own engine

`LevelMesh.Build(level)` gives triangles grouped by material (`floor`, `wall`, `ceiling`, `sky`, `water`,
`door`...) and by named part, in map units with Z up: positions, normals, UVs (one UV unit per 64 map units)
and indices.

```csharp
MeshBuilder mesh = LevelMesh.Build(level, entities: true);
foreach (MeshGroup g in mesh.Groups.Values)   // g.Material, g.Pos, g.Nrm, g.Uv, g.Idx
    Console.WriteLine($"{g.Material}: {g.Idx.Count / 3} triangles");
```

**In Unity** (a sketch, not tested inside Unity): convert to Y-up metres and keep the winding (swapping Y and Z
mirrors the mesh, so flip each triangle):

```csharp
var mb = LevelMesh.Build(level);
foreach (var g in mb.Groups.Values)
{
    var verts = new Vector3[g.VertexCount];
    var norms = new Vector3[g.VertexCount];
    var uvs = new Vector2[g.VertexCount];
    for (int i = 0; i < g.VertexCount; i++)
    {
        verts[i] = new Vector3(g.Pos[3 * i], g.Pos[3 * i + 2], g.Pos[3 * i + 1]) / 32f;   // Unity is left-handed: (x, z, y)
        norms[i] = new Vector3(g.Nrm[3 * i], g.Nrm[3 * i + 2], g.Nrm[3 * i + 1]);
        uvs[i] = new Vector2(g.Uv[2 * i], g.Uv[2 * i + 1]);
    }
    var tris = g.Idx.ToArray();
    for (int t = 0; t < tris.Length; t += 3) (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
    var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
    m.SetVertices(verts); m.SetNormals(norms); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
    var go = new GameObject(g.Material, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
    go.GetComponent<MeshFilter>().sharedMesh = m;
    go.GetComponent<MeshCollider>().sharedMesh = m;
}
```

Or import the `.glb` with a glTF importer (Unity's glTFast, Godot's built-in importer), which keeps the part names.

## The command line and the previewer

The same things without code: `noorlevel-cli generate --preset dungeon --seed 7 --set map_size=120 --set storeys=4
--format glb,quake --out levels` (`noorlevel-cli params` lists every `--set`), and the previewer app, whose
**Parameters** and **All parameters** panels set the same fields.
