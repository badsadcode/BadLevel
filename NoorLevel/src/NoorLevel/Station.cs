using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>A deep-sea station (<c>layout_style = "station"</c>; NoorLevel's own, not in BadLevel): a built
    /// object under pressure, modules on a spine of tubes.
    /// <list type="bullet">
    /// <item><b>Pods</b>: every room is a round or octagonal module, 8-14 cells across (rooms keep their mission
    /// kinds: start is the docking airlock, exit the core, key rooms, side rooms, one secret).</item>
    /// <item><b>Tubes</b>: every corridor is a pressure tube, 3 cells wide, <c>tube_height</c> high (3 m), with no
    /// diagonals and a curved cross-section (<c>corridor_profile</c>, applied by <see cref="CorridorProfiles"/>).
    /// Each tube end is a doorway into a pod (a <c>bulkhead</c> marker).</item>
    /// <item><b>Spine</b> (<c>spine</c>): <c>line</c> (the critical path along one axis, side pods off it),
    /// <c>ring</c> (4-8 pods round a central hub, the ring closed, spokes to the hub), <c>tree</c> (the critical
    /// path branching and turning). <c>auto</c> picks one from the seed.</item>
    /// <item><b>Decks</b>: 2-4 storeys (fewer on small maps), <see cref="Deck"/> apart. The airlock is on the top
    /// deck, the core alone on the bottom one (the deepest point); decks are joined by stair tubes only: no
    /// lifts, ladders or spiral stairs.</item>
    /// <item><b>Character rooms</b> (<c>character_rooms</c>, weights by <c>station_style</c> or
    /// <c>character_weights</c>): a moon pool (a stepped bowl of water in the floor), a domed lab (a tall round
    /// pod under a dome), a flooded wing (a side pod three steps down, water to 1 m), a collapsed section (an
    /// extra tube blocked by a cave-in), a hydroponics bay (tagged for dressing).</item>
    /// </list>
    /// The pipeline is the normal one: <see cref="BuildGraph"/> replaces the mission graph (from
    /// <see cref="MissionGraph.Build"/>), <see cref="Place"/> the room placement (from <see cref="Layout.PlaceRooms"/>);
    /// routing, decoration and validation are shared. <see cref="Carve"/> (end of the layout) cuts the moon pool,
    /// <see cref="Finish"/> (after the floods) floods the wing, collapses the tube and marks the bulkheads.
    /// Randomness: the attempt's own stream (only station levels draw here), so a failed attempt retries differently.</summary>
    public static class Station
    {
        public static readonly string[] Spines = { "line", "ring", "tree" };
        public static readonly string[] Styles = { "research", "mining", "military", "derelict" };
        public static readonly string[] CharacterKinds = { "moon_pool", "dome_lab", "flooded_wing", "collapsed", "hydroponics" };
        public const int Deck = 96;             // floor to floor between decks (3 m): 6 steps of 16 in a stair tube
        public const int FloodedDrop = 48;      // a flooded wing sits three steps below its parent pod
        public const int Margin = 4;            // solid cells kept round a pod (other pods, other tubes)

        public static bool IsStation(GenConfig cfg) => cfg.LayoutStyle == "station";

        /// <summary>The architecture style each station style reads as (when arch_style is left empty).</summary>
        public static string ArchFor(string style) =>
            style == "mining" ? "industrial" : style == "military" ? "bunker" : style == "derelict" ? "industrial" : "scifi";

        /// <summary>The spine this level uses: <c>auto</c> (or anything unknown) picks one from the seed.</summary>
        public static string SpineOf(GenConfig cfg) =>
            Spines.Contains(cfg.Spine) ? cfg.Spine : Spines[Rng.FromLabel($"{cfg.Seed}/station/spine").Range(0, Spines.Length)];

        /// <summary>Settings a station implies, on a copy: the architecture style from the station style, the
        /// spine resolved. Called once by <see cref="Generator.Generate(GenConfig)"/>.</summary>
        public static GenConfig Prepare(GenConfig cfg)
        {
            var c = cfg.Clone();
            if (string.IsNullOrEmpty(c.ArchStyle)) c.ArchStyle = ArchFor(c.StationStyle);
            c.Spine = SpineOf(c);
            if (c.StationStyle == "derelict") c.LightVariety = Math.Max(c.LightVariety, 0.8);   // some rooms dark
            return c;
        }

        /// <summary>Decks: storeys clamped to 2-4, and fewer on small maps (a deck change costs a long stair tube).</summary>
        public static int DecksFor(GenConfig cfg, string spine = null)
        {
            int s = Math.Max(2, Math.Min(4, cfg.Storeys));
            if (cfg.MapSize > 0) s = Math.Min(s, cfg.MapSize < 56 ? 2 : cfg.MapSize < 72 ? 3 : 4);
            if (cfg.MapSize > 0 && spine == "line") s = Math.Min(s, cfg.MapSize < 64 ? 2 : 3);   // a line's decks add up along it
            return s;
        }

        // ------------------------------------------------------------------------------------------
        // the mission graph
        // ------------------------------------------------------------------------------------------
        /// <summary>The station's mission graph: the critical path from the airlock (top deck) down to the core
        /// (bottom deck, alone there), keys on branches before their locks, side pods, one secret. Ring: the
        /// first pods after the airlock form the ring (closed by a required edge, spokes to the hub as loops),
        /// and the locks sit on the way down from the ring, so no loop can bypass them.</summary>
        public static MissionGraph BuildGraph(GenConfig cfg, Rng rng)
        {
            var g = new MissionGraph();
            string spine = SpineOf(cfg);
            int S = DecksFor(cfg, spine);
            // the pods: main_rooms + side_rooms between them, split by the spine (a line on a set map size gets
            // as many spine pods as its length holds, the rest go off its sides)
            int total = Math.Max(6, Math.Min(28, cfg.MainRooms + cfg.SideRooms));
            int nMain;
            if (spine == "line") nMain = cfg.MapSize > 0 ? 2 + (cfg.MapSize - 22 - (S - 1) * 13) / 14 : cfg.MainRooms;
            else if (spine == "tree") nMain = (total + 1) / 2;
            else nMain = cfg.MainRooms;
            nMain = Math.Max(3, Math.Min(12, nMain));
            int ringS = S - 1;                               // ring: on the top deck, the airlock one of its pods
            int k = 0;                                       // ring pods
            if (spine == "ring")
            {
                k = cfg.MapSize > 0 && cfg.MapSize < 64 ? 4 : Math.Max(4, Math.Min(8, total / 2 - 1));
                nMain = k + (S - 2) + 1;                     // the ring, one pod per deck below it, the core
            }
            S = Math.Min(S, nMain);                          // (a three-pod line has room for three decks at most)
            int sides = Math.Max(0, Math.Min(12, total - nMain - (spine == "ring" ? 1 : 0)));

            // --- critical path and its decks ---------------------------------------------------------
            var main = new List<Node> { g.AddNode("start", "small", depth: 0, storey: S - 1) };
            for (int i = 1; i < nMain - 1; i++)
            {
                string kind = rng.Choice(new[] { "combat", "combat", "hub", "supply" });
                string size = rng.WeightedBias(kind == "hub" ? new[] { "medium", "large" } : new[] { "small", "medium", "medium" }, cfg.SizeBias);
                main.Add(g.AddNode(kind, size, depth: i));
            }
            main.Add(g.AddNode("exit", "large", depth: nMain - 1, storey: 0));
            foreach (var n in main) n.Main = true;
            for (int i = 0; i + 1 < main.Count; i++) g.AddEdge(main[i].Id, main[i + 1].Id).Role = "spine";

            if (spine == "ring")
            {
                for (int i = 0; i < k; i++) main[i].Storey = ringS;
                for (int i = k; i < nMain - 1; i++) main[i].Storey = ringS - (i - k + 1);   // one deck per pod, down to deck 1
            }
            else
            {
                // S - 2 deck changes among the edges before the last pod (that one is on deck 1, over the core)
                var edges = Enumerable.Range(0, nMain - 2).ToList();
                var downs = new HashSet<int>(rng.Sample(edges, Math.Min(S - 2, edges.Count)));
                int s = S - 1;
                for (int i = 1; i < nMain - 1; i++)
                {
                    if (downs.Contains(i - 1)) s--;
                    main[i].Storey = Math.Max(1, s);
                }
            }

            // --- locks: on the way down (ring: only after the ring) -------------------------------------
            int nKeys = Math.Max(0, Math.Min(Math.Min(cfg.Keys, MissionGraph.KeyColors.Length), nMain - 2));
            int firstLock = spine == "ring" ? k - 1 : 1;         // edge index i joins main[i] -> main[i + 1]
            int lastLock = nMain - 2;
            nKeys = Math.Min(nKeys, lastLock - firstLock + 1);
            var lockEdges = new List<int>();
            for (int j = 0; j < nKeys; j++)
                lockEdges.Add(lastLock - (nKeys - 1 - j) * Math.Max(1, (lastLock - firstLock + 1) / nKeys));
            lockEdges = lockEdges.Select(x => Math.Max(firstLock, Math.Min(lastLock, x))).Distinct().OrderBy(x => x).ToList();
            for (int j = 0; j < lockEdges.Count; j++)
            {
                var e = g.Edges[lockEdges[j]];
                e.Kind = "locked"; e.Key = MissionGraph.KeyColors[j];
            }
            int zone = 0;
            for (int i = 0; i < main.Count; i++)
            {
                main[i].Zone = zone;
                if (zone < lockEdges.Count && i == lockEdges[zone]) zone++;
            }

            // --- the ring's hub ---------------------------------------------------------------------------
            Node hub = null;
            if (spine == "ring")
            {
                var spoke = main[rng.Range(0, k)];
                bool keyHub = lockEdges.Count > 0;                 // the first key waits in the hub, in the middle of it all
                hub = g.AddNode(keyHub ? "key" : "hub", "large", zone: 0, depth: spoke.Depth, key: keyHub ? MissionGraph.KeyColors[0] : null, storey: ringS);
                g.AddEdge(spoke.Id, hub.Id).Role = "spoke";
                g.Center = hub.Id;
            }

            // --- key branches ---------------------------------------------------------------------------
            for (int j = 0; j < lockEdges.Count; j++)
            {
                if (j == 0 && hub != null) continue;               // (in the hub)
                int first = j > 0 ? lockEdges[j - 1] + 1 : 1;
                var cands = main.GetRange(first, lockEdges[j] + 1 - first).Where(n => n.Kind != "start").ToList();
                if (cands.Count == 0) cands = new List<Node> { main[lockEdges[j]] };
                var parent = rng.Choice(cands);
                var prev = parent;
                if (rng.Random() < 0.35)
                {
                    var mid = g.AddNode("supply", rng.WeightedBias(new[] { "small", "medium" }, cfg.SizeBias), parent.Zone, parent.Depth, storey: parent.Storey);
                    g.AddEdge(prev.Id, mid.Id).Role = "branch";
                    prev = mid;
                }
                var keyNode = g.AddNode("key", "medium", parent.Zone, parent.Depth, MissionGraph.KeyColors[j], storey: parent.Storey);
                g.AddEdge(prev.Id, keyNode.Id).Role = "branch";
            }

            // --- side pods (at least eight pods in all) ------------------------------------------------------
            sides = Math.Max(sides, 8 - g.Nodes.Count - (cfg.Secret ? 1 : 0));
            for (int s = 0; s < sides; s++)
            {
                var parents = g.Nodes.Where(n => n.Kind != "exit" && n.Kind != "key" && n.Kind != "secret" && n != hub).ToList();
                var parent = rng.Choice(parents);
                var n2 = g.AddNode(rng.Choice(new[] { "supply", "combat" }), rng.WeightedBias(new[] { "small", "medium" }, cfg.SizeBias),
                                   parent.Zone, parent.Depth, storey: parent.Storey);
                g.AddEdge(parent.Id, n2.Id).Role = "branch";
            }

            // --- secret ---------------------------------------------------------------------------------
            if (cfg.Secret)
            {
                var parents = g.Nodes.Where(n => n.Kind != "start" && n.Kind != "exit" && n.Kind != "secret" && n != hub).ToList();
                var parent = rng.Choice(parents);
                var sct = g.AddNode("secret", "small", parent.Zone, parent.Depth, storey: parent.Storey);
                g.AddEdge(parent.Id, sct.Id, "secret").Role = "branch";
            }

            // --- the ring itself: closed, and more spokes (loops) ------------------------------------------
            if (spine == "ring")
            {
                var close = g.AddEdge(main[k - 1].Id, main[0].Id, tree: false);
                close.Role = "ring"; close.Required = true;
            }
            return g;
        }

        // ------------------------------------------------------------------------------------------
        // placement
        // ------------------------------------------------------------------------------------------
        /// <summary>Diameter of a pod (cells, 8-14) for its size.</summary>
        public static int PodSize(GenConfig cfg, Node node, Rng rng)
        {
            int d = node.Size == "large" ? rng.Int(12, 14) : node.Size == "small" ? rng.Int(8, 9) : rng.Int(10, 11);
            if (node.Kind == "start") d = rng.Int(8, 10);
            return Math.Max(8, Math.Min(14, (int)Math.Round(d * Math.Max(0.8, Math.Min(1.2, cfg.RoomScale)))));
        }

        static readonly Dictionary<string, Dictionary<string, double>> StyleWeights = new Dictionary<string, Dictionary<string, double>>
        {
            ["research"] = new Dictionary<string, double> { ["dome_lab"] = 3, ["hydroponics"] = 3, ["moon_pool"] = 2, ["flooded_wing"] = 0.5, ["collapsed"] = 0.5 },
            ["mining"] = new Dictionary<string, double> { ["moon_pool"] = 2.5, ["collapsed"] = 2, ["flooded_wing"] = 1.5, ["dome_lab"] = 0.7, ["hydroponics"] = 0.5 },
            ["military"] = new Dictionary<string, double> { ["moon_pool"] = 2, ["collapsed"] = 1, ["flooded_wing"] = 1, ["dome_lab"] = 0.7, ["hydroponics"] = 0.5 },
            ["derelict"] = new Dictionary<string, double> { ["flooded_wing"] = 3, ["collapsed"] = 3, ["moon_pool"] = 1, ["hydroponics"] = 1, ["dome_lab"] = 1 },
        };

        static bool Ordinary(Node n) => n.Kind == "combat" || n.Kind == "hub" || n.Kind == "supply";

        /// <summary>Choose the character rooms: kind -> node (the collapsed tube is chosen after placement).</summary>
        static List<(string Kind, int Node)> PickCharacters(MissionGraph g, GenConfig cfg, Rng rng, out bool collapse)
        {
            collapse = false;
            var w = cfg.CharacterWeights != null && cfg.CharacterWeights.Count > 0 ? cfg.CharacterWeights
                : StyleWeights.TryGetValue(cfg.StationStyle, out var sw) ? sw : StyleWeights["research"];
            var kinds = CharacterKinds.Where(k => w.TryGetValue(k, out var x) && x > 0).ToList();
            int n = Math.Max(0, Math.Min(3, cfg.CharacterRooms));
            var outp = new List<(string, int)>();
            var used = new HashSet<int>();
            var tree = g.Edges.Where(e => e.Tree).ToList();
            int Degree(Node node) => tree.Count(e => e.A == node.Id || e.B == node.Id);
            Node ParentOf(Node node) => g.Nodes[tree.First(e => e.B == node.Id).A];
            while (outp.Count + (collapse ? 1 : 0) < n && kinds.Count > 0)
            {
                string kind = rng.Choices(kinds, kinds.Select(k => w[k]).ToList());
                kinds.Remove(kind);
                if (kind == "collapsed") { collapse = true; continue; }
                List<Node> cands;
                if (kind == "flooded_wing")         // a leaf off the spine, never the hub's
                    cands = g.Nodes.Where(x => !x.Main && Degree(x) == 1 && (Ordinary(x) || x.Kind == "key") && x.Id != g.Center
                                               && ParentOf(x).Id != g.Center && !used.Contains(x.Id)).ToList();
                else
                    cands = g.Nodes.Where(x => Ordinary(x) && !used.Contains(x.Id)).ToList();
                if (kind == "dome_lab" && cands.Any(x => x.Size == "large")) cands = cands.Where(x => x.Size == "large").ToList();
                if (cands.Count == 0) continue;
                var pick = rng.Choice(cands);
                used.Add(pick.Id);
                outp.Add((kind, pick.Id));
            }
            return outp;
        }

        sealed class Plan
        {
            public readonly Dictionary<int, Room> Rooms = new Dictionary<int, Room>();
            public readonly List<(int X0, int Y0, int X1, int Y1)> Tubes = new List<(int, int, int, int)>();
        }

        static bool Hit((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b, int m) =>
            a.X0 - m < b.X1 && b.X0 - m < a.X1 && a.Y0 - m < b.Y1 && b.Y0 - m < a.Y1;

        static (int, int, int, int) Box(Room r) => (r.X, r.Y, r.X + r.W, r.Y + r.H);

        /// <summary>A pod of diameter d beside the parent in direction dir, gap cells away, centred on it (so the
        /// tube runs straight), and the tube's strip; null if it would crowd another pod or tube.</summary>
        static (int X, int Y, (int, int, int, int) Strip)? Try(Plan plan, Room parent, int d, (int X, int Y) dir, int gap, int? self = null)
        {
            int x, y;
            (int, int, int, int) strip;
            if (dir.X != 0)
            {
                x = dir.X > 0 ? parent.X + parent.W + gap : parent.X - gap - d;
                y = parent.Y + (parent.H - d) / 2;
                int cy = parent.Y + parent.H / 2;
                strip = dir.X > 0 ? (parent.X + parent.W, cy - 1, x, cy + 2) : (x + d, cy - 1, parent.X, cy + 2);
            }
            else
            {
                y = dir.Y > 0 ? parent.Y + parent.H + gap : parent.Y - gap - d;
                x = parent.X + (parent.W - d) / 2;
                int cx = parent.X + parent.W / 2;
                strip = dir.Y > 0 ? (cx - 1, parent.Y + parent.H, cx + 2, y) : (cx - 1, y + d, cx + 2, parent.Y);
            }
            var box = (x, y, x + d, y + d);
            foreach (var kv in plan.Rooms)
            {
                if (kv.Value == parent || kv.Key == self) continue;
                if (Hit(box, Box(kv.Value), Margin + 1) || Hit(strip, Box(kv.Value), 3)) return null;
            }
            foreach (var t in plan.Tubes)
                if (Hit(strip, t, 3) || Hit(box, t, 3)) return null;
            return (x, y, strip);
        }

        /// <summary>The 3-wide strip a straight tube between two pods side by side would take.</summary>
        static (int, int, int, int) Between(Room a, Room b)
        {
            int ox0 = Math.Max(a.X, b.X), ox1 = Math.Min(a.X + a.W, b.X + b.W);
            int oy0 = Math.Max(a.Y, b.Y), oy1 = Math.Min(a.Y + a.H, b.Y + b.H);
            if (ox1 > ox0 && oy1 <= oy0)               // one above the other
            {
                int cx = (ox0 + ox1) / 2;
                return a.Y < b.Y ? (cx - 1, a.Y + a.H, cx + 2, b.Y) : (cx - 1, b.Y + b.H, cx + 2, a.Y);
            }
            int cy = oy1 > oy0 ? (oy0 + oy1) / 2 : (a.Y + a.H / 2 + b.Y + b.H / 2) / 2;
            return a.X < b.X ? (a.X + a.W, cy - 1, b.X, cy + 2) : (b.X + b.W, cy - 1, a.X, cy + 2);
        }

        /// <summary>One leg of an L-shaped tube: from the pod's centre line to the point p (the corner).</summary>
        static (int, int, int, int) Leg(Room a, (int X, int Y) p)
        {
            int cx = a.X + a.W / 2, cy = a.Y + a.H / 2;
            if (Math.Abs(p.X - cx) >= Math.Abs(p.Y - cy))    // a horizontal leg
                return (Math.Min(cx, p.X) - 1, cy - 1, Math.Max(cx, p.X) + 2, cy + 2);
            return (cx - 1, Math.Min(cy, p.Y) - 1, cx + 2, Math.Max(cy, p.Y) + 2);
        }

        static Room NewPod(Node node, int d, int floor, int height) =>
            new Room { Id = -1, Node = node.Id, Kind = node.Kind, W = d, H = d, Floor = floor, Ceil = floor + height, Storey = node.Storey };

        /// <summary>Tube length (cells between the pods) for a height difference: short when level, long enough
        /// for the stairs on a straight run otherwise (Layout.PlanFloors: one step per cell, three cells clear at
        /// each end).</summary>
        static int TubeGap(GenConfig cfg, int df, Rng rng)
        {
            int steps = Math.Abs(df) / cfg.StepHeight;
            return steps == 0 ? rng.Int(3, 5) : steps + 6 + rng.Int(0, 1);
        }

        /// <summary>Place every node as a pod (layout_style = station). Room ids == node ids, like
        /// Layout.PlaceRooms. Throws LayoutException when the spine can't be laid out (the attempt retries).</summary>
        public static List<Room> Place(MissionGraph g, GenConfig cfg, Rng rng)
        {
            string spine = SpineOf(cfg);
            var chars = PickCharacters(g, cfg, rng, out bool collapse);
            var charOf = chars.ToDictionary(c => c.Node, c => c.Kind);
            var tree = g.Edges.Where(e => e.Tree).ToList();

            // floors and sizes
            var floor = new Dictionary<int, int>();
            foreach (var n in g.Nodes) floor[n.Id] = n.Storey * Deck;
            foreach (var (kind, id) in chars)
                if (kind == "flooded_wing") floor[id] -= FloodedDrop;
            var diam = new Dictionary<int, int>();
            var height = new Dictionary<int, int>();
            foreach (var n in g.Nodes)
            {
                int d = PodSize(cfg, n, rng);
                if (spine == "ring" && n.Main && n.Storey == RingDeck(g) && n.Kind != "start") d = Math.Min(d, cfg.MapSize > 0 && cfg.MapSize < 64 ? 9 : 10);   // the ring's pods stay small
                if (spine == "ring" && n.Id == g.Center) d = Math.Min(d, 12);
                int h = n.Kind == "exit" || n.Size == "large" ? 128 : rng.Choice(new[] { 112, 128 });
                if (charOf.TryGetValue(n.Id, out var ck))
                {
                    if (ck == "dome_lab") { d = Math.Max(d, 13); h = 224; }
                    if (ck == "moon_pool") d = Math.Max(d, 11);
                }
                diam[n.Id] = d; height[n.Id] = h;
            }

            var plan = new Plan();
            var placed = new List<int>();
            (double X, double Y) hubC = (0, 0);
            var dirs = Layout.Dirs.Select(t => (X: t.Item1, Y: t.Item2)).ToArray();

            if (spine == "ring")
            {
                // the hub in the middle; the ring pods on the mid-points of a square round it (and on its corners
                // when there are more than four), in path order clockwise from a random side
                var hub = g.Nodes[g.Center.Value];
                var ring = RingNodes(g);                       // main[1..k], in path order
                int k = ring.Count;
                int dh = diam[hub.Id];
                int dmax = ring.Max(n => diam[n.Id]);
                int R = Math.Max(dh / 2 + rng.Int(3, 4) + dmax / 2, dmax + 3);
                // slots: 0 N, 1 NE, 2 E, ... clockwise; the four mid-points always, corners when k > 4
                var slotPos = new (int X, int Y)[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) };
                var slots = new List<int> { 0, 2, 4, 6 };
                var corners = new List<int> { 1, 3, 5, 7 };
                rng.Shuffle(corners);
                slots.AddRange(corners.Take(k - 4));
                slots.Sort();
                // the pod the hub's (tree) spoke comes from sits on a mid-point: spokes run straight
                int spokeFrom = g.Edges.First(e => e.Role == "spoke").A;
                int si = ring.FindIndex(n => n.Id == spokeFrom);
                var mids = Enumerable.Range(0, k).Where(j => slots[j] % 2 == 0).ToList();
                int shift = mids[rng.Range(0, mids.Count)] - si;
                int rot = rng.Range(0, 4) * 2;
                int SlotOf(int i) => (slots[((i + shift) % k + k) % k] + rot) % 8;
                var hubRoom = NewPod(hub, dh, floor[hub.Id], height[hub.Id]);
                hubRoom.X = -dh / 2; hubRoom.Y = -dh / 2;
                plan.Rooms[hub.Id] = hubRoom;
                placed.Add(hub.Id);
                hubC = (hubRoom.X + dh / 2.0, hubRoom.Y + dh / 2.0);
                var ringRooms = new List<Room>();
                for (int i = 0; i < k; i++)
                {
                    var n = ring[i];
                    var (sx, sy) = slotPos[SlotOf(i)];
                    int d = diam[n.Id];
                    var r = NewPod(n, d, floor[n.Id], height[n.Id]);
                    r.X = (int)Math.Round(hubC.X + sx * R - d / 2.0);
                    r.Y = (int)Math.Round(hubC.Y + sy * R - d / 2.0);
                    foreach (var o in plan.Rooms.Values)
                        if (Hit(Box(r), Box(o), 3)) throw new LayoutException("station: ring pods too close");
                    plan.Rooms[n.Id] = r;
                    ringRooms.Add(r);
                    placed.Add(n.Id);
                }
                // keep the ring's tubes and the spokes clear of what hangs off the ring
                for (int i = 0; i < k; i++)
                {
                    Room a = ringRooms[i], b = ringRooms[(i + 1) % k];
                    int sa = SlotOf(i), sb = SlotOf(i + 1);
                    if (sa % 2 == 0 && sb % 2 == 0)              // mid to mid round an empty corner: an L
                    {
                        int corner = (sb - sa + 8) % 8 == 2 ? (sa + 1) % 8 : (sb + 1) % 8;
                        var (cx, cy) = slotPos[corner];
                        var p = ((int)Math.Round(hubC.X + cx * R), (int)Math.Round(hubC.Y + cy * R));
                        plan.Tubes.Add(Leg(a, p));
                        plan.Tubes.Add(Leg(b, p));
                    }
                    else plan.Tubes.Add(Between(a, b));
                }
                foreach (var j in mids)
                {
                    int i = ((j - shift) % k + k) % k;
                    plan.Tubes.Add(Between(hubRoom, ringRooms[i]));
                    if (ring[i].Id != spokeFrom && rng.Random() < 0.6)            // more spokes: loops
                        g.AddEdge(ring[i].Id, hub.Id, tree: false).Role = "spoke";
                }
            }
            else
            {
                var start = g.Nodes.First(n => n.Kind == "start");
                var r = NewPod(start, diam[start.Id], floor[start.Id], height[start.Id]);
                plan.Rooms[start.Id] = r;
                placed.Add(start.Id);
            }

            // everything else hangs off what is placed, tube by tube (breadth first over the tree edges). Of the
            // placements the spine allows, the one that keeps the station most compact wins (mostly).
            var axis = dirs[rng.Range(0, 4)];                          // line: the spine's direction
            var cameFrom = new Dictionary<int, (int X, int Y)>();      // the direction a pod was placed in
            var queue = new List<int>(placed);
            List<(int X, int Y)> Prefs(int pid, Node node, out int outCount)
            {
                outCount = 0;
                var parent = plan.Rooms[pid];
                List<(int X, int Y)> pref;
                var back = cameFrom.TryGetValue(pid, out var cf) ? (X: -cf.X, Y: -cf.Y) : (X: 0, Y: 0);
                if (spine == "ring")
                {
                    // outward from the hub first, then along the ring (never back towards the hub)
                    double ox = parent.X + parent.W / 2.0 - hubC.X, oy = parent.Y + parent.H / 2.0 - hubC.Y;
                    var outs = new List<(int X, int Y)>();
                    if (Math.Abs(ox) > 1.5) outs.Add((Math.Sign(ox), 0));
                    if (Math.Abs(oy) > 1.5) outs.Add((0, Math.Sign(oy)));
                    var inward = dirs.Where(d => d.X * ox + d.Y * oy < -1.5).ToList();
                    pref = outs.Concat(dirs.Where(d => !outs.Contains(d) && !inward.Contains(d))).ToList();
                    outCount = outs.Count;
                }
                else if (spine == "line")
                {
                    var perp = new List<(int X, int Y)> { (axis.Y, axis.X), (-axis.Y, -axis.X) };
                    if (node.Main) pref = new List<(int X, int Y)> { axis };               // the spine runs straight
                    else pref = perp.Concat(new[] { axis }).ToList();
                }
                else pref = dirs.ToList();
                return pref.Where(d => d != back && d != (0, 0)).Distinct().ToList();
            }
            int Spread(Room extra)                                     // the plan's longer side with one more pod
            {
                int x0 = extra.X, y0 = extra.Y, x1 = extra.X + extra.W, y1 = extra.Y + extra.H;
                foreach (var r in plan.Rooms.Values) { x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y); x1 = Math.Max(x1, r.X + r.W); y1 = Math.Max(y1, r.Y + r.H); }
                return Math.Max(x1 - x0, y1 - y0) * 4 + Math.Min(x1 - x0, y1 - y0);
            }
            (double Score, Room Pod, (int, int, int, int) Strip, (int X, int Y) Dir)? PlaceOff(int pid, Node node, Edge e)
            {
                var parent = plan.Rooms[pid];
                int cid = node.Id, d0 = diam[cid];
                int minGap = e.Kind == "locked" || e.Kind == "secret" ? 5 : 3;   // a door needs a straight stretch
                var options = new List<(int Score, double Tie, Room Pod, (int, int, int, int) Strip, (int X, int Y) Dir)>();
                var pref = Prefs(pid, node, out int outCount);
                bool onRing = spine == "ring" && placed.Contains(pid);          // a ring pod (or the hub): outwards first
                for (int pi = 0; pi < pref.Count; pi++)
                {
                    var dir = pref[pi];
                    foreach (var extra in new[] { 0, 2, 5 })
                    {
                        int gap = Math.Max(minGap, TubeGap(cfg, floor[cid] - parent.Floor, rng)) + extra;
                        var got = Try(plan, parent, d0, dir, gap);
                        if (got == null) continue;
                        var pod = NewPod(node, d0, floor[cid], height[cid]);
                        pod.X = got.Value.X; pod.Y = got.Value.Y;
                        // ring and line: their preference order first; a tree: compact, with some wandering
                        int rank = spine == "tree" ? 0 : spine == "ring" ? (pi < outCount || !onRing ? 0 : 1) : 0;
                        int score = rank * 1000 + extra * 8 + Spread(pod);
                        options.Add((score, rng.Random() * (spine == "tree" ? 40 : 4), pod, got.Value.Strip, dir));
                        break;
                    }
                }
                if (options.Count == 0) return null;
                var best = options.OrderBy(o => o.Score + o.Tie).First();
                return (best.Score + best.Tie, best.Pod, best.Strip, best.Dir);
            }
            for (int qi = 0; qi < queue.Count; qi++)
            {
                int pid = queue[qi];
                foreach (var e in tree.ToList())
                {
                    if (e.A != pid && e.B != pid) continue;
                    int cid = e.A == pid ? e.B : e.A;
                    if (plan.Rooms.ContainsKey(cid)) continue;
                    var node = g.Nodes[cid];
                    // a branch may hang off another pod of the same zone on the same deck: off any ring pod (where
                    // the station stays most compact), or off any pod at all when its own parent has no room left
                    bool movable = !node.Main && e.Kind != "locked";
                    List<int> Hosts(bool ringOnly) => queue.Where(q => q != pid && (!ringOnly || placed.Contains(q))).Select(q => g.Nodes[q])
                        .Where(q => q.Zone == node.Zone && q.Storey == g.Nodes[pid].Storey && q.Kind != "exit" && q.Kind != "secret"
                                    && q.Kind != "key" && q.Id != g.Center).Select(q => q.Id).ToList();
                    int host = pid;
                    var got = PlaceOff(pid, node, e);
                    if (movable && spine == "ring" && placed.Contains(pid))
                        foreach (var h in Hosts(true))
                        {
                            var alt = PlaceOff(h, node, e);
                            if (alt != null && (got == null || alt.Value.Score < got.Value.Score)) { got = alt; host = h; }
                        }
                    if (got == null && movable)
                        foreach (var h in Hosts(false))
                        {
                            got = PlaceOff(h, node, e);
                            if (got != null) { host = h; break; }
                        }
                    if (got == null) throw new LayoutException($"station: no room for pod {cid} ({node.Kind}, {spine})");
                    if (host != pid) { if (e.A == pid) e.A = host; else e.B = host; }
                    plan.Tubes.Add(got.Value.Strip);
                    cameFrom[cid] = got.Value.Dir;
                    plan.Rooms[cid] = got.Value.Pod;
                    queue.Add(cid);
                }
            }
            if (plan.Rooms.Count != g.Nodes.Count) throw new LayoutException("station: the tree did not reach every pod");

            // the collapsed section: an extra tube between two close pods on one deck, blocked later (Finish)
            if (collapse)
            {
                var cands = new List<(int Gap, int A, int B)>();
                var nodes = g.Nodes.Where(n => n.Kind != "secret" && n.Kind != "exit" && n.Id != g.Center).ToList();
                foreach (var a in nodes)
                    foreach (var b in nodes)
                    {
                        if (a.Id >= b.Id || a.Zone != b.Zone || g.Connected(a.Id, b.Id) || floor[a.Id] != floor[b.Id]) continue;
                        int gp = Layout.Gap(plan.Rooms[a.Id], plan.Rooms[b.Id]);
                        if (gp <= 16 && g.HopDistance(a.Id, b.Id) >= 2) cands.Add((gp, a.Id, b.Id));
                    }
                if (cands.Count > 0)
                {
                    cands.Sort();
                    var pick = rng.Choice(cands.Take(3).ToList());
                    g.AddEdge(pick.A, pick.B, tree: false).Role = "collapse";
                }
            }

            // shapes: round or octagonal (the domed lab is round)
            double roundW = cfg.StationStyle == "military" ? 0.3 : cfg.StationStyle == "research" ? 0.65 : 0.5;
            foreach (var kv in plan.Rooms)
            {
                var r = kv.Value;
                charOf.TryGetValue(kv.Key, out var ck);
                string shape = ck == "dome_lab" || rng.Random() < roundW ? "round" : "octagon";
                var shp = Shape.Make(r.W, r.H, shape, rng);
                if (!shp.Ok()) shp = Shape.Make(r.W, r.H, shape == "round" ? "octagon" : "round", rng);
                r.Shape = shp.Name;
                r.Mask = shp.Cells;
                r.Diag = shp.Diag;
                if (ck != null)
                {
                    r.Meta["station_room"] = ck;
                    r.Features.Add(ck);
                    if (ck == "dome_lab") r.Meta["arch_profile"] = "dome";      // Architecture.Plan: this profile, not the palette's
                }
                r.Meta["pod"] = true;
            }

            // normalise: ids == node ids, the layout from (Pad, Pad)
            var result = g.Nodes.Select(n => plan.Rooms[n.Id]).ToList();
            int mx = result.Min(r => r.X), my = result.Min(r => r.Y);
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Id = i;
                result[i].X += Layout.Pad - mx;
                result[i].Y += Layout.Pad - my;
            }
            return result;
        }

        static int RingDeck(MissionGraph g) => g.Center.HasValue ? g.Nodes[g.Center.Value].Storey : -1;

        /// <summary>The ring pods in path order: the main nodes on the hub's deck, from the start's side.</summary>
        static List<Node> RingNodes(MissionGraph g)
        {
            var main = new List<Node>();
            var cur = g.Nodes.First(n => n.Kind == "start");
            var spineEdges = g.Edges.Where(e => e.Role == "spine").ToList();
            while (cur != null)
            {
                main.Add(cur);
                var e = spineEdges.FirstOrDefault(x => x.A == cur.Id);
                cur = e == null ? null : g.Nodes[e.B];
            }
            var close = g.Edges.First(e => e.Role == "ring");
            int i0 = main.FindIndex(n => n.Id == close.B), i1 = main.FindIndex(n => n.Id == close.A);
            return main.GetRange(i0, i1 - i0 + 1);
        }

        // ------------------------------------------------------------------------------------------
        // geometry
        // ------------------------------------------------------------------------------------------
        /// <summary>The moon pool's bowl: cells round the pod's centre with their depth (16 per ring, 64 at the
        /// middle), so you can always walk out of it.</summary>
        public static List<(Cell C, int Depth)> PoolCells(Room r)
        {
            double cx = r.X + r.W / 2.0, cy = r.Y + r.H / 2.0;
            double rad = r.W / 2.0 - 2.2;
            var outp = new List<(Cell, int)>();
            foreach (var c in r.Cells())
            {
                if (r.IsDiag(c.X, c.Y)) continue;
                double d = Math.Sqrt(Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2));
                if (d > rad) continue;
                double t = d / Math.Max(1.0, rad);           // 0 in the middle, 1 at the rim
                int depth = t < 0.3 ? 64 : t < 0.55 ? 48 : t < 0.8 ? 32 : 16;
                outp.Add((c, depth));
            }
            return outp;
        }

        /// <summary>End of the layout (rooms built, tubes routed): cut the moon pool's bowl and fill it.</summary>
        public static void Carve(Level level)
        {
            foreach (var r in level.Rooms)
            {
                if (r.Get<string>("station_room") != "moon_pool") continue;
                r.Meta["reserved"] = PoolCells(r).Select(p => p.C).ToList();     // kept clear of furniture (RoomCtx reads it)
                foreach (var (c, depth) in PoolCells(r))
                {
                    if (level.RoomSpans(c, r).Count != 1) continue;
                    level.ReplaceIn(c, r.Slot.Lo, r.Slot.Hi, new[] { new Span(r.Floor - depth, r.Ceil) });
                    level.Water[c] = r.Floor - 8;
                    level.PartCells[c] = $"room:{r.Id}/moon_pool";
                }
            }
        }

        /// <summary>After the floods (before validation): flood the wing, collapse the extra tube, mark the bulkheads.</summary>
        public static void Finish(Level level)
        {
            var cfg = level.Config;
            int s = level.CellSize;
            var notes = new List<string>();
            // the flooded wing: water 1 m deep from its floor, through its tube's lower steps
            foreach (var r in level.Rooms.Where(r => r.Get<string>("station_room") == "flooded_wing"))
            {
                int S = r.Floor + Floods.Wade;
                var wet = Floods.Flood(level, r.Cells().Where(c => !level.Diag.ContainsKey(c)), S);
                if (wet == null) { notes.Add($"flooded wing {r.Id}: stayed dry"); continue; }
                foreach (var c in wet.OrderBy(c => c)) level.Water[c] = S;
                r.Meta["flooded"] = S;
                notes.Add($"flooded wing {r.Id}: {wet.Count} cells");
            }
            // the collapsed section: the middle of the extra tube caves in (two dead-end stubs remain)
            var edges = level.Graph.Edges.Where(e => e.Role == "collapse").Select(e => e.Id).ToHashSet();
            foreach (var corr in level.Corridors.Where(c => edges.Contains(c.Edge)))
            {
                string own = $"corridor:{corr.Id}";
                int n = corr.Path.Count;
                if (n < 5) continue;
                var block = new List<Cell>();
                for (int i = n / 2 - 1; i <= n / 2; i++)
                {
                    var (dx, dy) = Layout.Dir(corr.Path, i);
                    var (px, py) = (-dy, dx);
                    for (int v = -corr.Radius; v <= corr.Radius; v++) block.Add(corr.Path[i].Offset(px * v, py * v));
                }
                if (block.Any(c => !level.Spans.TryGetValue(c, out var sp) || sp.Count != 1 || level.OwnerOf(c, sp[0]) != own || level.Diag.ContainsKey(c)))
                    continue;                                  // a junction or something stacked: leave it open
                foreach (var c in block)
                {
                    level.ReplaceIn(c, int.MinValue / 2, int.MaxValue / 2, Array.Empty<Span>());
                    level.PartSolid[c] = $"{own}/collapse";
                    level.Water.Remove(c);
                }
                var gone = new HashSet<Cell>(block);
                level.Entities.RemoveAll(e => e.Bounds == null && gone.Contains(level.CellOf(e.Pos)));
                corr.Kind = corr.Kind == "open" ? "collapsed" : corr.Kind;
                notes.Add($"collapsed tube {corr.Id}");
            }
            // bulkheads: every tube end is a doorway into a pod; a marker with the doorway's box
            foreach (var corr in level.Corridors)
            {
                if (corr.Path.Count == 0) continue;
                foreach (var (end, room) in new[] { (0, corr.A), (corr.Path.Count - 1, corr.B) })
                {
                    var c = corr.Path[end];
                    var (dx, dy) = corr.Path.Count > 1 ? Layout.Dir(corr.Path, end == 0 ? 0 : corr.Path.Count - 2) : (1, 0);
                    if (dx != 0 && dy != 0) continue;
                    var (px, py) = (-dy, dx);
                    var cells = Enumerable.Range(-corr.Radius, 2 * corr.Radius + 1).Select(v => c.Offset(px * v, py * v)).ToList();
                    if (cells.Any(x => !level.Spans.ContainsKey(x))) continue;
                    int f = corr.Floors[end];
                    var lo = new Vec3(cells.Min(x => x.X) * s, cells.Min(x => x.Y) * s, f);
                    var hi = new Vec3((cells.Max(x => x.X) + 1) * s, (cells.Max(x => x.Y) + 1) * s, f + corr.Height);
                    level.Entities.Add(new Entity("bulkhead", new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, f)) { Bounds = (lo, hi) }
                        .With("room", room).With("corridor", corr.Id).With("facing", end == 0 ? new[] { -dx, -dy } : new[] { dx, dy }));
                }
            }
            var rooms = level.Rooms.Where(r => r.Meta.ContainsKey("station_room")).Select(r => $"{r.Get<string>("station_room")}:{r.Id}");
            level.Notes.Add($"station: style={cfg.StationStyle} spine={cfg.Spine} seed={cfg.Seed} decks={level.Rooms.Select(r => r.Floor / Deck).Distinct().Count()}"
                            + $" rooms=[{string.Join(", ", rooms)}]" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
        }

        /// <summary>What the GLB root node should carry (extras.noor.station): style, spine, seed.</summary>
        public static Dictionary<string, object> RootExtras(Level level) => !IsStation(level.Config) ? null : new Dictionary<string, object>
        {
            ["style"] = level.Config.StationStyle, ["spine"] = level.Config.Spine, ["seed"] = level.Config.Seed,
        };

        // ------------------------------------------------------------------------------------------
        // the station's own rules (tests; not part of validation)
        // ------------------------------------------------------------------------------------------
        /// <summary>Broken station rules, empty when it's a proper station: everything reachable from the
        /// airlock, the airlock on the top deck, the core the deepest point, every tube at least 2 cells long and
        /// profiled, no lifts / ladders / spirals, every pod round or octagonal.</summary>
        public static List<string> Check(Level level)
        {
            var errs = new List<string>();
            var start = level.Rooms.Single(r => r.Kind == "start");
            var core = level.Rooms.Single(r => r.Kind == "exit");
            var st = level.Entities.Single(e => e.Kind == "player_start");
            var node = Validator.NodeAt(level, st.Pos);
            if (node == null) errs.Add("airlock: the start is not in open space");
            else
            {
                var reach = Validator.Flood(level, node.Value, new HashSet<Cell>());
                var un = Validator.RoomsUnreached(level, reach);
                if (un.Count > 0) errs.Add($"not reachable from the airlock: rooms [{string.Join(", ", un)}]");
            }
            if (level.Rooms.Any(r => r.Floor > start.Floor)) errs.Add("airlock: not on the top deck");
            if (level.Rooms.Any(r => r != core && r.Floor <= core.Floor)) errs.Add("core: another pod as deep");
            string coreTag = $"room:{core.Id}";
            foreach (var kv in level.Spans)
                foreach (var sp in kv.Value)
                    if (sp.Floor < core.Floor && level.OwnerOf(kv.Key, sp) != coreTag) { errs.Add($"core: {kv.Key} is deeper"); goto deep; }
            deep:
            foreach (var c in level.Corridors)
            {
                if (c.Path.Count < 2) errs.Add($"tube {c.Id} is {c.Path.Count} cells long");
                if (string.IsNullOrEmpty(c.Profile) || c.Profile == "box") errs.Add($"tube {c.Id} has no profile");
            }
            var profiled = new HashSet<int>();
            foreach (var kv in level.CeilSlope)
                foreach (var sp in level.Spans[kv.Key])
                {
                    var o = level.OwnerOf(kv.Key, sp) ?? "";
                    if (o.StartsWith("corridor:")) profiled.Add(int.Parse(o.Substring(9)));
                }
            foreach (var c in level.Corridors)
                if (!profiled.Contains(c.Id) && c.Kind != "collapsed") errs.Add($"tube {c.Id}: no sloped ceiling");
            if (level.Rooms.Any(r => r.IsConnector)) errs.Add("a lift, ladder, stairwell or spiral room");
            if (level.Links.Count > 0) errs.Add("links (lifts / ladders)");
            if (level.Entities.Any(e => e.Kind == "lift" || e.Kind == "ladder" || e.Kind == "jumppad")) errs.Add("a lift or ladder entity");
            foreach (var r in level.Rooms)
                if (r.Shape != "round" && r.Shape != "octagon") errs.Add($"pod {r.Id} is {r.Shape}");
            return errs;
        }
    }
}
