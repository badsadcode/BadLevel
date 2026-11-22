using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Dressing (cfg.Dressing): prop markers, so a level looks lived and worked in.
    ///
    /// A prop is an empty marker, an entity of kind "prop": "a console, about this big, facing this way,
    /// belongs here". The game puts a real asset there (or a placeholder box from the footprint). This pass
    /// adds no geometry and never touches Spans: it runs after the level is built and appends to
    /// Level.Entities. Each room and corridor draws from its own label stream
    /// (<c>{seed}/{variant}/dressing/{room id}</c>), never from the shared one, so turning it on changes
    /// nothing else. The marker format is in <see cref="Markers"/>.
    ///
    /// What goes where comes from the room's program (<see cref="Rules"/>): consoles against the walls of a
    /// control room, desks and chairs in an office, bunks and lockers in quarters, shelves and crates in
    /// storage. Corridors get runs (cables on the ceiling, pipes on a wall), not furniture. Everywhere: some
    /// debris (more in dead ends), dead light fixtures by some lights, a sign by the doors.
    /// Every furniture block the room's program built (Programs.Furnish) also gets a prop marker in its
    /// place, tagged "blockout", so the game can hide the block and drop the real asset there.
    ///
    /// Rules every prop keeps:
    /// <list type="bullet">
    /// <item>floor props stay out of the doorway zones (RoomCtx.Clear), off stairs, ramps, water and
    /// hazards, inside their room and under its ceiling; wall and ceiling props over a doorway zone hang
    /// at least 2 m up (<see cref="Headroom"/>);</item>
    /// <item>doorways that were joined by a path two cells wide still are;</item>
    /// <item>no two props (or a prop and another entity) overlap;</item>
    /// <item>wall props have a wall behind them over their whole height.</item>
    /// </list></summary>
    public static class Dressing
    {
        public sealed class PropClass
        {
            public string Name, Mount;
            public double W, D, H;      // metres: width (along its right), depth (along its forward), height
            public double Elev;         // wall props: the bottom's height above the floor (metres)
            public bool Breaks = true;  // may be tagged "broken"
        }

        static PropClass P(string n, string mount, double w, double d, double h, double elev = 0, bool breaks = true) =>
            new PropClass { Name = n, Mount = mount, W = w, D = d, H = h, Elev = elev, Breaks = breaks };

        /// <summary>The prop vocabulary (STATIONS_BRIEF 2.2, plus bed, cabinet, grow_rack, water_tank and
        /// dive_suit for the station programs). Runs (pipe_run, cable_run) take their width from their length;
        /// debris, bones and stain from their label stream (0.4-1.5 m).</summary>
        public static readonly Dictionary<string, PropClass> Classes = new[]
        {
            P("console", "floor", 1.2, 0.6, 1.1), P("desk", "floor", 1.4, 0.7, 0.75), P("chair", "floor", 0.5, 0.5, 0.9),
            P("bench_lab", "floor", 2.0, 0.8, 0.95), P("specimen_tank", "floor", 0.9, 0.9, 2.0),
            P("server_rack", "floor", 0.6, 1.0, 2.0), P("locker", "floor", 0.5, 0.5, 1.9), P("bunk", "floor", 2.0, 0.9, 1.6),
            P("crate", "floor", 0.8, 0.8, 0.8), P("barrel", "floor", 0.6, 0.6, 0.9), P("shelf", "floor", 1.8, 0.5, 2.0),
            P("workbench", "floor", 2.0, 0.8, 0.95), P("toolboard", "wall", 1.6, 0.1, 1.0, elev: 1.0),
            P("pump", "floor", 1.2, 1.2, 1.4), P("valve_wheel", "wall", 0.4, 0.2, 0.4, elev: 1.1),
            P("pipe_run", "wall", 0, 0.3, 0.3, elev: 2.2), P("table", "floor", 1.8, 0.9, 0.75), P("stool", "floor", 0.4, 0.4, 0.5),
            P("cable_run", "ceiling", 0, 0.2, 0.2), P("vent_grate", "wall", 0.8, 0.1, 0.6, elev: 0.3),
            P("debris", "floor", 1.0, 1.0, 0.4, breaks: false), P("bones", "floor", 0.8, 0.5, 0.2, breaks: false),
            P("stain", "floor", 1.0, 1.0, 0.01, breaks: false), P("light_fixture_dead", "ceiling", 0.6, 0.3, 0.1, breaks: false),
            P("sign", "wall", 0.6, 0.05, 0.3, elev: 2.1),
            P("bed", "floor", 2.0, 0.9, 0.6), P("cabinet", "floor", 0.8, 0.5, 1.8), P("grow_rack", "floor", 1.8, 0.6, 2.0),
            P("water_tank", "floor", 1.0, 1.0, 1.8), P("dive_suit", "floor", 0.7, 0.5, 2.0),
        }.ToDictionary(p => p.Name);

        /// <summary>How a rule places its class: against a wall (floor, back to the wall), free-standing (floor,
        /// a free cell all round), mounted on a wall, or a pipe run along a wall.</summary>
        public enum Place { Wall, Free, Mount, Run }

        public sealed class Rule
        {
            public string Class;
            public Place Place;
            public double N;            // how many in an 80-cell room at density 1
            public bool Signature;      // the program's own props: at least one if it fits
        }

        static Rule R(string c, Place p, double n, bool sig = false) => new Rule { Class = c, Place = p, N = n, Signature = sig };

        /// <summary>What each program gets (STATIONS_BRIEF 2.2 and 2.3). Rooms without a program get crates
        /// and barrels; every room also gets the generic props (debris, stains, dead fixtures, signs).</summary>
        public static readonly Dictionary<string, Rule[]> Rules = new Dictionary<string, Rule[]>
        {
            ["control"] = new[] { R("console", Place.Wall, 3, true), R("cabinet", Place.Wall, 0.5) },
            ["lab"] = new[] { R("bench_lab", Place.Free, 1, true), R("specimen_tank", Place.Wall, 1.5, true), R("console", Place.Wall, 1), R("cabinet", Place.Wall, 0.5) },
            ["server"] = new[] { R("server_rack", Place.Wall, 3, true), R("console", Place.Wall, 1) },
            ["lobby"] = new[] { R("console", Place.Wall, 1, true), R("locker", Place.Wall, 3, true) },
            ["security"] = new[] { R("console", Place.Wall, 1, true), R("desk", Place.Free, 1, true), R("locker", Place.Wall, 2) },
            ["office"] = new[] { R("desk", Place.Free, 2, true), R("shelf", Place.Wall, 1), R("cabinet", Place.Wall, 1) },
            ["conference"] = new[] { R("desk", Place.Wall, 1, true), R("cabinet", Place.Wall, 0.5) },
            ["test_chamber"] = new[] { R("specimen_tank", Place.Wall, 2, true), R("bench_lab", Place.Free, 1, true), R("console", Place.Wall, 1) },
            ["quarters"] = new[] { R("bunk", Place.Wall, 2, true), R("locker", Place.Wall, 2, true), R("table", Place.Free, 1, true) },
            ["storage"] = new[] { R("shelf", Place.Wall, 3, true), R("crate", Place.Free, 3, true), R("barrel", Place.Free, 2) },
            ["loading"] = new[] { R("crate", Place.Free, 4, true), R("barrel", Place.Free, 2), R("shelf", Place.Wall, 1) },
            ["warehouse"] = new[] { R("shelf", Place.Wall, 3, true), R("crate", Place.Free, 3, true), R("barrel", Place.Free, 1) },
            ["workshop"] = new[] { R("workbench", Place.Wall, 2, true), R("toolboard", Place.Mount, 2, true), R("crate", Place.Free, 1), R("shelf", Place.Wall, 1) },
            ["pump"] = new[] { R("pump", Place.Free, 2, true), R("valve_wheel", Place.Mount, 2, true), R("pipe_run", Place.Run, 1, true) },
            ["boiler"] = new[] { R("pump", Place.Free, 1, true), R("valve_wheel", Place.Mount, 2, true), R("pipe_run", Place.Run, 2, true) },
            ["cafeteria"] = new[] { R("table", Place.Free, 2, true), R("crate", Place.Wall, 0.5) },
            ["galley"] = new[] { R("table", Place.Free, 2, true), R("crate", Place.Wall, 2, true), R("shelf", Place.Wall, 1) },
            ["medbay"] = new[] { R("bed", Place.Wall, 2, true), R("cabinet", Place.Wall, 2, true), R("console", Place.Wall, 0.5) },
            ["hydroponics"] = new[] { R("grow_rack", Place.Wall, 3, true), R("water_tank", Place.Free, 1, true) },
            ["moon_pool"] = new[] { R("crate", Place.Wall, 2, true), R("valve_wheel", Place.Mount, 1, true), R("pipe_run", Place.Run, 1, true), R("locker", Place.Wall, 1) },
            ["dive_locker"] = new[] { R("dive_suit", Place.Wall, 3, true), R("locker", Place.Wall, 2, true), R("crate", Place.Free, 0.5) },
            ["sonar_room"] = new[] { R("console", Place.Wall, 3, true), R("cabinet", Place.Wall, 0.5) },
            ["generator"] = new[] { R("pump", Place.Free, 1, true), R("valve_wheel", Place.Mount, 2, true), R("pipe_run", Place.Run, 2, true) },
            ["reactor"] = new[] { R("console", Place.Wall, 1, true), R("valve_wheel", Place.Mount, 2), R("pipe_run", Place.Run, 2) },
            ["atrium"] = new[] { R("crate", Place.Wall, 0.5) },
            ["transit"] = new[] { R("locker", Place.Wall, 1), R("crate", Place.Wall, 0.5) },
            ["closet"] = new[] { R("shelf", Place.Wall, 1, true), R("crate", Place.Free, 1) },
            // rooms without a program (levels without cfg.Programs), by their kind
            ["kind:start"] = new[] { R("locker", Place.Wall, 2, true), R("console", Place.Wall, 1, true), R("crate", Place.Free, 0.5) },
            ["kind:key"] = new[] { R("console", Place.Wall, 1, true), R("desk", Place.Free, 1), R("cabinet", Place.Wall, 1) },
            ["kind:supply"] = new[] { R("shelf", Place.Wall, 2, true), R("crate", Place.Free, 2), R("barrel", Place.Free, 1), R("locker", Place.Wall, 1) },
            ["kind:exit"] = new[] { R("console", Place.Wall, 2, true), R("valve_wheel", Place.Mount, 1), R("pipe_run", Place.Run, 1) },
            [""] = new[] { R("crate", Place.Free, 1, true), R("barrel", Place.Free, 0.7), R("locker", Place.Wall, 0.5), R("shelf", Place.Wall, 0.5),
                           R("desk", Place.Free, 0.4), R("console", Place.Wall, 0.3), R("pipe_run", Place.Run, 0.4) },
        };

        /// <summary>Program density: storage is dense, an atrium sparse.</summary>
        public static readonly Dictionary<string, double> ProgramDensity = new Dictionary<string, double>
        {
            ["storage"] = 1.5, ["warehouse"] = 1.3, ["loading"] = 1.3, ["galley"] = 1.2, ["quarters"] = 1.2, ["workshop"] = 1.2,
            ["atrium"] = 0.3, ["transit"] = 0.4, ["lobby"] = 0.7, ["reactor"] = 0.6, ["test_chamber"] = 0.7,
        };

        /// <summary>Furniture block names (Programs.Furnish) to prop classes; others keep their name, spaces as _.</summary>
        public static readonly Dictionary<string, string> BlockClass = new Dictionary<string, string>
        {
            ["desks"] = "desk", ["benches"] = "bench_lab", ["consoles"] = "console", ["workbenches"] = "workbench",
            ["server racks"] = "server_rack", ["shelving"] = "shelf", ["tanks"] = "tank", ["boilers"] = "boiler",
            ["tables"] = "table", ["conference table"] = "table", ["counter"] = "counter", ["bunks"] = "bunk", ["beds"] = "bed",
            ["planters"] = "grow_rack", ["sarcophagi"] = "sarcophagus", ["bookshelves"] = "bookshelf", ["weapon racks"] = "weapon_rack",
            ["barrels"] = "barrel", ["cell walls"] = "cell_wall", ["writing desks"] = "writing_desk", ["chests"] = "chest",
            ["reliquaries"] = "reliquary",
        };

        /// <summary>A prop over a doorway zone keeps this much headroom (units: 2 m) under its lowest point.</summary>
        public const int Headroom = 64;

        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        const double Eps = 0.5;

        // ------------------------------------------------------------------------------------------
        // the pass
        // ------------------------------------------------------------------------------------------

        /// <summary>Append the prop markers. Returns how many.</summary>
        public static int Run(Level level)
        {
            var cfg = level.Config;
            var space = new Space(level);
            int before = level.Entities.Count;
            var props = new List<Entity>();
            foreach (var room in level.Rooms)
            {
                if (room.IsConnector) continue;
                var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/dressing/{room.Id}");
                var st = new RoomState(level, room, space);
                Blockout(st, rng, props);
                st.Snapshot();                                     // which doorways are joined, with the blocks in
                if (cfg.DressingDensity > 0) DressRoom(st, rng, props);
            }
            if (cfg.DressingDensity > 0)
                foreach (var corr in level.Corridors)
                    DressCorridor(level, corr, space, Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/dressing/corridor/{corr.Id}"), props);
            level.Entities.AddRange(props);
            if (props.Count > 0) level.Notes.Add($"dressing: {props.Count} props");
            return level.Entities.Count - before;
        }

        /// <summary>Boxes taken so far (props and the level's other entities), by cell, for overlap tests.</summary>
        public sealed class Space
        {
            readonly Dictionary<Cell, List<(Vec3 Lo, Vec3 Hi)>> boxes = new Dictionary<Cell, List<(Vec3, Vec3)>>();
            readonly int s;

            public Space(Level level)
            {
                s = level.CellSize;
                foreach (var e in level.Entities)
                {
                    if (e.Bounds != null) Add(e.Bounds.Value);
                    else Add((new Vec3(e.Pos.X - 8, e.Pos.Y - 8, e.Pos.Z - 8), new Vec3(e.Pos.X + 8, e.Pos.Y + 8, e.Pos.Z + 8)));
                }
            }

            IEnumerable<Cell> Cells((Vec3 Lo, Vec3 Hi) b)
            {
                for (int x = (int)Math.Floor((b.Lo.X + Eps) / s); x <= (int)Math.Floor((b.Hi.X - Eps) / s); x++)
                    for (int y = (int)Math.Floor((b.Lo.Y + Eps) / s); y <= (int)Math.Floor((b.Hi.Y - Eps) / s); y++)
                        yield return new Cell(x, y);
            }

            public void Add((Vec3 Lo, Vec3 Hi) b)
            {
                foreach (var c in Cells(b))
                {
                    if (!boxes.TryGetValue(c, out var l)) boxes[c] = l = new List<(Vec3, Vec3)>();
                    l.Add(b);
                }
            }

            public bool Free((Vec3 Lo, Vec3 Hi) b)
            {
                foreach (var c in Cells(b))
                    if (boxes.TryGetValue(c, out var l))
                        foreach (var o in l)
                            if (Overlap(b, o)) return false;
                return true;
            }
        }

        public static bool Overlap((Vec3 Lo, Vec3 Hi) a, (Vec3 Lo, Vec3 Hi) b) =>
            a.Lo.X < b.Hi.X - Eps && b.Lo.X < a.Hi.X - Eps && a.Lo.Y < b.Hi.Y - Eps && b.Lo.Y < a.Hi.Y - Eps
            && a.Lo.Z < b.Hi.Z - Eps && b.Lo.Z < a.Hi.Z - Eps;

        /// <summary>The cells a box's floor print covers.</summary>
        public static IEnumerable<Cell> FloorCells(Level level, (Vec3 Lo, Vec3 Hi) b)
        {
            int s = level.CellSize;
            for (int x = (int)Math.Floor((b.Lo.X + Eps) / s); x <= (int)Math.Floor((b.Hi.X - Eps) / s); x++)
                for (int y = (int)Math.Floor((b.Lo.Y + Eps) / s); y <= (int)Math.Floor((b.Hi.Y - Eps) / s); y++)
                    yield return new Cell(x, y);
        }

        /// <summary>One room's state while it's dressed: where floor props may stand, what's taken, and which
        /// doorways were joined (by a path two cells wide) before anything was placed.</summary>
        public sealed class RoomState
        {
            public readonly Level Level;
            public readonly Room Room;
            public readonly RoomCtx Ctx;
            public readonly Space Space;
            public readonly HashSet<Cell> Occ = new HashSet<Cell>();         // floor cells props take
            public readonly HashSet<Cell> Reserved = new HashSet<Cell>();    // start, key, exit, doors, lifts...
            public readonly HashSet<Cell> NearStep = new HashSet<Cell>();    // the foot of a stair or ramp
            public readonly List<Cell> CellList;
            Dictionary<Cell, int> baseComp = new Dictionary<Cell, int>();
            public readonly bool DeadEnd;
            public readonly int Area;

            public RoomState(Level level, Room room, Space space)
            {
                Level = level;
                Room = room;
                Space = space;
                Ctx = new RoomCtx(level, room);
                CellList = room.Cells().ToList();
                Area = CellList.Count;
                int s = level.CellSize;
                foreach (var e in level.Entities)
                {
                    if (e.Bounds != null)
                    {
                        if (e.Kind == "light" || e.Kind == "torch" || e.Kind == "window") continue;
                        foreach (var c in FloorCells(level, e.Bounds.Value)) Reserved.Add(c);
                    }
                    else if (e.Kind == "player_start" || e.Kind == "key" || e.Kind == "spawn" || e.Kind == "flag" || e.Kind == "merchant" || e.Kind == "trap")
                    {
                        var c0 = level.CellOf(e.Pos);
                        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) Reserved.Add(c0.Offset(dx, dy));
                    }
                }
                foreach (var c in CellList)
                {
                    var rs = level.RoomSpans(c, room);
                    if (rs.Count == 0) continue;
                    int f = rs[0].Floor;
                    bool step = (f != room.Floor && Math.Abs(f - room.Floor) <= 24) || level.FloorSlope.ContainsKey(c);
                    if (!step) continue;
                    foreach (var (dx, dy) in D4) NearStep.Add(c.Offset(dx, dy));
                    NearStep.Add(c);
                }
                var g = level.Graph;
                DeadEnd = g != null && room.Node >= 0 && room.Node < g.Nodes.Count && g.Neighbors(room.Node).Distinct().Count() <= 1
                          && room.Kind != "start";
            }

            /// <summary>Remember which doorways are joined now: placing props must keep them joined.</summary>
            public void Snapshot() => baseComp = Components();

            /// <summary>Flat floor at the room's own floor level, nothing in the way (no water, hazard, ramp).</summary>
            public bool Flat(Cell c)
            {
                if (!Room.Contains(c) || Level.Diag.ContainsKey(c) || Level.Water.ContainsKey(c) || Level.Hazard.ContainsKey(c)
                    || Level.FloorSlope.ContainsKey(c)) return false;
                var rs = Level.RoomSpans(c, Room);
                return rs.Count == 1 && rs[0].Floor == Room.Floor;
            }

            /// <summary>A cell a floor prop may stand on.</summary>
            public bool Usable(Cell c) =>
                Flat(c) && !Ctx.Clear.Contains(c) && !Ctx.Used.Contains(c) && !Reserved.Contains(c) && !Occ.Contains(c) && !NearStep.Contains(c);

            public int Ceil(Cell c) => Level.RoomSpans(c, Room)[0].Ceil;

            /// <summary>Solid behind the cell's side dir over [z0, z1) (and not glass): a wall to put a prop on.</summary>
            public bool WallAt(Cell c, int dx, int dy, double z0, double z1)
            {
                var n = c.Offset(dx, dy);
                if (Room.Contains(n) || Level.Glass.Contains(n)) return false;
                return Level.FreeAt(n, (int)Math.Floor(z0), (int)Math.Ceiling(z1));
            }

            bool Walk(Cell c) => Room.Contains(c) && !Level.Diag.ContainsKey(c) && !Occ.Contains(c) && Level.RoomSpans(c, Room).Count > 0;

            /// <summary>Doorway cells -> component of the cells a 2-wide player can walk (4-moves, steps the
            /// validator allows).</summary>
            Dictionary<Cell, int> Components()
            {
                var walk = new HashSet<Cell>(CellList.Where(Walk));
                var wide = new HashSet<Cell>();
                foreach (var c in walk)
                    for (int ox = -1; ox <= 0; ox++)
                        for (int oy = -1; oy <= 0; oy++)
                        {
                            var a = c.Offset(ox, oy);
                            if (walk.Contains(a) && walk.Contains(a.Offset(1, 0)) && walk.Contains(a.Offset(0, 1)) && walk.Contains(a.Offset(1, 1)))
                            { wide.Add(c); ox = 1; break; }
                        }
                var comp = new Dictionary<Cell, int>();
                int id = 0;
                var outp = new Dictionary<Cell, int>();
                foreach (var d in Ctx.Doors)
                {
                    if (!wide.Contains(d)) continue;
                    if (!comp.ContainsKey(d))
                    {
                        var stack = new Stack<Cell>();
                        stack.Push(d);
                        comp[d] = id;
                        while (stack.Count > 0)
                        {
                            var c = stack.Pop();
                            var sc = Level.RoomSpans(c, Room)[0];
                            foreach (var (dx, dy) in D4)
                            {
                                var n = c.Offset(dx, dy);
                                if (!wide.Contains(n) || comp.ContainsKey(n)) continue;
                                if (!Validator.CanMove(Level, sc, Level.RoomSpans(n, Room)[0])) continue;
                                comp[n] = id;
                                stack.Push(n);
                            }
                        }
                        id++;
                    }
                    outp[d] = comp[d];
                }
                return outp;
            }

            /// <summary>Doorways joined before anything was placed are still joined.</summary>
            public bool DoorsJoined()
            {
                if (baseComp.Count < 2) return true;
                var now = Components();
                var map = new Dictionary<int, int>();
                foreach (var kv in baseComp)
                {
                    if (!now.TryGetValue(kv.Key, out var n)) return false;
                    if (map.TryGetValue(kv.Value, out var m)) { if (m != n) return false; }
                    else map[kv.Value] = n;
                }
                return true;
            }

            /// <summary>Take the floor cells of a floor prop if the doorways stay joined.</summary>
            public bool TryOccupy(IEnumerable<Cell> cells)
            {
                var added = cells.Where(c => Occ.Add(c)).ToList();
                if (DoorsJoined()) return true;
                foreach (var c in added) Occ.Remove(c);
                return false;
            }
        }

        static int CellsFor(double m) => Math.Max(1, (int)Math.Ceiling(m - 0.001));

        static Entity MakeProp(Level level, Room room, int? corr, string cls, string mount, Vec3 origin, int angle,
                               double w, double d, double h, Rng rng, bool breaks, params string[] tags)
        {
            var e = Markers.Make("prop", origin, angle, room);
            if (corr != null) e.With("corridor", corr.Value);
            e.With("class", cls).With("mount", mount).With("footprint", new[] { Math.Round(w, 2), Math.Round(d, 2), Math.Round(h, 2) })
             .With("variant", rng.Int(0, 7));
            var t = new List<string>(tags);
            if (breaks && rng.Random() < 0.15) t.Add("broken");
            if (h <= 0.02) t.Add("flat");                          // walk over it (a stain, a floor grate)
            if (t.Count > 0) e.With("tags", t.ToArray());
            e.Bounds = Markers.Box(origin, mount, w, d, h, angle);
            return e;
        }

        // ------------------------------------------------------------------------------------------
        // the furniture blocks
        // ------------------------------------------------------------------------------------------

        static void Blockout(RoomState st, Rng rng, List<Entity> props)
        {
            var level = st.Level;
            var room = st.Room;
            if (!room.Meta.TryGetValue("furnish_blocks", out var v) || !(v is List<(string Name, List<Cell> Cells, int? Height)> blocks)) return;
            int s = level.CellSize;
            var (rcx, rcy) = room.Center;
            foreach (var (name, cells, height) in blocks)
            {
                if (cells.Count == 0) continue;
                int x0 = cells.Min(c => c.X), x1 = cells.Max(c => c.X) + 1, y0 = cells.Min(c => c.Y), y1 = cells.Max(c => c.Y) + 1;
                bool alongX = x1 - x0 >= y1 - y0;
                double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
                int angle = alongX ? (rcy >= cy ? 90 : 270) : (rcx >= cx ? 0 : 180);   // facing the room's middle
                double w = alongX ? x1 - x0 : y1 - y0, d = alongX ? y1 - y0 : x1 - x0;
                double h = (height ?? room.Ceil - room.Floor) / (double)s;
                string cls = BlockClass.TryGetValue(name, out var bc) ? bc : name.Replace(' ', '_');
                var e = MakeProp(level, room, null, cls, "floor", new Vec3(cx * s, cy * s, room.Floor), angle, w, d, h, rng, false, "blockout");
                e.With("block", $"room:{room.Id}/{name}");
                props.Add(e);
                st.Space.Add(e.Bounds.Value);
                for (int x = x0; x < x1; x++) for (int y = y0; y < y1; y++) st.Occ.Add(new Cell(x, y));
            }
        }

        // ------------------------------------------------------------------------------------------
        // rooms
        // ------------------------------------------------------------------------------------------

        static int Count(Rng rng, double expected)
        {
            int k = (int)Math.Floor(expected);
            if (rng.Random() < expected - k) k++;
            return k;
        }

        static void DressRoom(RoomState st, Rng rng, List<Entity> props)
        {
            var level = st.Level;
            var room = st.Room;
            var cfg = level.Config;
            string prog = room.Get<string>("program") ?? "";
            if (room.Ceil - room.Floor < 96) return;              // a crawlspace: nothing stands in it
            var rules = Rules.TryGetValue(prog, out var rr) ? rr
                : prog == "" && Rules.TryGetValue("kind:" + room.Kind, out var kr) ? kr : Rules[""];
            double dens = cfg.DressingDensity * (ProgramDensity.TryGetValue(prog, out var pd) ? pd : 1.0);
            double size = Math.Max(0.5, Math.Min(3.0, st.Area / 80.0));
            foreach (var rule in rules)
            {
                int n = Count(rng, rule.N * dens * size);
                if (rule.Signature) n = Math.Max(n, 1);
                for (int i = 0; i < n; i++)
                {
                    var pc = Classes[rule.Class];
                    Entity e = null;
                    switch (rule.Place)
                    {
                        case Place.Wall: e = TryWall(st, pc, rng); break;
                        case Place.Free: e = TryFree(st, pc, rng, pc.W, pc.D, pc.H, ring: true); break;
                        case Place.Mount: e = TryMount(st, pc, rng, pc.W, pc.D, pc.H, pc.Elev, false); break;
                        case Place.Run: e = TryRun(st, rng); break;
                    }
                    if (e == null && rule.Signature && i == 0)     // the program's own piece: against a wall or not
                        e = rule.Place == Place.Wall ? TryFree(st, pc, rng, pc.W, pc.D, pc.H, ring: true)
                          : rule.Place == Place.Free ? TryWall(st, pc, rng) : null;
                    if (e == null) break;
                    props.Add(e);
                    Companions(st, rng, e, props);
                }
            }
            // generic clutter: debris and stains everywhere, more (and bones) in dead ends
            double clutter = cfg.DressingDensity * (st.DeadEnd ? 2.5 : 1.0);
            int nd = Count(rng, clutter * st.Area / 70.0), ns = Count(rng, cfg.DressingDensity * st.Area / 90.0);
            int nb = Count(rng, st.DeadEnd ? cfg.DressingDensity * 1.5 : 0.1 * cfg.DressingDensity);
            foreach (var (cls, n) in new[] { ("debris", nd), ("bones", nb), ("stain", ns) })
                for (int i = 0; i < n; i++)
                {
                    double sz = Math.Round(rng.Uniform(0.4, 1.5), 1);
                    double w = sz, d = cls == "bones" ? Math.Round(sz * 0.6, 1) : sz, h = cls == "debris" ? Math.Round(sz * 0.3, 2) : cls == "bones" ? 0.2 : 0.01;
                    var e = TryFree(st, Classes[cls], rng, w, d, h, ring: false);
                    if (e != null) props.Add(e);
                }
            DeadFixtures(st, rng, props);
            if (prog != "") Signs(st, rng, props, prog);
        }

        /// <summary>A floor prop with its back to a wall, facing into the room, with free floor in front of it.</summary>
        static Entity TryWall(RoomState st, PropClass pc, Rng rng)
        {
            var level = st.Level;
            var room = st.Room;
            int s = level.CellSize;
            int wc = CellsFor(pc.W), dc = CellsFor(pc.D);
            double z1 = room.Floor + pc.H * s;
            var cands = new List<(Cell C, int Dx, int Dy)>();
            foreach (var c in st.CellList)
                foreach (var (dx, dy) in D4)
                {
                    int tx = Math.Abs(dy), ty = Math.Abs(dx);       // along the wall
                    bool ok = true;
                    for (int k = 0; k < wc && ok; k++)
                    {
                        var b = c.Offset(tx * k, ty * k);
                        if (!st.WallAt(b, dx, dy, room.Floor, z1)) ok = false;
                        for (int j = 0; j <= dc && ok; j++)
                        {
                            var f = b.Offset(-dx * j, -dy * j);
                            if (j < dc) ok = st.Usable(f) && st.Ceil(f) >= z1;
                            else ok = st.Flat(f) && !st.Occ.Contains(f) && !st.Reserved.Contains(f);   // room to stand in front of it
                        }
                    }
                    if (ok) cands.Add((c, dx, dy));
                }
            rng.Shuffle(cands);
            foreach (var (c, dx, dy) in cands)
            {
                int tx = Math.Abs(dy), ty = Math.Abs(dx);
                // the wall line at the back, the middle of the wc cells along it, then d/2 into the room
                double wx = (c.X + 0.5 + dx * 0.5 + tx * (wc - 1) / 2.0) * s, wy = (c.Y + 0.5 + dy * 0.5 + ty * (wc - 1) / 2.0) * s;
                var origin = new Vec3(wx - dx * pc.D / 2 * s, wy - dy * pc.D / 2 * s, room.Floor);
                int angle = Markers.Angle(-dx, -dy);
                var box = Markers.Box(origin, "floor", pc.W, pc.D, pc.H, angle);
                if (!st.Space.Free(box)) continue;
                var cells = FloorCells(level, box).ToList();
                if (!cells.All(st.Usable) || !st.TryOccupy(cells)) continue;
                var e = MakeProp(level, room, null, pc.Name, "floor", origin, angle, pc.W, pc.D, pc.H, rng, pc.Breaks);
                st.Space.Add(box);
                return e;
            }
            return null;
        }

        /// <summary>A free-standing floor prop (with a free cell all round it when <paramref name="ring"/>).</summary>
        static Entity TryFree(RoomState st, PropClass pc, Rng rng, double w, double d, double h, bool ring)
        {
            var level = st.Level;
            var room = st.Room;
            int s = level.CellSize;
            bool flat = h <= 0.02;
            var cands = new List<(Cell C, int Angle)>();
            int[] angles = { 0, 90, 180, 270 };
            foreach (var c in st.CellList)
            {
                int a = angles[rng.Int(0, 3)];
                int ex = CellsFor(a == 0 || a == 180 ? d : w), ey = CellsFor(a == 0 || a == 180 ? w : d);
                bool ok = true;
                for (int x = -1; x <= ex && ok; x++)
                    for (int y = -1; y <= ey && ok; y++)
                    {
                        var f = c.Offset(x, y);
                        bool inside = x >= 0 && x < ex && y >= 0 && y < ey;
                        if (inside) ok = st.Usable(f) && st.Ceil(f) >= room.Floor + h * s;
                        else if (ring) ok = st.Room.Contains(f) && !st.Occ.Contains(f) && level.RoomSpans(f, room).Count > 0;
                    }
                if (ok) cands.Add((c, a));
            }
            rng.Shuffle(cands);
            foreach (var (c, a) in cands)
            {
                int ex = CellsFor(a == 0 || a == 180 ? d : w), ey = CellsFor(a == 0 || a == 180 ? w : d);
                var origin = new Vec3((c.X + ex / 2.0) * s, (c.Y + ey / 2.0) * s, room.Floor);
                var box = Markers.Box(origin, "floor", w, d, h, a);
                if (!st.Space.Free(box)) continue;
                var cells = FloorCells(level, box).ToList();
                if (!cells.All(st.Usable)) continue;
                if (!flat && !st.TryOccupy(cells)) continue;        // a stain is walked over: it takes no floor
                var e = MakeProp(level, room, null, pc.Name, "floor", origin, a, w, d, h, rng, pc.Breaks);
                st.Space.Add(box);
                return e;
            }
            return null;
        }

        /// <summary>A wall-mounted prop: a wall behind it over its height, its bottom elev metres up (in a
        /// doorway zone only if it hangs above <see cref="Headroom"/>).</summary>
        static Entity TryMount(RoomState st, PropClass pc, Rng rng, double w, double d, double h, double elev, bool nearDoors,
                               IEnumerable<(Cell, int, int)> only = null)
        {
            var level = st.Level;
            var room = st.Room;
            int s = level.CellSize;
            double z0 = room.Floor + elev * s, z1 = z0 + h * s;
            bool high = elev * s >= Headroom;
            var cands = new List<(Cell C, int Dx, int Dy)>();
            foreach (var (c, dx, dy) in only ?? st.CellList.SelectMany(c => D4.Select(p => (c, p.Item1, p.Item2))))
            {
                if (!st.Flat(c) || st.Reserved.Contains(c) || st.Ctx.Used.Contains(c)) continue;
                if (st.Ctx.Clear.Contains(c) && !high) continue;
                if (!nearDoors && st.NearStep.Contains(c)) continue;
                if (st.Ceil(c) < z1 || !st.WallAt(c, dx, dy, z0, z1)) continue;
                if (w > 1.0)                                       // wider than its cell: the wall goes on beside it
                {
                    int tx = Math.Abs(dy), ty = Math.Abs(dx);
                    var b = c.Offset(tx, ty);
                    if (!st.Flat(b) || st.Ceil(b) < z1 || !st.WallAt(b, dx, dy, z0, z1) || (st.Ctx.Clear.Contains(b) && !high)) continue;
                }
                cands.Add((c, dx, dy));
            }
            if (only == null) rng.Shuffle(cands);
            foreach (var (c, dx, dy) in cands)
            {
                int tx = Math.Abs(dy), ty = Math.Abs(dx);
                double off = w > 1.0 ? 0.5 : 0.0;
                var origin = new Vec3((c.X + 0.5 + dx * 0.5 + tx * off) * s, (c.Y + 0.5 + dy * 0.5 + ty * off) * s, z0);
                int angle = Markers.Angle(-dx, -dy);
                var box = Markers.Box(origin, "wall", w, d, h, angle);
                if (!st.Space.Free(box)) continue;
                var e = MakeProp(level, room, null, pc.Name, "wall", origin, angle, w, d, h, rng, pc.Breaks);
                st.Space.Add(box);
                return e;
            }
            return null;
        }

        /// <summary>A pipe run along a straight stretch of wall (3-8 m), high up.</summary>
        static Entity TryRun(RoomState st, Rng rng)
        {
            var level = st.Level;
            var room = st.Room;
            int s = level.CellSize;
            var pc = Classes["pipe_run"];
            double z0 = room.Floor + pc.Elev * s, z1 = z0 + pc.H * s;
            var ok = new HashSet<(Cell, int, int)>();
            foreach (var c in st.CellList)
                if (st.Flat(c) && st.Ceil(c) >= z1 && !st.Reserved.Contains(c))
                    foreach (var (dx, dy) in D4)
                        if (st.WallAt(c, dx, dy, z0, z1)) ok.Add((c, dx, dy));
            var runs = new List<(Cell Start, int Dx, int Dy, int Len)>();
            foreach (var (c, dx, dy) in ok.OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3))
            {
                int tx = Math.Abs(dy), ty = Math.Abs(dx);
                if (ok.Contains((c.Offset(-tx, -ty), dx, dy))) continue;   // not the start of a run
                int len = 1;
                while (ok.Contains((c.Offset(tx * len, ty * len), dx, dy))) len++;
                if (len >= 3) runs.Add((c, dx, dy, len));
            }
            rng.Shuffle(runs);
            foreach (var (c, dx, dy, len) in runs)
            {
                int tx = Math.Abs(dy), ty = Math.Abs(dx);
                int L = Math.Min(len, rng.Int(3, 8));
                int o = rng.Int(0, len - L);
                var a = c.Offset(tx * o, ty * o);
                var origin = new Vec3((a.X + 0.5 + dx * 0.5 + tx * (L - 1) / 2.0) * s, (a.Y + 0.5 + dy * 0.5 + ty * (L - 1) / 2.0) * s, z0);
                int angle = Markers.Angle(-dx, -dy);
                var box = Markers.Box(origin, "wall", L, pc.D, pc.H, angle);
                if (!st.Space.Free(box)) continue;
                var e = MakeProp(level, room, null, "pipe_run", "wall", origin, angle, L, pc.D, pc.H, rng, true);
                st.Space.Add(box);
                return e;
            }
            return null;
        }

        /// <summary>A chair at a desk, stools round a table.</summary>
        static void Companions(RoomState st, Rng rng, Entity e, List<Entity> props)
        {
            string cls = (string)e.Props["class"];
            if (cls != "desk" && cls != "table") return;
            var level = st.Level;
            int s = level.CellSize;
            int angle = (int)e.Props["angle"];
            var (fx, fy) = Markers.Dir(angle);
            var fp = (double[])e.Props["footprint"];
            var spots = new List<(double X, double Y, int Angle)>();
            if (cls == "desk")
            {
                if (rng.Random() < 0.85) spots.Add((e.Pos.X + fx * (fp[1] / 2 + 0.45) * s, e.Pos.Y + fy * (fp[1] / 2 + 0.45) * s, (angle + 180) % 360));
            }
            else
            {
                int rx = fy, ry = -fx;                             // its right
                spots.Add((e.Pos.X + fx * (fp[1] / 2 + 0.45) * s, e.Pos.Y + fy * (fp[1] / 2 + 0.45) * s, (angle + 180) % 360));
                spots.Add((e.Pos.X - fx * (fp[1] / 2 + 0.45) * s, e.Pos.Y - fy * (fp[1] / 2 + 0.45) * s, angle));
                spots.Add((e.Pos.X + rx * (fp[0] / 2 + 0.45) * s, e.Pos.Y + ry * (fp[0] / 2 + 0.45) * s, Markers.Angle(-rx, -ry)));
                spots.Add((e.Pos.X - rx * (fp[0] / 2 + 0.45) * s, e.Pos.Y - ry * (fp[0] / 2 + 0.45) * s, Markers.Angle(rx, ry)));
                int keep = rng.Int(2, 4);
                rng.Shuffle(spots);
                spots = spots.Take(keep).ToList();
            }
            var pc = Classes[cls == "desk" ? "chair" : "stool"];
            foreach (var (x, y, a) in spots)
            {
                var c = new Cell((int)Math.Floor(x / s), (int)Math.Floor(y / s));
                var origin = new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, st.Room.Floor);   // in the middle of its cell
                var box = Markers.Box(origin, "floor", pc.W, pc.D, pc.H, a);
                if (!st.Usable(c) || st.Ceil(c) < st.Room.Floor + pc.H * s || !st.Space.Free(box) || !st.TryOccupy(new[] { c })) continue;
                props.Add(MakeProp(level, st.Room, null, pc.Name, "floor", origin, a, pc.W, pc.D, pc.H, rng, pc.Breaks));
                st.Space.Add(box);
            }
        }

        /// <summary>Dead light fixtures on the ceiling next to some of the room's lights (Solitude kills some lights).</summary>
        static void DeadFixtures(RoomState st, Rng rng, List<Entity> props)
        {
            var level = st.Level;
            var room = st.Room;
            int s = level.CellSize;
            var pc = Classes["light_fixture_dead"];
            foreach (var light in level.Entities.Where(e => e.Kind == "light"))
            {
                var lc = level.CellOf(light.Pos);
                if (!room.Contains(lc) || light.Pos.Z < room.Floor || light.Pos.Z > room.Ceil) continue;
                if (rng.Random() >= 0.35 * Math.Min(1.0, level.Config.DressingDensity / 0.6)) continue;
                var dirs = D4.ToList();
                rng.Shuffle(dirs);
                foreach (var (dx, dy) in dirs)
                {
                    var c = lc.Offset(dx * 2, dy * 2);
                    if (!st.Flat(c) || level.Sky.Contains(c) || level.CeilSlope.ContainsKey(c)) continue;
                    int ceil = st.Ceil(c);
                    if (ceil - pc.H * s - room.Floor < Headroom) continue;
                    int angle = Markers.Angle(dx, dy);
                    var origin = new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, ceil);
                    var box = Markers.Box(origin, "ceiling", pc.W, pc.D, pc.H, angle);
                    if (!st.Space.Free(box)) continue;
                    props.Add(MakeProp(level, room, null, pc.Name, "ceiling", origin, angle, pc.W, pc.D, pc.H, rng, false));
                    st.Space.Add(box);
                    break;
                }
            }
        }

        /// <summary>A room's sign text: its program in capitals, numbered when there are several ("LAB 2").</summary>
        public static string SignText(Level level, Room room, string prog)
        {
            var same = level.Rooms.Where(r => r.Get<string>("program") == prog).OrderBy(r => r.Id).ToList();
            string name = prog switch
            {
                "pump" => "PUMP ROOM", "server" => "SERVER ROOM", "control" => "CONTROL ROOM", "boiler" => "BOILER ROOM",
                "generator" => "GENERATOR ROOM", "sonar_room" => "SONAR", "test_chamber" => "TEST CHAMBER",
                _ => prog.Replace('_', ' ').ToUpperInvariant(),
            };
            return same.Count > 1 ? $"{name} {same.IndexOf(room) + 1}" : name;
        }

        /// <summary>A sign on the wall beside a doorway (one or two per room), its text the room's program.</summary>
        static void Signs(RoomState st, Rng rng, List<Entity> props, string prog)
        {
            var level = st.Level;
            var room = st.Room;
            var pc = Classes["sign"];
            var doorSet = new HashSet<Cell>(st.Ctx.Doors);
            var seen = new HashSet<Cell>();
            var ways = new List<List<(Cell, int, int)>>();          // per doorway: the wall spots beside it
            foreach (var d in st.Ctx.Doors.OrderBy(c => c))
            {
                if (seen.Contains(d)) continue;
                var comp = new List<Cell>();
                var stack = new Stack<Cell>();
                stack.Push(d); seen.Add(d);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    comp.Add(c);
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                    {
                        var n = c.Offset(dx, dy);
                        if (doorSet.Contains(n) && seen.Add(n)) stack.Push(n);
                    }
                }
                var spots = new List<(Cell, int, int)>();
                foreach (var c in comp.OrderBy(c => c))
                    foreach (var (dx, dy) in D4)
                    {
                        var o = c.Offset(dx, dy);
                        if (room.Contains(o) || !level.SpansIn(o, room.Floor, room.Ceil).Any()) continue;   // not the way out
                        int tx = Math.Abs(dy), ty = Math.Abs(dx);
                        for (int k = 1; k <= 3; k++)
                            foreach (int sgn in new[] { 1, -1 })
                            {
                                var b = c.Offset(tx * k * sgn, ty * k * sgn);
                                spots.Add((b, dx, dy));     // (TryMount keeps those with a wall behind)
                            }
                    }
                ways.Add(spots);
            }
            rng.Shuffle(ways);
            int placed = 0;
            foreach (var spots in ways)
            {
                if (placed >= 2) break;
                var e = TryMount(st, pc, rng, pc.W, pc.D, pc.H, pc.Elev, true, spots.Distinct().ToList());
                if (e == null) continue;
                e.With("text", SignText(level, room, prog));
                props.Add(e);
                placed++;
            }
        }

        // ------------------------------------------------------------------------------------------
        // corridors: runs, not furniture
        // ------------------------------------------------------------------------------------------

        static void DressCorridor(Level level, Corridor corr, Space space, Rng rng, List<Entity> props)
        {
            var cfg = level.Config;
            int s = level.CellSize;
            string tag = $"corridor:{corr.Id}";
            var path = corr.Path;
            var floors = corr.Floors;
            if (path.Count < 2) return;
            var doorCells = new HashSet<Cell>();
            foreach (var e in level.Entities)
                if ((e.Kind == "door" || e.Kind == "secret_door") && e.Bounds != null)
                    foreach (var c in FloorCells(level, e.Bounds.Value)) doorCells.Add(c);
            bool Mine(Cell c, int f) => level.OwnerAt(c, f) == tag && level.SpansIn(c, f, f + 1).Any();
            int CeilAt(Cell c, int f) => level.SpansIn(c, f, f + 1).First().Ceil;
            double dens = Math.Min(1.0, cfg.DressingDensity / 0.6);

            if (corr.Vent)
            {
                // a grate at each mouth of the duct, inside the duct's end cell, facing the room
                foreach (var (i, j) in new[] { (0, 1), (path.Count - 1, path.Count - 2) })
                {
                    var p = path[i];
                    int dx = p.X - path[j].X, dy = p.Y - path[j].Y;
                    if (Math.Abs(dx) + Math.Abs(dy) != 1 || !Mine(p, floors[i])) continue;
                    var outside = p.Offset(dx, dy);
                    var own = level.OwnerAt(outside, floors[i]) ?? "";
                    if (!own.StartsWith("room:")) continue;
                    double w = 2 * corr.Radius + 1 - 0.2, h = Math.Min(corr.Height, CeilAt(p, floors[i]) - floors[i]) / (double)s - 0.2;
                    var origin = new Vec3((p.X + 0.5 + dx * 0.4) * s, (p.Y + 0.5 + dy * 0.4) * s, floors[i] + 0.1 * s);
                    int angle = Markers.Angle(dx, dy);
                    var box = Markers.Box(origin, "wall", w, 0.1, h, angle);
                    if (!space.Free(box)) continue;
                    var e = MakeProp(level, null, corr.Id, "vent_grate", "wall", origin, angle, w, 0.1, h, rng, true, "vent");
                    props.Add(e);
                    space.Add(box);
                }
                return;
            }

            // straight, level runs of the path (4 cells or more), broken at doors
            var runs = new List<(int A, int B, int Dx, int Dy)>();
            int i0 = 0;
            while (i0 < path.Count - 1)
            {
                int rdx = path[i0 + 1].X - path[i0].X, rdy = path[i0 + 1].Y - path[i0].Y;
                if (Math.Abs(rdx) + Math.Abs(rdy) != 1 || doorCells.Contains(path[i0])) { i0++; continue; }
                int j = i0;
                while (j + 1 < path.Count && path[j + 1].X - path[j].X == rdx && path[j + 1].Y - path[j].Y == rdy
                       && floors[j + 1] == floors[i0] && !doorCells.Contains(path[j + 1])) j++;
                if (j - i0 + 1 >= 4) runs.Add((i0, j, rdx, rdy));
                i0 = j + 1;
            }
            foreach (var (a0, b0, dx, dy) in runs)
            {
                int a = a0 + 1, b = b0 - 1;                        // off the ends (junctions, room mouths)
                int L = b - a + 1;
                if (L < 3) continue;
                int f = floors[a];
                var cells = Enumerable.Range(a, L).Select(k => path[k]).ToList();
                if (!cells.All(c => Mine(c, f))) continue;
                int ceil = cells.Min(c => CeilAt(c, f));
                if (ceil - f < 96) continue;
                double mx = (cells[0].X + cells[L - 1].X) / 2.0 + 0.5, my = (cells[0].Y + cells[L - 1].Y) / 2.0 + 0.5;
                int fwd = Markers.Angle(dy, -dx);                  // its right runs along the corridor
                // cables along the ceiling: most corridors
                if (rng.Random() < 0.45 + 0.45 * dens)
                {
                    var origin = new Vec3(mx * s, my * s, ceil);
                    var box = Markers.Box(origin, "ceiling", L, 0.2, 0.2, fwd);
                    if (space.Free(box))
                    {
                        props.Add(MakeProp(level, null, corr.Id, "cable_run", "ceiling", origin, fwd, L, 0.2, 0.2, rng, true));
                        space.Add(box);
                    }
                }
                // a pipe along one wall, and maybe a valve on it
                if (rng.Random() < 0.25 + 0.35 * dens)
                {
                    int side = rng.Random() < 0.5 ? 1 : -1;
                    int px = -dy * side, py = dx * side;            // towards that wall
                    var pipe = Classes["pipe_run"];
                    double z0 = f + pipe.Elev * s, z1 = z0 + pipe.H * s;
                    var edge = cells.Select(c => c.Offset(px * corr.Radius, py * corr.Radius)).ToList();
                    bool ok = ceil >= z1 && edge.All(c => Mine(c, f) && CeilAt(c, f) >= z1)
                              && edge.All(c => !level.SpansIn(c.Offset(px, py), (int)z0, (int)Math.Ceiling(z1)).Any() && !level.Glass.Contains(c.Offset(px, py)));
                    if (ok)
                    {
                        double ex = (edge[0].X + edge[L - 1].X) / 2.0 + 0.5 + px * 0.5, ey = (edge[0].Y + edge[L - 1].Y) / 2.0 + 0.5 + py * 0.5;
                        int ang = Markers.Angle(-px, -py);
                        var origin = new Vec3(ex * s, ey * s, z0);
                        var box = Markers.Box(origin, "wall", L, pipe.D, pipe.H, ang);
                        if (space.Free(box))
                        {
                            props.Add(MakeProp(level, null, corr.Id, "pipe_run", "wall", origin, ang, L, pipe.D, pipe.H, rng, true));
                            space.Add(box);
                            var valve = Classes["valve_wheel"];
                            if (rng.Random() < 0.5)
                            {
                                var vc = edge[rng.Int(0, L - 1)];
                                double v0 = f + valve.Elev * s, v1 = v0 + valve.H * s;
                                if (!level.SpansIn(vc.Offset(px, py), (int)v0, (int)Math.Ceiling(v1)).Any())
                                {
                                    var vo = new Vec3((vc.X + 0.5 + px * 0.5) * s, (vc.Y + 0.5 + py * 0.5) * s, v0);
                                    var vb = Markers.Box(vo, "wall", valve.W, valve.D, valve.H, ang);
                                    if (space.Free(vb))
                                    {
                                        props.Add(MakeProp(level, null, corr.Id, "valve_wheel", "wall", vo, ang, valve.W, valve.D, valve.H, rng, true));
                                        space.Add(vb);
                                    }
                                }
                            }
                        }
                    }
                }
                // now and then a grate or a stain on the floor (flat: walked over)
                if (rng.Random() < 0.3 * dens)
                {
                    var c = cells[rng.Int(0, L - 1)];
                    bool grate = rng.Random() < 0.5;
                    double w = grate ? 0.8 : Math.Round(rng.Uniform(0.6, 1.4), 1), d = grate ? 0.6 : w;
                    var origin = new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, f);
                    var box = Markers.Box(origin, "floor", w, d, grate ? 0.02 : 0.01, fwd);
                    if (!level.Water.ContainsKey(c) && space.Free(box))
                    {
                        props.Add(MakeProp(level, null, corr.Id, grate ? "vent_grate" : "stain", "floor", origin, fwd, w, d, grate ? 0.02 : 0.01, rng, false));
                        space.Add(box);
                    }
                }
            }
        }
    }
}
