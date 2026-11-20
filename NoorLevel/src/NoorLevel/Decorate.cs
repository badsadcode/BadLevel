using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>What a room's features may touch: its cells less doorway zones and what's taken.</summary>
    sealed class RoomCtx
    {
        public readonly Level Level;
        public readonly Room Room;
        public readonly HashSet<Cell> Used;
        public readonly HashSet<Cell> Clear = new HashSet<Cell>();
        public readonly List<Cell> Doors = new List<Cell>();
        public List<Cell> TopCells = new List<Cell>();

        public RoomCtx(Level level, Room room)
        {
            Level = level; Room = room;
            Used = new HashSet<Cell>(room.WorldDiag().Keys);
            foreach (var c in room.Cells())
            {
                if (room.IsDiag(c.X, c.Y)) continue;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = c.Offset(dx, dy);
                    if (!room.Contains(n) && level.SpansIn(n, room.Floor, room.Ceil).Any()) { Doors.Add(c); break; }
                }
            }
            foreach (var d in Doors)
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        var c = d.Offset(dx, dy);
                        if (room.Contains(c)) Clear.Add(c);
                    }
        }

        public bool Free(IEnumerable<Cell> cells) => cells.All(c => Room.Contains(c) && !Clear.Contains(c) && !Used.Contains(c));

        public void SetFloor(Cell c, int f)
        {
            Level.ReplaceIn(c, Room.Slot.Lo, Room.Slot.Hi, new[] { new Span(f, Room.Ceil) });
            Used.Add(c);
        }

        public void MakeSolid(Cell c)
        {
            Level.ReplaceIn(c, Room.Slot.Lo, Room.Slot.Hi, Array.Empty<Span>());
            Used.Add(c);
        }
    }

    /// <summary>Room features (they change the column spans inside a room) and the level's entities.
    /// Every feature keeps the area round doorways clear; the validator checks the rest.</summary>
    public static class Decorate
    {
        delegate bool Feature(RoomCtx ctx, Rng rng);

        static bool Pillars(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            if (Math.Min(r.W, r.H) < 9) return false;
            int spacing = rng.Choice(new[] { 4, 5 }), placed = 0;
            for (int py = r.Y + 2; py < r.Y + r.H - 3; py += spacing)
                for (int px = r.X + 2; px < r.X + r.W - 3; px += spacing)
                {
                    var ring = (from dx in Enumerable.Range(-1, 4) from dy in Enumerable.Range(-1, 4) select new Cell(px + dx, py + dy)).ToList();
                    if (!ctx.Free(ring)) continue;
                    foreach (var c in new[] { new Cell(px, py), new Cell(px + 1, py), new Cell(px, py + 1), new Cell(px + 1, py + 1) })
                    { ctx.MakeSolid(c); ctx.Level.PartSolid[c] = $"room:{r.Id}/pillars"; }
                    foreach (var c in ring) ctx.Used.Add(c);
                    placed++;
                }
            if (placed > 0) r.Features.Add("pillars");
            return placed > 0;
        }

        static bool StairBlock(RoomCtx ctx, Rng rng, int sign, string name)
        {
            var r = ctx.Room;
            if (Math.Min(r.W, r.H) < 9) return false;
            int step = ctx.Level.Config.StepHeight;
            for (int t = 0; t < 40; t++)
            {
                int pw = rng.Int(4, Math.Max(4, Math.Min(8, r.W - 5))), ph = rng.Int(4, Math.Max(4, Math.Min(8, r.H - 5)));
                int px = rng.Int(r.X + 2, r.X + r.W - 2 - pw), py = rng.Int(r.Y + 2, r.Y + r.H - 2 - ph);
                var border = (from y in Enumerable.Range(py - 1, ph + 2) from x in Enumerable.Range(px - 1, pw + 2) select new Cell(x, y)).ToList();
                if (!ctx.Free(border)) continue;
                char side = rng.Choice("nsew".ToCharArray());
                var depth = new Dictionary<Cell, int>();
                for (int y = py; y < py + ph; y++)
                    for (int x = px; x < px + pw; x++)
                    {
                        int d = side == 'w' ? x - px : side == 'e' ? px + pw - 1 - x : side == 's' ? y - py : py + ph - 1 - y;
                        int along = side == 'w' || side == 'e' ? y - py : x - px;
                        int length = side == 'w' || side == 'e' ? ph : pw;
                        bool inStair = Math.Abs(along - length / 2) <= 1;
                        depth[new Cell(x, y)] = d >= 2 || !inStair ? 3 : d + 1;
                    }
                foreach (var kv in depth)
                {
                    ctx.SetFloor(kv.Key, r.Floor + sign * kv.Value * step);
                    ctx.Level.PartCells[kv.Key] = $"room:{r.Id}/{name}";
                }
                foreach (var c in border) ctx.Used.Add(c);
                r.Features.Add(name);
                ctx.TopCells.AddRange(depth.Where(kv => kv.Value == 3).Select(kv => kv.Key));
                return true;
            }
            return false;
        }

        static bool Platform(RoomCtx ctx, Rng rng) => StairBlock(ctx, rng, +1, "platform");
        static bool Pit(RoomCtx ctx, Rng rng) => StairBlock(ctx, rng, -1, "pit");

        static bool Balcony(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room; var cfg = ctx.Level.Config;
            const int under = 128, deck = 144;
            if (r.Ceil - r.Floor < deck + 96) return false;
            var sides = new List<char> { 'n', 's', 'e', 'w' };
            rng.Shuffle(sides);
            foreach (var side in sides)
                foreach (var lowEnd in rng.Random() < 0.5 ? new[] { true, false } : new[] { false, true })
                {
                    int ulen = side == 'n' || side == 's' ? r.W : r.H, vlen = side == 'n' || side == 's' ? r.H : r.W;
                    int nSteps = under / cfg.StepHeight;
                    if (vlen < 3 + nSteps + 2 || ulen < 6) continue;
                    Cell ToCell(int u, int v) => side == 's' ? new Cell(r.X + u, r.Y + v) : side == 'n' ? new Cell(r.X + u, r.Y + r.H - 1 - v)
                                               : side == 'w' ? new Cell(r.X + v, r.Y + u) : new Cell(r.X + r.W - 1 - v, r.Y + u);
                    int ua = 0, ub = 0, run = -1;
                    for (int u = 0; u <= ulen; u++)
                    {
                        bool ok = u < ulen && Enumerable.Range(0, 3).All(v => r.Contains(ToCell(u, v)) && !ctx.Used.Contains(ToCell(u, v)));
                        if (ok && run < 0) run = u;
                        else if (!ok && run >= 0) { if (u - run > ub - ua) { ua = run; ub = u; } run = -1; }
                    }
                    if (ub - ua < 6) continue;
                    var deckCells = (from u in Enumerable.Range(ua, ub - ua) from v in Enumerable.Range(0, 3) select ToCell(u, v)).ToList();
                    int u0 = lowEnd ? ua : ub - 2;
                    var stairs = new Dictionary<Cell, int>();
                    for (int k = 0; k < nSteps; k++)
                        for (int du = 0; du < 2; du++) stairs[ToCell(u0 + du, 3 + k)] = r.Floor + under - k * cfg.StepHeight;
                    if (!ctx.Free(stairs.Keys) || deckCells.Any(c => ctx.Used.Contains(c) || !r.Contains(c))) continue;
                    foreach (var c in deckCells)
                    {
                        ctx.Level.ReplaceIn(c, r.Slot.Lo, r.Slot.Hi, new[] { new Span(r.Floor, r.Floor + under), new Span(r.Floor + deck, r.Ceil) });
                        ctx.Used.Add(c);
                        ctx.Level.PartCells[c] = $"room:{r.Id}/balcony";
                    }
                    foreach (var kv in stairs) { ctx.SetFloor(kv.Key, kv.Value); ctx.Level.PartCells[kv.Key] = $"room:{r.Id}/balcony stairs"; }
                    r.Features.Add("balcony");
                    ctx.TopCells.AddRange(deckCells);
                    return true;
                }
            return false;
        }

        static bool Crates(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var cells = r.Cells().ToList();
            int want = Math.Min(8, Math.Max(1, cells.Count / 28)), placed = 0;
            rng.Shuffle(cells);
            foreach (var c0 in cells)
            {
                if (placed >= want) break;
                var (w, h) = rng.Choice(new[] { (1, 1), (2, 1), (1, 2), (2, 2), (3, 1), (1, 3) });
                var block = (from dx in Enumerable.Range(0, w) from dy in Enumerable.Range(0, h) select c0.Offset(dx, dy)).ToList();
                var ring = (from dx in Enumerable.Range(-1, w + 2) from dy in Enumerable.Range(-1, h + 2) select c0.Offset(dx, dy)).ToList();
                if (!ctx.Free(ring)) continue;
                int height = rng.Random() < 0.25 ? 64 : 40;
                foreach (var c in block) { ctx.SetFloor(c, r.Floor + height); ctx.Level.PartCells[c] = $"room:{r.Id}/crates"; }
                foreach (var c in ring) ctx.Used.Add(c);
                placed++;
            }
            if (placed > 0) r.Features.Add("crates");
            return placed > 0;
        }

        static readonly Dictionary<string, double> CrateChance = new Dictionary<string, double>
            { ["combat"] = 0.8, ["arena"] = 0.7, ["hub"] = 0.6, ["supply"] = 0.7, ["exit"] = 0.5, ["start"] = 0.4 };

        static readonly Dictionary<string, (string Name, Feature F)[][]> FeaturesByKind = new Dictionary<string, (string, Feature)[][]>
        {
            ["start"] = new[] { new[] { ("pillars", (Feature)Pillars) }, new (string, Feature)[0] },
            ["arena"] = new[] { new[] { ("balcony", (Feature)Balcony), ("pillars", (Feature)Pillars) }, new[] { ("pit", (Feature)Pit), ("pillars", (Feature)Pillars) },
                                new[] { ("platform", (Feature)Platform), ("pillars", (Feature)Pillars) }, new[] { ("balcony", (Feature)Balcony), ("platform", (Feature)Platform) } },
            ["hub"] = new[] { new[] { ("balcony", (Feature)Balcony) }, new[] { ("platform", (Feature)Platform) }, new[] { ("pillars", (Feature)Pillars), ("platform", (Feature)Platform) } },
            ["combat"] = new[] { new[] { ("pillars", (Feature)Pillars) }, new[] { ("platform", (Feature)Platform) }, new[] { ("pit", (Feature)Pit) },
                                 new[] { ("balcony", (Feature)Balcony) }, new (string, Feature)[0] },
            ["supply"] = new[] { new (string, Feature)[0], new[] { ("pillars", (Feature)Pillars) } },
        };

        static double OptionWeight((string Name, Feature F)[] opt, Dictionary<string, double> fw)
        {
            if (opt.Length == 0) return fw.TryGetValue("none", out var n) ? n : 1.0;
            double w = 1;
            foreach (var (name, _) in opt) w *= fw.TryGetValue(name, out var x) ? x : 1.0;
            return w;
        }

        static Cell NearestOpen(Level level, Room room, double fx, double fy) =>
            room.Cells().Where(c => !level.Diag.ContainsKey(c) && level.RoomSpans(c, room).Count > 0)
                .OrderBy(c => Math.Pow(c.X + 0.5 - fx, 2) + Math.Pow(c.Y + 0.5 - fy, 2)).ThenBy(c => c).First();

        static bool FlatCell(Level level, Room room, Cell c)
        {
            var spans = level.RoomSpans(c, room);
            return spans.Count == 1 && spans[0].Floor == room.Floor && !level.Diag.ContainsKey(c) && room.Contains(c);
        }

        static void PlaceLights(Level level, Room room)
        {
            int area = room.W * room.H;
            var spots = new List<(double, double)>();
            if (area >= 140)
            {
                int nx = Math.Max(2, (int)Math.Round(room.W / 9.0)), ny = Math.Max(2, (int)Math.Round(room.H / 9.0));
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) spots.Add(((i + 0.5) / nx, (j + 0.5) / ny));
            }
            else spots.Add((0.5, 0.5));
            var lit = new HashSet<Cell>();
            foreach (var (fx, fy) in spots)
            {
                var c = NearestOpen(level, room, room.X + room.W * fx, room.Y + room.H * fy);
                if (!lit.Add(c)) continue;
                var sp = level.RoomSpans(c, room).Last();
                double z = Math.Min(sp.Ceil - 24, Math.Max(sp.Floor, room.Floor) + 192);
                level.Entities.Add(new Entity("light", new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, z))
                    .With("intensity", spots.Count > 1 ? 250 : 300));
            }
        }

        static Vec3 At(Level level, Room room, Cell c, double zOff) =>
            new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, level.RoomSpans(c, room)[0].Floor + zOff);

        static Entity DoorEntity(Level level, Corridor corr, string kind)
        {
            int r = corr.Radius;
            var path = corr.Path; var floors = corr.Floors;
            (int Score, int I)? best = null;
            foreach (var margin in new[] { r, 1 })
            {
                for (int i = margin + 2; i < path.Count - margin - 2; i++)
                {
                    if (!Layout.Straight(path, i, margin, true) || floors[i - 1] != floors[i] || floors[i] != floors[i + 1]) continue;
                    int score = kind == "secret_door" ? i : Math.Abs(i - path.Count / 2);
                    if (best == null || score < best.Value.Score) best = (score, i);
                }
                if (best != null) break;
            }
            if (best == null) throw new LayoutException($"no spot for {kind} in corridor {corr.Id}");
            int idx = best.Value.I;
            var c = path[idx]; int f = floors[idx], s = level.CellSize;
            int dx = path[idx + 1].X - path[idx].X;
            Vec3 lo, hi;
            if (dx != 0) { lo = new Vec3(c.X * s, (c.Y - r) * s, f); hi = new Vec3((c.X + 1) * s, (c.Y + r + 1) * s, f + corr.Height); }
            else { lo = new Vec3((c.X - r) * s, c.Y * s, f); hi = new Vec3((c.X + r + 1) * s, (c.Y + 1) * s, f + corr.Height); }
            int top = (int)hi.Z;
            for (int x = (int)(lo.X / s); x <= (int)((hi.X - 1) / s); x++)
                for (int y = (int)(lo.Y / s); y <= (int)((hi.Y - 1) / s); y++)
                    foreach (var sp in level.SpansIn(new Cell(x, y), f, f + 1)) top = Math.Max(top, sp.Ceil);
            hi.Z = top;
            var e = new Entity(kind, new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2)) { Bounds = (lo, hi) }.With("corridor", corr.Id);
            if (corr.Key != null) e.With("key", corr.Key);
            return e;
        }

        public static void Run(Level level, Rng rng)
        {
            var cfg = level.Config;
            foreach (var room in level.Rooms)
            {
                if (room.IsConnector) { PlaceLights(level, room); continue; }
                var ctx = new RoomCtx(level, room);
                var options = FeaturesByKind.TryGetValue(room.Kind, out var o) ? o : new[] { new (string, Feature)[0] };
                var ws = options.Select(opt => OptionWeight(opt, cfg.FeatureWeights)).ToList();
                (string, Feature)[] chosen;
                double total = ws.Sum();
                if (total <= 0) chosen = new (string, Feature)[0];
                else
                {
                    double pick = rng.Random() * total; int i = 0;
                    while (i < ws.Count - 1 && pick >= ws[i]) { pick -= ws[i]; i++; }
                    chosen = options[i];
                }
                foreach (var (_, f) in chosen) f(ctx, rng);
                if (rng.Random() < Math.Min(1.0, (CrateChance.TryGetValue(room.Kind, out var cc) ? cc : 0) * cfg.CrateScale)) Crates(ctx, rng);
                Populate(level, room, ctx, rng);
            }
            foreach (var corr in level.Corridors)
            {
                if (corr.Path.Count > 0)
                {
                    var mid = corr.Path[corr.Path.Count / 2];
                    int f = corr.Floors[corr.Path.Count / 2];
                    level.Entities.Add(new Entity("light", new Vec3((mid.X + 0.5) * level.CellSize, (mid.Y + 0.5) * level.CellSize, f + corr.Height - 24))
                        .With("intensity", 220));
                }
                if (corr.Kind == "locked") level.Entities.Add(DoorEntity(level, corr, "door"));
                else if (corr.Kind == "secret") level.Entities.Add(DoorEntity(level, corr, "secret_door"));
            }
        }

        static void Populate(Level level, Room room, RoomCtx ctx, Rng rng)
        {
            PlaceLights(level, room);
            var g = level.Graph; var node = g.Nodes[room.Node];
            double cx = room.X + room.W / 2.0, cy = room.Y + room.H / 2.0;
            if (room.Kind == "start")
            {
                var c = room.Cells().Where(x => FlatCell(level, room, x)).OrderBy(x => Math.Pow(x.X + 0.5 - cx, 2) + Math.Pow(x.Y + 0.5 - cy, 2)).ThenBy(x => x).First();
                int angle = 0;
                if (ctx.Doors.Count > 0)
                {
                    double dx = ctx.Doors.Average(d => d.X) - c.X, dy = ctx.Doors.Average(d => d.Y) - c.Y;
                    angle = ((int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 45.0) * 45 % 360 + 360) % 360;
                }
                level.Entities.Add(new Entity("player_start", At(level, room, c, 32)).With("angle", angle));
            }
            else if (room.Kind == "exit")
            {
                Cell? best = null; double bd = double.MaxValue;
                foreach (var c in room.Cells())
                {
                    var block = new[] { c, c.Offset(1, 0), c.Offset(0, 1), c.Offset(1, 1) };
                    if (!block.All(b => FlatCell(level, room, b))) continue;
                    double d = Math.Pow(c.X + 1 - cx, 2) + Math.Pow(c.Y + 1 - cy, 2);
                    if (d < bd) { bd = d; best = c; }
                }
                if (best == null) throw new LayoutException("no spot for the exit");
                int s = level.CellSize; var e = best.Value;
                var lo = new Vec3(e.X * s, e.Y * s, room.Floor); var hi = new Vec3((e.X + 2) * s, (e.Y + 2) * s, room.Floor + 64);
                level.Entities.Add(new Entity("exit", new Vec3((e.X + 1) * s, (e.Y + 1) * s, room.Floor + 32)) { Bounds = (lo, hi) });
            }
            else if (room.Kind == "key")
            {
                var c = room.Cells().Where(x => FlatCell(level, room, x) && !ctx.Clear.Contains(x))
                            .OrderBy(x => Math.Pow(x.X + 0.5 - cx, 2) + Math.Pow(x.Y + 0.5 - cy, 2)).ThenBy(x => x)
                            .Cast<Cell?>().FirstOrDefault()
                        ?? room.Cells().Where(x => FlatCell(level, room, x)).Cast<Cell?>().FirstOrDefault()
                        ?? throw new LayoutException("no spot for the key");
                level.Entities.Add(new Entity("key", At(level, room, c, 24)).With("color", node.Key));
            }
        }
    }
}
