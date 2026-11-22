using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NoorLevel;
using NoorLevel.Export;
using Xunit;

/// <summary>layout_style = "station" (Station.cs): a deep-sea station's rules, for every spine, seeds 1-8 and
/// map sizes 48-72; and the corridor profiles (CorridorProfiles.cs), in stations and outside them.</summary>
public class StationLayoutTests
{
    static GenConfig Station(int seed, string spine = "auto", int size = 56, Action<GenConfig> tweak = null)
    {
        var cfg = Recipes.Compile(Recipes.Presets["station"], seed);
        cfg.Spine = spine;
        cfg.MapSize = size;
        tweak?.Invoke(cfg);
        return cfg;
    }

    public static IEnumerable<object[]> Cases() =>
        from spine in new[] { "line", "ring", "tree" }
        from size in new[] { 48, 56, 64, 72 }
        from seed in Enumerable.Range(1, 8)
        select new object[] { spine, size, seed };

    [Theory]
    [MemberData(nameof(Cases))]
    public void StationRulesHold(string spine, int size, int seed)
    {
        var level = Generator.Generate(Station(seed, spine, size));
        Assert.True(level.Report.Ok);
        Assert.Equal(spine, level.Config.Spine);
        Assert.Empty(NoorLevel.Station.Check(level));      // all of the below, in one place

        // everything is reachable from the airlock (doors open)
        var start = level.Entities.Single(e => e.Kind == "player_start");
        var reach = Validator.Flood(level, Validator.NodeAt(level, start.Pos).Value, new HashSet<Cell>());
        Assert.Empty(Validator.RoomsUnreached(level, reach));
        // the airlock is on the top deck, the core alone at the deepest point
        var airlock = level.Rooms.Single(r => r.Kind == "start");
        var core = level.Rooms.Single(r => r.Kind == "exit");
        Assert.Equal(level.Rooms.Max(r => r.Floor), airlock.Floor);
        Assert.All(level.Rooms.Where(r => r != core), r => Assert.True(r.Floor > core.Floor));
        Assert.Equal(core.Floor, level.Spans.Values.SelectMany(s => s).Min(s => s.Floor));
        Assert.InRange(level.Rooms.Select(r => r.Floor / NoorLevel.Station.Deck).Distinct().Count(), 2, 4);
        // tubes: at least 2 cells long, 3 wide, profiled (with sloped ceiling cells)
        Assert.All(level.Corridors, c => Assert.True(c.Path.Count >= 2, $"tube {c.Id}: {c.Path.Count} cells"));
        Assert.All(level.Corridors, c => Assert.Equal("round", c.Profile));
        Assert.All(level.Corridors, c => Assert.Equal(1, c.Radius));
        Assert.Contains(level.CeilSlope.Keys, c => (level.OwnerOf(c, level.Spans[c][0]) ?? "").StartsWith("corridor:"));
        // no lifts, ladders or spirals
        Assert.DoesNotContain(level.Rooms, r => r.IsConnector);
        Assert.Empty(level.Links);
        Assert.DoesNotContain(level.Entities, e => e.Kind == "lift" || e.Kind == "ladder");
        // pods are round or octagonal, 8-14 cells across
        Assert.All(level.Rooms, r => Assert.Contains(r.Shape, new[] { "round", "octagon" }));
        Assert.All(level.Rooms, r => Assert.InRange(r.W, 8, 14));
        // keys before their doors, one secret
        Assert.Equal(level.Graph.Edges.Count(e => e.Kind == "locked"), level.Entities.Count(e => e.Kind == "key"));
        Assert.Single(level.Entities, e => e.Kind == "secret_door");
        // every tube end into a pod is a bulkhead
        Assert.True(level.Entities.Count(e => e.Kind == "bulkhead") >= level.Corridors.Count);
    }

