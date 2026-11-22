using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NoorLevel;
using NoorLevel.Export;
using Xunit;

/// <summary>Station markers (Dressing, StationEntities, the station program set): props stay out of
/// doorways, inside their rooms and off each other; every program gets its props; the gameplay markers are
/// there and reachable; same seed, same markers; with the flags off nothing changes.</summary>
public class StationTests
{
    static readonly ConcurrentDictionary<string, Level> Cache = new ConcurrentDictionary<string, Level>();

    /// <summary>A level with every station flag on (cached: several tests read the same levels).</summary>
    public static Level Station(string preset, int seed, string programSet = "station") =>
        Cache.GetOrAdd($"{preset}/{seed}/{programSet}", _ => Make(preset, seed, programSet));

    static Level Make(string preset, int seed, string programSet, Action<GenConfig> tweak = null)
    {
        var cfg = Recipes.Compile(Recipes.Presets[preset], seed);
        var set = new Dictionary<string, object> { ["dressing"] = true, ["station_entities"] = true, ["viewports"] = true };
        if (programSet != null) set["program_set"] = programSet;
        Recipes.SetFields(cfg, set);
        tweak?.Invoke(cfg);
        return Generator.Generate(cfg);
    }

    public static IEnumerable<object[]> Levels() =>
        new[] { ("facility", 1, "station"), ("facility", 2, "station"), ("facility", 3, "facility"), ("bunker", 1, null), ("bunker", 2, null) }
            .Select(t => new object[] { t.Item1, t.Item2, t.Item3 });

    static Room RoomOf(Level level, Entity e) =>
        e.Props.TryGetValue("room", out var r) && r is int id ? level.Rooms.First(x => x.Id == id) : null;

    static bool Tagged(Entity e, string tag) => e.Props.TryGetValue("tags", out var t) && ((string[])t).Contains(tag);

    static string Cls(Entity e) => (string)e.Props["class"];

