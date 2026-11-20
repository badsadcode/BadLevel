using System;
using System.Collections.Generic;
using System.Linq;
using NoorLevel;
using Xunit;

/// <summary>Group C features (openings, vistas, ramps, dungeon, floods) show up in the presets that ask for them.</summary>
public class OpeningsDungeonTests
{
    static readonly Dictionary<(string, int), Level> Cache = new Dictionary<(string, int), Level>();
    static Level Gen(string preset, int seed)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((preset, seed), out var l)) Cache[(preset, seed)] = l = Generator.Generate(preset, seed);
            return l;
        }
    }

    [Theory]
    [InlineData("fortress")]
    [InlineData("industrial")]
    [InlineData("grand")]
    [InlineData("compound")]
    public void WindowsAreCut(string preset)
    {
        var l = Gen(preset, 1);
        var wins = l.Entities.Where(e => e.Kind == "window").ToList();
        Assert.NotEmpty(wins);
        foreach (var w in wins)
        {
            Assert.NotNull(w.Bounds);
            Assert.True(w.Props.ContainsKey("glazed"));
        }
        // every glazed cell is an open span of a window
        foreach (var c in l.Glass)
            Assert.Contains(l.Owners[c], o => o.Name.StartsWith("window:"));
    }

    [Fact]
    public void GlazedWindowsGoInGlass()
    {
        var l = Gen("industrial", 1);   // glazed_chance 0.65
        Assert.Contains(l.Entities, e => e.Kind == "window" && (bool)e.Props["glazed"]);
        Assert.NotEmpty(l.Glass);
    }

    [Fact]
    public void FortressHasSlitsOnly()
    {
        var l = Gen("fortress", 2);     // glazed_chance 0
        Assert.Empty(l.Glass);
        foreach (var w in l.Entities.Where(e => e.Kind == "window"))
        {
            Assert.False((bool)w.Props["glazed"]);
            Assert.Equal(Openings.Slit, w.Bounds.Value.Hi.Z - w.Bounds.Value.Lo.Z);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GrandHasVistasOntoSkyAndHills(int seed)
    {
        var l = Gen("grand", seed);
        Assert.Contains(l.Rooms, r => r.Features.Contains("vista"));
        Assert.Contains(l.Entities, e => e.Kind == "window" && e.Props.ContainsKey("vista"));
        Assert.NotEmpty(l.Sky);
        Assert.NotEmpty(l.SkyWalls);
        Assert.Contains(l.PartCells.Values, p => p.StartsWith("vista:") && p.EndsWith("/terrain"));
        Assert.True(l.FloorSlope.Count > 100);
    }

    [Fact]
    public void GrandStairsBecomeRamps()
    {
        int ramps = new[] { 1, 2, 3 }.Sum(s => Gen("grand", s).Notes.Count(n => n.StartsWith("ramps:")));
        Assert.True(ramps > 0);
        // and nowhere else (ramps = 0 by default)
        Assert.DoesNotContain(Gen("classic", 1).Notes, n => n.StartsWith("ramps:"));
        Assert.Empty(Gen("classic", 1).FloorSlope);
    }

    [Theory]
    [InlineData("dungeon")]
    [InlineData("wizard")]
    public void TorchesAndGrates(string preset)
    {
        var l = Gen(preset, 1);
        Assert.True(l.Entities.Count(e => e.Kind == "torch") >= 10);
        Assert.Contains(l.Rooms, r => r.Features.Contains("grate"));
        Assert.Contains(l.PartCeiling.Values, p => p.EndsWith("/grate"));
        Assert.Contains(l.Entities, e => e.Kind == "light" && e.Props.ContainsKey("shaft"));
    }

    [Fact]
    public void DungeonHasTrapsAndWater()
    {
        int traps = 0, water = 0, floods = 0, sumps = 0;
        foreach (var s in new[] { 1, 2, 3 })
        {
            var l = Gen("dungeon", s);
            traps += l.Entities.Count(e => e.Kind == "trap");
            water += l.Water.Count;
            floods += l.Rooms.Count(r => r.Features.Contains("flooded"));
            sumps += l.Corridors.Count(c => c.Sump);
            Assert.Empty(Floods.BreathReport(l));
            Assert.True(l.Report.Ok, string.Join("; ", l.Report.Errors));
        }
        Assert.True(traps > 0);
        Assert.True(water > 0);
        Assert.True(floods + sumps > 0);
        Assert.Empty(Gen("classic", 1).Water);
    }

    [Fact]
    public void GalleriesAppear()
    {
        int g = new[] { ("facility", 1), ("facility", 2), ("dungeon", 1), ("wizard", 2) }
            .Sum(t => Gen(t.Item1, t.Item2).Rooms.Count(r => r.Features.Contains("gallery")));
        Assert.True(g > 0);
        var corr = new[] { ("facility", 1), ("dungeon", 1) }.SelectMany(t => Gen(t.Item1, t.Item2).Corridors);
        Assert.Contains(corr, c => c.Connector == "gallery");
    }

    [Fact]
    public void BreathReportFailsALongSwim()
    {
        var l = new Level { CellSize = 32 };
        for (int x = 0; x < 20; x++)
        {
            var c = new Cell(x, 0);
            l.AddSpan(c, new Span(0, 80));
            l.Water[c] = 80;                // no air anywhere along it
        }
        Assert.Single(Floods.BreathReport(l));
        l.Water[new Cell(7, 0)] = 32;       // a breath on the way
        Assert.Empty(Floods.BreathReport(l));
    }
}