    [Fact]
    public void RingIsClosedRoundItsHub()
    {
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var level = Generator.Generate(Station(seed, "ring"));
            var g = level.Graph;
            Assert.NotNull(g.Center);
            var close = g.Edges.Single(e => e.Role == "ring");
            Assert.True(close.Required);
            Assert.Contains(level.Corridors, c => c.Edge == close.Id);          // the ring is closed
            Assert.Contains(level.Corridors, c => g.Edges[c.Edge].Role == "spoke");
        }
    }

    [Fact]
    public void SameSeedSameStation()
    {
        string Ir(Level l) => Json.Write(LevelJson.ToIr(l));
        Assert.Equal(Ir(Generator.Generate("station", 5)), Ir(Generator.Generate("station", 5)));
        Assert.NotEqual(Ir(Generator.Generate("station", 5)), Ir(Generator.Generate("station", 6)));
    }

    [Theory]
    [InlineData("research"), InlineData("mining"), InlineData("military"), InlineData("derelict")]
    public void EveryStyleMakesAStation(string style)
    {
        foreach (var seed in new[] { 1, 2 })
        {
            var level = Generator.Generate(Station(seed, tweak: c => c.StationStyle = style));
            Assert.True(level.Report.Ok);
            Assert.Empty(NoorLevel.Station.Check(level));
            Assert.Equal(NoorLevel.Station.ArchFor(style), level.Config.ArchStyle);
            Assert.Contains(level.Notes, n => n.StartsWith($"station: style={style} "));
            Assert.Equal(style, (string)NoorLevel.Station.RootExtras(level)["style"]);
        }
    }

    [Theory]
    [InlineData("moon_pool"), InlineData("dome_lab"), InlineData("flooded_wing"), InlineData("collapsed"), InlineData("hydroponics")]
    public void CharacterRooms(string kind)
    {
        bool seen = false;
        foreach (var seed in Enumerable.Range(1, 6))
        {
            var level = Generator.Generate(Station(seed, "tree", 64, c =>
            {
                c.CharacterRooms = 1;
                c.CharacterWeights = new Dictionary<string, double> { [kind] = 1.0 };
            }));
            Assert.True(level.Report.Ok);
            Assert.Empty(NoorLevel.Station.Check(level));
            var room = level.Rooms.FirstOrDefault(r => r.Get<string>("station_room") == kind);
            switch (kind)
            {
                case "moon_pool":       // a bowl of water in the floor, stepped so you can walk out
                    if (room == null) continue;
                    var pool = room.Cells().Where(c => level.Water.ContainsKey(c)).ToList();
                    Assert.NotEmpty(pool);
                    Assert.All(pool, c => Assert.True(level.Spans[c][0].Floor < room.Floor));
                    Assert.True(pool.Min(c => level.Spans[c][0].Floor) >= room.Floor - 64);
                    break;
                case "dome_lab":        // a tall round pod under the dome profile
                    if (room == null) continue;
                    Assert.Equal("round", room.Shape);
                    Assert.Equal("dome", (string)((Dictionary<string, object>)room.Meta["arch"])["profile"]);
                    break;
                case "flooded_wing":    // water 1 m deep over its floor, down a few steps from its parent
                    if (room == null || !room.Meta.ContainsKey("flooded")) continue;
                    Assert.All(room.Cells().Where(c => !level.Diag.ContainsKey(c) && level.Spans.TryGetValue(c, out var sp) && sp[0].Floor == room.Floor),
                               c => Assert.Equal(room.Floor + Floods.Wade, level.Water[c]));
                    break;
                case "collapsed":       // an extra tube, caved in half way: two dead ends
                    if (!level.PartSolid.Values.Any(p => p.EndsWith("/collapse"))) continue;
                    Assert.Contains(level.Corridors, c => c.Kind == "collapsed");
                    break;
                case "hydroponics":
                    if (room == null) continue;
                    Assert.Contains("hydroponics", room.Features);
                    break;
            }
            seen = true;
        }
        Assert.True(seen, $"no {kind} in six seeds");
    }

    [Fact]
    public void SurpriseNeverDrawsTheStation()
    {
        for (int seed = 0; seed < 200; seed++) Assert.NotEqual("station", Recipes.Surprise(seed).Recipe.Name);
    }

    // ------------------------------------------------------------------------------ corridor profiles
    [Theory]
    [InlineData("classic", "round"), InlineData("classic", "chamfer"), InlineData("facility", "round"), InlineData("dungeon", "chamfer")]
    public void ProfilesWorkOutsideStations(string preset, string profile)
    {
        foreach (var seed in new[] { 1, 2 })
        {
            var plain = Generator.Generate(preset, seed);
            var cfg = Recipes.Compile(Recipes.Presets[preset], seed);
            cfg.CorridorProfile = profile;
            var level = Generator.Generate(cfg);
            Assert.True(level.Report.Ok);
            // the same level (the profile draws nothing and never fails a level), only the corridors' cross-section changed
            Assert.Equal(plain.Rooms.Count, level.Rooms.Count);
            Assert.Equal(plain.Spans.Keys.OrderBy(c => c), level.Spans.Keys.OrderBy(c => c));
            Assert.All(level.Corridors.Where(c => !c.Vent), c => Assert.Equal(profile, c.Profile));
            var sloped = level.CeilSlope.Keys.Where(c => !plain.CeilSlope.ContainsKey(c)).ToList();
            Assert.NotEmpty(sloped);
            foreach (var c in sloped)
            {
                var (a, b, cc, d) = level.CeilSlope[c];
                Assert.Equal(a + cc, b + d);                                    // planar: the .map compiler needs it
                Assert.True(level.Spans[c].Last().Ceil <= Math.Min(Math.Min(a, b), Math.Min(cc, d)));   // the span keeps the lowest point
            }
            if (profile == "chamfer") Assert.DoesNotContain(level.FloorSlope.Keys, c => !plain.FloorSlope.ContainsKey(c));
        }
    }

    [Fact]
    public void BoxIsTheDefaultAndChangesNothing()
    {
        var cfg = new GenConfig();
        Assert.Equal("box", cfg.CorridorProfile);
        Assert.Equal("", cfg.LayoutStyle);
        var level = Generator.Generate("classic", 3);
        Assert.All(level.Corridors, c => Assert.Equal("box", c.Profile));
        Assert.DoesNotContain(level.Notes, n => n.StartsWith("corridor profiles"));
    }

    [Fact]
    public void ProfileShapes()
    {
        // a 3-wide corridor: the wall corners come down, the axis stays at full height
        Assert.Equal(32, CorridorProfiles.Drop("chamfer", 0, 1.5, 32));
        Assert.Equal(0, CorridorProfiles.Drop("chamfer", 1, 1.5, 32));
        Assert.Equal(32, CorridorProfiles.Drop("round", 0, 1.5, 32));
        Assert.Equal(0, CorridorProfiles.Drop("box", 0, 1.5, 32));
        // a 5-wide one: round curves over two cells, chamfer cuts one
        Assert.InRange(CorridorProfiles.Drop("round", 1, 2.5, 64), 8, 20);
        Assert.Equal(0, CorridorProfiles.Drop("chamfer", 1, 2.5, 32));
    }

    [Fact]
    public void StationMapExportHasSlopedTubeCeilings()
    {
        var level = Generator.Generate("station", 1);
        string map = MapExport.ToMap(level, MapGame.Quake);
        Assert.True(level.CeilSlope.Count > 0);
        Assert.Contains("classname", map);
        var glb = Gltf.ToGlb(level);
        Assert.True(glb.Length > 1000);
    }
}
