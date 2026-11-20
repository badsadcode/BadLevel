using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Vistas (vistas.py): big windows in grand halls onto the world outside (the Unreal tradition).
    /// <para>The grand halls of the late-90s shooters had a trick: a wall of glass, and beyond it a sky and a
    /// landscape you would never walk on. Here, for the biggest, tallest halls (<c>cfg.Vistas</c> of them):</para>
    /// <list type="bullet">
    /// <item>A site: a straight, flat stretch of the hall's outer wall with clean rock behind it, deep and wide
    /// enough for a view (18-26 cells deep, the window plus 8 cells each side), with a cell of rock all round
    /// it. Nothing may be stacked above or below it either: the view goes up to the sky. Where the grid ends,
    /// it grows (east and north).</item>
    /// <item>The window: a glazed pane through the wall (4-12 cells wide, from a 48-unit sill to just under the
    /// hall's ceiling): see-through, not walkable (<c>level.Glass</c>), a <c>window</c> entity marked <c>vista</c>.</item>
    /// <item>The view: open to the sky. The ground falls away below the sill (96-256 units down), stays flat for
    /// a few cells, then rises into hills towards the horizon, higher at the sides, so the window frames a
    /// valley. The height is A(depth) + B(across): a sum, so each cell's four corners lie on one plane, and
    /// the hills are smooth sloped floors (<c>level.FloorSlope</c>). A few lights over it. Named
    /// <c>vista:N/terrain</c> for an artist to swap for a real skybox and landscape.</item>
    /// </list>
    /// Nobody can get there (glass), so the validator and the player box never see it.</summary>
    public static class Vistas
    {
        public static readonly (int, int)[] Sides = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        public static readonly int Sill = 48;
        public static readonly int Margin = 8;              // cells of view each side of the window

        public static int Q(double v, int k = 8) => (int)Math.Round(v / k) * k;

        public sealed class WinCell
        {
            public int T, Depth; public Cell C; public Span Sp; public bool Cut;
        }

        public sealed class Site
        {
            public (int, int) D;
            public bool AlongX;
            public List<WinCell> Win;
            public int Lo, Hi, V, T0, T1, Wall, Sgn;
            public List<Cell> Bay;
            public Func<int, int, Cell> Cell;
        }

        /// <summary>A flat stretch of the room's outer wall on side d with room for a view behind it.</summary>
        public static Site Site_(Level level, Room room, (int, int) d, Rng rng)
        {
            bool alongX = d.Item2 != 0;                     // the wall runs along x when the view faces n/s
            int lo_ = alongX ? room.X : room.Y, hi_ = alongX ? room.X + room.W : room.Y + room.H;
            int dsum = d.Item1 + d.Item2;

            Cell? Edge(int t)                               // the room's last cell on side d in row/column t
            {
                var cells = new List<Cell>();
                if (alongX) for (int y = room.Y; y < room.Y + room.H; y++) cells.Add(new Cell(t, y));
                else for (int x = room.X; x < room.X + room.W; x++) cells.Add(new Cell(x, t));
                cells = cells.Where(c => room.Contains(c)).ToList();
                if (cells.Count == 0) return null;
                Func<Cell, int> key = alongX ? (Func<Cell, int>)(c => c.Y) : (c => c.X);
                Cell best = cells[0];
                foreach (var c in cells)
                    if (dsum > 0 ? key(c) > key(best) : key(c) < key(best)) best = c;
                return best;
            }

            string mine = $"room:{room.Id}/";
            var runs = new List<List<WinCell>>();
            var run = new List<WinCell>();
            for (int t = lo_; t < hi_; t++)
            {
                var cq = Edge(t);
                bool ok = cq != null && !level.Diag.ContainsKey(cq.Value);
                bool cut = false;
                List<Span> sp = null;
                if (ok)
                {
                    var c = cq.Value;
                    sp = Tall(level, c, room);
                    string part = level.PartSolid.TryGetValue(c, out var ps) && !string.IsNullOrEmpty(ps) ? ps
                                : level.PartCells.TryGetValue(c, out var pc) ? pc : "";
                    if (sp.Count == 0 && part.StartsWith(mine))
                    {
                        // a pilaster or its base against the wall: the window takes its place; the view
                        // from the cell inside it decides how tall
                        var inner = c.Offset(-d.Item1, -d.Item2);
                        sp = room.Contains(inner) ? Tall(level, inner, room) : new List<Span>();
                        sp = sp.Count > 0 ? new List<Span> { new Span(room.Floor, sp[0].Ceil) } : new List<Span>();
                        cut = sp.Count > 0;
                    }
                    ok = sp.Count > 0;
                }
                int depth = 0;
                if (ok)
                {
                    var w = cq.Value.Offset(d.Item1, d.Item2);
                    depth = alongX ? w.Y : w.X;
                }
                if (ok && (run.Count == 0 || run[run.Count - 1].Depth == depth))
                    run.Add(new WinCell { T = t, Depth = depth, C = cq.Value, Sp = sp[0], Cut = cut });
                else
                {
                    if (run.Count >= 6) runs.Add(run);
                    run = ok ? new List<WinCell> { new WinCell { T = t, Depth = depth, C = cq.Value, Sp = sp[0], Cut = cut } } : new List<WinCell>();
                }
            }
            if (run.Count >= 6) runs.Add(run);
            rng.Shuffle(runs);
            foreach (var r in runs.OrderByDescending(x => x.Count).ToList())
            {
                var inner = r.GetRange(1, r.Count - 2);         // a cell of wall left at each end
                int W = Math.Min(inner.Count, rng.Int(6, 12));
                int s0 = (inner.Count - W) / 2;
                var win = inner.GetRange(s0, W);
                int head = win.Min(x => x.Sp.Ceil) - 24;
                int lo = room.Floor + Sill;
                // a window bay: the two cells in front of the pane rise to the hall's full height (cut up
                // into the vault), so the window can be as tall as the hall, not as its springing line
                var bay = new List<Cell>();
                foreach (var it in win)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        var b = it.C.Offset(-k * d.Item1, -k * d.Item2);
                        if (!room.Contains(b) || level.Diag.ContainsKey(b)) { bay = null; break; }
                        var own = level.Spans.TryGetValue(b, out var bs) ? bs.Where(x => x.Floor <= room.Floor + 16).ToList() : new List<Span>();
                        if ((own.Count > 0 && level.SpansIn(b, own[0].Ceil, room.Ceil + 16).Any()) || (own.Count == 0 && !it.Cut))
                        { bay = null; break; }
                        bay.Add(b);
                    }
                    if (bay == null) break;
                }
                if (bay != null && bay.Count > 0) head = room.Ceil - 32;
                if (head - lo < 160) continue;
                int wallDepth = win[0].Depth;
                int sgn = dsum;
                Func<int, int, Cell> cell = (u, v) => alongX ? new Cell(v, wallDepth + sgn * u) : new Cell(wallDepth + sgn * u, v);   // u: depth (0 = the wall), v: along the wall
                // the full view if it fits, else shallower and narrower ones
                foreach (var (V, M) in new[] { (rng.Int(18, 26), Margin), (14, 5), (10, 3) })
                {
                    int t0 = win[0].T - M, t1 = win[win.Count - 1].T + M;
                    bool ok = true;
                    for (int u = 0; u < V + 2 && ok; u++)
                        for (int v = t0 - 1; v < t1 + 2; v++)
                        {
                            var c = cell(u, v);
                            if (c.X < 1 || c.Y < 1 || level.Spans.ContainsKey(c) || level.Diag.ContainsKey(c) || level.Glass.Contains(c))
                            { ok = false; break; }
                        }
                    if (ok)
                        return new Site { D = d, AlongX = alongX, Win = win, Lo = lo, Hi = head, V = V, Bay = bay ?? new List<Cell>(),
                                          T0 = t0, T1 = t1, Wall = wallDepth, Sgn = sgn, Cell = cell };
                }
            }
            return null;
        }

        static List<Span> Tall(Level level, Cell c, Room room) =>
            level.Spans.TryGetValue(c, out var spans)
                ? spans.Where(s => s.Floor <= room.Floor + 16 && s.Ceil >= room.Floor + Sill + 160).ToList()
                : new List<Span>();

        public static void Build(Level level, Room room, Site site, int k, Rng rng)
        {
            int s = level.CellSize;
            int V = site.V, t0 = site.T0, t1 = site.T1;
            var cell = site.Cell;
            var win = site.Win;
            int g0 = room.Floor - rng.Choice(new[] { 96, 128, 160, 192, 256 });      // the ground below the sill
            int peak = room.Floor + rng.Choice(new[] { 160, 224, 288, 352 });        // the far hills, seen above the sill
            int flat = rng.Int(3, 5);
            double ph = rng.Uniform(0, 6.28), per = rng.Uniform(6, 11);
            int amp = rng.Choice(new[] { 24, 32, 48 });
            int wa = win[0].T, wb = win[win.Count - 1].T + 1;                        // the window's span along the wall
            double ridge = rng.Uniform(0.25, 0.5);

            int A(int u)                                                             // depth profile: flat, then rising
            {
                if (u <= flat) return g0;
                double x = (double)(u - flat) / Math.Max(1, V + 1 - flat);
                double rise = 3 * x * x - 2 * x * x * x;
                return Q(g0 + (peak - g0) * rise + ridge * 40 * Math.Sin(u * 0.9 + ph));
            }

            int B(int v)                                                             // across: waves, and the sides rise
            {
                int side = Math.Max(0, Math.Max(wa - 1 - v, v - wb - 1));
                return Q(amp * Math.Sin(v / per * 6.283 + ph) + 26 * side);
            }

            var tops = new List<int>();
            var cells = new Dictionary<Cell, (int, int, int, int)>();
            var cellOrder = new List<Cell>();
            for (int u = 1; u <= V; u++)
                for (int v = t0; v <= t1; v++)
                {
                    var c = cell(u, v);
                    // the heights at this cell's world corners (x0y0, x1y0, x1y1, x0y1)
                    var corners = new int[4];
                    int i = 0;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
                    {
                        int X = c.X + dx, Y = c.Y + dy;                              // a world corner
                        int depth = site.AlongX ? Y : X, across = site.AlongX ? X : Y;
                        int uc = site.Sgn > 0 ? depth - (site.Wall + 1) : site.Wall - depth;   // 0 at the wall
                        corners[i++] = A(uc) + B(across);
                    }
                    cells[c] = (corners[0], corners[1], corners[2], corners[3]);
                    cellOrder.Add(c);
                    tops.Add(corners.Max());
                }
            int sky = Math.Max(tops.Max() + 256, room.Ceil + 192);
            foreach (var c in cellOrder)
            {
                var cn = cells[c];
                int f = Math.Max(Math.Max(cn.Item1, cn.Item2), Math.Max(cn.Item3, cn.Item4));
                level.AddSpan(c, new Span(f, sky), $"vista:{k}");
                if (!(cn.Item1 == cn.Item2 && cn.Item2 == cn.Item3 && cn.Item3 == cn.Item4))
                    level.FloorSlope[c] = cn;
                level.Sky.Add(c);
                level.PartCells[c] = $"vista:{k}/terrain";
            }
            // the rock round the view (not the window wall) is sky, so the view has no edge; only where
            // nothing else is open beyond it, so no room ever sees a sky wall
            for (int u = 1; u < V + 2; u++)
                for (int v = t0 - 1; v < t1 + 2; v++)
                {
                    var c = cell(u, v);
                    if (cells.ContainsKey(c)) continue;
                    bool all = true;
                    for (int dx = -1; dx <= 1 && all; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var n = c.Offset(dx, dy);
                            if (!(cells.ContainsKey(n) || !level.Spans.ContainsKey(n))) { all = false; break; }
                        }
                    if (all) level.SkyWalls.Add(c);
                }
            // the pane (and its bay, carved below, after any pilaster in the way has gone)
            int lo = site.Lo, hi = site.Hi;
            string tag = $"room:{room.Id}";
            var panes = new List<Cell>();
            foreach (var it in win)
            {
                var c = it.C;
                if (it.Cut)                                 // the pilaster (or base) gives way to the window
                {
                    var gone = level.SpansIn(c, room.Floor - 16, it.Sp.Ceil).ToList();
                    int top = Math.Max(it.Sp.Ceil, gone.Count > 0 ? gone.Max(x => x.Ceil) : int.MinValue);
                    level.ReplaceIn(c, room.Floor - 16, it.Sp.Ceil, new[] { new Span(room.Floor, top) });
                    if (!level.Owners.TryGetValue(c, out var cos)) level.Owners[c] = cos = new List<Owner>();
                    if (!cos.Any(o => o.Name == tag)) cos.Add(new Owner(room.Floor, top, tag));
                    level.PartSolid.Remove(c);
                    level.PartCells.Remove(c);
                }
                var w = c.Offset(site.D.Item1, site.D.Item2);
                level.AddSpan(w, new Span(lo, hi));
                if (!level.Owners.TryGetValue(w, out var wos)) level.Owners[w] = wos = new List<Owner>();
                wos.Insert(0, new Owner(lo, hi, $"window:vista{k}"));
                level.Glass.Add(w);
                panes.Add(w);
            }
            foreach (var b in site.Bay)                     // the bay: up to the hall's ceiling, flat
            {
                var own = level.Spans.TryGetValue(b, out var bs) ? bs.Where(x => x.Floor <= room.Floor + 16).ToList() : new List<Span>();
                if (own.Count > 0 && own[0].Ceil < room.Ceil)
                {
                    level.ReplaceIn(b, own[0].Floor, own[0].Ceil, new[] { new Span(own[0].Floor, room.Ceil) });
                    level.Owners[b] = (level.Owners.TryGetValue(b, out var bos) ? bos : new List<Owner>())
                        .Select(o => new Owner(o.Lo, o.Name == tag ? room.Ceil : o.Hi, o.Name)).ToList();
                }
                level.CeilSlope.Remove(b);
                level.PartCeiling[b] = $"room:{room.Id}/window bay";
            }
            var bLo = new Vec3(panes.Min(p => p.X) * s, panes.Min(p => p.Y) * s, lo);
            var bHi = new Vec3((panes.Max(p => p.X) + 1) * s, (panes.Max(p => p.Y) + 1) * s, hi);
            level.Entities.Add(new Entity("window", Openings.Mid(bLo, bHi)) { Bounds = (bLo, bHi) }
                .With("glazed", true).With("vista", k).With("areas", new List<string> { tag, $"vista:{k}" }));
            // light over the view (Quake's sky doesn't light it)
            foreach (int u in new[] { V / 3, 2 * V / 3 })
                foreach (int v in new[] { t0 + (t1 - t0) / 4, t0 + 3 * (t1 - t0) / 4 })
                {
                    var c = cell(u, v);
                    level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, sky - 96)).With("intensity", 450));
                }
            level.Width = Math.Max(level.Width, cellOrder.Max(c => c.X) + 3);
            level.Height = Math.Max(level.Height, cellOrder.Max(c => c.Y) + 3);
            room.Meta["vista"] = new Dictionary<string, object>
            {
                ["id"] = k, ["side"] = new List<int> { site.D.Item1, site.D.Item2 }, ["window"] = win.Count, ["height"] = hi - lo,
                ["depth"] = V, ["ground_drop"] = room.Floor - g0, ["hills"] = peak - room.Floor,
            };
            room.Features.Add("vista");
        }

        public static int AddVistas(Level level)
        {
            var cfg = level.Config;
            int n = cfg.Vistas;
            if (n <= 0) return 0;
            var rng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/vista");
            var rooms = level.Rooms.Where(r => !r.IsConnector && r.Kind != "courtyard" && r.Kind != "secret" && r.Kind != "overlook"
                                               && r.Ceil - r.Floor >= 256 && Math.Min(r.W, r.H) >= 10)
                .OrderBy(r => -(r.W * r.H * (r.Ceil - r.Floor))).ToList();
            int made = 0;
            foreach (var room in rooms)
            {
                if (made >= n) break;
                var sides = Sides.ToList();
                rng.Shuffle(sides);
                foreach (var d in sides)
                {
                    var site = Site_(level, room, d, rng);
                    if (site != null)
                    {
                        Build(level, room, site, made, rng);
                        made++;
                        break;
                    }
                }
            }
            if (made > 0) level.Notes.Add($"vistas: {made}");
            return made;
        }
    }
}