    // ------------------------------------------------------------------------------------------
    // props
    // ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void PropsKeepOutOfDoorwaysInsideTheirRoomAndOffEachOther(string preset, int seed, string programSet)
    {
        var level = Station(preset, seed, programSet);
        Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
        var props = level.Entities.Where(e => e.Kind == "prop").ToList();
        Assert.True(props.Count > 20, $"only {props.Count} props");
        var clear = new Dictionary<int, RoomCtx>();
        foreach (var e in props)
        {
            Assert.NotNull(e.Bounds);
            var fp = (double[])e.Props["footprint"];
            Assert.Equal(3, fp.Length);
            Assert.Contains((string)e.Props["mount"], new[] { "floor", "wall", "ceiling" });
            Assert.Equal(0, (int)e.Props["angle"] % 90);
            var (lo, hi) = e.Bounds.Value;
            var room = RoomOf(level, e);
            if (room == null)
            {
                Assert.True(e.Props.ContainsKey("corridor"));
                string tag = $"corridor:{e.Props["corridor"]}";
                foreach (var c in Dressing.FloorCells(level, e.Bounds.Value))
                    Assert.True(level.OwnerAt(c, lo.Z) == tag || level.OwnerAt(c, hi.Z - 1) == tag, $"{Cls(e)} at {e.Pos} outside corridor {tag}");
                continue;
            }
            if (!clear.TryGetValue(room.Id, out var ctx)) clear[room.Id] = ctx = new RoomCtx(level, room);
            // inside the room: its floor print on room cells, between floor and ceiling
            foreach (var c in Dressing.FloorCells(level, e.Bounds.Value))
                Assert.True(room.Contains(c), $"{Cls(e)} at {e.Pos} leaves room {room.Id} at {c}");
            Assert.True(lo.Z >= room.Floor - 0.01 && hi.Z <= room.Ceil + 0.01, $"{Cls(e)} at {e.Pos} out of room {room.Id}'s height");
            if (Tagged(e, "blockout")) continue;               // a furniture block: Programs' own doorway rule
            // never in a doorway's clear zone, unless it hangs above a player's head
            bool inClear = Dressing.FloorCells(level, e.Bounds.Value).Any(ctx.Clear.Contains);
            if ((string)e.Props["mount"] == "floor") Assert.False(inClear, $"{Cls(e)} at {e.Pos} in a doorway zone of room {room.Id}");
            else if (inClear) Assert.True(lo.Z - room.Floor >= Dressing.Headroom, $"{Cls(e)} at {e.Pos} hangs low over a doorway zone");
        }
        // no two props overlap
        for (int i = 0; i < props.Count; i++)
            for (int j = i + 1; j < props.Count; j++)
                Assert.False(Dressing.Overlap(props[i].Bounds.Value, props[j].Bounds.Value),
                             $"{Cls(props[i])} at {props[i].Pos} overlaps {Cls(props[j])} at {props[j].Pos}");
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void DoorwaysStayJoined(string preset, int seed, string programSet)
    {
        var level = Station(preset, seed, programSet);
        foreach (var room in level.Rooms.Where(r => !r.IsConnector))
        {
            var st = new Dressing.RoomState(level, room, new Dressing.Space(level));
            var mine = level.Entities.Where(e => e.Kind == "prop" && RoomOf(level, e) == room && (string)e.Props["mount"] == "floor").ToList();
            foreach (var e in mine.Where(e => Tagged(e, "blockout")))
                foreach (var c in Dressing.FloorCells(level, e.Bounds.Value)) st.Occ.Add(c);
            st.Snapshot();
            var cells = mine.Where(e => !Tagged(e, "blockout") && !Tagged(e, "flat")).SelectMany(e => Dressing.FloorCells(level, e.Bounds.Value)).ToList();
            Assert.True(st.TryOccupy(cells), $"room {room.Id}: props cut a 2-cell path between its doorways");
        }
    }

    [Fact]
    public void EveryFurnitureBlockHasABlockoutProp()
    {
        var level = Station("facility", 1);
        foreach (var room in level.Rooms)
        {
            if (!room.Meta.TryGetValue("furnish_blocks", out var v)) continue;
            var blocks = (List<(string Name, List<Cell> Cells, int? Height)>)v;
            var marked = level.Entities.Where(e => e.Kind == "prop" && Tagged(e, "blockout") && RoomOf(level, e) == room).ToList();
            Assert.Equal(blocks.Count, marked.Count);
            foreach (var (b, e) in blocks.Zip(marked))
            {
                Assert.Equal($"room:{room.Id}/{b.Name}", e.Props["block"]);
                foreach (var c in b.Cells) Assert.Contains(c, Dressing.FloorCells(level, e.Bounds.Value));   // same place
                Assert.True(level.PartSolid.ContainsKey(b.Cells[0]) || level.PartCells.ContainsKey(b.Cells[0]));  // the geometry stays
            }
        }
    }

    /// <summary>Each program's own props (STATIONS_BRIEF 2.2 and 2.3), blockout blocks included.</summary>
    public static readonly Dictionary<string, string[]> Signature = new Dictionary<string, string[]>
    {
        ["control"] = new[] { "console" }, ["lab"] = new[] { "bench_lab", "specimen_tank" }, ["server"] = new[] { "server_rack" },
        ["lobby"] = new[] { "console", "locker" }, ["security"] = new[] { "console", "desk", "locker", "counter" },
        ["office"] = new[] { "desk" }, ["conference"] = new[] { "desk", "table" }, ["test_chamber"] = new[] { "specimen_tank", "bench_lab" },
        ["quarters"] = new[] { "bunk", "locker" }, ["storage"] = new[] { "shelf", "crate", "barrel" }, ["loading"] = new[] { "crate", "barrel" },
        ["warehouse"] = new[] { "shelf", "crate" }, ["workshop"] = new[] { "workbench", "toolboard" }, ["pump"] = new[] { "pump", "tank", "valve_wheel" },
        ["boiler"] = new[] { "pump", "valve_wheel", "pipe_run", "boiler" }, ["cafeteria"] = new[] { "table" }, ["galley"] = new[] { "table" },
        ["medbay"] = new[] { "bed", "cabinet" }, ["hydroponics"] = new[] { "grow_rack", "water_tank" },
        ["moon_pool"] = new[] { "crate", "valve_wheel", "pipe_run", "locker" }, ["dive_locker"] = new[] { "dive_suit", "locker" },
        ["sonar_room"] = new[] { "console" }, ["generator"] = new[] { "pump", "valve_wheel", "pipe_run", "generator" },
    };

    static readonly string[] StationPrograms = { "quarters", "galley", "medbay", "hydroponics", "moon_pool", "dive_locker", "sonar_room", "generator" };

    [Fact]
    public void EveryProgramGetsItsProps()
    {
        var seen = new Dictionary<string, int>();
        var levels = Enumerable.Range(1, 6).Select(s => Station("facility", s))
            .Concat(Enumerable.Range(3, 4).Select(s => Station("facility", s, "facility"))).ToList();
        foreach (var level in levels)
            foreach (var room in level.Rooms)
            {
                var prog = room.Get<string>("program");
                if (prog == null || !Signature.TryGetValue(prog, out var want)) continue;
                var classes = level.Entities.Where(e => e.Kind == "prop" && RoomOf(level, e) == room).Select(Cls).ToList();
                // a room whose floor is (nearly) all doorway zone has no floor for furniture: the doorways win
                var fresh = new Dressing.RoomState(level, room, new Dressing.Space(level));
                int free = fresh.CellList.Count(c => fresh.Usable(c) && !level.PartSolid.ContainsKey(c) && !level.PartCells.ContainsKey(c));
                if (free < 6 && !classes.Any(want.Contains)) continue;
                Assert.True(classes.Any(want.Contains), $"seed {level.Seed} room {room.Id} ({prog}, {room.W}x{room.H}) has none of [{string.Join(", ", want)}]: [{string.Join(", ", classes)}]");
                seen[prog] = seen.TryGetValue(prog, out var n) ? n + 1 : 1;
                if (prog == "moon_pool")
                    Assert.Contains(room.Cells(), c => level.Water.ContainsKey(c));     // its pool, open to the sea
            }
        foreach (var p in StationPrograms) Assert.True(seen.ContainsKey(p), $"no {p} in the station levels: [{string.Join(", ", seen.Keys)}]");
        foreach (var p in new[] { "office", "lab", "server", "storage", "control", "lobby", "security", "workshop" })
            Assert.True(seen.ContainsKey(p), $"no {p} room");
    }

    [Fact]
    public void SignsSayTheProgram()
    {
        var level = Station("facility", 1);
        var signs = level.Entities.Where(e => e.Kind == "prop" && Cls(e) == "sign").ToList();
        Assert.NotEmpty(signs);
        foreach (var e in signs)
        {
            var room = RoomOf(level, e);
            Assert.Equal(Dressing.SignText(level, room, room.Get<string>("program")), e.Props["text"]);
        }
    }

    // ------------------------------------------------------------------------------------------
    // gameplay markers
    // ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Levels))]
    public void GameplayMarkersAreThereAndReachable(string preset, int seed, string programSet)
    {
        var level = Station(preset, seed, programSet);
        Assert.True(level.Report.Ok, string.Join("; ", level.Report.Errors));
        var start = level.Entities.Single(e => e.Kind == "player_start");
        var reach = Validator.Flood(level, Validator.NodeAt(level, start.Pos).Value, new HashSet<Cell>(), Validator.LinkEdges(level));
        var loot = level.Entities.Where(e => e.Kind == "loot").ToList();
        var spawns = level.Entities.Where(e => e.Kind == "creature_spawn").ToList();
        Assert.True(loot.Count >= level.Rooms.Count(r => !r.IsConnector) / 2, $"only {loot.Count} loot");
        Assert.NotEmpty(spawns);
        Assert.Contains(spawns, e => (string)e.Props["role"] == "nest");
        Assert.Single(level.Entities, e => e.Kind == "airlock");
        Assert.Contains(level.Entities, e => e.Kind == "power_junction");
        Assert.Contains(level.Entities, e => e.Kind == "log_terminal");
        Assert.Contains(level.Entities, e => e.Kind == "hiding_spot");
        foreach (var e in loot) Assert.True(StationEntities.Reachable(level, reach, e.Pos, true), $"loot at {e.Pos} unreachable");
        foreach (var e in spawns) Assert.True(StationEntities.Reachable(level, reach, e.Pos, false), $"creature_spawn at {e.Pos} unreachable");
        Assert.Empty(StationEntities.ReachReport(level, reach));
        // one power junction per sector at most
        var sectors = level.Entities.Where(e => e.Kind == "power_junction")
            .Select(e => RoomOf(level, e).Get<string>("sector") ?? $"zone{level.Graph.Nodes[RoomOf(level, e).Node].Zone}").ToList();
        Assert.Equal(sectors.Count, sectors.Distinct().Count());
        // markers without a box stand in open space (the validator's rule)
        foreach (var e in level.Entities.Where(e => Markers.IsMarker(e.Kind) && e.Bounds == null))
            Assert.NotNull(Validator.NodeAt(level, e.Pos));
        // the airlock is in the start room
        var air = level.Entities.Single(e => e.Kind == "airlock");
        Assert.Equal("start", RoomOf(level, air).Kind);
    }

    [Fact]
    public void ValidatorRejectsUnreachableLoot()
    {
        var level = Station("facility", 1);
        var e = Markers.Make("loot", new Vec3(-1000, -1000, 0), null, null).With("class", "loot").With("size", "small");
        level.Entities.Add(e);
        try
        {
            var rep = Validator.Run(level);
            Assert.Contains(rep.Errors, x => x.StartsWith("loot") && x.Contains("unreachable"));
        }
        finally { level.Entities.Remove(e); }
    }

    [Fact]
    public void ViewportsSitOnOuterWalls()
    {
        var level = Station("facility", 2);
        var vps = level.Entities.Where(e => e.Kind == "viewport").ToList();
        Assert.NotEmpty(vps);
        foreach (var e in vps)
        {
            var (dx, dy) = Markers.Dir((int)e.Props["angle"]);
            var behind = level.CellOf(new Vec3(e.Pos.X - dx * 4, e.Pos.Y - dy * 4, e.Pos.Z));   // just inside the wall
            Assert.False(level.Spans.ContainsKey(behind), $"viewport at {e.Pos} has open space behind it");
        }
    }

    // ------------------------------------------------------------------------------------------
    // determinism, the flags off, the export
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void SameSeedSameMarkers()
    {
        var a = Make("facility", 2, "station");
        var b = Station("facility", 2);
        Assert.Equal(Json.Write(LevelJson.ToIr(a)), Json.Write(LevelJson.ToIr(b)));
        Assert.Equal(Gltf.ToGlb(a), Gltf.ToGlb(b));
        var c = Make("facility", 5, "station");
        Assert.NotEqual(Json.Write(c.Entities.Where(e => e.Kind == "prop").Select(LevelJson.EntityInfo).ToList()),
                        Json.Write(b.Entities.Where(e => e.Kind == "prop").Select(LevelJson.EntityInfo).ToList()));
    }

    [Fact]
    public void FlagsOffChangeNothing()
    {
        var cfg = new GenConfig();
        Assert.False(cfg.Dressing);
        Assert.False(cfg.StationEntities);
        Assert.False(cfg.Viewports);
        Assert.Equal("facility", cfg.ProgramSet);
        foreach (var preset in new[] { "facility", "bunker" })
        {
            var off = Generator.Generate(preset, 3);
            Assert.DoesNotContain(off.Entities, e => Markers.IsMarker(e.Kind));
            Assert.DoesNotContain(off.Rooms, r => r.Meta.ContainsKey("furnish_blocks"));
            // turning the markers on only adds markers: same space, same entities before them
            var on = Make(preset, 3, null);
            var ir = LevelJson.ToIr(off); var ir2 = LevelJson.ToIr(on);
            foreach (var k in new[] { "columns", "rooms", "corridors", "links" }) Assert.Equal(Json.Write(ir[k]), Json.Write(ir2[k]));
            Assert.Equal(Json.Write(off.Entities.Select(LevelJson.EntityInfo).ToList()),
                         Json.Write(on.Entities.Take(off.Entities.Count).Select(LevelJson.EntityInfo).ToList()));
            Assert.All(on.Entities.Skip(off.Entities.Count), e => Assert.True(Markers.IsMarker(e.Kind)));
        }
    }

    [Fact]
    public void GlbMarkersFollowTheInterchangeSpec()
    {
        var level = Station("facility", 1);
        var glb = Gltf.ToGlb(level);
        int jlen = BitConverter.ToInt32(glb, 12);
        var gltf = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(glb, 20, jlen));
        var nodes = ((List<object>)gltf["nodes"]).Cast<Dictionary<string, object>>().ToList();
        int markers = 0;
        foreach (var n in nodes)
        {
            if (!n.TryGetValue("extras", out var x) || !((Dictionary<string, object>)x).TryGetValue("kind", out var k)) continue;
            var extras = (Dictionary<string, object>)x;
            if (!Markers.IsMarker((string)k))
            {
                Assert.False(n.ContainsKey("rotation"));      // existing nodes as they were
                Assert.False(extras.ContainsKey("noor"));
                continue;
            }
            markers++;
            var noor = (Dictionary<string, object>)extras["noor"];
            Assert.Equal(1.0, noor["v"]);
            Assert.Equal(Markers.NoorMarker[(string)k], noor["marker"]);
            Assert.Equal(n["name"], noor["id"]);
            Assert.StartsWith((string)k + "_", (string)n["name"]);
            if (extras.TryGetValue("angle", out var ang))
            {
                var q = ((List<object>)n["rotation"]).Select(Convert.ToDouble).ToArray();
                Assert.Equal(1.0, Math.Sqrt(q.Sum(v => v * v)), 5);
                // the node's +Z (its forward) is the marker's facing, in glTF axes (map x, y -> glTF x, -z)
                var (dx, dy) = Markers.Dir(Convert.ToInt32(ang));
                var f = Rotate(q, 0, 0, 1);
                Assert.Equal(dx, f.X, 4); Assert.Equal(0, f.Y, 4); Assert.Equal(-dy, f.Z, 4);
            }
            if ((string)k == "prop")
            {
                Assert.Equal(3, ((List<object>)noor["footprint"]).Count);
                Assert.True(noor.ContainsKey("class") && noor.ContainsKey("mount") && noor.ContainsKey("variant"));
            }
        }
        Assert.Equal(level.Entities.Count(e => Markers.IsMarker(e.Kind)), markers);
    }

