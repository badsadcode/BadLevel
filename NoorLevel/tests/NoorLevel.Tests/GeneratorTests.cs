using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NoorLevel;
using NoorLevel.Export;
using NoorLevel.Preview;
using Xunit;

public class GeneratorTests
{
    public static IEnumerable<object[]> PresetSeeds() =>
        from p in Recipes.Presets.Keys from s in new[] { 1, 2 } select new object[] { p, s };

    [Theory]
    [MemberData(nameof(PresetSeeds))]
    public void EveryPresetMakesAValidLevel(string preset, int seed)
    {
        var level = Generator.Generate(preset, seed);
        Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
        Assert.Single(level.Entities, e => e.Kind == "player_start");
        if (level.Config.Mode == "arena") Assert.True(level.Entities.Count(e => e.Kind == "spawn") >= 2);   // deathmatch: spawns, no exit
        else Assert.Contains(level.Entities, e => e.Kind == "exit");
    }

    [Fact]
    public void SameSeedSameLevel()
    {
        var a = Generator.Generate("classic", 7);
        var b = Generator.Generate("classic", 7);
        Assert.Equal(Json.Write(LevelJson.ToIr(a)), Json.Write(LevelJson.ToIr(b)));
    }

    [Fact]
    public void DifferentSeedsDiffer()
    {
        var a = Generator.Generate("classic", 1);
        var b = Generator.Generate("classic", 2);
        Assert.NotEqual(Json.Write(LevelJson.ToIr(a)), Json.Write(LevelJson.ToIr(b)));
    }

    [Fact]
    public void DialsChangeTheLevel()
    {
        var small = Generator.Generate("classic", 3, r => r.Scale = 0.0);
        var big = Generator.Generate("classic", 3, r => r.Scale = 1.0);
        Assert.True(big.Rooms.Count > small.Rooms.Count);
    }

    [Fact]
    public void SpansNeverOverlapAndHaveHeadroom()
    {
        var level = Generator.Generate("fortress", 2);
        foreach (var kv in level.Spans)
            for (int i = 0; i < kv.Value.Count; i++)
            {
                if (!(level.OwnerOf(kv.Key, kv.Value[i]) ?? "").StartsWith("window:"))   // slits are 48 tall by design
                    Assert.True(kv.Value[i].Height >= level.Config.PlayerHeight);
                if (i > 0) Assert.True(kv.Value[i].Floor > kv.Value[i - 1].Ceil);
            }
    }
}

public class ExportTests
{
    static readonly Level L = Generator.Generate("classic", 1);

    [Theory]
    [InlineData("quake"), InlineData("quake2"), InlineData("valve"), InlineData("map"), InlineData("glb"),
     InlineData("obj"), InlineData("stl"), InlineData("json"), InlineData("svg")]
    public void EveryFormatExports(string format)
    {
        var data = Exporters.Export(L, format, out var name);
        Assert.True(data.Length > 1000);
        Assert.StartsWith("noorlevel_1", name);
    }

    [Fact]
    public void GlbIsWellFormed()
    {
        var glb = Gltf.ToGlb(L);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        int jlen = BitConverter.ToInt32(glb, 12);
        var json = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(glb, 20, jlen));
        Assert.Equal("2.0", ((Dictionary<string, object>)json["asset"])["version"]);
        Assert.NotEmpty((List<object>)json["meshes"]);
    }

    [Fact]
    public void MapHasBalancedBracesAndAStart()
    {
        foreach (var game in new[] { MapGame.Quake, MapGame.Quake2, MapGame.Valve220 })
        {
            var map = MapExport.ToMap(L, game);
            Assert.Equal(map.Count(c => c == '{'), map.Count(c => c == '}'));
            Assert.Contains("info_player_start", map);
            Assert.Contains("worldspawn", map);
        }
    }

    [Fact]
    public void QuakeZipHasMapAndWad()
    {
        var zip = new ZipArchive(new MemoryStream(Exporters.Export(L, "quake", out _)));
        Assert.Contains(zip.Entries, e => e.Name.EndsWith(".map"));
        var wad = zip.Entries.First(e => e.Name == Textures.WadName);
        using var s = wad.Open(); var head = new byte[4]; s.Read(head, 0, 4);
        Assert.Equal("WAD2", Encoding.ASCII.GetString(head));
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var text = Json.Write(LevelJson.ToViewer(L));
        var back = (Dictionary<string, object>)Json.Parse(text);
        Assert.Equal((double)L.Spans.Count, (double)((List<object>)back["columns"]).Count);
    }
}

