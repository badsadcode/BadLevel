using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Gameplay markers for a station (cfg.StationEntities) and viewports (cfg.Viewports): places a
    /// game acts on, in the same marker format as Dressing's props (<see cref="Markers"/>), appended after
    /// them. Positions come from label streams (<c>{seed}/{variant}/station/...</c>), never the shared one.
    /// <list type="bullet">
    /// <item><c>loot</c> (size small/medium/large): on desks, shelves, workbenches, tables, crates, or on
    /// the floor by a wall; 1-4 per room by program. Always reachable (from beside it, for one on a block
    /// the player can't step onto).</item>
    /// <item><c>log_terminal</c>: on a console or a desk.</item>
    /// <item><c>creature_spawn</c> (role nest / ambush / patrol): nests in the 1-2 dead-end rooms
    /// farthest from the start (graph distance), ambushes at corridor corners and vent mouths, a patrol in
    /// each sector's biggest room. Always reachable.</item>
    /// <item><c>hiding_spot</c>: in front of a locker, under a desk.</item>
    /// <item><c>power_junction</c>: on a wall, one per sector, in its engineering-like room.</item>
    /// <item><c>hazard_zone</c> (type flooded / electrified, with Bounds): each flooded area (not a moon
    /// pool); round some broken equipment.</item>
    /// <item><c>airlock</c>: in the player_start room, on the wall behind the player (where they came in).</item>
    /// <item><c>viewport</c> (cfg.Viewports): a porthole on an outer wall (nothing open behind it for 3 m).</item>
    /// </list>
    /// The validator checks (when cfg.StationEntities is on) that every creature_spawn and loot is reachable:
    /// <see cref="ReachReport"/>.</summary>
    public static class StationEntities
    {
        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Loot per room (min, max) by program; others 1-2.</summary>
        public static readonly Dictionary<string, (int Lo, int Hi)> LootPerRoom = new Dictionary<string, (int, int)>
        {
            ["storage"] = (2, 4), ["warehouse"] = (2, 4), ["loading"] = (1, 3), ["office"] = (1, 3), ["security"] = (2, 3),
            ["quarters"] = (1, 3), ["workshop"] = (1, 3), ["medbay"] = (2, 3), ["galley"] = (1, 3), ["dive_locker"] = (1, 2),
            ["closet"] = (1, 2), ["lab"] = (1, 2),
        };

        /// <summary>Where loot sits on a prop: its surface's height (metres).</summary>
        public static readonly Dictionary<string, double> Surfaces = new Dictionary<string, double>
        {
            ["desk"] = 0.75, ["table"] = 0.75, ["workbench"] = 0.95, ["bench_lab"] = 0.95, ["shelf"] = 1.0, ["crate"] = 0.8,
            ["barrel"] = 0.9, ["counter"] = 0, ["cabinet"] = 1.0,
        };

        /// <summary>Programs that hold a sector's power junction, first choice first.</summary>
        public static readonly string[] PowerRooms = { "generator", "pump", "boiler", "reactor", "workshop", "control", "server" };

        /// <summary>Things the player picks up (loot) can be grabbed from beside them: from a reachable floor
        /// in the same cell or next to it, at most this far below the item (units, 2 m).</summary>
        public const int Grab = 64;

        public static int Run(Level level)
        {
            var cfg = level.Config;
            int before = level.Entities.Count;
            var space = new Dressing.Space(level);
            var groups = new List<List<Entity>>();
            if (cfg.StationEntities && cfg.Mode != "arena")
            {
                var start = level.Entities.FirstOrDefault(e => e.Kind == "player_start");
                var sn = start == null ? null : Validator.NodeAt(level, start.Pos);
                if (sn != null)
                {
                    var reach = Validator.Flood(level, sn.Value, new HashSet<Cell>(), Validator.LinkEdges(level));
                    var ctx = new Ctx(level, space, reach, start);
                    groups.Add(Loot(ctx));
                    groups.Add(LogTerminals(ctx));
                    groups.Add(CreatureSpawns(ctx));
                    groups.Add(HidingSpots(ctx));
                    groups.Add(PowerJunctions(ctx));
                    groups.Add(HazardZones(ctx));
                    groups.Add(Airlock(ctx));
                }
            }
            if (cfg.Viewports) groups.Add(Viewports(level, space));
            foreach (var g in groups) level.Entities.AddRange(g);
            int n = level.Entities.Count - before;
            if (n > 0) level.Notes.Add($"station markers: {n}");
            return n;
        }

        sealed class Ctx
        {
            public readonly Level Level;
            public readonly Dressing.Space Space;
            public readonly HashSet<(int, int, int)> Reach;
            public readonly Entity Start;
            public readonly List<(Entity E, string Id)> Props = new List<(Entity, string)>();
            readonly Dictionary<int, Dressing.RoomState> states = new Dictionary<int, Dressing.RoomState>();

            public Ctx(Level level, Dressing.Space space, HashSet<(int, int, int)> reach, Entity start)
            {
                Level = level; Space = space; Reach = reach; Start = start;
                int k = 0;
                foreach (var e in level.Entities)
                    if (e.Kind == "prop") Props.Add((e, $"prop_{++k}"));      // the GLB node names
            }

            public Dressing.RoomState State(Room room)
            {
                if (!states.TryGetValue(room.Id, out var st)) states[room.Id] = st = new Dressing.RoomState(Level, room, Space);
                return st;
            }

            public Rng Rng(string what, int id) => NoorLevel.Rng.FromLabel($"{Level.Config.Seed}/{Level.Config.Variant}/station/{what}/{id}");

            public IEnumerable<(Entity E, string Id)> PropsIn(Room room) =>
                Props.Where(p => p.E.Props.TryGetValue("room", out var r) && r is int i && i == room.Id);

            public bool Stand(Vec3 pos) => Reachable(Level, Reach, pos, false);
        }

        /// <summary>Can a player get to this spot (stand on it), or, with <paramref name="grab"/>, reach it from
        /// a reachable floor in its cell or next to it, at most <see cref="Grab"/> below it?</summary>
        public static bool Reachable(Level level, HashSet<(int, int, int)> reach, Vec3 pos, bool grab)
        {
            var n = Validator.NodeAt(level, pos);
            if (n != null && reach.Contains(n.Value)) return true;
            if (!grab) return false;
            var c = level.CellOf(pos);
            foreach (var p in new[] { c, c.Offset(1, 0), c.Offset(-1, 0), c.Offset(0, 1), c.Offset(0, -1) })
                if (level.Spans.TryGetValue(p, out var spans))
                    for (int i = 0; i < spans.Count; i++)
                        if (reach.Contains((p.X, p.Y, i)) && spans[i].Floor <= pos.Z + 8 && pos.Z - spans[i].Floor <= Grab && spans[i].Ceil > pos.Z)
                            return true;
            return false;
        }

        /// <summary>The validator's rule (cfg.StationEntities): every creature_spawn stands where the player can
        /// get to, every loot can be grabbed. <paramref name="reach"/>: what's reachable with every key.</summary>
        public static List<string> ReachReport(Level level, HashSet<(int, int, int)> reach)
        {
            var errors = new List<string>();
            foreach (var e in level.Entities)
                if ((e.Kind == "creature_spawn" && !Reachable(level, reach, e.Pos, false))
                    || (e.Kind == "loot" && !Reachable(level, reach, e.Pos, true)))
                    errors.Add($"{e.Kind} at {e.Pos} unreachable");
            return errors;
        }

        static (Vec3, Vec3) Small(Vec3 p, double r = 6) => (new Vec3(p.X - r, p.Y - r, p.Z), new Vec3(p.X + r, p.Y + r, p.Z + 2 * r));

        static Vec3 CellFloor(Level level, Cell c, int floor) => new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, floor);

        static IEnumerable<Room> Rooms(Level level) => level.Rooms.Where(r => !r.IsConnector);

        // ------------------------------------------------------------------------------------------

        static List<Entity> Loot(Ctx x)
        {
            var level = x.Level;
            var outp = new List<Entity>();
            foreach (var room in Rooms(level))
            {
                var rng = x.Rng("loot", room.Id);
                string prog = room.Get<string>("program") ?? "";
                var (lo, hi) = LootPerRoom.TryGetValue(prog, out var lr) ? lr : (1, 2);
                int want = rng.Int(lo, hi);
                var st = x.State(room);
                // on things first
                var tops = new List<(Vec3 Pos, int Angle, string Id)>();
                foreach (var (p, id) in x.PropsIn(room))
                {
                    string cls = (string)p.Props["class"];
                    if (!Surfaces.TryGetValue(cls, out var sh)) continue;
                    var fp = (double[])p.Props["footprint"];
                    double z = p.Pos.Z + (p.Props.TryGetValue("tags", out var t) && ((string[])t).Contains("blockout") ? fp[2] : sh) * Markers.M;
                    if (z > p.Pos.Z + fp[2] * Markers.M + 0.01) continue;
                    tops.Add((new Vec3(p.Pos.X, p.Pos.Y, z), (int)p.Props["angle"], id));
                }
                rng.Shuffle(tops);
                // by the walls
                var floor = new List<Cell>();
                foreach (var c in st.CellList)
                    if (st.Flat(c) && !st.Ctx.Clear.Contains(c) && D4.Any(d => st.WallAt(c, d.Item1, d.Item2, room.Floor, room.Floor + 32)))
                        floor.Add(c);
                rng.Shuffle(floor);
                int ti = 0, fi = 0;
                for (int k = 0; k < want; k++)
                {
                    bool onTop = rng.Random() < 0.7;
                    Entity e = null;
                    for (int pass = 0; pass < 2 && e == null; pass++, onTop = !onTop)
                    {
                        if (onTop)
                            while (ti < tops.Count && e == null)
                            {
                                var (pos, ang, id) = tops[ti++];
                                if (Validator.NodeAt(level, pos) == null || !Reachable(level, x.Reach, pos, true)) continue;
                                e = Markers.Make("loot", pos, ang, room).With("class", "loot")
                                    .With("size", rng.Random() < 0.6 ? "small" : "medium").With("on", id);
                            }
                        else
                            while (fi < floor.Count && e == null)
                            {
                                var pos = CellFloor(level, floor[fi++], room.Floor);
                                if (!x.Space.Free(Small(pos)) || !Reachable(level, x.Reach, pos, true)) continue;
                                e = Markers.Make("loot", pos, null, room).With("class", "loot")
                                    .With("size", rng.Random() < 0.6 ? "medium" : "large");
                                x.Space.Add(Small(pos));
                            }
                    }
                    if (e == null) break;
                    outp.Add(e);
                }
            }
            return outp;
        }

        static List<Entity> LogTerminals(Ctx x)
        {
            var outp = new List<Entity>();
            var skipped = new List<(Room, List<(Entity, string)>)>();
            bool Put(Room room, List<(Entity E, string Id)> cands)
            {
                foreach (var (p, id) in cands)
                {
                    var fp = (double[])p.Props["footprint"];
                    var pos = new Vec3(p.Pos.X, p.Pos.Y, p.Pos.Z + fp[2] * Markers.M);
                    if (Validator.NodeAt(x.Level, pos) == null) continue;   // a solid block: no top to stand it on
                    outp.Add(Markers.Make("log_terminal", pos, (int)p.Props["angle"], room).With("class", "log_terminal").With("on", id));
                    return true;
                }
                return false;
            }
            foreach (var room in Rooms(x.Level))
            {
                var rng = x.Rng("log", room.Id);
                var cands = x.PropsIn(room).Where(p => (string)p.E.Props["class"] is var c && (c == "console" || c == "desk")).ToList();
                if (cands.Count == 0) continue;
                rng.Shuffle(cands);
                if (rng.Random() >= 0.6) skipped.Add((room, cands));
                else Put(room, cands);
            }
            foreach (var (room, cands) in skipped)                 // every station keeps at least one log
            {
                if (outp.Count > 0) break;
                Put(room, cands);
            }
            return outp;
        }

        static List<Entity> CreatureSpawns(Ctx x)
        {
            var level = x.Level;
            var g = level.Graph;
            int s = level.CellSize;
            var outp = new List<Entity>();
            // nests: the dead ends farthest from the start
            var startRoom = level.Rooms.FirstOrDefault(r => r.Contains(level.CellOf(x.Start.Pos)) && r.Kind == "start")
                            ?? level.Rooms.FirstOrDefault(r => r.Kind == "start");
            var dist = new Dictionary<int, int>();
            if (startRoom != null && g != null)
            {
                var q = new Queue<int>();
                dist[startRoom.Node] = 0; q.Enqueue(startRoom.Node);
                while (q.Count > 0)
                {
                    int a = q.Dequeue();
                    foreach (var b in g.Neighbors(a).OrderBy(b => b))
                        if (!dist.ContainsKey(b)) { dist[b] = dist[a] + 1; q.Enqueue(b); }
                }
            }
            var rng0 = x.Rng("nest", 0);
            var dead = Rooms(level).Where(r => r != startRoom && dist.ContainsKey(r.Node) && g.Neighbors(r.Node).Distinct().Count() <= 1)
                                   .OrderByDescending(r => dist[r.Node]).ThenBy(r => r.Id).ToList();
            int nests = Math.Min(dead.Count, rng0.Int(1, 2));
            foreach (var room in dead)
            {
                if (nests == 0) break;
                var st = x.State(room);
                Cell? best = null; int bd = -1;
                foreach (var c in st.CellList)
                {
                    if (!st.Flat(c)) continue;
                    var pos = CellFloor(level, c, room.Floor);
                    if (!x.Space.Free(Small(pos)) || !x.Stand(pos)) continue;
                    int d = st.Ctx.Doors.Count == 0 ? 0 : st.Ctx.Doors.Min(dd => Math.Abs(dd.X - c.X) + Math.Abs(dd.Y - c.Y));
                    if (d > bd) { bd = d; best = c; }
                }
                if (best == null) continue;
                var bp = CellFloor(level, best.Value, room.Floor);
                outp.Add(Markers.Make("creature_spawn", bp, TowardDoor(st, best.Value), room).With("class", "creature").With("role", "nest"));
                x.Space.Add(Small(bp));
                nests--;
            }
            // ambushes: corridor corners, vent mouths
            foreach (var corr in level.Corridors)
            {
                var rng = x.Rng("ambush", corr.Id);
                var path = corr.Path; var fl = corr.Floors;
                if (path.Count < 3) continue;
                var spots = new List<(int I, int Dx, int Dy)>();
                if (corr.Vent)
                {
                    spots.Add((0, path[0].X - path[1].X, path[0].Y - path[1].Y));
                    int l = path.Count - 1;
                    spots.Add((l, path[l].X - path[l - 1].X, path[l].Y - path[l - 1].Y));
                    if (rng.Random() >= 0.6) continue;
                }
                else
                {
                    for (int i = 1; i < path.Count - 1; i++)
                    {
                        int ax = path[i].X - path[i - 1].X, ay = path[i].Y - path[i - 1].Y, bx = path[i + 1].X - path[i].X, by = path[i + 1].Y - path[i].Y;
                        if ((ax != bx || ay != by) && Math.Abs(ax) + Math.Abs(ay) == 1 && Math.Abs(bx) + Math.Abs(by) == 1
                            && fl[i - 1] == fl[i] && fl[i] == fl[i + 1]) spots.Add((i, -bx, -by));   // facing back the way you come
                    }
                    if (spots.Count == 0 || rng.Random() >= 0.4) continue;
                }
                rng.Shuffle(spots);
                foreach (var (i, dx, dy) in spots)
                {
                    var pos = CellFloor(level, path[i], fl[i]);
                    if (Math.Abs(dx) + Math.Abs(dy) != 1 || !x.Stand(pos) || !x.Space.Free(Small(pos))) continue;
                    var e = Markers.Make("creature_spawn", pos, Markers.Angle(dx, dy), null).With("corridor", corr.Id)
                        .With("class", "creature").With("role", "ambush");
                    if (corr.Vent) e.With("tags", new[] { "vent" });
                    outp.Add(e);
                    x.Space.Add(Small(pos));
                    break;
                }
            }
            // patrols: each sector's biggest room
            foreach (var grp in Rooms(level).Where(r => r != startRoom && r.Kind != "secret")
                                            .GroupBy(r => r.Get<string>("sector") ?? $"zone{(g != null && r.Node < g.Nodes.Count ? g.Nodes[r.Node].Zone : 0)}")
                                            .OrderBy(gr => gr.Min(r => r.Id)))
            {
                var room = grp.OrderByDescending(r => r.W * r.H).ThenBy(r => r.Id).First();
                var st = x.State(room);
                var (cx, cy) = room.Center;
                foreach (var c in st.CellList.Where(st.Flat).OrderBy(c => Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2)).ThenBy(c => c))
                {
                    var pos = CellFloor(level, c, room.Floor);
                    if (!x.Space.Free(Small(pos)) || !x.Stand(pos)) continue;
                    outp.Add(Markers.Make("creature_spawn", pos, TowardDoor(st, c), room).With("class", "creature").With("role", "patrol"));
                    x.Space.Add(Small(pos));
                    break;
                }
            }
            return outp;
        }

        /// <summary>The map yaw (a multiple of 90) from a cell towards the room's nearest doorway.</summary>
        static int TowardDoor(Dressing.RoomState st, Cell c)
        {
            if (st.Ctx.Doors.Count == 0) return 0;
            var d = st.Ctx.Doors.OrderBy(dd => Math.Abs(dd.X - c.X) + Math.Abs(dd.Y - c.Y)).ThenBy(dd => dd).First();
            int dx = d.X - c.X, dy = d.Y - c.Y;
            return Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 180) : (dy > 0 ? 90 : 270);
        }

        static List<Entity> HidingSpots(Ctx x)
        {
            var level = x.Level;
            var outp = new List<Entity>();
            foreach (var room in Rooms(level))
            {
                var rng = x.Rng("hide", room.Id);
                var st = x.State(room);
                var cands = x.PropsIn(room).Where(p => (string)p.E.Props["class"] is var c && (c == "locker" || c == "desk")
                                                       && !(p.E.Props.TryGetValue("tags", out var t) && ((string[])t).Contains("blockout"))).ToList();
                rng.Shuffle(cands);
                int n = 0;
                foreach (var (p, id) in cands)
                {
                    if (n >= 2) break;
                    if (rng.Random() >= 0.6) continue;
                    string cls = (string)p.Props["class"];
                    int ang = (int)p.Props["angle"];
                    Vec3 pos;
                    int face;
                    if (cls == "locker")                                  // stand in front of it, facing it
                    {
                        var (fx, fy) = Markers.Dir(ang);
                        var c = level.CellOf(p.Pos).Offset(fx, fy);
                        if (!st.Flat(c)) continue;
                        pos = CellFloor(level, c, room.Floor);
                        if (!x.Space.Free(Small(pos))) continue;
                        face = (ang + 180) % 360;
                    }
                    else { pos = new Vec3(p.Pos.X, p.Pos.Y, p.Pos.Z); face = ang; }   // under the desk
                    if (Validator.NodeAt(level, pos) == null) continue;
                    outp.Add(Markers.Make("hiding_spot", pos, face, room).With("class", cls).With("on", id));
                    n++;
                }
            }
            return outp;
        }

        static List<Entity> PowerJunctions(Ctx x)
        {
            var level = x.Level;
            var g = level.Graph;
            int s = level.CellSize;
            var outp = new List<Entity>();
            const double W = 0.6, D = 0.25, H = 0.8, Elev = 1.2;
            foreach (var grp in Rooms(level).GroupBy(r => r.Get<string>("sector") ?? $"zone{(g != null && r.Node < g.Nodes.Count ? g.Nodes[r.Node].Zone : 0)}")
                                            .OrderBy(gr => gr.Min(r => r.Id)))
            {
                var rng = x.Rng("power", grp.Min(r => r.Id));
                var rooms = grp.OrderBy(r => { int i = Array.IndexOf(PowerRooms, r.Get<string>("program") ?? ""); return i < 0 ? 99 : i; })
                               .ThenBy(r => r.Kind == "start" ? 1 : 0).ThenBy(r => r.Id).ToList();
                Entity made = null;
                foreach (var room in rooms)
                {
                    var st = x.State(room);
                    double z0 = room.Floor + Elev * s, z1 = z0 + H * s;
                    var cands = new List<(Cell, int, int)>();
                    foreach (var c in st.CellList)
                    {
                        if (!st.Flat(c) || st.Ctx.Clear.Contains(c) || st.Ceil(c) < z1) continue;
                        foreach (var (dx, dy) in D4)
                            if (st.WallAt(c, dx, dy, z0, z1)) cands.Add((c, dx, dy));
                    }
                    rng.Shuffle(cands);
                    foreach (var (c, dx, dy) in cands)
                    {
                        if (!x.Stand(CellFloor(level, c, room.Floor))) continue;
                        var origin = new Vec3((c.X + 0.5 + dx * 0.5) * s, (c.Y + 0.5 + dy * 0.5) * s, z0);
                        int ang = Markers.Angle(-dx, -dy);
                        var box = Markers.Box(origin, "wall", W, D, H, ang);
                        if (!x.Space.Free(box)) continue;
                        made = Markers.Make("power_junction", origin, ang, room).With("class", "power_junction")
                            .With("mount", "wall").With("footprint", new[] { W, D, H });
                        made.Bounds = box;
                        x.Space.Add(box);
                        break;
                    }
                    if (made != null) break;
                }
                if (made != null) outp.Add(made);
            }
            return outp;
        }

        static List<Entity> HazardZones(Ctx x)
        {
            var level = x.Level;
            int s = level.CellSize;
            var outp = new List<Entity>();
            var pool = new HashSet<Cell>();
            foreach (var r in level.Rooms.Where(r => r.Get<string>("program") == "moon_pool"))
                foreach (var c in r.Cells()) pool.Add(c);
            var seen = new HashSet<Cell>();
            foreach (var c0 in level.Water.Keys.OrderBy(c => c))
            {
                if (seen.Contains(c0) || pool.Contains(c0)) continue;
                var comp = new List<Cell>();
                var stack = new Stack<Cell>();
                stack.Push(c0); seen.Add(c0);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    comp.Add(c);
                    foreach (var (dx, dy) in D4)
                    {
                        var n = c.Offset(dx, dy);
                        if (level.Water.ContainsKey(n) && !pool.Contains(n) && seen.Add(n)) stack.Push(n);
                    }
                }
                int x0 = comp.Min(c => c.X), x1 = comp.Max(c => c.X) + 1, y0 = comp.Min(c => c.Y), y1 = comp.Max(c => c.Y) + 1;
                int zlo = comp.Min(c => level.Spans[c][0].Floor), zhi = comp.Max(c => level.Water[c]);
                var lo = new Vec3(x0 * s, y0 * s, zlo);
                var hi = new Vec3(x1 * s, y1 * s, zhi);
                var first = comp.OrderBy(c => c).First();
                string own = level.OwnerAt(first, level.Spans[first][0].Floor) ?? "";
                Room room = own.StartsWith("room:") ? level.Rooms.FirstOrDefault(r => $"room:{r.Id}" == own) : null;
                var e = Markers.Make("hazard_zone", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, zlo), null, room);
                if (room == null && own.StartsWith("corridor:") && int.TryParse(own.Substring(9), out var cid)) e.With("corridor", cid);
                e.With("type", "flooded").With("size", new[] { (double)(x1 - x0), (zhi - zlo) / Markers.M, (double)(y1 - y0) });
                e.Bounds = (lo, hi);
                outp.Add(e);
            }
            // live wires round some broken equipment (one per room at most)
            var done = new HashSet<int>();
            foreach (var (p, id) in x.Props)
            {
                if (!(p.Props.TryGetValue("tags", out var t) && ((string[])t).Contains("broken"))) continue;
                string cls = (string)p.Props["class"];
                if (cls != "console" && cls != "server_rack" && cls != "pump" && cls != "specimen_tank") continue;
                if (!(p.Props.TryGetValue("room", out var ro) && ro is int rid) || done.Contains(rid)) continue;
                if (x.Rng("electrified", rid).Random() >= 0.5) { done.Add(rid); continue; }
                var room = level.Rooms.First(r => r.Id == rid);
                var (plo, phi) = p.Bounds.Value;
                var lo = new Vec3(plo.X - s, plo.Y - s, room.Floor);
                var hi = new Vec3(phi.X + s, phi.Y + s, room.Floor + 2 * s);
                var e = Markers.Make("hazard_zone", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, room.Floor), null, room)
                    .With("type", "electrified").With("size", new[] { (hi.X - lo.X) / s, 2.0, (hi.Y - lo.Y) / s }).With("on", id);
                e.Bounds = (lo, hi);
                outp.Add(e);
                done.Add(rid);
            }
            return outp;
        }

        /// <summary>The airlock: a hatch on the wall behind the player at the start (they came in through it),
        /// facing the way the player faces.</summary>
        static List<Entity> Airlock(Ctx x)
        {
            var level = x.Level;
            int s = level.CellSize;
            var outp = new List<Entity>();
            var sc = level.CellOf(x.Start.Pos);
            var room = level.Rooms.FirstOrDefault(r => r.Kind == "start" && r.Contains(sc)) ?? level.Rooms.FirstOrDefault(r => r.Contains(sc) && !r.IsConnector);
            if (room == null) return outp;
            var st = x.State(room);
            const double W = 1.0, D = 0.2, H = 2.2;
            double z1 = room.Floor + H * s;
            int face = x.Start.Props.TryGetValue("angle", out var a) ? Convert.ToInt32(a) : 0;
            int behind = (int)Math.Round(((face + 180) % 360) / 90.0, MidpointRounding.AwayFromZero) % 4 * 90;
            foreach (int turn in new[] { 0, 90, 270, 180 })
            {
                var (dx, dy) = Markers.Dir((behind + turn) % 360);
                int tx = Math.Abs(dy), ty = Math.Abs(dx);
                // walk from the start to that wall, then the nearest stretch of solid wall along it
                var c = sc;
                while (room.Contains(c.Offset(dx, dy))) c = c.Offset(dx, dy);
                var cands = st.CellList.Where(q => st.Flat(q) && st.Ceil(q) >= z1 && st.WallAt(q, dx, dy, room.Floor, z1))
                                       .OrderBy(q => Math.Abs(q.X - c.X) + Math.Abs(q.Y - c.Y)).ThenBy(q => q).ToList();
                foreach (var q in cands)
                {
                    var origin = new Vec3((q.X + 0.5 + dx * 0.5) * s, (q.Y + 0.5 + dy * 0.5) * s, room.Floor);
                    int ang = Markers.Angle(-dx, -dy);
                    var box = Markers.Box(origin, "wall", W, D, H, ang);
                    if (!x.Space.Free(box)) continue;
                    var e = Markers.Make("airlock", origin, ang, room).With("mount", "wall").With("footprint", new[] { W, D, H });
                    e.Bounds = box;
                    x.Space.Add(box);
                    outp.Add(e);
                    return outp;
                }
            }
            return outp;
        }

        /// <summary>Viewports (cfg.Viewports): a porthole on an outer wall of about four rooms in ten, where
        /// nothing is open for 3 m behind the wall (the sea, as far as the level knows).</summary>
        static List<Entity> Viewports(Level level, Dressing.Space space)
        {
            int s = level.CellSize;
            var outp = new List<Entity>();
            const double W = 0.6, D = 0.1, H = 0.6, Elev = 1.3;
            foreach (var room in Rooms(level))
            {
                if (room.Kind == "courtyard" || room.Cells().Any(c => level.Sky.Contains(c))) continue;
                var rng = Rng.FromLabel($"{level.Config.Seed}/{level.Config.Variant}/viewport/{room.Id}");
                if (rng.Random() >= 0.4) continue;
                var st = new Dressing.RoomState(level, room, space);
                double z0 = room.Floor + Elev * s, z1 = z0 + H * s;
                var cands = new List<(Cell, int, int)>();
                foreach (var c in st.CellList)
                {
                    if (!st.Flat(c) || st.Ctx.Clear.Contains(c) || st.Ceil(c) < z1) continue;
                    foreach (var (dx, dy) in D4)
                        if (!room.Contains(c.Offset(dx, dy)) && Enumerable.Range(1, 3).All(k => !level.Spans.ContainsKey(c.Offset(dx * k, dy * k))))
                            cands.Add((c, dx, dy));
                }
                rng.Shuffle(cands);
                foreach (var (c, dx, dy) in cands)
                {
                    var origin = new Vec3((c.X + 0.5 + dx * 0.5) * s, (c.Y + 0.5 + dy * 0.5) * s, z0);
                    int ang = Markers.Angle(-dx, -dy);
                    var box = Markers.Box(origin, "wall", W, D, H, ang);
                    if (!space.Free(box)) continue;
                    var e = Markers.Make("viewport", origin, ang, room).With("mount", "wall").With("footprint", new[] { W, D, H });
                    e.Bounds = box;
                    space.Add(box);
                    outp.Add(e);
                    break;
                }
            }
            return outp;
        }
    }
}
