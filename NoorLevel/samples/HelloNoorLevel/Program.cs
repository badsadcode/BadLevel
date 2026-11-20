// The examples from docs/USING_THE_DLL.md, as one program that builds and runs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NoorLevel;
using NoorLevel.Export;

static class Program
{
    static void Main()
    {
        // 1. The quickest way: a preset and a seed.
        Level level = Generator.Generate("dungeon", seed: 7);
        Console.WriteLine($"dungeon 7: {level.Rooms.Count} rooms, finishable: {level.Report.Ok}");

        // 2. Turn the dials of a preset (0..1 each).
        Level tall = Generator.Generate("classic", 3, r => { r.Verticality = 1.0; r.Combat = 0.2; });
        Console.WriteLine($"classic 3, verticality 1: {tall.Rooms.Select(r => r.Storey).Distinct().Count()} storeys");

        // 3. Full control: compile a preset, then set any parameter.
        GenConfig cfg = Recipes.Compile(Recipes.Presets["fortress"], seed: 12);
        cfg.MapSize = 120;          // the level's longer side, in metres (cells)
        cfg.Storeys = 5;            // floors
        cfg.Stacking = 1.0;         // rooms over rooms
        cfg.Pillars = false;        // no free-standing columns
        cfg.ArchStyle = "egyptian";
        Level fort = Generator.Generate(cfg);
        Console.WriteLine($"fortress 12: footprint {Generator.Footprint(fort)} m, {fort.Rooms.Select(r => r.Storey).Distinct().Count()} storeys");

        // 4. The same by name (as recipes, the CLI and the previewer do; weights merge).
        GenConfig cfg2 = Recipes.Compile(Recipes.Presets["classic"], seed: 5);
        Recipes.SetFields(cfg2, new Dictionary<string, object>
        {
            ["map_size"] = 96, ["keys"] = 2,
            ["feature_weights"] = new Dictionary<string, object> { ["balcony"] = 3.0 },
        }, merge: true);
        Level byName = Generator.Generate(cfg2);
        Console.WriteLine($"classic 5 by name: keys {byName.Entities.Count(e => e.Kind == "key")}");

        // 5. Read the level: where to start, the rooms, the open space.
        Entity start = level.Entities.First(e => e.Kind == "player_start");
        Console.WriteLine($"start at {start.Pos} (map units, Z up), facing {start.Props["angle"]} degrees");
        foreach (Room room in level.Rooms.Take(3))
            Console.WriteLine($"  room {room.Id} {room.Kind} at ({room.X},{room.Y}) {room.W}x{room.H} cells, floor {room.Floor}");
        int open = level.Spans.Sum(kv => kv.Value.Count);
        Console.WriteLine($"  {level.Spans.Count} columns, {open} open spans");

        // 6. Export: any format by name, or a format's own call.
        Directory.CreateDirectory("out");
        foreach (var format in new[] { "glb", "quake", "noorengine" })
        {
            byte[] data = Exporters.Export(level, format, out string fileName);
            File.WriteAllBytes(Path.Combine("out", fileName), data);
            Console.WriteLine($"  wrote out/{fileName} ({data.Length / 1024} KB)");
        }
        File.WriteAllText("out/dungeon_7.map", MapExport.ToMap(level, MapGame.Quake2));
        File.WriteAllBytes("out/dungeon_7.glb", Gltf.ToGlb(level));

        // 7. A mesh for your own engine (Z-up map units, grouped by material).
        MeshBuilder mesh = LevelMesh.Build(level, entities: true);
        foreach (var g in mesh.Groups.Values.OrderBy(g => g.Material))
            Console.WriteLine($"  {g.Material,-12} {g.VertexCount,6} vertices {g.Idx.Count / 3,6} triangles");

        // 8. When it can't: a GenerationException (no valid level within cfg.MaxAttempts).
        try { Generator.Generate(new GenConfig { Seed = 1, MainRooms = 2, MaxAttempts = 1, Storeys = 6, Keys = 2 }); }
        catch (GenerationException e) { Console.WriteLine("  (as expected sometimes) " + e.Message.Split('\n')[0]); }
    }
}
