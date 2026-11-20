using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Packed layouts: a floor plan divided into rooms (Doom-era maps, dense dungeons) (packed.py).
    ///
    /// The normal layout grows a level room by room, each placed beside its parent with a corridor between, so
    /// levels come out as islands on strings: 20-38% of the bounding box is floor and a third or more of that
    /// is corridor. Doom-era maps are the other way round: a block of space divided into rooms that share their
    /// walls, doors straight through, most of the block floor.
    ///
    /// Here (<c>cfg.Packing</c> >= 0.5):
    /// <list type="number">
    /// <item><b>A site</b>: a rectangle sized to hold every room of the mission (plus spare plots).</item>
    /// <item><b>Plots</b>: the site split again and again (the largest plot first, along its long side, at
    /// 35-65%) until there are enough; neighbouring plots share an edge.</item>
    /// <item><b>The mission onto the plots</b>: the spanning tree of the mission graph is embedded neighbour to
    /// neighbour, depth first: the root on a plot that suits its size, each child on a free plot touching its
    /// parent's (the size that fits best, and with enough free neighbours of its own for its children). If it
    /// doesn't fit, a new floor plan.</item>
    /// <item><b>Rooms in their plots</b>: each room fills its plot less a wall (3 cells between neighbours): the
    /// corridor between two neighbours is a short doorway passage through that wall. Neighbours differ by at
    /// most one step (the passage holds it); larger height changes are the rooms' own. Unused plots stay solid,
    /// which gives the plan its irregular edge and inner holes. Room shapes then cut the corners inside the plots.</item>
    /// </list></summary>
    public static class Packed
    {
        public static readonly int WallLo = 1, WallHi = 2;   // cells of wall at a plot's low and high sides: 3 between neighbouring rooms
        public static readonly int MinPlot = 10;             // a plot holds a room of at least 6 cells across

        public sealed class Plot
        {
            public int X, Y, W, H, Id = -1;
            public Plot(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
            public int Area => W * H;
        }

        /// <summary>How many required (w, h) get a plot of their own at least that big (greedy, biggest first).</summary>
        public static int Homed(List<Plot> plots, List<(int W, int H)> need)
        {
            var free = plots.OrderBy(p => p.Area).ToList();       // (stable, as Python's sorted)
            int n = 0;
            foreach (var (w, h) in need.OrderBy(wh => -wh.W * wh.H))
            {
                for (int i = 0; i < free.Count; i++)
                {
                    var p = free[i];
                    if (p.W >= w && p.H >= h)
                    {
                        free.RemoveAt(i);
                        n++;
                        break;
                    }
                }
            }
            return n;
        }

        public static bool Holds(List<Plot> plots, List<(int W, int H)> need) => Homed(plots, need) == need.Count;

        /// <summary>Split the site into <paramref name="n"/> plots (the largest first), never so far that the
        /// fixed-size rooms (stair cores, lift shafts, mega-halls: <paramref name="need"/>) lose the plots they fit in.</summary>
        public static List<Plot> SplitSite(int W, int H, int n, Rng rng, List<(int W, int H)> need = null)
        {
            need ??= new List<(int W, int H)>();
            var plots = new List<Plot> { new Plot(0, 0, W, H) };
            while (plots.Count < n)
            {
                plots = plots.OrderBy(p => -p.Area).ToList();
                bool split = false;
                for (int i = 0; i < plots.Count; i++)
                {
                    var p = plots[i];
                    bool horiz = Math.Abs(p.W - p.H) > 2 ? p.W >= p.H : rng.Random() < 0.5;
                    int length = horiz ? p.W : p.H;
                    if (length < 2 * MinPlot)
                    {
                        horiz = !horiz;
                        length = horiz ? p.W : p.H;
                        if (length < 2 * MinPlot) continue;
                    }
                    int cut = (int)Math.Round(length * rng.Uniform(0.35, 0.65), MidpointRounding.ToEven);
                    cut = Math.Max(MinPlot, Math.Min(length - MinPlot, cut));
                    Plot a, b;
                    if (horiz) { a = new Plot(p.X, p.Y, cut, p.H); b = new Plot(p.X + cut, p.Y, p.W - cut, p.H); }
                    else { a = new Plot(p.X, p.Y, p.W, cut); b = new Plot(p.X, p.Y + cut, p.W, p.H - cut); }
                    var trial = plots.Take(i).Concat(new[] { a, b }).Concat(plots.Skip(i + 1)).ToList();
                    if (need.Count > 0 && Homed(trial, need) < Homed(plots, need))
                        continue;                            // keep this plot whole for a fixed-size room
                    plots = trial;
                    split = true;
                    break;
                }
                if (!split) break;                           // nothing left to split
            }
            for (int k = 0; k < plots.Count; k++) plots[k].Id = k;
            return plots;
        }

        /// <summary>Plots sharing an edge at least <paramref name="minShared"/> long (room enough for a doorway).</summary>
        public static Dictionary<int, List<int>> Adjacency(List<Plot> plots, int minShared)
        {
            var adj = plots.ToDictionary(p => p.Id, p => new List<int>());
            foreach (var a in plots)
                foreach (var b in plots)
                {
                    if (a.Id >= b.Id) continue;
                    int shared = 0;
                    if (a.X + a.W == b.X || b.X + b.W == a.X)
                        shared = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
                    else if (a.Y + a.H == b.Y || b.Y + b.H == a.Y)
                        shared = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                    if (shared >= minShared)
                    {
                        adj[a.Id].Add(b.Id);
                        adj[b.Id].Add(a.Id);
                    }
                }
            return adj;
        }

        /// <summary>Node -> plot for the mission's spanning tree, neighbours on neighbouring plots (depth first,
        /// with backtracking; <paramref name="deadline"/> bounds the search). A plot holds several rooms as long
        /// as their vertical slots don't overlap (storeys stacked, a stair core through them all).</summary>
        public static Dictionary<int, int> Embed(MissionGraph g, int root, List<Plot> plots, Dictionary<int, List<int>> adj,
                                                 Dictionary<int, double> target, Dictionary<int, (int Lo, int Hi)> slots,
                                                 Func<int, Plot, bool> fits, Rng rng, int[] deadline)
        {
            var children = g.Nodes.ToDictionary(n => n.Id, n => new List<int>());
            var tree = g.Edges.Where(e => e.Tree).ToList();
            var seen = new HashSet<int> { root };
            var order = new List<int> { root };
            while (order.Count > 0)
            {
                int a = order[0];
                order.RemoveAt(0);
                foreach (var e in tree)
                {
                    if (a != e.A && a != e.B) continue;
                    int b = e.A == a ? e.B : e.A;
                    if (seen.Add(b))
                    {
                        children[a].Add(b);
                        order.Add(b);
                    }
                }
            }
            var byId = plots.ToDictionary(p => p.Id);

            double Fit(int node, Plot p) => Math.Abs(Math.Log(Math.Max(1.0, p.Area) / Math.Max(1.0, target[node])));

            var assign = new Dictionary<int, int>();
            var occ = plots.ToDictionary(p => p.Id, p => new List<(int Lo, int Hi)>());

            bool FreeFor(int node, int pid) => fits(node, byId[pid]) && !occ[pid].Any(s => Layout.VerticalConflict(slots[node], s));

            int FreeNeighbours(int pid) => adj[pid].Count(q => occ[q].Count == 0);

            bool PlaceChildren(int node) => PlaceKid(node, children[node], 0);

            bool PlaceKid(int node, List<int> kids, int i)
            {
                if (i == kids.Count)
                {
                    var snapA = new Dictionary<int, int>(assign);
                    var snapO = occ.ToDictionary(kv => kv.Key, kv => new List<(int Lo, int Hi)>(kv.Value));
                    foreach (var k in kids)                  // then each child's own subtree
                    {
                        if (!PlaceChildren(k))
                        {
                            assign.Clear();
                            foreach (var kv in snapA) assign[kv.Key] = kv.Value;
                            occ.Clear();
                            foreach (var kv in snapO) occ[kv.Key] = kv.Value;
                            return false;
                        }
                    }
                    return true;
                }
                deadline[0]--;
                if (deadline[0] < 0) return false;
                int kid = kids[i];
                var opts = adj[assign[node]].Where(q => FreeFor(kid, q)).ToList();
                int need = children[kid].Count;
                opts = opts.Select(q => (q, k1: FreeNeighbours(q) < need, k2: Fit(kid, byId[q]) + rng.Random() * 0.3))
                           .OrderBy(t => t.k1).ThenBy(t => t.k2).Select(t => t.q).ToList();
                foreach (var q in opts.Take(4))
                {
                    assign[kid] = q;
                    occ[q].Add(slots[kid]);
                    if (PlaceKid(node, kids, i + 1)) return true;
                    occ[q].Remove(slots[kid]);
                    assign.Remove(kid);
                }
                return false;
            }

            var starts = plots.Where(p => fits(root, p)).Select(p => (p, k: Fit(root, p) + rng.Random() * 0.5))
                              .OrderBy(t => t.k).Select(t => t.p).Take(6).ToList();
            foreach (var p in starts)
            {
                assign.Clear();
                foreach (var v in occ.Values) v.Clear();
                assign[root] = p.Id;
                occ[p.Id].Add(slots[root]);
                if (PlaceChildren(root)) return new Dictionary<int, int>(assign);
            }
            return null;
        }

        public static readonly string[] Fixed = { "megaroom" };   // (and the connectors): their size is their geometry, centred in a plot

        /// <summary>Rooms for every node on a divided floor plan (see the class summary). Several storeys share
        /// one plan: a plot holds a room per storey (or a tall room, or a stair core through all).
        /// <paramref name="make"/>(node, x, y, floor, parentFloor) proposes a room. Throws LayoutException if
        /// the mission does not fit.</summary>
        public static Dictionary<int, Room> PlacePacked(MissionGraph g, GenConfig cfg, Rng rng, Func<Node, int, int, int, int, Room> make)
        {
            int root = (cfg.Composition == "hub" || cfg.Composition == "central") && g.Center.HasValue ? g.Center.Value : 0;
            int step = cfg.StepHeight;
            var tree = g.Edges.Where(e => e.Tree).ToList();
            var nbrs = g.Nodes.ToDictionary(n => n.Id, n => new List<(int Other, Edge E)>());
            foreach (var e in tree)
            {
                nbrs[e.A].Add((e.B, e));
                nbrs[e.B].Add((e.A, e));
            }
            bool multi = cfg.Storeys > 1;
            var floorOf = new Dictionary<int, int>();
            if (multi)
            {
                // a storey's rooms share its floor (level doorways); connectors climb between storeys
                int H_ = cfg.StoreyHeight;
                foreach (var n in g.Nodes)
                    if (!MissionGraph.ConnectorKinds.Contains(n.Kind))
                        floorOf[n.Id] = n.Storey * H_;
                foreach (var n in g.Nodes)
                    if (MissionGraph.ConnectorKinds.Contains(n.Kind))
                    {
                        var fl = nbrs[n.Id].Where(m => floorOf.ContainsKey(m.Other)).Select(m => floorOf[m.Other]).ToList();
                        floorOf[n.Id] = fl.Min();
                        n.Top = fl.Max();
                    }
            }
            else
            {
                // neighbours differ by at most one step (the doorway passage holds it)
                floorOf[root] = 0;
                var order = new List<int> { root };
                while (order.Count > 0)
                {
                    int a = order[0];
                    order.RemoveAt(0);
                    foreach (var (b, e) in nbrs[a])
                    {
                        if (floorOf.ContainsKey(b)) continue;
                        int f = floorOf[a] + rng.Choice(new[] { -1, 0, 0, 1 }) * step;
                        if (e.Kind != "open")
                            f = floorOf[a];                      // a locked or secret door needs a level doorway
                        floorOf[b] = Math.Max(0, Math.Min(f, cfg.MaxLevel * cfg.LevelStep));
                        order.Add(b);
                    }
                }
            }
            var protos = new Dictionary<int, Room>();
            foreach (var n in g.Nodes)
            {
                var r = make(n, 0, 0, floorOf[n.Id], 0);
                if (r == null)
                    throw new LayoutException($"packed: connector {n.Kind} can't climb {(n.Top ?? 0) - floorOf[n.Id]}");
                protos[n.Id] = r;
            }
            var fixedIds = new HashSet<int>(protos.Where(kv => kv.Value.IsConnector || Fixed.Contains(kv.Value.Kind)).Select(kv => kv.Key));
            var slots = protos.ToDictionary(kv => kv.Key, kv => kv.Value.Slot);
            int walls = WallLo + WallHi;
            var target = protos.ToDictionary(kv => kv.Key, kv => (double)((kv.Value.W + walls) * (kv.Value.H + walls)));

            bool Fits(int node, Plot p)
            {
                var r = protos[node];
                int innerW = p.W - walls, innerH = p.H - walls;
                if (fixedIds.Contains(node))
                    return r.W <= innerW && r.H <= innerH;
                return innerW >= 6 && innerH >= 6;
            }

            // the site holds the busiest storey band (connectors count on every storey they cross)
            var bands = new Dictionary<int, List<int>>();
            foreach (var kv in protos)
            {
                var r = kv.Value;
                int sh = Math.Max(1, cfg.StoreyHeight);
                int lo = multi ? FloorDiv(r.Floor, sh) : 0;
                int hi = multi && (r.IsConnector || Fixed.Contains(r.Kind)) ? FloorDiv(r.Ceil - 1, sh) : lo;
                for (int s = lo; s <= hi; s++)
                {
                    if (!bands.TryGetValue(s, out var l)) bands[s] = l = new List<int>();
                    l.Add(kv.Key);
                }
            }
            double total = bands.Values.Max(ids => ids.Sum(i => target[i]));
            int count = bands.Values.Max(ids => ids.Count);
            for (int attempt = 0; attempt < 40; attempt++)
            {
                double spare = 1.3 + 0.05 * attempt;
                double aspect = rng.Uniform(1.0, 1.6);
                int W = (int)Math.Sqrt(total * spare * aspect) + 1;
                int H = (int)(total * spare / W) + 1;
                var need = fixedIds.Select(i => (protos[i].W + walls, protos[i].H + walls)).ToList();
                var plots = SplitSite(W, H, (int)(count * spare) + 2, rng, need);
                if (!Holds(plots, need)) continue;
                var adj = Adjacency(plots, walls + 6);
                var assign = Embed(g, root, plots, adj, target, slots, Fits, rng, new[] { 4000 });
                if (assign == null) continue;
                var byId = plots.ToDictionary(p => p.Id);
                var rooms = new Dictionary<int, Room>();
                foreach (var node in g.Nodes)
                {
                    var p = byId[assign[node.Id]];
                    var room = protos[node.Id];
                    if (fixedIds.Contains(node.Id))              // its own size, centred in the plot
                    {
                        room.X = p.X + WallLo + FloorDiv(p.W - walls - room.W, 2);
                        room.Y = p.Y + WallLo + FloorDiv(p.H - walls - room.H, 2);
                    }
                    else
                    {
                        room.X = p.X + WallLo;
                        room.Y = p.Y + WallLo;
                        room.W = p.W - walls;
                        room.H = p.H - walls;
                    }
                    rooms[node.Id] = room;
                }
                return rooms;
            }
            throw new LayoutException("packed: the mission did not fit a floor plan");
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
    }
}