public class PreviewApiTests
{
    [Fact]
    public void GenerateThenExport()
    {
        var api = new PreviewApi();
        var r = api.Handle("POST", "/api/generate", new Dictionary<string, string>(),
            "{\"preset\":\"compact\",\"seed\":5,\"dials\":{\"scale\":0.3},\"settings\":{\"keys\":0}}");
        Assert.Equal(200, r.Status);
        var j = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(r.Body));
        var id = ((double)j["id"]).ToString();
        Assert.Equal(0.0, (double)((Dictionary<string, object>)j["settings"])["keys"]);
        var glb = api.Handle("GET", "/api/level.glb", new Dictionary<string, string> { ["id"] = id }, null);
        Assert.Equal("model/gltf-binary", glb.ContentType);
        var ex = api.Handle("GET", "/api/export", new Dictionary<string, string> { ["id"] = id, ["format"] = "obj" }, null);
        Assert.EndsWith(".zip", ex.FileName);
    }

    [Fact]
    public void InfoListsPresetsDialsFormats()
    {
        var info = PreviewApi.Info();
        Assert.Equal(Recipes.Presets.Count + 1, ((System.Collections.IList)info["presets"]).Count);   // + surprise
        Assert.Equal(10, ((string[])info["dials"]).Length);
        Assert.True(((System.Collections.IList)info["formats"]).Count >= 9);
    }
}

public class SlopeWaterExportTests
{
    /// <summary>A level with a vaulted ceiling, a ramp, water, a lava pool and spikes injected
    /// (until the modules that make them are in): every exporter must handle them.</summary>
    public static Level Dressed()
    {
        var level = Generator.Generate("classic", 1);
        var room = level.Rooms.Where(r => !r.IsConnector).OrderByDescending(r => r.W * r.H).First();
        var cells = room.Cells().Where(c => level.Spans.TryGetValue(c, out var s) && s.Count == 1 && !level.Diag.ContainsKey(c)).ToList();
        foreach (var c in cells)
        {
            var sp = level.Spans[c][0];
            int u = c.X - room.X;
            // a barrel vault along y: low at the walls, high in the middle (the span keeps the lowest point)
            int h(int x) => sp.Ceil + (int)(64 * Math.Sin(Math.PI * Math.Clamp((double)x / room.W, 0, 1)));
            level.CeilSlope[c] = (h(u), h(u + 1), h(u + 1), h(u));
        }
        var ramp = cells.Where(c => c.Y == room.Y + 1).OrderBy(c => c.X).Take(4).ToList();
        for (int i = 0; i < ramp.Count; i++)
        {
            var c = ramp[i]; var sp = level.Spans[c][0];
            level.FloorSlope[c] = (sp.Floor - 16 + 4 * i, sp.Floor - 12 + 4 * i, sp.Floor - 12 + 4 * i, sp.Floor - 16 + 4 * i);
        }
        foreach (var c in cells.Where(c => c.Y == room.Y + room.H / 2).Take(5)) level.Water[c] = level.Spans[c][0].Floor + 40;
        foreach (var c in cells.Where(c => c.Y == room.Y + room.H / 2 + 2).Take(3)) level.Hazard[c] = "lava";
        foreach (var c in cells.Where(c => c.Y == room.Y + room.H / 2 + 3).Take(3)) level.Hazard[c] = "spikes";
        return level;
    }

    [Fact]
    public void EveryFormatHandlesSlopesAndWater()
    {
        var level = Dressed();
        foreach (var f in Exporters.Formats) Assert.True(Exporters.Export(level, f.Id, out _).Length > 1000, f.Id);
        var map = MapExport.ToMap(level, MapGame.Quake);
        Assert.Contains("*water0", map);
        Assert.Contains("*lava1", map);
        Assert.Contains("trigger_hurt", map);
        Assert.Equal(map.Count(c => c == '{'), map.Count(c => c == '}'));
        var dump = Environment.GetEnvironmentVariable("NOORLEVEL_DUMP");
        if (!string.IsNullOrEmpty(dump))
            foreach (var g in new[] { MapGame.Quake, MapGame.Quake2, MapGame.Valve220 })
                File.WriteAllText(Path.Combine(dump, $"dressed_{g}.map"), MapExport.ToMap(level, g));
    }
}

public class PillarSwitchTests
{
    public static IEnumerable<object[]> Presets() => Recipes.Presets.Keys.Select(p => new object[] { p });

    /// <summary>Tom: "those pillars can be really annoying". With Pillars off no preset has a free-standing
    /// column of any kind: room pillars, yard colonnades, piers under vaults, corridor colonnades, the
    /// landmark's column fallback.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void PillarsOffMeansNoColumns(string preset)
    {
        foreach (var seed in new[] { 1, 2 })
        {
            var cfg = Recipes.Compile(Recipes.Presets[preset], seed);
            cfg.Pillars = false;
            var level = Generator.Generate(cfg);
            Assert.True(level.Report.Ok);
            Assert.DoesNotContain(level.PartSolid.Values, p => p.EndsWith("/pillars") || p.EndsWith("/piers") || p.EndsWith("/colonnade"));
            Assert.DoesNotContain(level.Rooms, r => r.Features.Contains("pillars") || r.Features.Contains("colonnade"));
            Assert.All(level.Corridors.SelectMany(c => c.Beats), b => Assert.Equal(0, (int)b["pillars"]));
            Assert.DoesNotContain(level.Rooms, r => r.Meta.TryGetValue("landmark", out var lm) && lm is Dictionary<string, object> d && d.TryGetValue("shape", out var s) && (string)s == "column");
        }
    }
}

