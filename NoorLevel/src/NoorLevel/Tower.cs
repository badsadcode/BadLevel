using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>The wizard's tower (Noor's idea): a helix stair round a void, impossible-looking flights
    /// across it (tower.py).
    ///
    /// One tall round hall is the spine of the level. Inside it:
    /// <list type="bullet">
    /// <item>The helix: a stair 3-4 cells wide climbs round the inside of the wall, about 5-6 units per
    /// cell, with a flat landing at every storey. The landings are a quarter turn apart (Delta), so each
    /// storey's door faces a different way. Where two laps pass over the same cell the column holds both
    /// (stacked spans); the ground floor runs under the helix wherever there's headroom.</item>
    /// <item>The void: the middle is open from the ground floor to the sky. Fall in and you land on the
    /// ground floor, where the helix starts: a short cut down, never a trap.</item>
    /// <item>Impossible staircases: straight flights across the void, 3 wide, one per lap, each turned a
    /// quarter turn from the last, so they pass over and under each other (always with headroom). Each
    /// joins the helix at two points half a lap apart: valid geometry, a short cut up.</item>
    /// <item>The top is open to the sky (an observatory roof).</item>
    /// </list>
    /// The graph (BuildTowerGraph, cfg.Tower): the tower is the hub; each storey hangs one to three rooms
    /// off its landing, the start on the ground floor, the exit (the observatory) at the top; keycard
    /// doors on the upper landings, their keys on the storeys below.</summary>
    public static class Tower
    {
        public static readonly double Delta = Math.PI / 2;     // the next storey's landing is a quarter turn further round
        public static readonly double Ring = 3.5;              // the helix's width (cells, from the wall inwards)
        public static readonly int Slab = 16;                  // stair thickness under a lap or a flight
        public static readonly double LandingCells = 2.5;      // half the landing's length along the ring
        public static readonly int MinGround = 96;             // the ground floor runs under the helix where it's at least this high

        /// <summary>The disc of radius R in a (2R+1)^2 box: local cell -> (distance from the centre, angle).
        /// Keys in (u outer, v inner) order, as Python's dict.</summary>
        public static (int D, List<Cell> Order, Dictionary<Cell, (double D, double Ang)> Cells) Geom(int R)
        {
            int D = 2 * R + 1;
            double c = D / 2.0;
            var order = new List<Cell>();
            var cells = new Dictionary<Cell, (double, double)>();
            for (int u = 0; u < D; u++)
                for (int v = 0; v < D; v++)
                {
                    double d = Hypot(u + 0.5 - c, v + 0.5 - c);
                    if (d < R + 0.5)
                    {
                        double ang = PyMod(Math.Atan2(v + 0.5 - c, u + 0.5 - c), 2 * Math.PI);
                        var k = new Cell(u, v);
                        order.Add(k);
                        cells[k] = (d, ang);
                    }
                }
            return (D, order, cells);
        }

        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
        internal static double PyMod(double a, double m) { double r = a % m; return r < 0 ? r + m : r; }

        /// <summary>Height of the helix at an unrolled angle phi (radians from the ground landing).</summary>
        public sealed class Helix
        {
            public readonly int R, S, H;
            public readonly double Theta0, Per, Wl;

            public Helix(int r, int storeys, int h, double theta0)
            {
                R = r; S = storeys; H = h; Theta0 = theta0;
                double rmid = r - Ring / 2;
                Per = 2 * Math.PI + Delta;                      // unrolled angle per storey
                Wl = (LandingCells / rmid) / Per;               // landing half-width, in storeys
            }

            public double StoreyAt(double phi) => (phi - Theta0) / Per;

            public int? Height(double phi)
            {
                double sf = StoreyAt(phi);
                if (sf < -Wl || sf > S - 1 + Wl) return null;
                double k = Math.Round(sf);
                if (Math.Abs(sf - k) <= Wl) return (int)k * H;
                double kf = Math.Floor(sf);
                double t = (sf - kf - Wl) / (1 - 2 * Wl);
                return (int)Math.Round(H * (kf + t) / 8) * 8;
            }

            public List<int> Floors(double ang)
            {
                var outp = new SortedSet<int>();
                for (int m = -1; m < S + 2; m++)
                {
                    var h = Height(ang + 2 * Math.PI * m);
                    if (h != null) outp.Add(h.Value);
                }
                return outp.ToList();
            }
        }

        /// <summary>Fill room.Meta with the tower's plan: radius, storeys, landing angles, flights.</summary>
        public static void Plan(Room room, int H, Rng rng)
        {
            double theta0 = rng.Choice(new[] { 0.0, 0.5, 1.0, 1.5 }) * Math.PI;
            room.Meta["theta0"] = theta0;
            room.Meta["H"] = H;
        }

        public static void Build(Level level, Room room)
        {
            var m = room.Meta;
            int R = (int)m["R"], S = (int)m["storeys"], H = (int)m["H"];
            double theta0 = (double)m["theta0"];
            var hx = new Helix(R, S, H, theta0);
            var (D, order, cells) = Geom(R);
            int bas = room.Floor, ceil = room.Ceil;
            string owner = $"room:{room.Id}";
            var columns = new Dictionary<Cell, List<int>>();
            foreach (var uv in order)
            {
                var (d, ang) = cells[uv];
                if (d >= R - Ring)                                 // the helix ring
                {
                    var fl = hx.Floors(ang);
                    if (fl.Count == 0 || fl.Min() >= MinGround)
                        fl = new[] { 0 }.Concat(fl).ToList();      // the ground floor under the stair
                    columns[uv] = fl;
                }
                else columns[uv] = new List<int> { 0 };            // the void
            }
            // the impossible staircases: flights across the void
            var flights = new List<Dictionary<string, object>>();
            var rng = Rng.FromLabel($"{level.Config.Seed}/{level.Config.Variant}/tower/{room.Id}");
            double voidR = R - Ring;
            // every flight the helix allows: four directions, every lap; from one point of the helix
            // straight across the void to the point half a lap further round, rising (a short cut up)
            double L = 2 * voidR;
            var options = new List<(double Mid, int J, double Alpha, int Ha, int Hb)>();
            for (int j = 0; j < 4; j++)
            {
                double alpha = theta0 + Math.PI / 2 + j * Math.PI / 2;
                for (int ma = -1; ma < S + 1; ma++)
                {
                    double pa = alpha + 2 * Math.PI * ma;
                    var ha = hx.Height(pa);
                    if (ha == null || ha < 140) continue;
                    for (int mb = -1; mb < S + 1; mb++)
                    {
                        var hb = hx.Height(alpha + Math.PI + 2 * Math.PI * mb);
                        if (hb != null && 0 < hb - ha && hb - ha <= 15 * (L - 1))
                            options.Add(((ha.Value + hb.Value) / 2.0, j, alpha, ha.Value, hb.Value));
                    }
                }
            }
            rng.Shuffle(options);
            var flightMeta = new List<(int Dir, double Mid)>();
            foreach (var (mid, j, alpha, ha, hb) in options.OrderBy(o => o.Mid))
            {
                if (flights.Count >= S - 1 || flightMeta.Any(f => Math.Abs(mid - f.Mid) < 160 || f.Dir == j))
                    continue;                                      // spread over the height, each a new direction
                double ux = Math.Cos(alpha), uy = Math.Sin(alpha);
                var deck = new List<(Cell C, int H)>();
                foreach (var uv in order)
                {
                    var (d, _) = cells[uv];
                    if (d >= voidR) continue;
                    double x = uv.X + 0.5 - D / 2.0, y = uv.Y + 0.5 - D / 2.0;
                    double along = x * ux + y * uy;                // -voidR .. voidR, from A to B
                    double across = -x * uy + y * ux;
                    if (Math.Abs(across) <= 1.5)
                    {
                        double t = (along + voidR) / (2 * voidR);
                        deck.Add((uv, (int)Math.Round((ha + (hb - ha) * t) / 8) * 8));
                    }
                }
                // headroom against what's already in those columns (another flight, the ground)
                int need = level.Config.PlayerHeight + Slab + 16;
                if (!deck.All(p => columns[p.C].Min(f => Math.Abs(p.H - f)) >= need))
                    continue;
                foreach (var (c, h) in deck)
                {
                    var l = new List<int>(columns[c]) { h };
                    l.Sort();
                    columns[c] = l;
                }
                flightMeta.Add((j, mid));
                flights.Add(new Dictionary<string, object>
                {
                    ["dir"] = j, ["mid"] = mid, ["angle"] = (int)Math.Round(PyMod(alpha * 180 / Math.PI, 360)),
                    ["from"] = ha, ["to"] = hb, ["cells"] = deck.Count,
                });
            }
            foreach (var uv in order)
            {
                var fl = columns[uv];
                var c = new Cell(room.X + uv.X, room.Y + uv.Y);
                var spans = new List<Span>();
                for (int i = 0; i < fl.Count; i++)
                {
                    int top = i + 1 < fl.Count ? fl[i + 1] - Slab : ceil - bas;
                    spans.Add(new Span(bas + fl[i], bas + top));
                }
                foreach (var sp in spans)
                    level.AddSpan(c, sp, owner, (sp.Equals(spans[0]) ? room.Floor - 64 : sp.Floor, sp.Ceil));
                level.Sky.Add(c);
                if (cells[uv].D < voidR && fl.Count > 1)
                    level.PartCells[c] = $"{owner}/flight";
                else if (cells[uv].D >= voidR)
                    level.PartCells[c] = $"{owner}/helix";
            }
            m["flights"] = flights;
        }

        /// <summary>For each storey: the wall cells of its landing, and its height.</summary>
        public static List<(HashSet<Cell> Door, int Floor)> Ports(Room room)
        {
            var m = room.Meta;
            int R = (int)m["R"], S = (int)m["storeys"], H = (int)m["H"];
            double theta0 = (double)m["theta0"];
            var (_, order, cells) = Geom(R);
            var outp = new List<(HashSet<Cell>, int)>();
            double rmid = R - Ring / 2;
            double half = (LandingCells * 0.8) / rmid;
            for (int k = 0; k < S; k++)
            {
                double ang = PyMod(theta0 + k * Delta, 2 * Math.PI);
                var door = new HashSet<Cell>();
                foreach (var uv in order)
                {
                    var (d, a) = cells[uv];
                    double diff = Math.Abs(PyMod(a - ang + Math.PI, 2 * Math.PI) - Math.PI);
                    if (d >= R - 1.0 && diff <= half) door.Add(new Cell(room.X + uv.X, room.Y + uv.Y));
                }
                outp.Add((door, room.Floor + k * H));
            }
            return outp;
        }

        // ------------------------------------------------------------------------------------------
        // the mission graph
        // ------------------------------------------------------------------------------------------
        public static MissionGraph BuildTowerGraph(GenConfig cfg, Rng rng)
        {
            var g = new MissionGraph();
            int S = Math.Max(3, cfg.Storeys);
            var tower = g.AddNode("tower", "tower", storey: 0);
            tower.Main = true;
            g.Center = tower.Id;
            int nKeys = Math.Max(0, Math.Min(Math.Min(cfg.Keys, 2), S - 2));
            var locks = nKeys > 0
                ? new[] { S - 1, Math.Max(2, S / 2) }.Take(nKeys).Distinct().OrderBy(x => x).ToList()
                : new List<int>();
            int ZoneOf(int s) => locks.Count(L => s >= L);
            int depth = 1;
            var keyStorey = new List<(int L, int Ks)>();          // (Python dict: insertion order)
            for (int i = 0; i < locks.Count; i++)
            {
                int L = locks[i];
                int lo = i > 0 ? locks[i - 1] : 0;
                keyStorey.Add((L, L - lo > 1 ? rng.Range(lo, L) : lo));
            }
            for (int s = 0; s < S; s++)
            {
                int z = ZoneOf(s);
                int nRooms = s == 0 || s == S - 1 ? 1 : rng.Choice(new[] { 1, 2, 2, 3 });
                var prev = tower;
                for (int j = 0; j < nRooms; j++)
                {
                    string kind, size;
                    if (s == 0 && j == 0) { kind = "start"; size = "medium"; }
                    else if (s == S - 1 && j == nRooms - 1) { kind = "exit"; size = "medium"; }
                    else
                    {
                        kind = rng.Choice(new[] { "combat", "combat", "hub", "supply" });
                        size = rng.WeightedBias(new[] { "small", "medium", "medium", "large" }, cfg.SizeBias);
                    }
                    var n = g.AddNode(kind, size, zone: z, depth: depth, storey: s);
                    n.Main = j == 0;
                    depth++;
                    var e = g.AddEdge(prev.Id, n.Id);
                    if (prev == tower && locks.Contains(s))
                    {
                        e.Kind = "locked";
                        e.Key = MissionGraph.KeyColors[locks.IndexOf(s)];
                    }
                    prev = n;
                }
                // a key room for a lock further up hangs off this storey's last room
                foreach (var (L, ks) in keyStorey)
                    if (ks == s)
                    {
                        var k = g.AddNode("key", "medium", zone: z, depth: depth, storey: s, key: MissionGraph.KeyColors[locks.IndexOf(L)]);
                        g.AddEdge(prev.Id, k.Id);
                    }
            }
            if (cfg.Secret)
            {
                var parents = g.Nodes.Where(n => n.Kind != "tower" && n.Kind != "start" && n.Kind != "exit" && n.Kind != "secret").ToList();
                if (parents.Count > 0)
                {
                    var p = rng.Choice(parents);
                    var sct = g.AddNode("secret", "small", zone: p.Zone, depth: p.Depth, storey: p.Storey);
                    g.AddEdge(p.Id, sct.Id, kind: "secret");
                }
            }
            return g;
        }
    }
}
