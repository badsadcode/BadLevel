using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NoorLevel;
using NoorLevel.Export;
using Xunit;

/// <summary>Existing output must not change: SHA-256 of the .glb and the IR JSON for a fixed set of levels
/// (facility, bunker, classic, dungeon; seeds 1-8) with every new feature at its default (off). The hashes
/// are in golden_hashes.txt; a change that should change them (a bug fix) regenerates that file on purpose:
///     NOORLEVEL_GOLDEN_WRITE=1 dotnet test --filter GoldenTests
/// and says why in its commit.</summary>
public class GoldenTests
{
    public static readonly string[] Presets = { "facility", "bunker", "classic", "dungeon" };
    public static readonly int[] Seeds = { 1, 2, 3, 4, 5, 6, 7, 8 };

    static string Sha(byte[] b) { using var s = SHA256.Create(); return string.Concat(s.ComputeHash(b).Select(x => x.ToString("x2"))); }

    static string HashFile => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "golden_hashes.txt");

    public static IEnumerable<object[]> Cases() => from p in Presets from s in Seeds select new object[] { p, s };

    static Dictionary<string, string> Load() =>
        File.Exists(HashFile)
            ? File.ReadAllLines(HashFile).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split(' ')).ToDictionary(p => p[0], p => p[1])
            : new Dictionary<string, string>();

    static (string Glb, string Json) Hashes(string preset, int seed)
    {
        var level = Generator.Generate(preset, seed);
        return (Sha(Gltf.ToGlb(level)), Sha(Encoding.UTF8.GetBytes(Json.Write(LevelJson.ToIr(level)))));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutputIsUnchanged(string preset, int seed)
    {
        if (Environment.GetEnvironmentVariable("NOORLEVEL_GOLDEN_WRITE") == "1") return;   // (WriteGolden does it)
        var golden = Load();
        Assert.True(golden.Count > 0, "golden_hashes.txt is missing");
        var (glb, json) = Hashes(preset, seed);
        Assert.Equal(golden[$"{preset}/{seed}/glb"], glb);
        Assert.Equal(golden[$"{preset}/{seed}/json"], json);
    }

    [Fact]
    public void WriteGolden()
    {
        if (Environment.GetEnvironmentVariable("NOORLEVEL_GOLDEN_WRITE") != "1") return;
        var lines = new List<string> { "# NoorLevel golden outputs: SHA-256 of Gltf.ToGlb and Json.Write(LevelJson.ToIr) (see GoldenTests.cs)" };
        foreach (var p in Presets)
            foreach (var s in Seeds)
            {
                var (glb, json) = Hashes(p, s);
                lines.Add($"{p}/{s}/glb {glb}");
                lines.Add($"{p}/{s}/json {json}");
            }
        File.WriteAllLines(HashFile, lines);
    }
}