public class NoorEngineExportTests
{
    /// <summary>The NoorEngine project: a manifest the engine accepts, a scene whose ids are unique UUIDs with
    /// parents that exist, one mesh entity (with a collider) per glTF mesh, the player, and Coth with a route.</summary>
    [Theory]
    [InlineData("dungeon"), InlineData("wizard"), InlineData("grand")]
    public void ProjectIsComplete(string preset)
    {
        var level = Generator.Generate(preset, 1);
        var files = NoorEngineExport.Project(level).ToDictionary(f => f.Path, f => f.Data);
        Assert.Contains(files.Keys, k => k.EndsWith(".noorproj"));
        var manifest = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(files.First(f => f.Key.EndsWith(".noor.json")).Value));
        Assert.Equal("level", manifest["kind"]);
        Assert.Equal("NoorLevel", ((Dictionary<string, object>)manifest["generator"])["tool"]);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", (string)manifest["id"]);
        var glb = files.First(f => f.Key.EndsWith(".glb")).Value;
        int jlen = BitConverter.ToInt32(glb, 12);
        var gltf = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(glb, 20, jlen));
        int meshes = ((List<object>)gltf["meshes"]).Count;
        var scene = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(files.First(f => f.Key.EndsWith(".noorscene")).Value));
        var ents = ((List<object>)scene["entities"]).Cast<Dictionary<string, object>>().ToList();
        var ids = ents.Select(e => (string)e["id"]).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ents, e => Assert.True(e["parent"] == null || ids.Contains((string)e["parent"])));
        var comps = ents.Select(e => (Dictionary<string, object>)e["components"]).ToList();
        Assert.Equal(meshes, comps.Count(c => c.ContainsKey("mesh")));
        Assert.All(comps.Where(c => c.ContainsKey("mesh")), c => Assert.True((double)((Dictionary<string, object>)c["mesh"])["mesh_index"] < meshes));
        Assert.Single(ents, e => (string)e["name"] == "Player");
        var coth = ents.Single(e => (string)e["name"] == "Coth");
        Assert.Equal(NoorEngineExport.CothSheet, ((Dictionary<string, object>)((Dictionary<string, object>)coth["components"])["sprite"])["sheet"]);
        var route = Encoding.UTF8.GetString(files["Scripts/coth_wander.luau"]);
        Assert.True(route.Split('\n').Count(l => l.TrimStart().StartsWith("{ ")) > 20, "Coth's route is too short");
    }
}

public class ParameterTests
{
    /// <summary>Every documented parameter is a real GenConfig field, and the API sets any of them by name.</summary>
    [Fact]
    public void EveryDocumentedParameterExists()
    {
        foreach (var p in ConfigDocs.All) Assert.True(Recipes.ConfigField(p.Name) != null, p.Name);
        var cfg = Recipes.Compile(Recipes.Presets["classic"], 1);
        Recipes.SetFields(cfg, new Dictionary<string, object> { ["arch_style"] = "egyptian", ["storeys"] = 5.0, ["stacking"] = 0.8,
            ["feature_weights"] = new Dictionary<string, object> { ["pillars"] = 0.0 } }, merge: true);
        Assert.Equal("egyptian", cfg.ArchStyle);
        Assert.Equal(5, cfg.Storeys);
        Assert.Equal(0.0, cfg.FeatureWeights["pillars"]);
        Assert.True(cfg.FeatureWeights.ContainsKey("none"));      // merged, not replaced
    }

    [Theory]
    [InlineData("classic", 80), InlineData("dungeon", 120), InlineData("industrial", 160)]
    public void MapSizeFitsTheLevel(string preset, int size)
    {
        var cfg = Recipes.Compile(Recipes.Presets[preset], 2);
        cfg.MapSize = size;
        var level = Generator.Generate(cfg);
        Assert.True(level.Report.Ok);
        int got = Generator.Footprint(level);
        Assert.InRange(got, (int)(size * 0.6), (int)(size * 1.35));
    }

    [Fact]
    public void SixStoreysAndStackingMakeValidLevels()
    {
        foreach (var seed in new[] { 1, 2 })
        {
            var cfg = Recipes.Compile(Recipes.Presets["fortress"], seed);
            cfg.Storeys = 6; cfg.Stacking = 1.0;
            var level = Generator.Generate(cfg);
            Assert.True(level.Report.Ok);
            Assert.True(level.Rooms.Select(r => r.Storey).Distinct().Count() >= 4);
        }
    }
}
