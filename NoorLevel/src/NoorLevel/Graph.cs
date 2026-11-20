using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    public sealed class Node
    {
        public int Id;
        public string Kind;    // start | combat | arena | hub | key | supply | secret | exit | courtyard | megaroom | stairwell | lift | ladder | spiral | tower
        public string Size;    // small | medium | large | yard | mega | connector
        public int Zone;       // number of locks passed to get here
        public int Depth;      // distance along the critical path (difficulty ramp)
        public string Key;
        public int Storey;     // for connectors: the lower storey they start from
        public int? Top;       // connectors: the upper landing's floor (set in the layout)
        public bool Main;      // on the critical path
        public string Style;   // mega-rooms: hangar | works | terraces
    }

    public sealed class Edge
    {
        public int Id, A, B;
        public string Kind = "open";   // open | locked | secret
        public string Key;
        public bool Tree = true;       // false for loop edges
        public string Connector;       // stairs / drop for edges that change storey
        public bool Merged;            // rooms placed touching: one hall, no corridor
    }

    /// <summary>Mission graph (graph.py): the abstract structure of a level, built so that pacing and
    /// gating make sense before any geometry exists: a critical path start -> ... -> exit; key branches
    /// (the key for a locked door sits on a side branch reachable before that door); side rooms; loops
    /// (chosen in the layout, only inside one lock zone); an optional secret room behind a secret door.</summary>
    public sealed class MissionGraph
    {
        public List<Node> Nodes = new List<Node>();
        public List<Edge> Edges = new List<Edge>();
        public int? Center;            // hub / central compositions: the node everything gathers around

        public static readonly string[] KeyColors = { "red", "blue" };
        public static readonly string[] ConnectorKinds = { "stairwell", "lift", "ladder", "spiral", "tower" };

        public Node AddNode(string kind, string size, int zone = 0, int depth = 0, string key = null, int storey = 0)
        {
            var n = new Node { Id = Nodes.Count, Kind = kind, Size = size, Zone = zone, Depth = depth, Key = key, Storey = storey };
            Nodes.Add(n);
            return n;
        }

        public Edge AddEdge(int a, int b, string kind = "open", string key = null, bool tree = true, string connector = null, bool merged = false)
        {
            var e = new Edge { Id = Edges.Count, A = a, B = b, Kind = kind, Key = key, Tree = tree, Connector = connector, Merged = merged };
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
            return seen.TryGetValue(b, out var d) ? d : 1000000000;
        }

        /// <summary>build_graph.</summary>
        public static MissionGraph Build(GenConfig cfg, Rng rng)
        {
            if (cfg.Tower)
            {
                var tg = Tower.BuildTowerGraph(cfg, rng);       // the wizard's tower: a helix as the level's spine
                if (tg != null) return tg;                      // (null while tower.py is a stub: a normal graph)
            }
            var g = new MissionGraph();
            int nMain = Math.Max(3, cfg.MainRooms);
            int nKeys = Math.Max(0, Math.Min(Math.Min(cfg.Keys, KeyColors.Length), nMain - 2));

            // --- critical path ---------------------------------------------------------------
            var main = new List<Node> { g.AddNode("start", "medium", depth: 0) };
            int arenaAt = Math.Max(1, (int)Math.Round((nMain - 1) * 0.66));
            for (int i = 1; i < nMain - 1; i++)
            {
                if (i == arenaAt) main.Add(g.AddNode("arena", "large", depth: i));
                else
                {
                    string kind = rng.Choice(new[] { "combat", "combat", "hub" });
                    string size = rng.WeightedBias(kind == "hub" ? new[] { "medium", "medium", "large" } : new[] { "small", "medium", "medium" },
                                                   cfg.SizeBias);
                    main.Add(g.AddNode(kind, size, depth: i));
                }
            }
            main.Add(g.AddNode("exit", "medium", depth: nMain - 1));
            for (int i = 0; i + 1 < main.Count; i++) g.AddEdge(main[i].Id, main[i + 1].Id);
            foreach (var n in main) n.Main = true;
            bool radial = cfg.Composition == "hub" || cfg.Composition == "central";
            if (radial) g.Center = main[arenaAt].Id;     // the arena: mega-rooms and yards pick it first

            // --- locks and key branches --------------------------------------------------------
            // Lock edges are spread along the path; edge index i joins main[i] -> main[i+1].
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
                var e = g.Edges[lockEdges[k]];      // main path edges were created first, in order
                e.Kind = "locked"; e.Key = KeyColors[k];
            }
            // zones along the main path
            int zone = 0;
            for (int i = 0; i < main.Count; i++)
            {
                main[i].Zone = zone;
                if (zone < lockEdges.Count && i == lockEdges[zone]) zone++;
            }
            for (int k = 0; k < lockEdges.Count; k++)
            {
                // branch from a main room in the same zone, before the lock
                int first = k > 0 ? lockEdges[k - 1] + 1 : 0;
                var cands = main.GetRange(first, lockEdges[k] + 1 - first);
                var parent = rng.Choice(cands.Count > 1 ? cands.Skip(1).ToList() : cands);
                if (radial && cands.Contains(g.Nodes[g.Center.Value]) && rng.Random() < 0.8)
                    parent = g.Nodes[g.Center.Value];       // key branches hang off the centre
                var prev = parent;
                if (rng.Random() < 0.6)
                {
                    var mid = g.AddNode(rng.Choice(new[] { "combat", "supply" }), rng.WeightedBias(new[] { "small", "medium" }, cfg.SizeBias),
                                        parent.Zone, parent.Depth);
                    g.AddEdge(prev.Id, mid.Id);
                    prev = mid;
                }
                var keyNode = g.AddNode("key", "medium", parent.Zone, parent.Depth, KeyColors[k]);
                g.AddEdge(prev.Id, keyNode.Id);
            }

            // --- side rooms ----------------------------------------------------------------------
            for (int s = 0; s < cfg.SideRooms; s++)
            {
                var parents = g.Nodes.Where(n => n.Kind != "exit" && n.Kind != "key" && n.Kind != "secret").ToList();
                var parent = rng.Choice(parents);
                if (radial && rng.Random() < 0.75) parent = g.Nodes[g.Center.Value];                  // spokes
                else if (cfg.Composition == "linear") parent = rng.Choice(parents.Where(n => n.Main).ToList());   // short ribs off the spine
                var n2 = g.AddNode(rng.Choice(new[] { "supply", "combat" }), rng.WeightedBias(new[] { "small", "medium" }, cfg.SizeBias),
                                   parent.Zone, parent.Depth);
                g.AddEdge(parent.Id, n2.Id);
            }

            // --- loops: chosen in the layout, once we know which rooms are close (Layout.ChooseLoops)

            // --- secret --------------------------------------------------------------------------
            if (cfg.Secret)
            {
                var parents = g.Nodes.Where(n => n.Kind != "start" && n.Kind != "exit" && n.Kind != "secret").ToList();
                var parent = rng.Choice(parents);
                var s = g.AddNode("secret", "small", parent.Zone, parent.Depth);
                g.AddEdge(parent.Id, s.Id, "secret");
            }

            if (cfg.Mode == "arena") MakeArena(g);
            if (cfg.MegaRooms > 0) MakeMegaRooms(g, cfg, rng);
            if (cfg.OpenAreas > 0) MakeOpenAreas(g, cfg, rng);
            if (cfg.Storeys > 1) AssignStoreys(g, cfg, rng, main.Select(n => n.Id).ToList());
            return g;
        }

        /// <summary>Deathmatch: no start, no exit, no locks. The path's ends become ordinary rooms (spawns
        /// go in later, arena.py); keys and secrets are off in arena recipes, and locks are cleared here
        /// in case a config asks for them anyway.</summary>
        public static void MakeArena(MissionGraph g)
        {
            foreach (var n in g.Nodes)
            {
                if (n.Kind == "start") n.Kind = "combat";
                else if (n.Kind == "exit") n.Kind = "hub";
                else if (n.Kind == "key") { n.Kind = "supply"; n.Key = null; }
            }
            foreach (var e in g.Edges)
                if (e.Kind == "locked") { e.Kind = "open"; e.Key = null; }
            foreach (var n in g.Nodes) n.Zone = 0;          // one zone: loops may join any two rooms
        }

        /// <summary>Turn the arena (then hubs) into mega-rooms, each with a character (hangar, works, terraces).</summary>
        public static void MakeMegaRooms(MissionGraph g, GenConfig cfg, Rng rng)
        {
            var cands = g.Nodes.Where(n => n.Kind == "arena" || n.Kind == "hub")
                .Select(n => (N: n, K1: n.Kind != "arena" ? 1 : 0, K2: -g.Neighbors(n.Id).Count(), K3: rng.Random()))
                .ToList();
            var sorted = cands.OrderBy(t => t.K1).ThenBy(t => t.K2).ThenBy(t => t.K3).Select(t => t.N).ToList();
            var styles = new[] { "hangar", "works", "terraces" };
            foreach (var n in sorted.Take(cfg.MegaRooms))
            {
                n.Kind = "megaroom"; n.Size = "mega";
                n.Style = rng.WeightedBias(styles, cfg.MegaStyles);
            }
        }

        /// <summary>Turn a few well-connected rooms into courtyards (open to the sky). Hubs and the arena
        /// first: places where routes meet make good yards; start, exit, keys and secrets stay rooms.</summary>
        public static void MakeOpenAreas(MissionGraph g, GenConfig cfg, Rng rng)
        {
            var rank = new Dictionary<string, int> { ["hub"] = 0, ["arena"] = 1, ["combat"] = 2 };
            var cands = g.Nodes.Where(n => rank.ContainsKey(n.Kind)).ToList();
            var degree = cands.ToDictionary(n => n.Id, n => g.Neighbors(n.Id).Count());
            var keyed = cands.Select(n => (N: n, K1: rank[n.Kind], K2: -degree[n.Id], K3: rng.Random())).ToList();
            foreach (var n in keyed.OrderBy(t => t.K1).ThenBy(t => t.K2).ThenBy(t => t.K3).Select(t => t.N).Take(cfg.OpenAreas).ToList())
            {
                n.Kind = "courtyard"; n.Size = "yard";
            }
        }

        /// <summary>Give every room a storey, then turn tree edges that change storey into vertical
        /// connections: a stair corridor, or a connector room (stairwell / lift / ladder / spiral).</summary>
        public static void AssignStoreys(MissionGraph g, GenConfig cfg, Rng rng, List<int> main)
        {
            int S = cfg.Storeys;
            Dictionary<int, int> best = null;
            int bestUsed = -1;
            bool deep = cfg.Underground;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                var st = new Dictionary<int, int> { [main[0]] = deep ? S - 1 : rng.Range(0, S) };
                if (deep)
                {
                    // underground: in from the surface (top storey) and down, one storey at a time, all the
                    // way to the bottom before the big hall (the deep chamber) when there's room for it
                    int arena = main.FindIndex(n => g.Nodes[n].Kind == "megaroom" || g.Nodes[n].Kind == "arena");
                    if (arena < 0) arena = main.Count - 1;
                    var span = arena >= S ? Enumerable.Range(1, arena).ToList() : Enumerable.Range(1, main.Count - 1).ToList();
                    var downs = new HashSet<int>(rng.Sample(span, Math.Min(S - 1, span.Count)));
                    for (int i = 1; i < main.Count; i++)
                        st[main[i]] = st[main[i - 1]] - (downs.Contains(i) ? 1 : 0);
                }
                else
                {
                    for (int i = 0; i + 1 < main.Count; i++)
                    {
                        int s = st[main[i]];
                        if (rng.Random() < cfg.StoreyChange)
                            s += (0 < s && s < S - 1) ? rng.Choice(new[] { -1, 1 }) : (s == 0 ? 1 : -1);
                        st[main[i + 1]] = s;
                    }
                }
                // branches: mostly the parent's storey, sometimes one up or down (keys in the basement...)
                var order = g.Edges.Where(e => e.Tree).ToList();
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var e in order)
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
                int used = st.Values.Distinct().Count();
                if (best == null || used > bestUsed) { bestUsed = used; best = st; }
                if (used == S) break;
            }
            foreach (var n in g.Nodes) n.Storey = best[n.Id];

            var weights = cfg.ConnectorWeights;
            var kinds = weights.Keys.ToList();
            bool packed = cfg.Packing >= 0.5;
            foreach (var e in g.Edges.ToList())
            {
                int sa = g.Nodes[e.A].Storey, sb = g.Nodes[e.B].Storey;
                if (sa == sb) continue;
                if (!e.Tree)
                {
                    e.Connector = "drop";          // decided for real in the layout (drop if it fits)
                    continue;
                }
                string kind = rng.Choices(kinds, kinds.Select(k => weights[k]).ToList());
                if (packed && kind == "stairs") kind = "stairwell";   // packed plans: doorways through walls can't climb a storey
                if ((kind == "stairs" || e.Kind == "secret") && !packed)
                {
                    e.Connector = "stairs";
                    continue;
                }
                var loNode = sa < sb ? g.Nodes[e.A] : g.Nodes[e.B];
                var c = g.AddNode(kind, "connector", loNode.Zone, Math.Min(g.Nodes[e.A].Depth, g.Nodes[e.B].Depth), storey: Math.Min(sa, sb));
                int oldB = e.B;
                e.B = c.Id;                        // a -> connector keeps the lock, if any
                g.AddEdge(c.Id, oldB);
            }
        }
    }
}
