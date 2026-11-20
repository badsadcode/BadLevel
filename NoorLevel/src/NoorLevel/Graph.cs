using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    public sealed class Node
    {
        public int Id;
        public string Kind;    // start | combat | arena | hub | key | supply | secret | exit | stairwell | lift | ladder | spiral
        public string Size;    // small | medium | large | connector
        public int Zone;       // locks passed to get here
        public int Depth;      // distance along the critical path
        public string Key;
        public int Storey;
        public int? Top;       // connectors: the upper landing's floor
        public bool Main;
    }

    public sealed class Edge
    {
        public int Id, A, B;
        public string Kind = "open";   // open | locked | secret
        public string Key;
        public bool Tree = true;       // false for loops
        public string Connector;       // "stairs" | "drop" for edges that change storey
    }

    /// <summary>The abstract structure of a level, decided before any geometry: a critical path,
    /// keys on side branches before their locks, side rooms, loops (chosen in the layout), a secret,
    /// storeys and the connectors between them.</summary>
    public sealed class MissionGraph
    {
        public List<Node> Nodes = new List<Node>();
        public List<Edge> Edges = new List<Edge>();
        public int? Center;

        public static readonly string[] KeyColors = { "red", "blue" };
        public static readonly string[] ConnectorKinds = Room.ConnectorKinds;

        public Node AddNode(string kind, string size, int zone = 0, int depth = 0, string key = null, int storey = 0)
        {
            var n = new Node { Id = Nodes.Count, Kind = kind, Size = size, Zone = zone, Depth = depth, Key = key, Storey = storey };
            Nodes.Add(n);
            return n;
        }

        public Edge AddEdge(int a, int b, string kind = "open", string key = null, bool tree = true)
        {
            var e = new Edge { Id = Edges.Count, A = a, B = b, Kind = kind, Key = key, Tree = tree };
            Edges.Add(e);
            return e;
        }

        public IEnumerable<int> Neighbors(int id)
        {
            foreach (var e in Edges)
            {
                if (e.A == id) yield return e.B;
                else if (e.B == id) yield return e.A;
            }
        }

        public bool Connected(int a, int b) => Edges.Any(e => (e.A == a && e.B == b) || (e.A == b && e.B == a));

        public int HopDistance(int a, int b)
        {
            var seen = new Dictionary<int, int> { [a] = 0 };
            var frontier = new List<int> { a };
            while (frontier.Count > 0)
            {
                var next = new List<int>();
                foreach (var n in frontier)
                    foreach (var m in Neighbors(n))
                        if (!seen.ContainsKey(m)) { seen[m] = seen[n] + 1; next.Add(m); }
                frontier = next;
            }
            return seen.TryGetValue(b, out var d) ? d : int.MaxValue / 2;
        }

        public static MissionGraph Build(GenConfig cfg, Rng rng)
        {
            var g = new MissionGraph();
            int nMain = Math.Max(3, cfg.MainRooms);
            int nKeys = Math.Max(0, Math.Min(Math.Min(cfg.Keys, KeyColors.Length), nMain - 2));

            // the critical path
            var main = new List<Node> { g.AddNode("start", "medium") };
            int arenaAt = Math.Max(1, (int)Math.Round((nMain - 1) * 0.66));
            for (int i = 1; i < nMain - 1; i++)
            {
                if (i == arenaAt) main.Add(g.AddNode("arena", "large", depth: i));
                else
                {
                    string kind = rng.Choice(new[] { "combat", "combat", "hub" });
                    string size = rng.Weighted(kind == "hub" ? new[] { "medium", "medium", "large" } : new[] { "small", "medium", "medium" }, cfg.SizeBias);
                    main.Add(g.AddNode(kind, size, depth: i));
                }
            }
            main.Add(g.AddNode("exit", "medium", depth: nMain - 1));
            for (int i = 0; i + 1 < main.Count; i++) g.AddEdge(main[i].Id, main[i + 1].Id);
            foreach (var n in main) n.Main = true;
            bool radial = cfg.Composition == "hub" || cfg.Composition == "central";
            if (radial) g.Center = main[arenaAt].Id;

            // locks along the path, keys on side branches before them
            var lockEdges = new List<int>();
            if (nKeys > 0)
            {
                int lo = 1, span = nMain - 1 - lo;
                for (int k = 0; k < nKeys; k++)
                {
                    int idx = lo + (k + 1) * span / (nKeys + 1);
                    idx = Math.Max(idx, lockEdges.Count > 0 ? lockEdges[lockEdges.Count - 1] + 1 : 1);
                    idx = Math.Min(idx, nMain - 2);
                    if (lockEdges.Count > 0 && idx <= lockEdges[lockEdges.Count - 1]) break;
                    lockEdges.Add(idx);
                }
            }
            for (int k = 0; k < lockEdges.Count; k++)
            {
                var e = g.Edges[lockEdges[k]];
                e.Kind = "locked"; e.Key = KeyColors[k];
            }
            int zone = 0;
            for (int i = 0; i < main.Count; i++)
            {
                main[i].Zone = zone;
                if (zone < lockEdges.Count && i == lockEdges[zone]) zone++;
            }
            for (int k = 0; k < lockEdges.Count; k++)
            {
                int first = k > 0 ? lockEdges[k - 1] + 1 : 0;
                var cands = main.GetRange(first, lockEdges[k] + 1 - first);
                var parent = rng.Choice(cands.Count > 1 ? cands.Skip(1).ToList() : cands);
                if (radial && g.Center.HasValue && cands.Any(n => n.Id == g.Center) && rng.Random() < 0.8)
                    parent = g.Nodes[g.Center.Value];
                var prev = parent;
                if (rng.Random() < 0.6)
                {
                    var mid = g.AddNode(rng.Choice(new[] { "combat", "supply" }), rng.Weighted(new[] { "small", "medium" }, cfg.SizeBias),
                                        parent.Zone, parent.Depth);
                    g.AddEdge(prev.Id, mid.Id);
                    prev = mid;
                }
                var keyNode = g.AddNode("key", "medium", parent.Zone, parent.Depth, KeyColors[k]);
                g.AddEdge(prev.Id, keyNode.Id);
            }

            // side rooms
            for (int s = 0; s < cfg.SideRooms; s++)
            {
                var parents = g.Nodes.Where(n => n.Kind != "exit" && n.Kind != "key" && n.Kind != "secret").ToList();
                var parent = rng.Choice(parents);
                if (radial && g.Center.HasValue && rng.Random() < 0.75) parent = g.Nodes[g.Center.Value];
                else if (cfg.Composition == "linear") parent = rng.Choice(parents.Where(n => n.Main).ToList());
                var n = g.AddNode(rng.Choice(new[] { "supply", "combat" }), rng.Weighted(new[] { "small", "medium" }, cfg.SizeBias),
                                  parent.Zone, parent.Depth);
                g.AddEdge(parent.Id, n.Id);
            }

            // the secret
            if (cfg.Secret)
            {
                var parents = g.Nodes.Where(n => n.Kind != "start" && n.Kind != "exit" && n.Kind != "secret").ToList();
                var parent = rng.Choice(parents);
                var s = g.AddNode("secret", "small", parent.Zone, parent.Depth);
                g.AddEdge(parent.Id, s.Id, "secret");
            }

            if (cfg.Storeys > 1) AssignStoreys(g, cfg, rng, main.Select(n => n.Id).ToList());
            return g;
        }

        /// <summary>Every room gets a storey; tree edges that change storey become a stair corridor or
        /// a connector room (stairwell, lift, ladder, spiral).</summary>
        static void AssignStoreys(MissionGraph g, GenConfig cfg, Rng rng, List<int> main)
        {
            int S = cfg.Storeys;
            Dictionary<int, int> best = null;
            int bestUsed = -1;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                var st = new Dictionary<int, int> { [main[0]] = rng.Int(0, S - 1) };
                for (int i = 0; i + 1 < main.Count; i++)
                {
                    int s = st[main[i]];
                    if (rng.Random() < cfg.StoreyChange)
                        s += (0 < s && s < S - 1) ? rng.Choice(new[] { -1, 1 }) : (s == 0 ? 1 : -1);
                    st[main[i + 1]] = s;
                }
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var e in g.Edges.Where(e => e.Tree))
                    {
                        foreach (var (p, c) in new[] { (e.A, e.B), (e.B, e.A) })
                        {
                            if (st.ContainsKey(p) && !st.ContainsKey(c))
                            {
                                int s = st[p];
                                if (g.Nodes[c].Kind != "secret" && rng.Random() < 0.35)
                                    s = Math.Max(0, Math.Min(S - 1, s + rng.Choice(new[] { -1, 1 })));
                                st[c] = s;
                                changed = true;
                            }
                        }
                    }
                }
                int used = st.Values.Distinct().Count();
                if (used > bestUsed) { bestUsed = used; best = st; }
                if (used == S) break;
            }
            foreach (var n in g.Nodes) n.Storey = best[n.Id];

            var kinds = cfg.ConnectorWeights.Keys.ToList();
            foreach (var e in g.Edges.ToList())
            {
                int sa = g.Nodes[e.A].Storey, sb = g.Nodes[e.B].Storey;
                if (sa == sb) continue;
                if (!e.Tree) { e.Connector = "drop"; continue; }
                string kind = rng.Weighted(kinds, cfg.ConnectorWeights);
                if (kind == "stairs" || e.Kind == "secret") { e.Connector = "stairs"; continue; }
                var lowNode = sa < sb ? g.Nodes[e.A] : g.Nodes[e.B];
                var c = g.AddNode(kind, "connector", lowNode.Zone, Math.Min(g.Nodes[e.A].Depth, g.Nodes[e.B].Depth), storey: Math.Min(sa, sb));
                int oldB = e.B;
                e.B = c.Id;                       // a -> connector keeps the lock, if any
                g.AddEdge(c.Id, oldB);
            }
        }
    }
}
