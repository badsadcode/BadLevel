using System;
using System.Collections.Generic;
using System.Linq;
using NoorLevel;
using Xunit;
using Xunit.Abstractions;

public class LayoutTests
{
    readonly ITestOutputHelper output;
    public LayoutTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void MergedHallsHappen()
    {
        int halls = 0, merged = 0, terraces = 0, levels = 0;
        foreach (var preset in Recipes.Presets.Keys)
            for (int seed = 1; seed <= 3; seed++)
            {
                var level = Generator.Generate(preset, seed);
                levels++;
                halls += level.Halls.Count;
                merged += level.Graph.Edges.Count(e => e.Merged);
                terraces += level.Rooms.Count(r => r.Features.Contains("terrace"));
                // a merged edge has no corridor; its rooms touch along a seam and share a hall
                foreach (var e in level.Graph.Edges.Where(e => e.Merged))
                {
                    Assert.DoesNotContain(level.Corridors, c => c.Edge == e.Id);
                    Assert.True(HallSpace.SeamOk(level.Rooms[e.A], level.Rooms[e.B]));
                    Assert.Contains(level.Halls, h => h.Contains(e.A) && h.Contains(e.B));
                }
                foreach (var h in level.Halls) Assert.True(h.Count <= level.Config.MaxHallRooms);
                var attempts = level.Notes.First(n => n.StartsWith("attempts:"));
                output.WriteLine($"{preset} {seed}: halls={level.Halls.Count} {attempts}");
            }
        output.WriteLine($"{levels} levels, {halls} halls, {merged} merged edges, {terraces} terraced rooms");
        Assert.True(halls > 0, "no merged halls in any preset");
    }

    [Fact]
    public void RegistryKnowsEveryKind()
    {
        foreach (var k in new[] { "start", "combat", "arena", "hub", "supply", "key", "secret", "exit", "overlook",
                                  "stairwell", "lift", "ladder", "spiral", "courtyard", "megaroom", "tower" })
            Assert.NotNull(Spaces.SpaceFor(k));
        Assert.Equal(new[] { "stairwell", "lift", "ladder", "spiral", "tower" }, Spaces.VerticalKinds());
    }

    [Fact]
    public void VariantsDiffer()
    {
        var cfg = Recipes.Compile(Recipes.Presets["classic"], 5);
        var a = Generator.Generate(cfg);
        var c2 = Recipes.Compile(Recipes.Presets["classic"], 5);
        c2.Variant = 1;
        var b = Generator.Generate(c2);
        Assert.NotEqual(a.Rooms.Select(r => (r.X, r.Y, r.W, r.H)), b.Rooms.Select(r => (r.X, r.Y, r.W, r.H)));
    }

    [Theory]
    [InlineData("hub"), InlineData("central"), InlineData("linear")]
    public void CompositionsBuild(string comp)
    {
        var level = Generator.Generate("classic", 4, r => r.Overrides = c => c.Composition = comp);
        Assert.True(level.Report.Ok);
    }

    [Fact]
    public void ArenaModeAndPlaceholderSpacesBuild()
    {
        // arena graphs (no start/exit nodes, extra loops) build their layout; courtyards and mega-rooms
        // are placeholders until spaces/open_area.py and spaces/mega.py are ported
        var cfg = Recipes.Compile(Recipes.Presets["classic"], 2);
        cfg.Mode = "arena"; cfg.Keys = 0; cfg.Secret = false; cfg.OpenAreas = 1; cfg.MegaRooms = 1;
        var rng = Rng.FromLabel("arena-test");
        for (int i = 0; i < 20; i++)
        {
            try
            {
                var g = MissionGraph.Build(cfg, rng);
                Assert.DoesNotContain(g.Nodes, n => n.Kind == "start" || n.Kind == "exit");
                var level = Layout.BuildLayout(g, cfg, rng, cfg.Seed);
                Assert.Contains(level.Rooms, r => r.Kind == "megaroom");
                Assert.Contains(level.Rooms, r => r.Kind == "courtyard");
                return;
            }
            catch (LayoutException) { }
        }
        Assert.Fail("no arena layout in 20 attempts");
    }

    public static IEnumerable<object[]> Levers() => new[]
    {
        new object[] { "beat_shapes" }, new object[] { "diagonal_pull" }, new object[] { "pacing_contrast" },
        new object[] { "vents" }, new object[] { "yard_setback" }, new object[] { "merge" },
    };

    [Theory]
    [MemberData(nameof(Levers))]
    public void LayoutLeversBuild(string lever)
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            var level = Generator.Generate("classic", seed, r => r.Overrides = c =>
            {
                switch (lever)
                {
                    case "beat_shapes": c.BeatShapes = 1.0; c.BeatSpacing = 10; break;
                    case "diagonal_pull": c.DiagonalPull = 1.0; c.DiagonalChance = 0.8; break;
                    case "pacing_contrast": c.PacingContrast = true; c.OpenAreas = 1; break;
                    case "vents": c.Vents = 1.0; c.Loops = 4; break;
                    case "yard_setback": c.YardSetback = 3; c.OpenAreas = 1; break;
                    case "merge": c.MergeChance = 1.0; c.MaxHallRooms = 4; break;
                }
            });
            Assert.True(level.Report.Ok);
            output.WriteLine($"{lever} {seed}: beats={level.Corridors.Sum(c => c.Beats.Count)} vents={level.Corridors.Count(c => c.Vent)} " +
                             $"diag={level.Corridors.Count(c => c.Diagonal)} halls={level.Halls.Count} {level.Notes.Last()}");
        }
    }

    [Fact]
    public void UndergroundGoesDown()
    {
        var cfg = Recipes.Compile(Recipes.Presets["fortress"], 3);
        cfg.Underground = true;
        var g = MissionGraph.Build(cfg, Rng.FromLabel("deep"));
        var main = g.Nodes.Where(n => n.Main).ToList();
        Assert.Equal(cfg.Storeys - 1, main[0].Storey);
        for (int i = 1; i < main.Count; i++) Assert.True(main[i].Storey <= main[i - 1].Storey);
    }
}
