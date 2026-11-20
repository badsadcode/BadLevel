using System.Collections.Generic;
using System.Linq;
using NoorLevel;
using Xunit;

/// <summary>Arena, mirror (team maps), tower, market and programs show up in the presets that use them.</summary>
public class GroupETests
{
    public static IEnumerable<object[]> Seeds() => new[] { 1, 2, 3 }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ArenaSpreadsSpawnsAndKeepsItsFacts(int seed)
    {
        foreach (var preset in new[] { "arena", "duel" })
        {
            var level = Generator.Generate(preset, seed);
            var spawns = level.Entities.Where(e => e.Kind == "spawn").ToList();
            Assert.True(spawns.Count >= 4, $"{preset}/{seed}: {spawns.Count} spawns");
            Assert.All(spawns, e => Assert.Equal(0, (int)e.Props["angle"] % 45));
            Assert.Contains(level.Entities, e => e.Kind == "jumppad");          // the guaranteed high ground
            Assert.DoesNotContain(level.Entities, e => e.Kind == "weapon" || e.Kind == "quad" || e.Kind == "health");
            Assert.True(level.Arena.ContainsKey("fairness"));
            Assert.Equal(spawns.Count, (int)level.Arena["spawns"]);
            Assert.NotEmpty((List<Dictionary<string, object>>)level.Arena["weapon_spots"]);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void CtfIsTwoMirroredHalvesWithFlagsAndTeamSpawns(int seed)
    {
        var level = Generator.Generate("ctf", seed);
        var flags = level.Entities.Where(e => e.Kind == "flag").ToList();
        Assert.Equal(2, flags.Count);
        Assert.Equal(new[] { 1, 2 }, flags.Select(f => (int)f.Props["team"]).OrderBy(t => t));
        int seam = (int)level.Arena["seam"];
        double sx = seam * level.CellSize;
        Assert.Equal(2 * sx - flags[0].Pos.X, flags[1].Pos.X, 3);           // mirror images across the seam
        var spawns = level.Entities.Where(e => e.Kind == "spawn").ToList();
        Assert.True(spawns.Count(e => (int)e.Props["team"] == 1) >= 4);
        Assert.Equal(spawns.Count(e => (int)e.Props["team"] == 1), spawns.Count(e => (int)e.Props["team"] == 2));
        Assert.Equal(0, level.Rooms.Count % 2);
        Assert.Contains(level.Rooms, r => r.Meta.ContainsKey("mirror_of"));
        Assert.True((int)level.Arena["lanes"] >= 2);
        Assert.Contains(level.PartCells.Values, v => v.EndsWith("/lane"));
        // every column has its mirror twin
        foreach (var c in level.Spans.Keys) Assert.True(level.Spans.ContainsKey(new Cell(2 * seam - 1 - c.X, c.Y)));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void WizardHasAHelixTowerAsItsSpine(int seed)
    {
        var level = Generator.Generate("wizard", seed);
        var tower = Assert.Single(level.Rooms, r => r.Kind == "tower");
        Assert.Equal(level.Graph.Center, tower.Node);
        Assert.Contains(level.PartCells.Values, v => v == $"room:{tower.Id}/helix");
        Assert.True(tower.Cells().Count(c => level.Spans[c].Count > 1) > 20, "the helix laps stack");
        Assert.True(tower.Cells().All(c => level.Sky.Contains(c)));
        Assert.Contains(level.Rooms, r => r.Get<string>("program") == "observatory");
        Assert.Contains(level.Rooms, r => r.Get<string>("program") == "entrance hall");
    }

    [Theory]
    [InlineData("facility", "admin")]
    [InlineData("complex", "admin")]
    [InlineData("dungeon", "keep")]
    [InlineData("wizard", "apprentice")]
    public void ProgramsGiveRoomsAJobAndFurniture(string preset, string firstSector)
    {
        int furnished = 0;
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var level = Generator.Generate(preset, seed);
            var ordinary = level.Rooms.Where(r => !r.IsConnector && Programs.RoomKinds.Contains(r.Kind)).ToList();
            Assert.All(ordinary, r => Assert.False(string.IsNullOrEmpty(r.Get<string>("program")), $"room {r.Id} has no program"));
            var start = level.Rooms.First(r => r.Kind == "start");
            Assert.Equal(firstSector, start.Get<string>("sector"));
            var names = new HashSet<string>(ordinary.Select(r => r.Get<string>("program")));
            furnished += level.PartCells.Values.Concat(level.PartSolid.Values)
                .Count(v => ordinary.Any(r => v.StartsWith($"room:{r.Id}/") && v.Length > $"room:{r.Id}/".Length));
            Assert.True(names.Count >= 3, $"{preset}/{seed}: programs {string.Join(",", names)}");
        }
        Assert.True(furnished > 0, $"{preset}: no furniture");
    }

    [Fact]
    public void MarketFillsAYardWithStalls()
    {
        var level = new Level { Config = new GenConfig(), CellSize = 32 };
        var room = new Room { Id = 0, Kind = "courtyard", X = 0, Y = 0, W = 24, H = 20, Floor = 0, Ceil = 320 };
        level.Rooms.Add(room);
        foreach (var c in room.Cells()) level.AddSpan(c, new Span(0, 320), "room:0", room.Slot);
        var ctx = new RoomCtx(level, room);
        Assert.True(Market.FMarket(ctx, Rng.FromLabel("market-test")));
        var merchants = level.Entities.Where(e => e.Kind == "merchant").ToList();
        Assert.True(merchants.Count >= 4, $"{merchants.Count} stalls");
        Assert.Equal(merchants.Count, room.Get<List<Dictionary<string, object>>>("stalls").Count);
        Assert.Contains(level.PartCells.Values, v => v == "room:0/stall 0");
        Assert.Contains(level.PartCells.Values, v => v == "room:0/well");
        Assert.Contains("market", room.Features);
        // a counter: a 40-unit block under the awning
        var counter = level.PartCells.First(kv => kv.Value.StartsWith("room:0/stall") && level.Spans[kv.Key][0].Floor == 40).Key;
        Assert.Equal(2, level.Spans[counter].Count);
        // too small for a market
        var small = new Room { Id = 1, Kind = "courtyard", X = 40, Y = 0, W = 12, H = 12, Floor = 0, Ceil = 320 };
        Assert.False(Market.FMarket(new RoomCtx(level, small), Rng.FromLabel("x")));
    }

    [Fact]
    public void JumpLaunchReachesTheTarget()
    {
        var (speed, pitch, yaw) = Arena.JumpLaunch(new Vec3(0, 0, 0), new Vec3(128, 0, 96));
        Assert.True(speed > 0);
        Assert.True(pitch < 0);
        Assert.Equal(0.0, yaw);
    }
}