    static (double X, double Y, double Z) Rotate(double[] q, double x, double y, double z)
    {
        double qx = q[0], qy = q[1], qz = q[2], qw = q[3];
        // v' = v + 2w (q x v) + 2 q x (q x v)
        double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
        double ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
        return (x + 2 * (qw * cx + ccx), y + 2 * (qw * cy + ccy), z + 2 * (qw * cz + ccz));
    }

    [Fact]
    public void ViewerAndIrCarryTheMarkers()
    {
        var level = Station("facility", 1);
        var ir = LevelJson.ToIr(level);
        var ents = (List<Dictionary<string, object>>)ir["entities"];
        Assert.Contains(ents, d => (string)d["kind"] == "prop" && ((Dictionary<string, object>)d["props"]).ContainsKey("footprint") && d.ContainsKey("lo"));
        var viewer = LevelJson.ToViewer(level);
        Assert.Equal(level.Entities.Count, ((List<Dictionary<string, object>>)viewer["entities"]).Count);
    }

    [Fact]
    public void EveryFormatExportsAStation()
    {
        var level = Station("facility", 1);
        foreach (var f in Exporters.Formats.Where(f => f.Id != "noorengine"))
            Assert.True(Exporters.Export(level, f.Id, out _).Length > 0, f.Id);
    }
}
