using System.Collections.Generic;
using System.Linq;
using NoorLevel;
using Xunit;
using Xunit.Abstractions;

/// <summary>Group B features (open_area.py, compound.py, mega.py, packed.py) show up in the presets that use them.</summary>
public class OpenMegaPackedTests
{
    readonly ITestOutputHelper output;
    public OpenMegaPackedTests(ITestOutputHelper output) { this.output = output; }

    static readonly int[] Seeds = { 1, 2, 3 };

    [Theory]
    [InlineData("compound")]
    [InlineData("grand")]
    [InlineData("market")]
    public void CourtyardsAreOpenToTheSky(string preset)
    {
        foreach (var seed in Seeds)
        {
            var level = Generator.Generate(preset, seed);
            Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
            var yards = level.Rooms.Where(r => r.Kind == "courtyard").ToList();
            Assert.NotEmpty(yards);
            foreach (var y in yards)
            {
                var sky = y.Cells().Count(level.Sky.Contains);
                output.WriteLine($"{preset}/{seed} yard {y.Id} {y.W}x{y.H} h={y.Ceil - y.Floor} sky={sky} features={string.Join(",", y.Features)}");
                Assert.True(sky > y.Cells().Count() / 2, $"{preset}/{seed}: yard {y.Id} has only {sky} sky cells");
            }
            // every sky cell is open (a carved column), never solid rock
            Assert.All(level.Sky, c => Assert.True(level.Spans.ContainsKey(c)));
        }
    }

    [Fact]
    public void CompoundHasBuildingsAndRoofscape()
    {
        int buildings = 0, roofscape = 0, ladders = 0;
        foreach (var seed in Seeds)
        {
            var level = Generator.Generate("compound", seed);
            Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
            foreach (var y in level.Rooms.Where(r => r.Kind == "courtyard"))
            {
                if (y.Meta.TryGetValue("buildings", out var b)) buildings += ((List<Dictionary<string, object>>)b).Count;
                roofscape += y.Get("roofscape", 0);
            }
            Assert.Equal(level.PartCells.Values.Count(v => v.EndsWith("/roofscape")),
                         level.Owners.Count(kv => kv.Value.Any(o => o.Name.StartsWith("roof:"))));
            ladders += level.Entities.Count(e => e.Kind == "ladder" && e.Props.ContainsKey("building"));
        }
        output.WriteLine($"buildings={buildings} roofscape cells={roofscape} building ladders={ladders}");
        Assert.True(buildings > 0);
        Assert.True(roofscape > 0);
    }

    [Theory]
    [InlineData("fortress")]
    [InlineData("industrial")]
    [InlineData("explore")]
    [InlineData("bunker")]
    [InlineData("runekeep")]
    [InlineData("outpost")]
    public void MegaRoomsBuildSubAreas(string preset)
    {
        int withRegions = 0;
        foreach (var seed in Seeds)
        {
            var level = Generator.Generate(preset, seed);
            Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
            var megas = level.Rooms.Where(r => r.Kind == "megaroom").ToList();
            Assert.NotEmpty(megas);
            foreach (var m in megas)
            {
                var style = m.Get<string>("style");
                Assert.Contains(style, MegaSpace.Styles);
                Assert.Contains(style, m.Features);
                var regions = (List<Dictionary<string, object>>)m.Meta["regions"];
                output.WriteLine($"{preset}/{seed} mega {m.W}x{m.H} {style}: " + string.Join(", ", regions.Select(r => $"{r["name"]}({r["size"]})")));
                if (regions.Count > 1) withRegions++;
            }
        }
        Assert.True(withRegions > 0, $"{preset}: no mega-room built a sub-area");
    }

    [Theory]
    [InlineData("techbase")]
    [InlineData("runekeep")]
    [InlineData("outpost")]
    public void PackedPlansShareWalls(string preset)
    {
        foreach (var seed in Seeds)
        {
            var level = Generator.Generate(preset, seed);
            Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
            // a divided floor plan: tree neighbours sit on neighbouring plots, one shared wall (3 cells) apart
            int checkedEdges = 0;
            foreach (var e in level.Graph.Edges.Where(e => e.Tree))
            {
                var a = level.RoomByNode(e.A);
                var b = level.RoomByNode(e.B);
                if (a.IsConnector || b.IsConnector || a.Kind == "megaroom" || b.Kind == "megaroom") continue;   // centred in their plots
                int dx = System.Math.Max(b.X - (a.X + a.W), a.X - (b.X + b.W));
                int dy = System.Math.Max(b.Y - (a.Y + a.H), a.Y - (b.Y + b.H));
                Assert.Equal(Packed.WallLo + Packed.WallHi, System.Math.Max(dx, dy));
                Assert.True(System.Math.Min(dx, dy) <= -6, "neighbours share at least a doorway's length of wall");
                checkedEdges++;
            }
            int minX = level.Rooms.Min(r => r.X), minY = level.Rooms.Min(r => r.Y);
            int maxX = level.Rooms.Max(r => r.X + r.W), maxY = level.Rooms.Max(r => r.Y + r.H);
            double fill = level.Rooms.Sum(r => (double)r.W * r.H) / ((maxX - minX) * (maxY - minY));
            output.WriteLine($"{preset}/{seed}: wall-sharing tree edges={checkedEdges} fill={fill:0.00}");
            Assert.True(checkedEdges > 0);
            Assert.True(fill > 0.35, $"{preset}/{seed}: room fill {fill:0.00}");
        }
    }
}
