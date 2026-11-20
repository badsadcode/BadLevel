using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    public sealed class LayoutException : Exception
    {
        public LayoutException(string message) : base(message) { }
    }

    /// <summary>A minimal binary min-heap (netstandard2.1 has no PriorityQueue). Ties pop in push order.</summary>
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

    /// <summary>Spatial layout (layout.py): mission graph -> rooms on a grid -> corridors with stairs.
    /// <list type="number">
    /// <item>Rooms are placed by walking the graph's spanning tree from the start room. Each child is put
    /// next to its parent in a random direction (or as the composition says), on a floor level close to
    /// the parent's, far enough away that a corridor can climb the height difference. Some neighbours
    /// are placed touching instead: one merged hall, no corridor.</item>
    /// <item>Corridors are routed with A* over the grid (multi-source from every cell of room A to any
    /// cell of room B), keeping a solid wall between them and other rooms / corridors, with a turn
    /// penalty for straighter halls.</item>
    /// <item>Height changes become real stairs, placed only on straight stretches so every step is full
    /// width.</item>
    /// </list></summary>
    public static class Layout
    {
        public static readonly (int, int)[] Dirs = { (1, 0), (0, 1), (-1, 0), (0, -1) };
        // 8 directions in 45-degree order, so turn angle = index difference
        public static readonly (int, int)[] Dirs8 = { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        public static readonly Dictionary<int, double> TurnCost = new Dictionary<int, double> { [0] = 0.0, [1] = 1.5, [2] = 2.5, [3] = 8.0 };
        public static readonly Dictionary<int, int> CorridorHeight = new Dictionary<int, int> { [1] = 128, [2] = 160, [3] = 192 };
        public const int Pad = 4;    // solid cells around the whole layout
        public const int Slab = 16;  // minimum solid between stacked spaces

        // ------------------------------------------------------------------------------ room placement
        public static (int W, int H, int Height) RoomSize(GenConfig cfg, string size, Rng rng)
        {
            var (lo, hi, hmin, hmax) = cfg.RoomSizes[size];
            lo = Math.Max(6, (int)Math.Round(lo * cfg.RoomScale));
            hi = Math.Max(lo, (int)Math.Round(hi * cfg.RoomScale));
            int w = rng.Int(lo, hi), h = rng.Int(lo, hi);
            int height = rng.Range(hmin, hmax + 1, 16);
            double v = cfg.SizeVariety;
            if (v > 0 && (size == "small" || size == "medium" || size == "large"))
            {
                // a wider spread of sizes: from a closet to a hall in the same level (0.66x - 2x at 1)
                double f = Math.Pow(2, rng.Uniform(-0.6 * v, 1.0 * v));
                w = Math.Max(6, Math.Min(40, (int)Math.Round(w * f)));
                h = Math.Max(6, Math.Min(40, (int)Math.Round(h * f)));
                height = (int)Math.Round(height * Math.Sqrt(f) / 16) * 16;
            }
            height = Math.Max(128, (int)Math.Round(height * cfg.HeightScale / 16) * 16);
            return (w, h, height);
        }

        public static bool VerticalConflict((int Lo, int Hi) a, (int Lo, int Hi) b, int gap = Slab) => a.Lo < b.Hi + gap && b.Lo < a.Hi + gap;

        /// <summary>Footprints too close and on overlapping heights (rooms on other storeys may stack).
        /// setback: extra distance around open areas, so their surroundings can become roofs.</summary>
        public static bool Overlaps(Room r, IEnumerable<Room> rooms, int gap, int setback = 0)
        {
            foreach (var o in rooms)
            {
                if (!VerticalConflict(r.Slot, o.Slot)) continue;
                int g = gap + (r.Kind == "courtyard" || o.Kind == "courtyard" ? setback : 0);
                if (r.X - g < o.X + o.W && o.X - g < r.X + r.W && r.Y - g < o.Y + o.H && o.Y - g < r.Y + r.H) return true;
            }
            return false;
        }

        static bool IsConnectorKind(string kind) => Array.IndexOf(MissionGraph.ConnectorKinds, kind) >= 0;

        /// <summary>Floor heights decided up front for multi-storey levels: storey * height + a small offset.</summary>
        public static Dictionary<int, int> StoreyFloors(MissionGraph g, GenConfig cfg, Rng rng)
        {
            int H = cfg.StoreyHeight;
            var floors = new Dictionary<int, int>();
            var tree = g.Edges.Where(e => e.Tree).ToList();
            foreach (var n in g.Nodes)
                if (!IsConnectorKind(n.Kind) && n.Kind != "secret")
                    floors[n.Id] = n.Storey * H + rng.Choice(new[] { 0, 0, 32, 64 });
            foreach (var n in g.Nodes)
                if (n.Kind == "secret")
                {
                    var e = g.Edges.First(x => x.A == n.Id || x.B == n.Id);
                    int parent = e.B == n.Id ? e.A : e.B;
                    floors[n.Id] = floors.TryGetValue(parent, out var f) ? f : n.Storey * H;
                }
            foreach (var n in g.Nodes)
                if (IsConnectorKind(n.Kind))
                {
                    var nb = tree.Where(e => e.A == n.Id || e.B == n.Id).Select(e => e.B == n.Id ? e.A : e.B).ToList();
                    floors[n.Id] = nb.Min(m => floors[m]);
                    n.Top = nb.Max(m => floors[m]);
                }
            return floors;
        }

        public static List<Room> PlaceRooms(MissionGraph g, GenConfig cfg, Rng rng)
        {
            bool multi = cfg.Storeys > 1;
            var preset = multi ? StoreyFloors(g, cfg, rng) : new Dictionary<int, int>();

            // a candidate room for a node, or null if its space type can't exist at this height
            Room Make(Node node, int x, int y, int floor, int parentFloor) => Spaces.SpaceFor(node.Kind).Propose(node, floor, cfg, rng, multi);

            string comp = cfg.Composition;
            int root = (comp == "hub" || comp == "central") && g.Center.HasValue ? g.Center.Value : 0;
            if (cfg.Packing >= 0.5)
            {
                var packed = Packed.PlacePacked(g, cfg, rng, Make);         // a divided floor plan (packed.py)
                if (packed != null)
                {
                    var res = g.Nodes.Select(n => packed[n.Id]).ToList();
                    foreach (var r in res) Spaces.SpaceFor(r.Kind).AssignShape(r, g.Nodes[r.Node], cfg, rng, false);
                    int mx = res.Min(r => r.X), my = res.Min(r => r.Y);
                    for (int idx = 0; idx < res.Count; idx++)
                    {
                        res[idx].Id = idx;
                        res[idx].X += Pad - mx;
                        res[idx].Y += Pad - my;
                    }
                    return res;
                }
            }
            var first = g.Nodes[root];
            var rooms = new Dictionary<int, Room>();
            var r0 = Make(first, 0, 0, preset.TryGetValue(root, out var f0) ? f0 : 0, 0) ?? throw new LayoutException("start room");
            rooms[root] = r0;
            int[] spokes = comp == "hub" || comp == "central" ? new[] { rng.Range(0, 4) } : new[] { 0 };
            var axis = comp == "linear" ? Dirs[rng.Range(0, 4)] : (0, 0);

            int maxFloor = cfg.MaxLevel * cfg.LevelStep;
            var halls = new List<List<int>>();       // node ids per merged hall
            var order = new List<int> { root };
            var treeEdges = g.Edges.Where(e => e.Tree).ToList();
            int setback = cfg.YardSetback;
            for (int i = 0; i < order.Count; i++)
            {
                int pid = order[i];
                var parent = rooms[pid];
                foreach (var e in treeEdges)
                {
                    if (e.A != pid && e.B != pid) continue;
                    int cid = e.A == pid ? e.B : e.A;
                    if (rooms.ContainsKey(cid)) continue;
                    var node = g.Nodes[cid];
                    Room placed = null;
                    // merged hall: some neighbours are placed touching the parent, one space, no corridor
                    var hallOf = new Dictionary<int, List<int>>();
                    foreach (var h0_ in halls) foreach (var rr in h0_) hallOf[rr] = h0_;
                    bool merge = HallSpace.Mergeable(e, parent, node, g) && rng.Random() < cfg.MergeChance
                                 && (hallOf.TryGetValue(pid, out var ph) ? ph.Count : 1) < cfg.MaxHallRooms;
                    int tries = 60 + (int)(60 * cfg.Spaciousness);    // bigger rooms are harder to fit
                    double pull = cfg.DiagonalPull;
                    bool diagWish = pull > 0 && !merge && !IsConnectorKind(node.Kind) && node.Kind != "secret"
                                    && rng.Random() < cfg.DiagonalChance * pull;
                    bool diagPlace = false;
                    for (int t = 0; t < tries; t++)
                    {
                        if (merge && t >= 25) merge = false;            // couldn't fit: fall back to a corridor
                        int floor;
                        if (multi) floor = preset[cid];
                        else if (node.Kind == "secret") floor = parent.Floor;
                        else
                        {
                            int dl = rng.Choice(new[] { -1, 0, 0, 1, 1, 2 });
                            floor = Math.Min(Math.Max(parent.Floor + dl * cfg.LevelStep, 0), maxFloor);
                        }
                        var cand = Make(node, 0, 0, floor, parent.Floor)
                                   ?? throw new LayoutException($"connector {node.Kind} can't climb {(node.Top ?? floor) - floor}");
                        int w = cand.W, h = cand.H;
                        // corridor climb between the two attachment heights
                        int pf = PortFloor(parent, cand.Floor, cand.TopFloor);
                        int cf = PortFloor(cand, parent.Floor, parent.TopFloor);
                        int climb = Math.Abs(pf - cf) / cfg.StepHeight;
                        int gap = Math.Max(cfg.RoomGap + 4, climb + 2 * cfg.CorridorRadius + 8) + rng.Int(0, 5);
                        if (comp == "central" && pid == root)
                            gap = Math.Max(cfg.RoomGap + 4, climb + 2 * cfg.CorridorRadius + 6);   // a tight ring
                        if (setback != 0 && (node.Kind == "courtyard" || parent.Kind == "courtyard"))
                            gap += setback;                    // an open area keeps room around it for its roofs
                        bool stack = multi && t % 3 == 2 && !merge;
                        diagPlace = diagWish && t < tries / 2 && !stack;
                        int x, y;
                        if (merge)
                        {
                            if (!multi && node.Kind != "secret")
                            {
                                int mf = parent.Floor + rng.Choice(new[] { 0, 0, 0, 16, 32, 48, 64, -32, -64 });
                                mf = Math.Min(Math.Max(mf, 0), maxFloor);
                                int ht = cand.Ceil - cand.Floor;
                                cand.Floor = mf; cand.Ceil = mf + ht;
                            }
                            if (Math.Abs(cand.Floor - parent.Floor) > 64) continue;
                            int mdx, mdy;
                            if (comp == "tree" || t >= 15) (mdx, mdy) = rng.Choice(Dirs);
                            else   // merged rooms follow the composition too (a spine stays a spine)
                                (mdx, mdy) = ComposedDir(comp, rooms[root], parent, pid == root, node, spokes, axis, rng);
                            const int lap = 6;
                            if (mdx != 0)
                            {
                                if (Math.Min(h, parent.H) < lap + 2) continue;
                                x = mdx > 0 ? parent.X + parent.W : parent.X - w;
                                y = parent.Y + rng.Int(-(h - lap), parent.H - lap);
                            }
                            else
                            {
                                if (Math.Min(w, parent.W) < lap + 2) continue;
                                y = mdy > 0 ? parent.Y + parent.H : parent.Y - h;
                                x = parent.X + rng.Int(-(w - lap), parent.W - lap);
                            }
                            cand.X = x; cand.Y = y;
                            var others = rooms.Values.Where(o => o != parent);
                            if (Overlaps(cand, others, cfg.RoomGap + 2 * cfg.CorridorRadius + 2, setback) || Overlaps(cand, new[] { parent }, 0))
                                continue;
                            if (!HallSpace.SeamOk(cand, parent)) continue;
                            placed = cand;
                            break;
                        }
                        if (stack)
                        {
                            // try to sit over / under an existing room on another storey, near the parent
                            var others = rooms.Values.Where(o => !VerticalConflict(cand.Slot, o.Slot)
                                                                  && Math.Abs(o.X - parent.X) + Math.Abs(o.Y - parent.Y) < 40).ToList();
                            if (others.Count == 0) continue;
                            var o = rng.Choice(others);
                            x = o.X + rng.Int(-(w / 2), Math.Max(0, o.W - w / 2));
                            y = o.Y + rng.Int(-(h / 2), Math.Max(0, o.H - h / 2));
                        }
                        else
                        {
                            var (dx, dy) = comp == "tree" || t >= 30 ? rng.Choice(Dirs)
                                : ComposedDir(comp, rooms[root], parent, pid == root, node, spokes, axis, rng);
                            if (comp == "linear" && node.Main && t < 30)
                            {
                                // the spine: rooms centred on the axis, not shifted sideways
                                if (dx != 0) { x = dx > 0 ? parent.X + parent.W + gap : parent.X - gap - w; y = parent.Y + (parent.H - h) / 2 + rng.Int(-1, 1); }
                                else { y = dy > 0 ? parent.Y + parent.H + gap : parent.Y - gap - h; x = parent.X + (parent.W - w) / 2 + rng.Int(-1, 1); }
                            }
                            else if (diagPlace)
                            {
                                // off the parent's corner, so the corridor between them cuts across at 45
                                // degrees (the gap on both axes leaves room past the square doorways)
                                int sx = dx != 0 ? dx : rng.Choice(new[] { -1, 1 });
                                int sy = dy != 0 ? dy : rng.Choice(new[] { -1, 1 });
                                x = sx > 0 ? parent.X + parent.W + gap : parent.X - gap - w;
                                y = sy > 0 ? parent.Y + parent.H + gap : parent.Y - gap - h;
                                x -= sx * rng.Int(0, Math.Max(0, Math.Min(w, parent.W) / 2));
                                y -= sy * rng.Int(0, Math.Max(0, Math.Min(h, parent.H) / 2));
                            }
                            else if (dx != 0) { x = dx > 0 ? parent.X + parent.W + gap : parent.X - gap - w; y = parent.Y + rng.Int(-(h - 4), parent.H - 4); }
                            else { y = dy > 0 ? parent.Y + parent.H + gap : parent.Y - gap - h; x = parent.X + rng.Int(-(w - 4), parent.W - 4); }
                        }
                        cand.X = x; cand.Y = y;
                        if (stack && !Overlaps(cand, new[] { parent }, gap - 4) &&
                            Math.Abs((x + w / 2.0) - (parent.X + parent.W / 2.0)) + Math.Abs((y + h / 2.0) - (parent.Y + parent.H / 2.0)) > 60)
                            continue;
                        if (!Overlaps(cand, rooms.Values, cfg.RoomGap + 2 * cfg.CorridorRadius + 2, setback))
                        {
                            placed = cand;
                            if (diagPlace) placed.Meta["diag_from"] = pid;     // its corridor cuts across at 45 degrees
                            break;
                        }
                    }
                    if (placed == null) throw new LayoutException($"could not place room for node {cid}");
                    rooms[cid] = placed;
                    order.Add(cid);
                    if (merge)
                    {
                        e.Merged = true;
                        if (!hallOf.TryGetValue(pid, out var h0))
                        {
                            h0 = new List<int> { pid };
                            halls.Add(h0);
                        }
                        h0.Add(cid);
                    }
                }
            }
            if (rooms.Count != g.Nodes.Count) throw new LayoutException("graph spanning tree did not reach every node");

            // normalise so the layout starts at (Pad, Pad); room ids == node ids
            var result = g.Nodes.Select(n => rooms[n.Id]).ToList();
            var mergedNodes = new HashSet<int>(halls.SelectMany(h => h));
            foreach (var r in result) Spaces.SpaceFor(r.Kind).AssignShape(r, g.Nodes[r.Node], cfg, rng, mergedNodes.Contains(r.Node));
            int minx = result.Min(r => r.X), miny = result.Min(r => r.Y);
            for (int idx = 0; idx < result.Count; idx++)
            {
                result[idx].Id = idx;
                result[idx].X += Pad - minx;
                result[idx].Y += Pad - miny;
            }
            foreach (var h in halls)
                foreach (var n in h) rooms[n].Meta["hall"] = h.OrderBy(v => v).ToList();
            return result;
        }

        /// <summary>Which side of the parent a new room goes on, by composition. hub / central: the root's
        /// children go round its four sides in turn (spokes); further rooms keep heading outwards, away
        /// from the centre. linear: the critical path keeps to one axis (the spine), side rooms branch off
        /// perpendicular (ribs).</summary>
        public static (int, int) ComposedDir(string comp, Room root, Room parent, bool fromRoot, Node node, int[] spokes, (int, int) axis, Rng rng)
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

        /// <summary>Height at which a corridor from room towards a room at otherFloor attaches.</summary>
        public static int PortFloor(Room room, int otherFloor, int? otherTop = null)
        {
            var landings = room.Get<List<int>>("landings");
            if (landings != null && landings.Count > 0)          // a tower: the landing nearest that height
                return landings.OrderBy(f => Math.Abs(f - otherFloor)).First();
            if (room.TopFloor == null) return room.Floor;
            int target = otherFloor;
            return Math.Abs(target - room.TopFloor.Value) < Math.Abs(target - room.Floor) ? room.TopFloor.Value : room.Floor;
        }

        // ------------------------------------------------------------------------------ corridors
        /// <summary>Direction of the path at i (Python indexing: a one-cell path gives (0, 0)).</summary>
        public static (int, int) Dir(List<Cell> path, int i)
        {
            int j = Math.Min(i, path.Count - 2);
            Cell a = path[j < 0 ? j + path.Count : j], b = path[j + 1];
            return (b.X - a.X, b.Y - a.Y);
        }

        public static bool Straight(List<Cell> path, int i, int reach, bool axisOnly = false)
        {
            int lo = i - reach - 1, hi = i + reach + 1;
            if (lo < 0 || hi >= path.Count) return false;
            var d0 = (path[lo + 1].X - path[lo].X, path[lo + 1].Y - path[lo].Y);
            if (axisOnly && d0.Item1 != 0 && d0.Item2 != 0) return false;
            for (int j = lo + 1; j < hi; j++)
                if ((path[j + 1].X - path[j].X, path[j + 1].Y - path[j].Y) != d0) return false;
            return true;
        }

        /// <summary>Floor height for every centre-line cell, with stairs on straight runs.</summary>
        public static List<int> PlanFloors(List<Cell> path, int fa, int fb, GenConfig cfg, int? r = null)
        {
            int rr = r ?? cfg.CorridorRadius;
            int steps = Math.Abs(fb - fa) / cfg.StepHeight, n = path.Count;
            if (steps == 0) return Enumerable.Repeat(fa, n).ToList();
            int margin = rr + 2, reach = rr;
            if (cfg.Packing >= 0.5) { margin = 1; reach = 0; }   // packed: the (straight) doorway passage holds the stairs
            var cands = new List<int>();
            for (int i = margin; i < n - margin; i++) if (Straight(path, i, reach)) cands.Add(i);
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

        /// <summary>Band half-width (in cells across a 45-degree band) matching a straight corridor of radius r.</summary>
        public static int DiagHalfWidth(int r) => Math.Max(1, (int)Math.Round((2 * r + 1) * 32 / 45.25 - 1));

        /// <summary>Cells covered by the corridor around one centre-line cell.</summary>
        public static IEnumerable<(Cell C, int Dist)> Stamp(Cell center, (int, int) direction, int r, int k)
        {
            var (dx, dy) = direction;
            if (dx != 0 && dy != 0)
            {
                bool same = dx == dy;  // (1,1)/(-1,-1): band across x-y; otherwise across x+y
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
            if (solid.Count != 2) return null;
            foreach (var c in Shape.AllCorners)
            {
                var ss = Shape.SolidSides(c);
                if (solid.Contains(ss[0]) && solid.Contains(ss[1])) return c;
            }
            return null;
        }

        /// <summary>Smooth 45-degree walls: a solid cell with exactly two adjacent open sides (both cells of
        /// this corridor, at most one stair step apart) becomes a diagonal half cell. Covers diagonal
        /// stretches and chamfers inner corners of bends. Anything ambiguous stays solid.</summary>
        public static List<Cell> AddHalfCells(Level level, int cid, List<Cell> carved)
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
                var spans = new HashSet<Span>();
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
                if (corner == null) continue;      // open sides opposite each other: not a corner
                // on stairs the two neighbours may differ by one step: take the overlap
                int f = spans.Max(s => s.Floor), ceil = spans.Min(s => s.Ceil);
                if (f - spans.Min(s => s.Floor) > level.Config.StepHeight || ceil - f < level.Config.PlayerHeight) continue;
                level.AddSpan(c, new Span(f, ceil), own);
                level.Diag[c] = corner.Value;
                level.DiagSpan[c] = new Span(f, ceil);
                added.Add(c);
            }
            return added;
        }

        /// <summary>Cells of empty space between two room footprints (0 if they touch or overlap).</summary>
        public static int Gap(Room a, Room b)
        {
            int dx = Math.Max(Math.Max(b.X - (a.X + a.W), a.X - (b.X + b.W)), 0);
            int dy = Math.Max(Math.Max(b.Y - (a.Y + a.H), a.Y - (b.Y + b.H)), 0);
            return dx + dy;
        }

        /// <summary>Loops make circuits: pick rooms apart in the graph (2+ hops, 3+ preferred) but close in
        /// space, so the loop corridor is short. Now and then a long one on purpose.</summary>
        public static void ChooseLoops(MissionGraph g, List<Room> rooms, GenConfig cfg, Rng rng)
        {
            int nLoops = cfg.Loops + (cfg.Composition == "central" ? 2 : 0);
            for (int k = 0; k < nLoops; k++)
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
                        if (cfg.Packing >= 0.5 && Gap(a, b) > 4) continue;       // packed: loops only through a shared wall
                        // close in space; bigger circuits (3+ hops) and same-height pairs (junctions) preferred;
                        // radial compositions bring loops back to their centre
                        int bonus = g.Center.HasValue && (g.Center == a.Node || g.Center == b.Node) ? -20 : 0;
                        cands.Add((Gap(a, b) + (hops == 2 ? 10 : 0) + (a.Floor != b.Floor ? 12 : 0) + bonus, a.Node, b.Node));
                    }
                if (cands.Count == 0) break;
                cands.Sort();
                var pick = rng.Random() < cfg.LongLoopChance && cfg.Composition != "linear"
                    ? rng.Choice(cands.Skip(cands.Count / 2).ToList())          // a deliberately long one (not on a spine)
                    : rng.Choice(cands.Take(3).ToList());
                var e = g.AddEdge(pick.A, pick.B, tree: false);
                if (g.Nodes[pick.A].Storey != g.Nodes[pick.B].Storey) e.Connector = "drop";
            }
            if (cfg.Mode == "arena") ArenaLoops(g, rooms, cfg);
        }

        /// <summary>Deathmatch hates dead ends: every room with one way in gets a second, to the nearest
        /// room it isn't joined to (routed like any loop; if it can't be routed, it stays a dead end).</summary>
        public static void ArenaLoops(MissionGraph g, List<Room> rooms, GenConfig cfg)
        {
            bool packed = cfg.Packing >= 0.5;
            foreach (var a in rooms)
            {
                if (a.IsConnector || a.Kind == "secret") continue;
                if (g.Neighbors(a.Node).Count() != 1) continue;
                var cands = new List<(int Score, int B)>();
                foreach (var b in rooms)
                {
                    if (b == a || b.IsConnector || b.Kind == "secret" || g.Connected(a.Node, b.Node)) continue;
                    if (packed && Gap(a, b) > 4) continue;
                    cands.Add((Gap(a, b) + (a.Floor != b.Floor ? 12 : 0), b.Node));
                }
                if (cands.Count > 0)
                {
                    int b = cands.Min().B;
                    var e = g.AddEdge(a.Node, b, tree: false);
                    if (g.Nodes[a.Node].Storey != g.Nodes[b].Storey) e.Connector = "drop";
                }
            }
        }

        /// <summary>(port cells or null, floor) where a corridor from room to other attaches.</summary>
        public static (HashSet<Cell> Port, int Floor) Attachment(Room room, Room other) => Spaces.SpaceFor(room.Kind).Attachment(room, other);

        /// <summary>A one-way drop: the corridor runs level from hi, then falls down a short shaft to lo's floor.</summary>
        public static bool DropFits(Room hi, Room lo, GenConfig cfg, int h = 128)
        {
            if (hi.IsConnector || lo.IsConnector) return false;
            int d = hi.Floor - lo.Floor;
            return 64 <= d && d <= cfg.MaxDrop;
        }

        /// <summary>Floors and ceilings for a drop corridor: level at fa, a 2-cell shaft (floor fb, ceiling
        /// fa + h) on a straight stretch, then level at fb. Null if there is no straight stretch.</summary>
        public static (List<int> Floors, List<int> Ceils)? PlanDrop(List<Cell> path, int fa, int fb, int r, int h)
        {
            int n = path.Count;
            var cands = new List<int>();
            for (int i = r + 2; i < n - r - 3; i++) if (Straight(path, i, r + 1)) cands.Add(i);
            if (cands.Count == 0) return null;
            int t = cands.OrderBy(i => Math.Abs(i - n / 2)).First();
            return (Enumerable.Range(0, n).Select(i => i < t ? fa : fb).ToList(),
                    Enumerable.Range(0, n).Select(i => i < t + 2 ? fa + h : fb + h).ToList());
        }

        public static Level BuildLayout(MissionGraph g, GenConfig cfg, Rng rng, int seed)
        {
            var rooms = PlaceRooms(g, cfg, rng);
            int width = rooms.Max(r => r.X + r.W) + Pad;
            int height = rooms.Max(r => r.Y + r.H) + Pad;
            // leave room for corridors to swing around the outside of the layout
            const int extra = 8;
            foreach (var r in rooms) { r.X += extra; r.Y += extra; }
            width += 2 * extra;
            height += 2 * extra;

            var level = new Level { Seed = seed, CellSize = cfg.Cell, Width = width, Height = height, Rooms = rooms, Graph = g, Config = cfg };
            foreach (var room in rooms) Spaces.SpaceFor(room.Kind).Build(level, room, cfg);

            HallSpace.BuildHalls(level, g, cfg);
            ChooseLoops(g, rooms, cfg, rng);
            var router = new Router(rooms, width, height, cfg);
            // route tree edges first (they must succeed), then loops (optional)
            foreach (var e in g.Edges.OrderBy(e => e.Tree ? 0 : 1).ThenBy(e => e.Id).ToList())
            {
                if (e.Merged) continue;                          // one hall: the rooms touch, no corridor
                Room a = rooms[e.A], b = rooms[e.B];
                bool drop = false;
                if (e.Connector == "drop")
                {
                    var (hi, lo) = a.Floor > b.Floor ? (a, b) : (b, a);
                    if (DropFits(hi, lo, cfg) && rng.Random() < cfg.DropChance) { a = hi; b = lo; drop = true; }
                }
                var (r, diag) = CorridorSpace.CorridorStyle(g, e, a, b, cfg, rng);
                bool vent = cfg.Vents > 0 && !e.Tree && e.Kind == "open" && !drop
                            && !(a.IsConnector || b.IsConnector) && Gap(a, b) <= 14       // a short-cut, not a trek
                            && Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/vent/{e.Id}").Random() < cfg.Vents;
                if (vent) { r = 1; diag = false; }               // a duct: 3 wide, straight runs, low (vent_height)
                var (aPort, fa) = Attachment(a, b);
                var (bPort, fb) = Attachment(b, a);
                List<Cell> path = null; List<int> floors = null, ceils = null;
                int k = 1;
                (int, int) env = (0, 0);
                // preferred style first, then fall back to a plain narrow corridor
                foreach (var (rr, dd) in new[] { (r, diag), (r, false), (1, false) }.Distinct())
                {
                    int hh = vent ? cfg.VentHeight : CorridorSpace.CorridorHeight(cfg, a, b, rr);
                    env = (Math.Min(fa, fb), Math.Max(fa, fb) + hh);
                    // a level loop may cross other corridors at its own height: junctions instead of detours
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
                    var inner = cand.Count >= 2 ? cand.GetRange(1, cand.Count - 2) : new List<Cell>();
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
                for (int i = 0; i + 1 < path.Count; i++) { var d = Dir(path, i); if (d.Item1 != 0 && d.Item2 != 0) { anyDiag = true; break; } }
                diag = diag && anyDiag;
                int h = vent ? cfg.VentHeight : CorridorSpace.CorridorHeight(cfg, a, b, r);
                var corr = CarveCorridor(level, router, e.Id, a, b, path, floors, ceils, r, k, h, diag,
                                         e.Kind, e.Key, drop ? "drop" : null, env);
                if (vent)
                {
                    corr.Vent = true;
                    string tag = $"corridor:{corr.Id}";
                    foreach (var kv in level.Owners)
                        if (kv.Value.Any(o => o.Name == tag) && !level.PartCells.ContainsKey(kv.Key))
                            level.PartCells[kv.Key] = $"{tag}/vent";     // the duct, to swap for a real one
                }
                if (cfg.CorridorBeats && e.Kind != "secret" && !vent)
                    CorridorSpace.AddBeats(level, router, corr, rng);
            }
            if (cfg.Storeys > 1 && cfg.Galleries)
                Openings.AddGalleries(level, router, rng);
            if (cfg.Underground && cfg.Storeys > 1)
                Openings.AddOverlook(level, router, rng);
            return level;
        }

        /// <summary>Carve a routed corridor into the level and register it.</summary>
        public static Corridor CarveCorridor(Level level, Router router, int edgeId, Room a, Room b, List<Cell> path, List<int> floors,
                                             List<int> ceils, int r, int k, int h, bool diag, string kind = "open", string key = null,
                                             string connector = null, (int Lo, int Hi)? env = null)
        {
            int cid = level.Corridors.Count;
            var corr = new Corridor { Id = cid, Edge = edgeId, A = a.Id, B = b.Id, Path = path, Floors = floors, Kind = kind, Key = key,
                                      Radius = r, Height = h, Diagonal = diag, Connector = connector };
            // each carved cell takes the floor of its nearest centre-line cell
            var claim = new Dictionary<Cell, (int Dist, int I, int F, int Top)>();
            var claimOrder = new List<Cell>();                  // (Python dict order)
            for (int i = 0; i < path.Count; i++)
            {
                var dirs = new List<(int, int)> { Dir(path, i) };
                if (i > 0 && !dirs.Contains(Dir(path, i - 1))) dirs.Add(Dir(path, i - 1));   // union of both stamps at bends
                foreach (var d in dirs)
                    foreach (var (c, dist) in Stamp(path[i], d, r, k))
                    {
                        var pri = (dist, i, floors[i], ceils[i]);
                        if (!claim.TryGetValue(c, out var old)) { claim[c] = pri; claimOrder.Add(c); }
                        else if (pri.CompareTo(old) < 0) claim[c] = pri;
                    }
            }
            var carved = new List<Cell>();
            foreach (var c in claimOrder)
            {
                var (_, _, f, top) = claim[c];
                if (!level.FreeAt(c, f, top, Slab) || a.Contains(c) || b.Contains(c))
                {
                    // the rooms themselves, something stacked too close, or a corridor we cross
                    foreach (var sp in level.SpansIn(c, f, top).ToList())
                    {
                        var o = level.OwnerOf(c, sp) ?? "";
                        if (o.StartsWith("corridor:") && sp.Floor == f && int.TryParse(o.Substring(9), out var j) && !corr.Junctions.Contains(j))
                            corr.Junctions.Add(j);
                    }
                    continue;
                }
                level.AddSpan(c, new Span(f, top), $"corridor:{cid}");
                carved.Add(c);
            }
            // a junction opening against another corridor's 45-degree half cell: make that cell fully open
            foreach (var c in carved)
                foreach (var (dx, dy) in Dirs)
                {
                    var n = c.Offset(dx, dy);
                    if (level.Diag.TryGetValue(n, out var corner) && Shape.SolidSides(corner).Contains((-dx, -dy)) &&
                        (level.OwnerOf(n, level.Spans[n][0]) ?? "").StartsWith("corridor:"))
                    { level.Diag.Remove(n); level.DiagSpan.Remove(n); }
                }
            carved.AddRange(AddHalfCells(level, cid, carved));
            var envv = env ?? (floors.Min(), ceils.Max());
            foreach (var c in carved)
                foreach (var sp in level.SpansIn(c, envv.Lo, envv.Hi).ToList()) router.AddOpen(c, sp.Floor, sp.Ceil);
            level.Corridors.Add(corr);
            return corr;
        }

        /// <summary>A 45-degree half cell must be alone in its column; where something got stacked over or
        /// under one, its open triangle is filled in (always safe: it only removes open space).</summary>
        public static void CleanupDiagonals(Level level)
        {
            foreach (var c in level.Diag.Keys.ToList())
            {
                if (level.Spans.TryGetValue(c, out var spans) && spans.Count > 1)
                {
                    if (level.DiagSpan.TryGetValue(c, out var sp)) spans.Remove(sp);
                    level.Diag.Remove(c);
                }
                else if (!level.Spans.ContainsKey(c)) level.Diag.Remove(c);   // (C# safety net: a half cell with no span left is solid)
            }
        }
    }

    /// <summary>A* corridor router on the cell grid. Each route has its own half width r (3, 5 or 7 cells
    /// wide). With diagonal the centre line may also move at 45 degrees; diagonal stretches are carved as
    /// a band of half-width k cells (measured across the band), so a diagonal corridor is about as wide
    /// as a straight one.</summary>
    public sealed class Router
    {
        public const int MaxReach = 7;
        public readonly List<Room> Rooms;
        public readonly int W, H;
        public readonly GenConfig Cfg;
        public readonly Dictionary<Cell, List<int>> RoomAt = new Dictionary<Cell, List<int>>();
        // cells around rooms: cell -> [(room id, Chebyshev distance to the room's box)]
        readonly Dictionary<Cell, List<(int Room, int Dist)>> nearRoom = new Dictionary<Cell, List<(int, int)>>();
        // every corridor cell carved so far, with its vertical range
        public readonly Dictionary<Cell, List<(int Lo, int Hi)>> OpenCells = new Dictionary<Cell, List<(int, int)>>();

        public Router(List<Room> rooms, int width, int height, GenConfig cfg)
        {
            Rooms = rooms; W = width; H = height; Cfg = cfg;
            foreach (var room in rooms) Index(room);
        }

        void Index(Room room)
        {
            foreach (var c in room.Cells())
            {
                if (!RoomAt.TryGetValue(c, out var l)) RoomAt[c] = l = new List<int>();
                l.Add(room.Id);
            }
            int R = MaxReach;
            for (int y = room.Y - R; y < room.Y + room.H + R; y++)
                for (int x = room.X - R; x < room.X + room.W + R; x++)
                    if (!room.Contains(x, y))
                    {
                        var c = new Cell(x, y);
                        if (!nearRoom.TryGetValue(c, out var l)) nearRoom[c] = l = new List<(int, int)>();
                        l.Add((room.Id, room.Dist(x, y)));
                    }
        }

        /// <summary>Register a room added after placement (an overlook booth): the router avoids it like
        /// any other room and can route into it.</summary>
        public void AddRoom(Room room)
        {
            if (!Rooms.Contains(room)) Rooms.Add(room);
            Index(room);
        }

        public void RemoveRoom(Room room)
        {
            foreach (var cells in RoomAt.Values) cells.Remove(room.Id);
            foreach (var lst in nearRoom.Values) lst.RemoveAll(t => t.Room == room.Id);
            if (Rooms.Count > 0 && Rooms[Rooms.Count - 1] == room) Rooms.RemoveAt(Rooms.Count - 1);
        }

        public void AddOpen(Cell c, int lo, int hi)
        {
            if (!OpenCells.TryGetValue(c, out var l)) OpenCells[c] = l = new List<(int, int)>();
            l.Add((lo, hi));
        }

        /// <summary>(blocked, soft): cells too close to other corridors, and cells where a level corridor
        /// may cross another one at exactly its own floor height (a junction, at a small cost).</summary>
        public (HashSet<Cell> Hard, HashSet<Cell> Soft) CorridorBlock(int ext, (int Lo, int Hi) env, int? crossFloor = null)
        {
            int reach = ext + 1;
            var hard = new HashSet<Cell>(); var soft = new HashSet<Cell>();
            foreach (var kv in OpenCells)
            {
                var conflicting = kv.Value.Where(rg => Layout.VerticalConflict(env, rg, 32)).ToList();
                if (conflicting.Count == 0) continue;
                bool crossable = crossFloor.HasValue && conflicting.All(rg => rg.Lo == crossFloor.Value);
                var target = crossable ? soft : hard;
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++) target.Add(kv.Key.Offset(dx, dy));
            }
            soft.ExceptWith(hard);
            return (hard, soft);
        }

        /// <summary>Extra cost of having the corridor centre at c, or null if blocked.</summary>
        public double? Allowed(Cell c, Room a, Room b, int r, int ext, HashSet<Cell> blocked, (int Lo, int Hi) env,
                               HashSet<Cell> bPort, HashSet<Cell> soft = null)
        {
            int x = c.X, y = c.Y;
            if (x <= ext + 2 || y <= ext + 2 || x >= W - ext - 3 || y >= H - ext - 3) return null;
            if (RoomAt.TryGetValue(c, out var rids) && rids.Count > 0)
            {
                foreach (var rid in rids)
                {
                    if (rid == a.Id || rid == b.Id)
                    {
                        var room = Rooms[rid];
                        if (room.IsDiag(x, y) || (rid == b.Id && bPort != null && !bPort.Contains(c))) return null;
                        return 0.0;
                    }
                }
                if (rids.Any(rid => Layout.VerticalConflict(Rooms[rid].Slot, env))) return null;
            }
            if (blocked.Contains(c)) return null;
            double cost = soft != null && soft.Contains(c) ? 3.0 : 0.0;      // crossing: allowed, but cross quickly
            if (nearRoom.TryGetValue(c, out var near))
                foreach (var (rid, dist) in near)
                {
                    if (rid != a.Id && rid != b.Id)
                    {
                        if (dist <= ext + 1 && Layout.VerticalConflict(Rooms[rid].Slot, env)) return null;
                        continue;
                    }
                    if (dist > r + 1) continue;
                    var room = rid == a.Id ? a : b;
                    // inside the wall margin of an endpoint room: only straight in, away from corners
                    bool insideX = room.X + r <= x && x <= room.X + room.W - 1 - r;
                    bool insideY = room.Y + r <= y && y <= room.Y + room.H - 1 - r;
                    bool aboveBelow = room.X <= x && x < room.X + room.W;
                    bool leftRight = room.Y <= y && y < room.Y + room.H;
                    if (!((aboveBelow && insideX) || (leftRight && insideY))) return null;
                    cost += 6.0;
                }
            return cost;
        }

        public bool NearEndpoint(Cell c, Room a, Room b, int ext) =>
            nearRoom.TryGetValue(c, out var near) && near.Any(t => (t.Room == a.Id || t.Room == b.Id) && t.Dist <= ext + 2);

        public bool InRoom(Cell c, Room room) => RoomAt.TryGetValue(c, out var l) && l.Contains(room.Id);

        public List<Cell> Route(Room a, Room b, int r = 1, bool diagonal = false, int k = 1, (int Lo, int Hi)? envIn = null,
                                HashSet<Cell> aPort = null, HashSet<Cell> bPort = null, int? crossFloor = null)
        {
            var env = envIn ?? (0, 128);
            int ext = diagonal ? Math.Max(r, k) : r;
            var (blocked, soft) = CorridorBlock(ext, env, crossFloor);
            // perimeter cells of a, away from its corners so the opening fits
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
            // with diagonal_pull a 45-degree step costs less than its length (1.414 -> 1.0 at 1): rooms sit
            // side by side on the grid, so a true-length diagonal is almost never the shortest way
            double dstep = 1.414 - 0.414 * Cfg.DiagonalPull;
            double Heur(Cell c)
            {
                int dx = Math.Max(Math.Max(bx0 - c.X, 0), c.X - bx1), dy = Math.Max(Math.Max(by0 - c.Y, 0), c.Y - by1);
                return diagonal ? Math.Max(dx, dy) + (dstep - 1.0) * Math.Min(dx, dy) : dx + dy;
            }
            var cache = new Dictionary<Cell, double?>();
            double? Ok(Cell c)
            {
                if (!cache.TryGetValue(c, out var v)) cache[c] = v = Allowed(c, a, b, r, ext, blocked, env, bPort, soft);
                return v;
            }
            var open = new MinHeap<(double G, Cell C, int D)>();
            var best = new Dictionary<(Cell, int), double>();
            var prev = new Dictionary<(Cell, int), (Cell, int)>();
            foreach (var c in starts) { best[(c, -1)] = 0; open.Push(Heur(c), (0, c, -1)); }
            (Cell, int)? goal = null;
            long limit = (long)W * H * (diagonal ? 8 : 4), pops = 0;
            while (open.Count > 0)
            {
                var (_, item) = open.Pop();
                var (gcost, c, d) = item;
                if (++pops > limit) break;
                if (best.TryGetValue((c, d), out var bc) && bc < gcost) continue;
                if (b.Contains(c) && !b.IsDiag(c.X, c.Y) && (bPort == null || bPort.Contains(c))) { goal = (c, d); break; }
                for (int nd = 0; nd < 8; nd += diagonal ? 1 : 2)
                {
                    var (dx, dy) = Layout.Dirs8[nd];
                    var n = c.Offset(dx, dy);
                    if (a.Contains(n)) continue;
                    double turnCost = 0;
                    if (d >= 0)
                    {
                        int turn = Math.Min(Math.Abs(nd - d), 8 - Math.Abs(nd - d));
                        if (turn > 3) continue;
                        turnCost = Layout.TurnCost[turn];
                    }
                    var extra = Ok(n);
                    if (extra == null) continue;
                    double step = 1.0;
                    if (dx != 0 && dy != 0)
                    {
                        // diagonal: no corner cutting, and never next to a doorway
                        if (Ok(c.Offset(dx, 0)) == null || Ok(c.Offset(0, dy)) == null) continue;
                        if (InRoom(c, a) || InRoom(c, b) || InRoom(n, a) || InRoom(n, b)) continue;
                        if (NearEndpoint(c, a, b, ext) || NearEndpoint(n, a, b, ext)) continue;
                        step = dstep;
                    }
                    double ng = gcost + step + extra.Value + turnCost;
                    if (!best.TryGetValue((n, nd), out var old) || ng < old)
                    {
                        best[(n, nd)] = ng;
                        prev[(n, nd)] = (c, d);
                        open.Push(ng + Heur(n), (ng, n, nd));
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
}
