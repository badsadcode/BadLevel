using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    public sealed class LayoutException : Exception
    {
        public LayoutException(string message) : base(message) { }
    }

    /// <summary>A minimal binary min-heap (netstandard2.1 has no PriorityQueue).</summary>
    sealed class MinHeap<T>
    {
        readonly List<(double Key, long Order, T Item)> items = new List<(double, long, T)>();
        long counter;
        public int Count => items.Count;
        public void Push(double key, T item)
        {
            items.Add((key, counter++, item));
            int i = items.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (Less(items[i], items[p])) { (items[i], items[p]) = (items[p], items[i]); i = p; } else break;
            }
        }
        public (double Key, T Item) Pop()
        {
            var top = items[0];
            var last = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            if (items.Count > 0)
            {
                items[0] = last;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < items.Count && Less(items[l], items[m])) m = l;
                    if (r < items.Count && Less(items[r], items[m])) m = r;
                    if (m == i) break;
                    (items[i], items[m]) = (items[m], items[i]);
                    i = m;
                }
            }
            return (top.Key, top.Item);
        }
        static bool Less((double Key, long Order, T) a, (double Key, long Order, T) b) => a.Key < b.Key || (a.Key == b.Key && a.Order < b.Order);
    }

    /// <summary>Mission graph -> rooms on a grid -> corridors with stairs. Rooms are placed by walking
    /// the graph's spanning tree from the root, each beside its parent; corridors are routed with A*
    /// between them (a wall kept to everything else); height changes become stairs on straight runs.</summary>
    public static class Layout
    {
        static readonly (int, int)[] Dirs = { (1, 0), (0, 1), (-1, 0), (0, -1) };
        static readonly (int, int)[] Dirs8 = { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        static readonly double[] TurnCost = { 0.0, 1.5, 2.5, 8.0 };
        const int Pad = 4, Slab = 16;

        // ---------------------------------------------------------------------------------- rooms
        public static (int W, int H, int Height) RoomSize(GenConfig cfg, string size, Rng rng)
        {
            var (lo, hi, hmin, hmax) = cfg.RoomSizes.TryGetValue(size, out var t) ? t : cfg.RoomSizes["medium"];
            lo = Math.Max(6, (int)Math.Round(lo * cfg.RoomScale));
            hi = Math.Max(lo, (int)Math.Round(hi * cfg.RoomScale));
            int w = rng.Int(lo, hi), h = rng.Int(lo, hi);
            int height = rng.Range(hmin, hmax + 1, 16);
            if (cfg.SizeVariety > 0)
            {
                double f = Math.Pow(2, rng.Uniform(-0.6 * cfg.SizeVariety, 1.0 * cfg.SizeVariety));
                w = Math.Max(6, Math.Min(40, (int)Math.Round(w * f)));
                h = Math.Max(6, Math.Min(40, (int)Math.Round(h * f)));
                height = (int)Math.Round(height * Math.Sqrt(f) / 16) * 16;
            }
            height = Math.Max(128, (int)Math.Round(height * cfg.HeightScale / 16) * 16);
            return (w, h, height);
        }

        public static Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            if (Array.IndexOf(Room.ConnectorKinds, node.Kind) >= 0)
            {
                int top = node.Top ?? floor;
                var meta = new Dictionary<string, object>();
                bool wide = cfg.Spaciousness >= 0.5 && node.Kind != "spiral";
                var size = Connectors.Size(node.Kind, top - floor, rng, wide, meta);
                if (size == null) return null;
                var room = new Room { Id = -1, Node = node.Id, Kind = node.Kind, W = size.Value.W, H = size.Value.H,
                                      Floor = floor, Ceil = top + 160, Storey = node.Storey, TopFloor = top, Meta = meta };
                Connectors.ApplyShape(room);
                return room;
            }
            int H = cfg.StoreyHeight;
            var (w, h, height) = RoomSize(cfg, node.Size, rng);
            if (multi)
            {
                if ((node.Kind == "arena" || node.Kind == "hub") && rng.Random() < cfg.TallRoomChance)
                    height = rng.Range(H + 96, 2 * H - 32 + 1, 16);
                else
                    height = Math.Min(height, H - 64);
            }
            return new Room { Id = -1, Node = node.Id, Kind = node.Kind, W = w, H = h, Floor = floor, Ceil = floor + height, Storey = node.Storey };
        }

        static void AssignShape(Room room, Node node, GenConfig cfg, Rng rng)
        {
            if (room.IsConnector) return;
            var shape = Shape.Pick(room.W, room.H, node.Size, node.Kind, rng, cfg.ShapeWeights);
            room.Shape = shape.Name;
            if (shape.Name != "rect" || shape.Diag.Count > 0) { room.Mask = shape.Cells; room.Diag = shape.Diag; }
        }

        static void BuildRoom(Level level, Room room)
        {
            if (room.IsConnector) { Connectors.Build(level, room, level.Config.StepHeight); return; }
            foreach (var c in room.Cells()) level.AddSpan(c, new Span(room.Floor, room.Ceil), "room:" + room.Id, room.Slot);
            foreach (var kv in room.WorldDiag())
            {
                level.Diag[kv.Key] = kv.Value;
                level.DiagSpan[kv.Key] = new Span(room.Floor, room.Ceil);
            }
        }

        /// <summary>(port cells or null, floor) where a corridor from room towards other attaches.</summary>
        static (HashSet<Cell> Port, int Floor) Attachment(Room room, Room other)
        {
            if (!room.IsConnector) return (null, room.Floor);
            var p = Connectors.Ports(room);
            int target = other.IsConnector ? (other.TopFloor ?? other.Floor) : other.Floor;
            return Math.Abs(target - (room.TopFloor ?? room.Floor)) < Math.Abs(target - room.Floor) ? p[1] : p[0];
        }

        static int PortFloor(Room room, int otherFloor)
        {
            if (room.TopFloor == null) return room.Floor;
            return Math.Abs(otherFloor - room.TopFloor.Value) < Math.Abs(otherFloor - room.Floor) ? room.TopFloor.Value : room.Floor;
        }

        static bool VerticalConflict((int Lo, int Hi) a, (int Lo, int Hi) b, int gap = Slab) => a.Lo < b.Hi + gap && b.Lo < a.Hi + gap;

        static bool Overlaps(Room r, IEnumerable<Room> rooms, int gap)
        {
            foreach (var o in rooms)
            {
                if (!VerticalConflict(r.Slot, o.Slot)) continue;
                if (r.X - gap < o.X + o.W && o.X - gap < r.X + r.W && r.Y - gap < o.Y + o.H && o.Y - gap < r.Y + r.H) return true;
            }
            return false;
        }

        static Dictionary<int, int> StoreyFloors(MissionGraph g, GenConfig cfg, Rng rng)
        {
            int H = cfg.StoreyHeight;
            var floors = new Dictionary<int, int>();
            var tree = g.Edges.Where(e => e.Tree).ToList();
            foreach (var n in g.Nodes)
                if (Array.IndexOf(Room.ConnectorKinds, n.Kind) < 0 && n.Kind != "secret")
                    floors[n.Id] = n.Storey * H + rng.Choice(new[] { 0, 0, 32, 64 });
            foreach (var n in g.Nodes.Where(n => n.Kind == "secret"))
            {
                var e = g.Edges.First(x => x.A == n.Id || x.B == n.Id);
                int parent = e.A == n.Id ? e.B : e.A;
                floors[n.Id] = floors.TryGetValue(parent, out var f) ? f : n.Storey * H;
            }
            foreach (var n in g.Nodes.Where(n => Array.IndexOf(Room.ConnectorKinds, n.Kind) >= 0))
            {
                var nb = tree.Where(e => e.A == n.Id || e.B == n.Id).Select(e => e.A == n.Id ? e.B : e.A).ToList();
                floors[n.Id] = nb.Min(m => floors[m]);
                n.Top = nb.Max(m => floors[m]);
            }
            return floors;
        }

        static (int, int) ComposedDir(string comp, Room root, Room parent, bool fromRoot, Node node, int[] spokes, (int, int) axis, Rng rng)
        {
            if (comp == "hub" || comp == "central")
            {
                if (fromRoot) { var d = Dirs[spokes[0] % 4]; spokes[0]++; return d; }
                var (rx, ry) = root.Center; var (px, py) = parent.Center;
                double ox = px - rx, oy = py - ry;
                var outward = Math.Abs(ox) >= Math.Abs(oy) ? (ox > 0 ? 1 : -1, 0) : (0, oy > 0 ? 1 : -1);
                if (rng.Random() < 0.75) return outward;
                return rng.Choice(new[] { (outward.Item2, outward.Item1), (-outward.Item2, -outward.Item1) });
            }
            if (comp == "linear")
            {
                if (node.Main) return axis;
                return rng.Choice(new[] { (axis.Item2, axis.Item1), (-axis.Item2, -axis.Item1) });
            }
            return rng.Choice(Dirs);
        }

        public static List<Room> PlaceRooms(MissionGraph g, GenConfig cfg, Rng rng)
        {
            bool multi = cfg.Storeys > 1;
            var preset = multi ? StoreyFloors(g, cfg, rng) : new Dictionary<int, int>();
            string comp = cfg.Composition;
            int root = (comp == "hub" || comp == "central") && g.Center.HasValue ? g.Center.Value : 0;
            var rooms = new Dictionary<int, Room>();
            var r0 = Propose(g.Nodes[root], preset.TryGetValue(root, out var f0) ? f0 : 0, cfg, rng, multi)
                     ?? throw new LayoutException("start room");
            rooms[root] = r0;
            var spokes = new[] { rng.Int(0, 3) };
            var axis = Dirs[rng.Int(0, 3)];
            int maxFloor = cfg.MaxLevel * cfg.LevelStep;
            var order = new List<int> { root };
            var tree = g.Edges.Where(e => e.Tree).ToList();
            for (int i = 0; i < order.Count; i++)
            {
                int pid = order[i];
                var parent = rooms[pid];
                foreach (var e in tree)
                {
                    if (e.A != pid && e.B != pid) continue;
                    int cid = e.A == pid ? e.B : e.A;
                    if (rooms.ContainsKey(cid)) continue;
                    var node = g.Nodes[cid];
                    Room placed = null;
                    int tries = 60 + (int)(60 * cfg.Spaciousness);
                    for (int t = 0; t < tries && placed == null; t++)
                    {
                        int floor;
                        if (multi) floor = preset[cid];
                        else if (node.Kind == "secret") floor = parent.Floor;
                        else
                        {
                            int dl = rng.Choice(new[] { -1, 0, 0, 1, 1, 2 });
                            floor = Math.Min(Math.Max(parent.Floor + dl * cfg.LevelStep, 0), maxFloor);
                        }
                        var cand = Propose(node, floor, cfg, rng, multi)
                                   ?? throw new LayoutException($"connector {node.Kind} can't climb {(node.Top ?? floor) - floor}");
                        int w = cand.W, h = cand.H;
                        int pf = PortFloor(parent, cand.Floor), cf = PortFloor(cand, parent.Floor);
                        int climb = Math.Abs(pf - cf) / cfg.StepHeight;
                        int gap = Math.Max(cfg.RoomGap + 4, climb + 2 * cfg.CorridorRadius + 8) + rng.Int(0, 5);
                        if (comp == "central" && pid == root) gap = Math.Max(cfg.RoomGap + 4, climb + 2 * cfg.CorridorRadius + 6);
                        bool stack = multi && t % 3 == 2;
                        int x, y;
                        if (stack)
                        {
                            var others = rooms.Values.Where(o => !VerticalConflict(cand.Slot, o.Slot) && Math.Abs(o.X - parent.X) + Math.Abs(o.Y - parent.Y) < 40).ToList();
                            if (others.Count == 0) continue;
                            var o = rng.Choice(others);
                            x = o.X + rng.Int(-(w / 2), Math.Max(0, o.W - w / 2));
                            y = o.Y + rng.Int(-(h / 2), Math.Max(0, o.H - h / 2));
                        }
                        else
                        {
                            var (dx, dy) = comp == "tree" || t >= 30 ? rng.Choice(Dirs) : ComposedDir(comp, rooms[root], parent, pid == root, node, spokes, axis, rng);
                            if (comp == "linear" && node.Main && t < 30)
                            {
                                if (dx != 0) { x = dx > 0 ? parent.X + parent.W + gap : parent.X - gap - w; y = parent.Y + (parent.H - h) / 2 + rng.Int(-1, 1); }
                                else { y = dy > 0 ? parent.Y + parent.H + gap : parent.Y - gap - h; x = parent.X + (parent.W - w) / 2 + rng.Int(-1, 1); }
                            }
                            else if (dx != 0) { x = dx > 0 ? parent.X + parent.W + gap : parent.X - gap - w; y = parent.Y + rng.Int(-(h - 4), parent.H - 4); }
                            else { y = dy > 0 ? parent.Y + parent.H + gap : parent.Y - gap - h; x = parent.X + rng.Int(-(w - 4), parent.W - 4); }
                        }
                        cand.X = x; cand.Y = y;
                        if (stack && !Overlaps(cand, new[] { parent }, gap - 4) &&
                            Math.Abs((x + w / 2.0) - (parent.X + parent.W / 2.0)) + Math.Abs((y + h / 2.0) - (parent.Y + parent.H / 2.0)) > 60)
                            continue;
                        if (!Overlaps(cand, rooms.Values, cfg.RoomGap + 2 * cfg.CorridorRadius + 2)) placed = cand;
                    }
                    if (placed == null) throw new LayoutException($"could not place room for node {cid}");
                    rooms[cid] = placed;
                    order.Add(cid);
                }
            }
            if (rooms.Count != g.Nodes.Count) throw new LayoutException("the spanning tree did not reach every node");
            var result = g.Nodes.Select(n => rooms[n.Id]).ToList();
            foreach (var r in result) AssignShape(r, g.Nodes[r.Node], cfg, rng);
            int minx = result.Min(r => r.X), miny = result.Min(r => r.Y);
            for (int idx = 0; idx < result.Count; idx++)
            {
                result[idx].Id = idx;
                result[idx].X += Pad - minx;
                result[idx].Y += Pad - miny;
            }
            return result;
        }

        static int Gap(Room a, Room b)
        {
            int dx = Math.Max(Math.Max(b.X - (a.X + a.W), a.X - (b.X + b.W)), 0);
            int dy = Math.Max(Math.Max(b.Y - (a.Y + a.H), a.Y - (b.Y + b.H)), 0);
            return dx + dy;
        }

        /// <summary>Loops: rooms apart in the graph (2+ hops) but close in space.</summary>
        static void ChooseLoops(MissionGraph g, List<Room> rooms, GenConfig cfg, Rng rng)
        {
            int n = cfg.Loops + (cfg.Composition == "central" ? 2 : 0);
            for (int k = 0; k < n; k++)
            {
                var cands = new List<(int Score, int A, int B)>();
                foreach (var a in rooms)
                    foreach (var b in rooms)
                    {
                        var na = g.Nodes[a.Node]; var nb = g.Nodes[b.Node];
                        if (a.Id >= b.Id || na.Zone != nb.Zone || g.Connected(a.Node, b.Node)) continue;
                        if (a.IsConnector || b.IsConnector || a.Kind == "secret" || b.Kind == "secret" || a.Kind == "exit" || b.Kind == "exit") continue;
                        int hops = g.HopDistance(a.Node, b.Node);
                        if (hops < 2) continue;
                        int bonus = g.Center.HasValue && (g.Center == a.Node || g.Center == b.Node) ? -20 : 0;
                        cands.Add((Gap(a, b) + (hops == 2 ? 10 : 0) + (a.Floor != b.Floor ? 12 : 0) + bonus, a.Node, b.Node));
                    }
                if (cands.Count == 0) break;
                cands.Sort((x, y) => x.Score != y.Score ? x.Score.CompareTo(y.Score) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
                var pick = rng.Random() < cfg.LongLoopChance && cfg.Composition != "linear"
                    ? rng.Choice(cands.Skip(cands.Count / 2).ToList()) : rng.Choice(cands.Take(3).ToList());
                var e = g.AddEdge(pick.A, pick.B, tree: false);
                if (g.Nodes[pick.A].Storey != g.Nodes[pick.B].Storey) e.Connector = "drop";
            }
        }

        // ---------------------------------------------------------------------------------- router
        sealed class Router
        {
            const int MaxReach = 7;
            readonly List<Room> rooms;
            readonly int w, h;
            readonly GenConfig cfg;
            readonly Dictionary<Cell, List<int>> roomAt = new Dictionary<Cell, List<int>>();
            readonly Dictionary<Cell, List<(int Room, int Dist)>> nearRoom = new Dictionary<Cell, List<(int, int)>>();
            public readonly Dictionary<Cell, List<(int Lo, int Hi)>> OpenCells = new Dictionary<Cell, List<(int, int)>>();

            public Router(List<Room> rooms, int width, int height, GenConfig cfg)
            {
                this.rooms = rooms; w = width; h = height; this.cfg = cfg;
                foreach (var room in rooms)
                {
                    foreach (var c in room.Cells())
                    {
                        if (!roomAt.TryGetValue(c, out var l)) roomAt[c] = l = new List<int>();
                        l.Add(room.Id);
                    }
                    for (int y = room.Y - MaxReach; y < room.Y + room.H + MaxReach; y++)
                        for (int x = room.X - MaxReach; x < room.X + room.W + MaxReach; x++)
                            if (!room.Contains(x, y))
                            {
                                var c = new Cell(x, y);
                                if (!nearRoom.TryGetValue(c, out var l)) nearRoom[c] = l = new List<(int, int)>();
                                l.Add((room.Id, room.Dist(x, y)));
                            }
                }
            }

            public void AddOpen(Cell c, int lo, int hi)
            {
                if (!OpenCells.TryGetValue(c, out var l)) OpenCells[c] = l = new List<(int, int)>();
                l.Add((lo, hi));
            }

            (HashSet<Cell> Hard, HashSet<Cell> Soft) CorridorBlock(int ext, (int, int) env, int? crossFloor)
            {
                int reach = ext + 1;
                var hard = new HashSet<Cell>(); var soft = new HashSet<Cell>();
                foreach (var kv in OpenCells)
                {
                    var conflicting = kv.Value.Where(rg => VerticalConflict(env, rg, 32)).ToList();
                    if (conflicting.Count == 0) continue;
                    bool crossable = crossFloor.HasValue && conflicting.All(rg => rg.Item1 == crossFloor.Value);
                    var target = crossable ? soft : hard;
                    for (int dy = -reach; dy <= reach; dy++)
                        for (int dx = -reach; dx <= reach; dx++) target.Add(kv.Key.Offset(dx, dy));
                }
                soft.ExceptWith(hard);
                return (hard, soft);
            }

            double? Allowed(Cell c, Room a, Room b, int r, int ext, HashSet<Cell> blocked, (int, int) env, HashSet<Cell> bPort, HashSet<Cell> soft)
            {
                int x = c.X, y = c.Y;
                if (x <= ext + 2 || y <= ext + 2 || x >= w - ext - 3 || y >= h - ext - 3) return null;
                if (roomAt.TryGetValue(c, out var rids))
                {
                    foreach (var rid in rids)
                    {
                        if (rid == a.Id || rid == b.Id)
                        {
                            var room = rooms[rid];
                            if (room.IsDiag(x, y) || (rid == b.Id && bPort != null && !bPort.Contains(c))) return null;
                            return 0.0;
                        }
                    }
                    if (rids.Any(rid => VerticalConflict(rooms[rid].Slot, env))) return null;
                }
                if (blocked.Contains(c)) return null;
                double cost = soft.Contains(c) ? 3.0 : 0.0;
                if (nearRoom.TryGetValue(c, out var near))
                    foreach (var (rid, dist) in near)
                    {
                        if (rid != a.Id && rid != b.Id)
                        {
                            if (dist <= ext + 1 && VerticalConflict(rooms[rid].Slot, env)) return null;
                            continue;
                        }
                        if (dist > r + 1) continue;
                        var room = rid == a.Id ? a : b;
                        bool insideX = room.X + r <= x && x <= room.X + room.W - 1 - r;
                        bool insideY = room.Y + r <= y && y <= room.Y + room.H - 1 - r;
                        bool aboveBelow = room.X <= x && x < room.X + room.W;
                        bool leftRight = room.Y <= y && y < room.Y + room.H;
                        if (!((aboveBelow && insideX) || (leftRight && insideY))) return null;
                        cost += 6.0;
                    }
                return cost;
            }

            bool NearEndpoint(Cell c, Room a, Room b, int ext) =>
                nearRoom.TryGetValue(c, out var near) && near.Any(t => (t.Room == a.Id || t.Room == b.Id) && t.Dist <= ext + 2);

            bool InRoom(Cell c, Room room) => roomAt.TryGetValue(c, out var l) && l.Contains(room.Id);

            public List<Cell> Route(Room a, Room b, int r, bool diagonal, int k, (int Lo, int Hi) env, HashSet<Cell> aPort, HashSet<Cell> bPort, int? crossFloor)
            {
                int ext = diagonal ? Math.Max(r, k) : r;
                var (blocked, soft) = CorridorBlock(ext, env, crossFloor);
                var starts = new List<Cell>();
                foreach (var c in a.Cells())
                {
                    if (a.IsDiag(c.X, c.Y) || (aPort != null && !aPort.Contains(c))) continue;
                    bool onX = c.X == a.X || c.X == a.X + a.W - 1, onY = c.Y == a.Y || c.Y == a.Y + a.H - 1;
                    bool okX = a.X + r <= c.X && c.X <= a.X + a.W - 1 - r, okY = a.Y + r <= c.Y && c.Y <= a.Y + a.H - 1 - r;
                    if ((onY && okX) || (onX && okY)) starts.Add(c);
                }
                if (starts.Count == 0) throw new LayoutException($"room {a.Id} too small for a corridor of radius {r}");
                int bx0 = b.X, by0 = b.Y, bx1 = b.X + b.W - 1, by1 = b.Y + b.H - 1;
                double heur(Cell c)
                {
                    int dx = Math.Max(Math.Max(bx0 - c.X, 0), c.X - bx1), dy = Math.Max(Math.Max(by0 - c.Y, 0), c.Y - by1);
                    return diagonal ? Math.Max(dx, dy) + 0.414 * Math.Min(dx, dy) : dx + dy;
                }
                var cache = new Dictionary<Cell, double?>();
                double? allowed(Cell c)
                {
                    if (!cache.TryGetValue(c, out var v)) cache[c] = v = Allowed(c, a, b, r, ext, blocked, env, bPort, soft);
                    return v;
                }
                var open = new MinHeap<(double G, Cell C, int D)>();
                var best = new Dictionary<(Cell, int), double>();
                var prev = new Dictionary<(Cell, int), (Cell, int)>();
                foreach (var c in starts) { best[(c, -1)] = 0; open.Push(heur(c), (0, c, -1)); }
                (Cell, int)? goal = null;
                long limit = (long)w * h * (diagonal ? 8 : 4), pops = 0;
                while (open.Count > 0)
                {
                    var (_, item) = open.Pop();
                    var (gcost, c, d) = item;
                    if (++pops > limit) break;
                    if (best.TryGetValue((c, d), out var bc) && bc < gcost) continue;
                    if (b.Contains(c) && !b.IsDiag(c.X, c.Y) && (bPort == null || bPort.Contains(c))) { goal = (c, d); break; }
                    for (int nd = 0; nd < 8; nd += diagonal ? 1 : 2)
                    {
                        var (dx, dy) = Dirs8[nd];
                        var n = c.Offset(dx, dy);
                        if (a.Contains(n)) continue;
                        double turnCost = 0;
                        if (d >= 0)
                        {
                            int turn = Math.Min(Math.Abs(nd - d), 8 - Math.Abs(nd - d));
                            if (turn > 3) continue;
                            turnCost = TurnCost[turn];
                        }
                        var extra = allowed(n);
                        if (extra == null) continue;
                        double step = 1.0;
                        if (dx != 0 && dy != 0)
                        {
                            if (allowed(c.Offset(dx, 0)) == null || allowed(c.Offset(0, dy)) == null) continue;
                            if (InRoom(c, a) || InRoom(c, b) || InRoom(n, a) || InRoom(n, b)) continue;
                            if (NearEndpoint(c, a, b, ext) || NearEndpoint(n, a, b, ext)) continue;
                            step = 1.414;
                        }
                        double ng = gcost + step + extra.Value + turnCost;
                        if (!best.TryGetValue((n, nd), out var old) || ng < old)
                        {
                            best[(n, nd)] = ng;
                            prev[(n, nd)] = (c, d);
                            open.Push(ng + heur(n), (ng, n, nd));
                        }
                    }
                }
                if (goal == null) throw new LayoutException($"no corridor route {a.Id}->{b.Id}");
                var path = new List<Cell> { goal.Value.Item1 };
                var st = goal.Value;
                while (prev.TryGetValue(st, out var p)) { st = p; path.Add(st.Item1); }
                path.Reverse();
                return path;
            }
        }

        // ---------------------------------------------------------------------------------- corridors
        static (int, int) Dir(List<Cell> path, int i)
        {
            int j = Math.Min(i, path.Count - 2);
            return (path[j + 1].X - path[j].X, path[j + 1].Y - path[j].Y);
        }

        internal static bool Straight(List<Cell> path, int i, int reach, bool axisOnly = false)
        {
            int lo = i - reach - 1, hi = i + reach + 1;
            if (lo < 0 || hi >= path.Count) return false;
            var d0 = (path[lo + 1].X - path[lo].X, path[lo + 1].Y - path[lo].Y);
            if (axisOnly && d0.Item1 != 0 && d0.Item2 != 0) return false;
            for (int j = lo + 1; j < hi; j++)
                if ((path[j + 1].X - path[j].X, path[j + 1].Y - path[j].Y) != d0) return false;
            return true;
        }

        static List<int> PlanFloors(List<Cell> path, int fa, int fb, GenConfig cfg, int r)
        {
            int steps = Math.Abs(fb - fa) / cfg.StepHeight, n = path.Count;
            if (steps == 0) return Enumerable.Repeat(fa, n).ToList();
            int margin = r + 2, reach = r;
            var cands = Enumerable.Range(margin, Math.Max(0, n - 2 * margin)).Where(i => Straight(path, i, reach)).ToList();
            if (cands.Count < steps) return null;
            double mid = (n - 1) / 2.0;
            var chosen = cands.OrderBy(i => Math.Abs(i - mid)).ThenBy(i => i).Take(steps).OrderBy(i => i).ToList();
            int sign = fb > fa ? 1 : -1, k = 0, ci = 0;
            var floors = new List<int>();
            for (int i = 0; i < n; i++)
            {
                while (ci < chosen.Count && chosen[ci] <= i) { k++; ci++; }
                floors.Add(fa + sign * k * cfg.StepHeight);
            }
            return floors;
        }

        static (List<int> Floors, List<int> Ceils)? PlanDrop(List<Cell> path, int fa, int fb, int r, int h)
        {
            int n = path.Count;
            var cands = Enumerable.Range(r + 2, Math.Max(0, n - r - 3 - (r + 2))).Where(i => Straight(path, i, r + 1)).ToList();
            if (cands.Count == 0) return null;
            int t = cands.OrderBy(i => Math.Abs(i - n / 2)).First();
            return (Enumerable.Range(0, n).Select(i => i < t ? fa : fb).ToList(),
                    Enumerable.Range(0, n).Select(i => i < t + 2 ? fa + h : fb + h).ToList());
        }

        static int DiagHalfWidth(int r) => Math.Max(1, (int)Math.Round((2 * r + 1) * 32 / 45.25 - 1));

        static IEnumerable<(Cell C, int Dist)> Stamp(Cell center, (int, int) dir, int r, int k)
        {
            var (dx, dy) = dir;
            if (dx != 0 && dy != 0)
            {
                bool same = dx == dy;
                for (int oy = -k - 1; oy <= k + 1; oy++)
                    for (int ox = -k - 1; ox <= k + 1; ox++)
                    {
                        int x = center.X + ox, y = center.Y + oy;
                        int band = same ? (x - y) - (center.X - center.Y) : (x + y) - (center.X + center.Y);
                        int along = same ? (x + y) - (center.X + center.Y) : (x - y) - (center.X - center.Y);
                        if (Math.Abs(band) <= k && Math.Abs(along) <= 1) yield return (new Cell(x, y), Math.Abs(ox) + Math.Abs(oy));
                    }
            }
            else
                for (int oy = -r; oy <= r; oy++)
                    for (int ox = -r; ox <= r; ox++) yield return (center.Offset(ox, oy), Math.Abs(ox) + Math.Abs(oy));
        }

        static Corner? SolidCorner(HashSet<(int, int)> solid)
        {
            foreach (var c in Shape.AllCorners)
            {
                var ss = Shape.SolidSides(c);
                if (solid.Count == 2 && solid.Contains(ss[0]) && solid.Contains(ss[1])) return c;
            }
            return null;
        }

        /// <summary>Smooth 45-degree walls: a solid cell with exactly two adjacent open sides (cells of this
        /// corridor, at most a step apart) becomes a half cell.</summary>
        static List<Cell> AddHalfCells(Level level, int cid, List<Cell> carved)
        {
            string own = "corridor:" + cid;
            var cand = new SortedSet<Cell>();
            foreach (var c in carved)
                foreach (var (dx, dy) in Dirs)
                {
                    var n = c.Offset(dx, dy);
                    if (!level.Spans.ContainsKey(n)) cand.Add(n);
                }
            var added = new List<Cell>();
            foreach (var c in cand)
            {
                var openSides = new HashSet<(int, int)>(); var solidSides = new HashSet<(int, int)>();
                var spans = new List<Span>();
                bool bad = false;
                foreach (var (dx, dy) in Dirs)
                {
                    var n = c.Offset(dx, dy);
                    if (level.Spans.TryGetValue(n, out var ns))
                    {
                        if (ns.Count != 1 || level.OwnerOf(n, ns[0]) != own || level.Diag.ContainsKey(n)) { bad = true; break; }
                        openSides.Add((dx, dy)); spans.Add(ns[0]);
                    }
                    else solidSides.Add((dx, dy));
                }
                if (bad || openSides.Count != 2) continue;
                var corner = SolidCorner(solidSides);
                if (corner == null) continue;
                int f = spans.Max(s => s.Floor), ceil = spans.Min(s => s.Ceil);
                if (f - spans.Min(s => s.Floor) > level.Config.StepHeight || ceil - f < level.Config.PlayerHeight) continue;
                level.AddSpan(c, new Span(f, ceil), own);
                level.Diag[c] = corner.Value;
                level.DiagSpan[c] = new Span(f, ceil);
                added.Add(c);
            }
            return added;
        }

        static Corridor Carve(Level level, Router router, Edge e, Room a, Room b, List<Cell> path, List<int> floors, List<int> ceils,
                              int r, int k, int h, bool diag, string connector, (int, int) env)
        {
            int cid = level.Corridors.Count;
            var corr = new Corridor { Id = cid, Edge = e.Id, A = a.Id, B = b.Id, Path = path, Floors = floors, Kind = e.Kind, Key = e.Key,
                                      Radius = r, Height = h, Diagonal = diag, Connector = connector };
            var claim = new Dictionary<Cell, (int Dist, int I, int F, int Top)>();
            for (int i = 0; i < path.Count; i++)
            {
                var dirs = new HashSet<(int, int)> { Dir(path, i) };
                if (i > 0) dirs.Add(Dir(path, i - 1));
                foreach (var d in dirs)
                    foreach (var (c, dist) in Stamp(path[i], d, r, k))
                    {
                        var pri = (dist, i, floors[i], ceils[i]);
                        if (!claim.TryGetValue(c, out var old) || pri.CompareTo(old) < 0) claim[c] = pri;
                    }
            }
            var carved = new List<Cell>();
            foreach (var kv in claim.OrderBy(kv => kv.Key))
            {
                var c = kv.Key; int f = kv.Value.F, top = kv.Value.Top;
                if (!level.FreeAt(c, f, top, Slab) || a.Contains(c) || b.Contains(c))
                {
                    foreach (var sp in level.SpansIn(c, f, top).ToList())
                    {
                        var o = level.OwnerOf(c, sp) ?? "";
                        if (o.StartsWith("corridor:") && sp.Floor == f && int.TryParse(o.Substring(9), out var j) && !corr.Junctions.Contains(j))
                            corr.Junctions.Add(j);
                    }
                    continue;
                }
                level.AddSpan(c, new Span(f, top), "corridor:" + cid);
                carved.Add(c);
            }
            // a junction opening onto another corridor's half cell: make that cell fully open
            foreach (var c in carved)
                foreach (var (dx, dy) in Dirs)
                {
                    var n = c.Offset(dx, dy);
                    if (level.Diag.TryGetValue(n, out var corner) && Shape.SolidSides(corner).Contains((-dx, -dy)) &&
                        (level.OwnerOf(n, level.Spans[n][0]) ?? "").StartsWith("corridor:"))
                    { level.Diag.Remove(n); level.DiagSpan.Remove(n); }
                }
            carved.AddRange(AddHalfCells(level, cid, carved));
            foreach (var c in carved)
                foreach (var sp in level.SpansIn(c, env.Item1, env.Item2)) router.AddOpen(c, sp.Floor, sp.Ceil);
            level.Corridors.Add(corr);
            return corr;
        }

        static int CorridorHeight(GenConfig cfg, int r)
        {
            int baseH = r == 1 ? 128 : r == 2 ? 160 : 192;
            return (int)Math.Round(baseH * (cfg.CorridorHeight / 128.0) / 16) * 16;
        }

        static (int R, bool Diag) CorridorStyle(MissionGraph g, Edge e, Room a, Room b, GenConfig cfg, Rng rng)
        {
            if (a.IsConnector || b.IsConnector)
            {
                int fw = new[] { a, b }.Where(x => x.IsConnector).Max(x => x.Get("fw", 3));
                return (fw >= 5 ? 2 : 1, false);
            }
            int r; bool diag;
            if (e.Kind == "secret") { r = 1; diag = false; }
            else
            {
                var kinds = new HashSet<string> { g.Nodes[e.A].Kind, g.Nodes[e.B].Kind };
                int[] opts = !e.Tree ? new[] { 1, 1, 2 }
                    : kinds.Overlaps(new[] { "arena", "hub" }) ? new[] { 1, 2, 2, 3 }
                    : kinds.Overlaps(new[] { "supply", "secret", "key" }) ? new[] { 1, 1, 2 } : new[] { 1, 2, 2 };
                r = rng.Weighted(opts, cfg.CorridorWidthBias);
                diag = rng.Random() < cfg.DiagonalChance;
            }
            int smallest = new[] { a.W, a.H, b.W, b.H }.Min();
            r = Math.Max(1, Math.Min(Math.Min(r, cfg.MaxCorridorRadius), (smallest - 3) / 2));
            return (r, diag);
        }

        public static Level Build(MissionGraph g, GenConfig cfg, Rng rng)
        {
            var rooms = PlaceRooms(g, cfg, rng);
            int extra = 8;
            int width = rooms.Max(r => r.X + r.W) + Pad + 2 * extra, height = rooms.Max(r => r.Y + r.H) + Pad + 2 * extra;
            foreach (var r in rooms) { r.X += extra; r.Y += extra; }
            var level = new Level { Seed = cfg.Seed, CellSize = cfg.Cell, Width = width, Height = height, Rooms = rooms, Graph = g, Config = cfg };
            foreach (var room in rooms) BuildRoom(level, room);
            ChooseLoops(g, rooms, cfg, rng);
            var router = new Router(rooms, width, height, cfg);
            foreach (var e in g.Edges.OrderBy(e => e.Tree ? 0 : 1).ThenBy(e => e.Id).ToList())
            {
                Room a = rooms[e.A], b = rooms[e.B];
                bool drop = false;
                if (e.Connector == "drop")
                {
                    var (hi, lo) = a.Floor > b.Floor ? (a, b) : (b, a);
                    if (!hi.IsConnector && !lo.IsConnector && 64 <= hi.Floor - lo.Floor && hi.Floor - lo.Floor <= cfg.MaxDrop && rng.Random() < cfg.DropChance)
                    { a = hi; b = lo; drop = true; }
                }
                var (r, diag) = CorridorStyle(g, e, a, b, cfg, rng);
                var (aPort, fa) = Attachment(a, b);
                var (bPort, fb) = Attachment(b, a);
                List<Cell> path = null; List<int> floors = null, ceils = null; int k = 1;
                (int, int) env = (0, 0);
                foreach (var (rr, dd) in new[] { (r, diag), (r, false), (1, false) }.Distinct())
                {
                    int hh = CorridorHeight(cfg, rr);
                    env = (Math.Min(fa, fb), Math.Max(fa, fb) + hh);
                    int? cross = !e.Tree && fa == fb && !drop ? fa : (int?)null;
                    List<Cell> cand;
                    try { cand = router.Route(a, b, rr, dd, DiagHalfWidth(rr), env, aPort, bPort, cross); }
                    catch (LayoutException) { continue; }
                    if (!e.Tree)
                    {
                        var (ax, ay) = a.Center; var (bx, by) = b.Center;
                        if (cand.Count > 1.5 * (Math.Abs(ax - bx) + Math.Abs(ay - by)) + 12) continue;
                    }
                    while (cand.Count > 1 && a.Contains(cand[1])) cand.RemoveAt(0);
                    if (cand.Count < 3) continue;
                    var inner = cand.GetRange(1, cand.Count - 2);
                    List<int> fl, cl;
                    if (drop)
                    {
                        var plan = PlanDrop(inner, fa, fb, rr, hh);
                        fl = plan?.Floors; cl = plan?.Ceils;
                    }
                    else
                    {
                        fl = PlanFloors(inner, fa, fb, cfg, rr);
                        cl = fl?.Select(f => f + hh).ToList();
                    }
                    if (fl != null) { path = inner; floors = fl; ceils = cl; r = rr; diag = dd; k = DiagHalfWidth(rr); break; }
                }
                if (path == null)
                {
                    if (e.Tree) throw new LayoutException($"no usable corridor {a.Id}->{b.Id}");
                    level.Notes.Add($"loop {e.A}-{e.B} dropped");
                    continue;
                }
                bool anyDiag = false;
                for (int i = 0; i + 1 < path.Count; i++) { var d = Dir(path, i); if (d.Item1 != 0 && d.Item2 != 0) anyDiag = true; }
                Carve(level, router, e, a, b, path, floors, ceils, r, k, CorridorHeight(cfg, r), diag && anyDiag, drop ? "drop" : null, env);
            }
            return level;
        }

        /// <summary>A half cell must be alone in its column; where something was stacked over one, fill it.</summary>
        public static void CleanupDiagonals(Level level)
        {
            foreach (var c in level.Diag.Keys.ToList())
            {
                if (level.Spans.TryGetValue(c, out var spans) && spans.Count > 1)
                {
                    if (level.DiagSpan.TryGetValue(c, out var sp)) spans.Remove(sp);
                    level.Diag.Remove(c);
                }
                else if (!level.Spans.ContainsKey(c)) level.Diag.Remove(c);
            }
        }
    }
}
