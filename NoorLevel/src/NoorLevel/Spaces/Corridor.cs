using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Corridors as a space type (spaces/corridor.py): their style (width, diagonals), their
    /// beats, their contents. Routing and carving are core infrastructure and live in
    /// <see cref="Layout"/>; this decides what a corridor is: how wide, whether it may run at 45
    /// degrees, where it gets chambers / alcoves / vaults, and what sits in it (lights, doors).</summary>
    public static class CorridorSpace
    {
        public const int Slab = 16;
        public static readonly string[] BigKinds = { "megaroom", "courtyard" };

        public static bool IsBig(Room room) => Array.IndexOf(BigKinds, room.Kind) >= 0 || room.Ceil - room.Floor >= 288;

        /// <summary>Pacing by contrast: the way into a big space is narrow and low, so arriving is a release.</summary>
        public static bool Compressed(GenConfig cfg, Room a, Room b) =>
            cfg.PacingContrast && !(a.IsConnector || b.IsConnector) && (IsBig(a) || IsBig(b));

        public static int CorridorHeight(GenConfig cfg, Room a, Room b, int r)
        {
            double f = cfg.CorridorHeight / 128.0;          // spaciousness raises it (128 by default: unchanged)
            if (Compressed(cfg, a, b)) return (int)Math.Round(112 * f / 16) * 16;
            int baseH = Layout.CorridorHeight.TryGetValue(r, out var bh) ? bh : 128 + 32 * (r - 1);
            return (int)Math.Round(baseH * f / 16) * 16;
        }

        public static (int R, bool Diag) CorridorStyle(MissionGraph g, Edge e, Room a, Room b, GenConfig cfg, Rng rng)
        {
            var (r, diag) = CorridorStyle0(g, e, a, b, cfg, rng);
            if (cfg.Packing >= 0.5) return (1, false);        // packed: a doorway through the wall, 3 wide, straight
            if (!(a.IsConnector || b.IsConnector) &&
                (a.Meta.TryGetValue("diag_from", out var da) && da is int ia && ia == b.Id ||
                 b.Meta.TryGetValue("diag_from", out var db) && db is int ib && ib == a.Id))
                diag = true;                                  // placed off the other's corner for it (diagonal_pull)
            return (r, diag);
        }

        public static (int R, bool Diag) CorridorStyle0(MissionGraph g, Edge e, Room a, Room b, GenConfig cfg, Rng rng)
        {
            var kinds = new HashSet<string> { g.Nodes[e.A].Kind, g.Nodes[e.B].Kind };
            int smallest = new[] { a.W, a.H, b.W, b.H }.Min();
            if (Compressed(cfg, a, b))
            {
                // narrow and low into a big space; with spaciousness 5 wide (still a squeeze next to a hall)
                int rc = cfg.Spaciousness >= 0.5 ? 2 : 1;
                return (Math.Max(1, Math.Min(rc, (smallest - 3) / 2)), false);
            }
            if (a.IsConnector || b.IsConnector)               // connector doors: 3 cells wide, or 5 when wide (fw)
            {
                int fw = new[] { a, b }.Where(x => x.IsConnector).Max(x => x.Get("fw", 3));
                return (fw >= 5 ? 2 : 1, false);
            }
            int r; bool diag;
            if (e.Kind == "secret") { r = 1; diag = false; }
            else
            {
                int[] opts;
                if (!e.Tree) opts = new[] { 1, 1, 2 };
                else if (kinds.Overlaps(new[] { "arena", "hub", "courtyard", "megaroom" })) opts = new[] { 1, 2, 2, 3 };
                else if (kinds.Overlaps(new[] { "supply", "secret", "key" })) opts = new[] { 1, 1, 2 };
                else opts = new[] { 1, 2, 2 };
                r = rng.WeightedBias(opts, cfg.CorridorWidthBias);
                diag = rng.Random() < cfg.DiagonalChance;
            }
            // the opening must fit the smaller room
            r = Math.Max(1, Math.Min(Math.Min(r, cfg.MaxCorridorRadius), (smallest - 3) / 2));
            return (r, diag);
        }

        public static (int, int) Perp((int X, int Y) d) => (-d.Y, d.X);

        /// <summary>Is any space of another area within one cell of c, in the vertical range [lo, hi)?</summary>
        public static bool OthersNear(Level level, Cell c, int lo, int hi, string own)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var n = c.Offset(dx, dy);
                    foreach (var sp in level.SpansIn(n, lo - Slab, hi + Slab))
                        if (level.OwnerOf(n, sp) != own) return true;
                }
            return false;
        }

        /// <summary>Break a long corridor up: every beat_spacing cells, on a straight level stretch,
        /// something changes: a chamber (wider, taller, maybe pillars for cover), alcoves (side niches)
        /// or a vault (much higher ceiling); with beat_shapes, rooms along the way (rotunda, octagon,
        /// crossing, aisles). Long corridors stay long.</summary>
        public static void AddBeats(Level level, Router router, Corridor corr, Rng rng)
        {
            var cfg = level.Config;
            var path = corr.Path; var floors = corr.Floors;
            int n = path.Count, r = corr.Radius, h = corr.Height;
            int sp = cfg.BeatSpacing;
            if (n < 2 * sp || corr.Connector == "drop" || corr.Connector == "gallery") return;
            string own = $"corridor:{corr.Id}";
            const int L2 = 3;
            var targets = new List<int>();
            for (int t0 = sp; t0 < n - sp / 2; t0 += sp) targets.Add(t0);
            double shaped = cfg.BeatShapes;
            foreach (var t in targets)
            {
                string shapedKind = null;
                if (shaped > 0 && rng.Random() < shaped * 0.6)      // rooms along the way ("some variety")
                    shapedKind = rng.Choice(new[] { "rotunda", "octagon", "crossing", "aisles" });
                var plan = new List<(string Kind, bool Shrink)> { (null, false) };   // (null: the classic beat)
                if (shapedKind != null)
                    plan = new List<(string, bool)> { (shapedKind, false), (shapedKind, true), ("chamber", false) };   // too big: smaller, a chamber
                bool ok = false;
                string kind = null;
                int i = 0, f = 0, top = 0, cx = 0, cy = 0;
                List<Cell> zone = null, pillars = null;
                Dictionary<Cell, Corner> diag = null;
                foreach (var (pk, shrink) in plan)
                {
                    kind = pk;
                    int R = kind == "rotunda" || kind == "octagon" || kind == "crossing" ? r + rng.Int(3, 4) : 0;
                    if (shrink) R = Math.Max(r + 2, R - 1);
                    int reach = Math.Max(L2, R) + (kind == "aisles" ? 2 : 0);
                    var cands = new List<int>();
                    for (int j = Math.Max(reach + 3, t - 4); j < Math.Min(n - reach - 3, t + 5); j++)
                    {
                        if (!Layout.Straight(path, j, reach + 1, axisOnly: true)) continue;
                        int s0 = j - reach - 1, s1 = Math.Min(n, j + reach + 2);
                        bool level1 = true;
                        for (int q = s0; q < s1; q++) if (floors[q] != floors[s0]) { level1 = false; break; }
                        if (level1) cands.Add(j);
                    }
                    if (cands.Count == 0) continue;                 // (try the next, smaller plan)
                    i = cands.OrderBy(j => Math.Abs(j - t)).First();
                    if (kind == null) kind = rng.Choices(new[] { "chamber", "alcoves", "vault" }, new[] { 0.5, 0.3, 0.2 });
                    f = floors[i];
                    var d = Layout.Dir(path, i);
                    var pd = Perp(d);
                    cx = path[i].X; cy = path[i].Y;
                    diag = new Dictionary<Cell, Corner>();
                    zone = new List<Cell>();
                    pillars = new List<Cell>();
                    Cell At(int u, int v) => new Cell(cx + d.Item1 * u + pd.Item1 * v, cy + d.Item2 * u + pd.Item2 * v);
                    if (kind == "rotunda" || kind == "octagon")
                    {
                        // a round or octagonal room the corridor runs through (the room shapes, 45-degree walls)
                        int D = 2 * R + 1;
                        var shp = Shape.Make(D, D, kind == "rotunda" ? "round" : "octagon", rng);
                        foreach (var c in shp.Cells.OrderBy(c => c)) zone.Add(new Cell(cx - R + c.X, cy - R + c.Y));
                        foreach (var kv in shp.Diag) diag[new Cell(cx - R + kv.Key.X, cy - R + kv.Key.Y)] = kv.Value;
                        top = f + h + (kind == "rotunda" ? 96 : 64);
                        int p_ = R - 2;
                        if (kind == "octagon" && p_ > r + 1 && rng.Random() < 0.6)
                            foreach (var a in new[] { -1, 1 }) foreach (var b in new[] { -1, 1 }) pillars.Add(new Cell(cx + a * p_, cy + b * p_));
                    }
                    else if (kind == "crossing")
                    {
                        // a transept: the corridor crossed by a wide arm
                        top = f + h + 64;
                        int arm = r + 1;
                        for (int u = -R; u <= R; u++)
                            for (int v = -R; v <= R; v++)
                                if (Math.Abs(u) <= arm || Math.Abs(v) <= arm) zone.Add(At(u, v));
                    }
                    else if (kind == "aisles")
                    {
                        // a hall with a colonnade down each side of the corridor
                        top = f + h + 96;
                        int A = L2 + 2;
                        for (int u = -A; u <= A; u++)
                            for (int v = -(r + 3); v <= r + 3; v++) zone.Add(At(u, v));
                        for (int u = -A + 1; u < A; u += 2)
                            foreach (var v in new[] { -(r + 1), r + 1 }) pillars.Add(At(u, v));
                    }
                    else if (kind == "chamber")
                    {
                        R = r + 2; top = f + h + 64;
                        for (int u = -L2; u <= L2; u++)
                            for (int v = -R; v <= R; v++) zone.Add(At(u, v));
                        if (rng.Random() < 0.6)
                            foreach (var v in new[] { -(r + 1), r + 1 }) pillars.Add(At(0, v));
                    }
                    else if (kind == "alcoves")
                    {
                        top = f + h;
                        var sides = rng.Choice(new[] { new[] { -1 }, new[] { 1 }, new[] { -1, 1 } });
                        foreach (var s in sides)
                            foreach (var u in new[] { -1, 0, 1 })
                                for (int v = -r; v < r + 3; v++) zone.Add(At(u, s * v));
                    }
                    else
                    {
                        top = f + h + 96;                           // a vault
                        for (int u = -L2; u <= L2; u++)
                            for (int v = -r; v <= r; v++) zone.Add(At(u, v));
                    }
                    // everything new must be solid now (or ours, flat at f) and keep a wall to other areas
                    ok = true;
                    foreach (var c in zone)
                    {
                        var spans = level.Spans.TryGetValue(c, out var ss) ? ss : new List<Span>();
                        var mine = spans.Where(s_ => level.OwnerOf(c, s_) == own).ToList();
                        bool nearDiag = false;
                        for (int dx = -1; dx <= 1 && !nearDiag; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                                if (level.Diag.ContainsKey(c.Offset(dx, dy))) { nearDiag = true; break; }
                        int topc = top, fc = f;
                        if (nearDiag || spans.Any(s_ => !mine.Contains(s_) && s_.Floor < topc + Slab && fc - Slab < s_.Ceil)) { ok = false; break; }
                        if (mine.Any(m => m.Floor != fc) || OthersNear(level, c, f, top, own)) { ok = false; break; }
                    }
                    if (ok) break;
                }
                if (!ok) continue;
                if (!level.Config.Pillars) pillars.Clear();      // (no columns: carve those cells too)
                var cells = new List<Cell>();
                var pillarSet = new HashSet<Cell>(pillars);
                foreach (var c in zone)
                {
                    if (pillarSet.Contains(c)) continue;
                    bool had = level.SpansIn(c, f, f + 1).Any();
                    level.ReplaceIn(c, f - 1, f + h, Array.Empty<Span>());          // our own span (if any) ...
                    level.AddSpan(c, new Span(f, top), level.OwnerAt(c, f) == null ? own : null);   // ... now taller / new
                    if (diag.TryGetValue(c, out var corner) && !had)                 // the rim: a 45-degree half cell
                    {
                        level.Diag[c] = corner;
                        level.DiagSpan[c] = new Span(f, top);
                    }
                    else cells.Add(c);
                }
                foreach (var c in pillars) level.ReplaceIn(c, f - 1, top, Array.Empty<Span>());
                foreach (var c in cells) router.AddOpen(c, f, top);
                int sz = level.CellSize;
                corr.Beats.Add(new Dictionary<string, object>
                {
                    ["type"] = kind, ["index"] = i, ["cells"] = cells,
                    ["pos"] = new Vec3((cx + 0.5) * sz, (cy + 0.5) * sz, f), ["top"] = top, ["pillars"] = pillars.Count,
                });
            }
        }

        /// <summary>Lights along the corridor and in its beats, and its door if any (no encounters or
        /// rewards: those are the game's).</summary>
        internal static void Populate(Level level, Corridor corr, Rng rng, Dictionary<string, object> info)
        {
            var ents = level.Entities;
            int s = level.CellSize;
            for (int i = 3; i < corr.Path.Count - 3; i += 7)
            {
                var c = corr.Path[i]; int f = corr.Floors[i];
                ents.Add(new Entity("light", new Vec3((c.X + 0.5) * s, (c.Y + 0.5) * s, f + corr.Height - 16)).With("intensity", 200));
            }
            foreach (var beat in corr.Beats)       // light in every chamber, alcove and vault
            {
                var pos = (Vec3)beat["pos"];
                int top = (int)beat["top"];
                ents.Add(new Entity("light", new Vec3(pos.X, pos.Y, top - 24)).With("intensity", 260));
            }
            // TODO(wave 1b): DecorateStubs.DoorEntity -> Decorate.DoorEntity
            Entity door = null;
            if (corr.Kind == "locked") door = DecorateStubs.DoorEntity(level, corr, "door");
            else if (corr.Kind == "secret") door = DecorateStubs.DoorEntity(level, corr, "secret_door");
            if (door != null) ents.Add(door);
        }
    }
}
