using System.Collections.Generic;
using System.Linq;
using NoorLevel;
using Xunit;

public class RecipeTests
{
    [Fact]
    public void AllPythonPresetsArePorted()
    {
        var names = new[] { "compact", "classic", "bunker", "fortress", "industrial", "compound", "techbase", "runekeep", "outpost",
                            "grand", "facility", "complex", "dungeon", "wizard", "market", "arena", "ctf", "duel", "explore" };
        Assert.Equal(names.OrderBy(n => n), Recipes.Presets.Keys.OrderBy(n => n));
    }

    [Fact]
    public void OverridesWinAndDictsMerge()
    {
        var cfg = Recipes.Compile(Recipes.Presets["bunker"], 1);
        Assert.Equal("bunker", cfg.ArchStyle);
        Assert.True(cfg.Underground);
        Assert.Equal("linear", cfg.Composition);
        Assert.Equal(0.0, cfg.FeatureWeights["balcony"]);
        Assert.True(cfg.FeatureWeights.ContainsKey("platform"));       // merged into the dial-made weights
        Assert.Equal(2.0, cfg.MegaStyles["hangar"]);
        var market = Recipes.Compile(Recipes.Presets["market"], 1);
        Assert.Equal((22, 30, 320, 320), market.RoomSizes["yard"]);
        var arena = Recipes.Compile(Recipes.Presets["arena"], 1);
        Assert.Equal("arena", arena.Mode);
        Assert.Equal(1.5, arena.JumpPads);
    }

    [Fact]
    public void UnknownOverrideThrows()
    {
        var r = Recipes.Presets["classic"].Copy();
        r.Overrides["no_such_field"] = 1;
        Assert.Throws<System.ArgumentException>(() => Recipes.Compile(r, 1));
    }

    [Fact]
    public void BigSpacesPickCentralComposition()
    {
        var cfg = Recipes.Compile(Recipes.Presets["grand"], 1);
        Assert.Equal("central", cfg.Composition);
        Assert.True(cfg.PacingContrast);
        Assert.Equal(192, cfg.ArchHeadroom);                         // spaciousness 1
        Assert.True(cfg.ArchWeights["dome"] > 0);                     // variety
    }

    [Fact]
    public void SurpriseIsStableAndValid()
    {
        var a = Recipes.Surprise(42);
        var b = Recipes.Surprise(42);
        Assert.Equal(a.Label, b.Label);
        Assert.StartsWith("surprise: " + a.Recipe.Name, a.Label);
        var cfg = Recipes.Compile(a.Recipe, 42);
        Recipes.SetFields(cfg, a.Extras);
    }
}

public class DecorateTests
{
    static Level FirstWith(string preset, System.Func<Level, bool> pred)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var level = Generator.Generate(preset, seed);
            Assert.True(level.Report.Ok);
            if (pred(level)) return level;
        }
        return null;
    }

    [Fact]
    public void JumpPadTowersHaveAPadAndAOneWayLink()
    {
        var level = FirstWith("duel", l => l.Entities.Any(e => e.Kind == "jumppad"));
        Assert.NotNull(level);
        var pad = level.Entities.First(e => e.Kind == "jumppad");
        Assert.NotNull(pad.Bounds);
        Assert.Contains(level.Links, k => k.Kind == "jumppad" && !k.TwoWay && k.Interaction == "jump");
        Assert.Contains(level.Rooms, r => r.Features.Contains("tower") && r.Meta.ContainsKey("towers"));
    }

    [Fact]
    public void DungeonsGetChasmsWithSpikes()
    {
        var level = FirstWith("dungeon", l => l.Rooms.Any(r => r.Features.Contains("chasm")));
        Assert.NotNull(level);
        Assert.Contains(level.Hazard.Values, h => h == "spikes");
    }

    [Fact]
    public void TechbasesGetSlimePools()
    {
        var level = FirstWith("techbase", l => l.Rooms.Any(r => r.Features.Contains("hazard")));
        Assert.NotNull(level);
        Assert.Contains(level.Hazard.Values, h => h == "slime");
    }

    [Fact]
    public void ArenaLevelsHaveSpawnsNotKeys()
    {
        var level = Generator.Generate("arena", 1);
        Assert.True(level.Entities.Count(e => e.Kind == "spawn") >= 2);
        Assert.DoesNotContain(level.Entities, e => e.Kind == "key");
    }

    [Fact]
    public void UndergroundStartIsOpenToTheSky()
    {
        var level = Generator.Generate("bunker", 1);
        var start = level.Rooms.First(r => r.Kind == "start");
        Assert.Contains("surface", start.Features);
        Assert.NotEmpty(level.Sky);
    }
}
