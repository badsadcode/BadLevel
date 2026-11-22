using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>What a room's features may touch (decorate.py RoomCtx): its cells less the doorway
    /// zones (<see cref="Clear"/>) and what's taken (<see cref="Used"/>). The optional attributes
    /// Python sets on the context later (top_cells, pads, deck_cells, pedestal, prog_clear) are
    /// fields here; "not set" is an empty list (or null for <see cref="Pedestal"/> / <see cref="ProgClear"/>).</summary>
    public sealed class RoomCtx
    {
        public readonly Level Level;
        public readonly Room Room;
        public readonly HashSet<Cell> Used;
        public readonly List<Cell> Doors;
        public readonly HashSet<Cell> Clear = new HashSet<Cell>();
        public List<Cell> TopCells = new List<Cell>();     // high ground (platform tops, tower tops...)
        public List<Cell> Pads = new List<Cell>();         // jump pad cells
        public List<Cell> DeckCells = new List<Cell>();    // a balcony or gallery deck
        public List<Cell> Pedestal;                        // the key's plinth
        public HashSet<Cell> ProgClear;                    // programs.py: furniture doorway zones

        public RoomCtx(Level level, Room room)
        {
            Level = level;
            Room = room;
            Used = new HashSet<Cell>(room.WorldDiag().Keys);
            Used.UnionWith(Decorate.MetaCells(room, "terrace"));
            Used.UnionWith(Decorate.MetaCells(room, "overlook"));      // the view from the booth
            Used.UnionWith(Decorate.MetaCells(room, "reserved"));      // kept clear (a station's moon pool)
            var gallery = Decorate.MetaCells(room, "gallery").ToList();
            if (gallery.Count > 0)    // the upper-storey gallery deck: keep features off it
            {
                Used.UnionWith(gallery);
                DeckCells = gallery;
            }
            Doors = DoorCells();
            foreach (var d in Doors)
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        var c = d.Offset(dx, dy);
                        if (room.Contains(c)) Clear.Add(c);
                    }
        }

        List<Cell> DoorCells()
        {
            var outp = new List<Cell>();
            foreach (var c in Room.Cells())
            {
                if (Room.IsDiag(c.X, c.Y)) continue;
                foreach (var (dx, dy) in Decorate.D4)
                {
                    var n = c.Offset(dx, dy);
                    if (!Room.Contains(n) && Level.SpansIn(n, Room.Floor, Room.Ceil).Any()) { outp.Add(c); break; }
                }
            }
            return outp;
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

    /// <summary>Room features and gameplay entities (decorate.py).
    ///
    /// Features change the column spans inside a room: pillars (2x2 solid columns, cover), platform
    /// (raised block with a staircase, high ground), pit (sunken area with a staircase), balcony
    /// (walkable deck along a wall with open space under it, and a long staircase up), pedestal
    /// (one-step plinth for the key), crates, hazard pools, chasms with a bridge, jump-pad towers.
    /// Every feature keeps the area around doorways clear, so connectivity is preserved; the
    /// validator double-checks this afterwards.</summary>
    public static class Decorate
    {
        public delegate bool Feature(RoomCtx ctx, Rng rng);

        internal static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Cells stored in room.Meta[key] (a list of Cells, (x, y) tuples or [x, y] lists).</summary>
        public static IEnumerable<Cell> MetaCells(Room room, string key)
        {
            if (!room.Meta.TryGetValue(key, out var v) || v == null) yield break;
            if (v is IEnumerable<Cell> cells) { foreach (var c in cells) yield return c; yield break; }
            if (v is IEnumerable seq)
                foreach (var o in seq)
                {
                    switch (o)
                    {
                        case Cell c: yield return c; break;
                        case ValueTuple<int, int> t: yield return new Cell(t.Item1, t.Item2); break;
                        case IList l when l.Count >= 2: yield return new Cell(Convert.ToInt32(l[0]), Convert.ToInt32(l[1])); break;
                    }
                }
        }

        // ------------------------------------------------------------------
        // features
        // ------------------------------------------------------------------
        public static bool FPillars(RoomCtx ctx, Rng rng)
        {
            if (!ctx.Level.Config.Pillars) return false;            // pillars off (GenConfig.Pillars)
            var r = ctx.Room;
            if (Math.Min(r.W, r.H) < 9) return false;
            int spacing = rng.Choice(new[] { 4, 5 });
            int placed = 0;
            for (int py = r.Y + 2; py < r.Y + r.H - 3; py += spacing)
                for (int px = r.X + 2; px < r.X + r.W - 3; px += spacing)
                {
                    var cells = new[] { new Cell(px, py), new Cell(px + 1, py), new Cell(px, py + 1), new Cell(px + 1, py + 1) };
                    // keep one free cell ring around every pillar
                    var ring = new List<Cell>();
                    for (int dx = -1; dx < 3; dx++) for (int dy = -1; dy < 3; dy++) ring.Add(new Cell(px + dx, py + dy));
                    if (ctx.Free(ring))
                    {
                        foreach (var c in cells) ctx.MakeSolid(c);
                        foreach (var c in ring) ctx.Used.Add(c);
                        placed++;
                    }
                }
            if (placed > 0) r.Features.Add("pillars");
            return placed > 0;
        }

        public static bool StairBlock(RoomCtx ctx, Rng rng, int sign, string name)
        {
            var r = ctx.Room;
            if (Math.Min(r.W, r.H) < 9) return false;
            int step = ctx.Level.Config.StepHeight;
            for (int t = 0; t < 40; t++)
            {
                int pw = rng.Int(4, Math.Max(4, Math.Min(8, r.W - 5)));
                int ph = rng.Int(4, Math.Max(4, Math.Min(8, r.H - 5)));
                int px = rng.Int(r.X + 2, r.X + r.W - 2 - pw);
                int py = rng.Int(r.Y + 2, r.Y + r.H - 2 - ph);
                var rect = new List<Cell>();
                for (int y = py; y < py + ph; y++) for (int x = px; x < px + pw; x++) rect.Add(new Cell(x, y));
                var border = new List<Cell>();
                for (int y = py - 1; y < py + ph + 1; y++) for (int x = px - 1; x < px + pw + 1; x++) border.Add(new Cell(x, y));
                if (!ctx.Free(border)) continue;
                // stairs go in from one side: two strips of 3 cells, then the top
                char side = rng.Choice(new[] { 'n', 's', 'e', 'w' });
                var depth = new List<(Cell C, int Lvl)>();
                foreach (var c in rect)
                {
                    int x = c.X, y = c.Y;
                    int d = side == 'w' ? x - px : side == 'e' ? px + pw - 1 - x : side == 's' ? y - py : py + ph - 1 - y;
                    int along = side == 'w' || side == 'e' ? y - py : x - px;
                    int length = side == 'w' || side == 'e' ? ph : pw;
                    int centre = length / 2;
                    bool inStair = Math.Abs(along - centre) <= 1;
                    depth.Add((c, d >= 2 || !inStair ? 3 : d + 1));
                }
                foreach (var (c, lvl) in depth) ctx.SetFloor(c, r.Floor + sign * lvl * step);
                foreach (var c in border) ctx.Used.Add(c);
                ctx.Room.Features.Add(name);
                ctx.TopCells = ctx.TopCells.Concat(depth.Where(p => p.Lvl == 3).Select(p => p.C)).ToList();
                return true;
            }
            return false;
        }

        /// <summary>A hazard pool: a rectangle of floor one step down, flooded with slime or lava. One
        /// step, so you can always walk out (it hurts, it never traps); a free ring around it.</summary>
        public static bool FHazard(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            if (Math.Min(r.W, r.H) < 9) return false;
            int step = ctx.Level.Config.StepHeight;
            for (int t = 0; t < 40; t++)
            {
                int pw = rng.Int(3, Math.Max(3, Math.Min(9, r.W - 5)));
                int ph = rng.Int(3, Math.Max(3, Math.Min(9, r.H - 5)));
                int px = rng.Int(r.X + 2, r.X + r.W - 2 - pw);
                int py = rng.Int(r.Y + 2, r.Y + r.H - 2 - ph);
                var rect = new List<Cell>();
                for (int y = py; y < py + ph; y++) for (int x = px; x < px + pw; x++) rect.Add(new Cell(x, y));
                var border = new List<Cell>();
                for (int y = py - 1; y < py + ph + 1; y++) for (int x = px - 1; x < px + pw + 1; x++) border.Add(new Cell(x, y));
                if (!ctx.Free(border)) continue;
                foreach (var c in rect)
                {
                    ctx.SetFloor(c, r.Floor - step);
                    ctx.Level.Hazard[c] = ctx.Level.Config.HazardKind;
                }
                foreach (var c in border) ctx.Used.Add(c);
                r.Features.Add("hazard");
                return true;
            }
            return false;
        }

        /// <summary>A jump-pad tower (arena high ground): a block 64-160 units tall with no stairs, and a
        /// 2x2 jump pad one cell off one face that throws the player onto it. The way down is a drop (a
        /// one-way link up, so the validator knows the top is reachable and the drop brings you back).</summary>
        public static bool FTower(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var level = ctx.Level;
            if (Math.Min(r.W, r.H) < 10) return false;
            var hs = new[] { 64, 80, 96, 128, 160 }.Where(h => r.Ceil - r.Floor >= h + 152).ToList();  // the arc: 64 over the top + a player
            if (hs.Count == 0) return false;
            int s = level.CellSize;
            for (int t = 0; t < 40; t++)
            {
                int bw = rng.Int(3, 5), bh = rng.Int(3, 5);
                int px = rng.Int(r.X + 2, r.X + r.W - 2 - bw);
                int py = rng.Int(r.Y + 2, r.Y + r.H - 2 - bh);
                char side = rng.Choice(new[] { 'n', 's', 'e', 'w' });
                int padX, padY;
                if (side == 'e') { padX = px + bw + 1; padY = py + bh / 2 - 1; }
                else if (side == 'w') { padX = px - 3; padY = py + bh / 2 - 1; }
                else if (side == 'n') { padX = px + bw / 2 - 1; padY = py + bh + 1; }
                else { padX = px + bw / 2 - 1; padY = py - 3; }
                var rect = new List<Cell>();
                for (int x = px; x < px + bw; x++) for (int y = py; y < py + bh; y++) rect.Add(new Cell(x, y));
                var pad = new List<Cell>();
                foreach (var x in new[] { padX, padX + 1 }) foreach (var y in new[] { padY, padY + 1 }) pad.Add(new Cell(x, y));
                var zone = new HashSet<Cell>();
                foreach (var c in rect.Concat(pad))
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) zone.Add(c.Offset(dx, dy));
                if (!ctx.Free(zone)) continue;
                int h = rng.Choice(hs);
                foreach (var c in rect)
                {
                    ctx.SetFloor(c, r.Floor + h);
                    level.PartCells[c] = $"room:{r.Id}/tower";
                }
                ctx.Used.UnionWith(zone);
                double pcx = (padX + 1) * s, pcy = (padY + 1) * s;          // the pad's centre (a cell corner)
                // land 1.5 cells in from the face the pad looks at
                double tx = side == 'e' ? (px + bw) * s - 48 : side == 'w' ? px * s + 48 : pcx;
                double ty = side == 'n' ? (py + bh) * s - 48 : side == 's' ? py * s + 48 : pcy;
                var target = new Vec3(tx, ty, r.Floor + h + 24);
                var e = new Entity("jumppad", new Vec3(pcx, pcy, r.Floor + 8))
                {
                    Bounds = (new Vec3(padX * s, padY * s, r.Floor), new Vec3((padX + 2) * s, (padY + 2) * s, r.Floor + 16)),
                };
                e.With("target", target).With("rise", h).With("room", r.Id);
                level.Entities.Add(e);
                var a = new Vec3((padX + 0.5) * s, (padY + 0.5) * s, r.Floor + 24);
                level.Links.Add(new Link { Kind = "jumppad", A = a, B = target, TwoWay = false, Interaction = "jump", Accessible = false, Room = r.Id });
                ctx.TopCells = ctx.TopCells.Concat(rect).ToList();
                ctx.Pads = ctx.Pads.Concat(pad).ToList();
                r.Features.Add("tower");
                if (!(r.Meta.TryGetValue("towers", out var tw) && tw is List<Dictionary<string, object>> towers))
                    r.Meta["towers"] = towers = new List<Dictionary<string, object>>();
                towers.Add(new Dictionary<string, object>
                {
                    ["rect"] = new List<int> { px, py, bw, bh }, ["height"] = h,
                    ["pad"] = new List<int> { padX, padY }, ["side"] = side.ToString(),
                });
                return true;
            }
            return false;
        }

        /// <summary>Deeper than any drop the validator allows (max_drop 288): a fall, not a route.</summary>
        public static readonly int ChasmDepth = 384;

        /// <summary>A chasm across the room with spikes at the bottom and a bridge over it (dungeons): a
        /// band from wall to wall, 4-7 cells deep, ChasmDepth down, and a bridge 2 cells wide across it.
        /// Falling in is death, never a softlock: it's deeper than any drop the validator walks, so the
        /// only way across is the bridge. The band keeps clear of doorways, and nothing may be under it
        /// or beside its walls down there. Piers in its way stay standing, rising out of the abyss.</summary>
        public static bool FChasm(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var level = ctx.Level;
            if (Math.Min(r.W, r.H) < 10) return false;
            int D = ChasmDepth;
            // 2 cells in front of every doorway stay floor
            var nearDoor = new HashSet<Cell>();
            foreach (var d in ctx.Doors)
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) nearDoor.Add(d.Offset(dx, dy));
            (List<Cell> Pit, HashSet<Cell> Bridge, int P, int Bw, bool AlongX, int B0)? best = null;
            var roomSpan = new Span(r.Floor, r.Ceil);
            for (int t = 0; t < 30; t++)                         // the biggest pit of 30 tries
            {
                bool alongX = rng.Random() < 0.5;                // the band runs along x (spans the room's height)
                int L = alongX ? r.W : r.H;
                if (L < 10) continue;
                int bw = rng.Int(4, Math.Min(7, L - 6));
                int p = rng.Int(alongX ? r.X + 3 : r.Y + 3, (alongX ? r.X : r.Y) + L - 3 - bw);
                var rows = r.Cells().Where(c => p <= (alongX ? c.X : c.Y) && (alongX ? c.X : c.Y) < p + bw).ToList();
                // piers and wall members (solid) stay standing in the chasm; the chasm takes the band's
                // plain floor; cells by a doorway, half cells and raised bits stay standing as ledges
                var band = rows.Where(c => level.Spans.TryGetValue(c, out var sp) && sp.Count > 0).ToList();
                var plain = band.Where(c => !nearDoor.Contains(c) && !level.Diag.ContainsKey(c))
                                .Where(c => { var rs = level.RoomSpans(c, r); return rs.Count == 1 && rs[0].Equals(roomSpan); }).ToList();
                if (band.Count == 0 || plain.Count < 0.6 * band.Count) continue;
                // the bridge: 2 cells wide, across the band, not at its ends
                var across = band.Select(c => alongX ? c.Y : c.X).Distinct().OrderBy(v => v).ToList();
                if (across.Count < 6) continue;
                int b0 = rng.Int(across[0] + 2, across[across.Count - 1] - 3);
                var bridge = new HashSet<Cell>(band.Where(c => { int v = alongX ? c.Y : c.X; return b0 <= v && v <= b0 + 1; }));
                if (!bridge.All(c => { var rs = level.RoomSpans(c, r); return rs.Count > 0 && rs[0].Floor == r.Floor; }))
                    continue;                                    // the bridge is plain floor all the way across
                var pit = plain.Where(c => !bridge.Contains(c)).ToList();
                var bs = new HashSet<Cell>(band);
                bool ok = pit.All(c => !level.SpansIn(c, r.Floor - D - 16, r.Floor - 64).Any());
                foreach (var c in pit)                           // the pit's walls: nothing open down there
                    foreach (var (dx, dy) in D4)
                    {
                        var n = c.Offset(dx, dy);
                        if (!bs.Contains(n) && !r.Contains(n) && level.SpansIn(n, r.Floor - D - 16, r.Floor).Any()) ok = false;
                    }
                if (ok && (best == null || pit.Count > best.Value.Pit.Count))
                    best = (pit, bridge, p, bw, alongX, b0);
            }
            if (best == null) return false;
            var (bPit, bBridge, bp, bbw, balongX, bb0) = best.Value;
            string tag = $"room:{r.Id}";
            foreach (var c in bPit)
            {
                level.ReplaceIn(c, r.Floor - 64, r.Ceil, new[] { new Span(r.Floor - D, r.Ceil) });
                if (level.Owners.TryGetValue(c, out var os))
                    level.Owners[c] = os.Select(o => new Owner(o.Name == tag ? r.Floor - D : o.Lo, o.Hi, o.Name)).ToList();
                else
                    level.Owners[c] = new List<Owner>();
                ctx.Used.Add(c);
                level.Hazard[c] = "spikes";
                level.PartCells[c] = $"room:{r.Id}/chasm";
            }
            foreach (var c in bBridge)
            {
                ctx.Used.Add(c);
                level.PartCells[c] = $"room:{r.Id}/bridge";
            }
            r.Features.Add("chasm");
            r.Meta["chasm"] = new Dictionary<string, object>
            {
                ["band"] = new List<int> { bp, bbw }, ["axis"] = balongX ? "x" : "y", ["bridge"] = bb0, ["depth"] = D,
            };
            return true;
        }

        public static bool FPlatform(RoomCtx ctx, Rng rng) => StairBlock(ctx, rng, +1, "platform");

        public static bool FPit(RoomCtx ctx, Rng rng) => StairBlock(ctx, rng, -1, "pit");

        public static bool FBalcony(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var cfg = ctx.Level.Config;
            const int under = 128, deck = 144;
            if (r.Ceil - r.Floor < deck + 96) return false;
            var sides = new List<char> { 'n', 's', 'e', 'w' };
            rng.Shuffle(sides);
            foreach (var side in sides)
            {
                foreach (var stairOnLowEnd in rng.Sample(new[] { true, false }, 2))
                {
                    // local frame: u runs along the balcony wall, v away from it
                    int ulen, vlen;
                    if (side == 'n' || side == 's') { ulen = r.W; vlen = r.H; }
                    else { ulen = r.H; vlen = r.W; }
                    int nSteps = under / cfg.StepHeight;  // 8
                    if (vlen < 3 + nSteps + 2 || ulen < 6) continue;

                    Cell ToCell(int u, int v)
                    {
                        if (side == 's') return new Cell(r.X + u, r.Y + v);
                        if (side == 'n') return new Cell(r.X + u, r.Y + r.H - 1 - v);
                        if (side == 'w') return new Cell(r.X + v, r.Y + u);
                        return new Cell(r.X + r.W - 1 - v, r.Y + u);
                    }

                    // longest straight stretch of wall where a 3-deep deck fits the room shape
                    var okU = new List<bool>();
                    for (int u = 0; u < ulen; u++)
                        okU.Add(Enumerable.Range(0, 3).All(v => r.Contains(ToCell(u, v)) && !ctx.Used.Contains(ToCell(u, v))));
                    okU.Add(false);
                    int ua = 0, ub = 0;
                    int? run = null;
                    for (int u = 0; u < okU.Count; u++)
                    {
                        bool ok = okU[u];
                        if (ok && run == null) run = u;
                        else if (!ok && run != null)
                        {
                            if (u - run.Value > ub - ua) { ua = run.Value; ub = u; }
                            run = null;
                        }
                    }
                    if (ub - ua < 6) continue;
                    var deckCells = new List<Cell>();
                    for (int u = ua; u < ub; u++) for (int v = 0; v < 3; v++) deckCells.Add(ToCell(u, v));
                    int u0 = stairOnLowEnd ? ua : ub - 2;
                    var stairs = new List<(Cell C, int F)>();
                    var stairIdx = new Dictionary<Cell, int>();
                    for (int k = 0; k < nSteps; k++)
                        for (int du = 0; du < 2; du++)
                        {
                            var c = ToCell(u0 + du, 3 + k);
                            int f = r.Floor + under - k * cfg.StepHeight;
                            if (stairIdx.TryGetValue(c, out var at)) stairs[at] = (c, f);
                            else { stairIdx[c] = stairs.Count; stairs.Add((c, f)); }
                        }
                    // deck cells may sit over doorways (corridors pass underneath), stairs may not
                    if (!ctx.Free(stairs.Select(x => x.C)) || deckCells.Any(c => ctx.Used.Contains(c) || !r.Contains(c))) continue;
                    foreach (var c in deckCells)
                    {
                        ctx.Level.ReplaceIn(c, r.Slot.Lo, r.Slot.Hi, new[] { new Span(r.Floor, r.Floor + under), new Span(r.Floor + deck, r.Ceil) });
                        ctx.Used.Add(c);
                    }
                    foreach (var (c, f) in stairs) ctx.SetFloor(c, f);
                    ctx.Room.Features.Add("balcony");
                    ctx.DeckCells = deckCells;
                    return true;
                }
            }
            return false;
        }

        public static bool FPedestal(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var avoid = new HashSet<Cell>(ctx.Clear);
            avoid.UnionWith(ctx.Used);
            var block = FlatBlock(ctx, r.X + r.W / 2.0, r.Y + r.H / 2.0, avoid);
            if (block == null) return false;
            foreach (var c in block) ctx.SetFloor(c, r.Floor + ctx.Level.Config.StepHeight);
            r.Features.Add("pedestal");
            ctx.Pedestal = block;
            return true;
        }

        /// <summary>Scatter crates / low walls: 40-unit blocks hide a crouching player (low cover), some
        /// 64-unit ones block sight entirely. Each keeps a walkable ring around it.</summary>
        public static bool FCrates(RoomCtx ctx, Rng rng)
        {
            var r = ctx.Room;
            var cells = r.Cells().ToList();
            int area = cells.Count;
            int want = Math.Min(8, Math.Max(1, area / 28));
            rng.Shuffle(cells);
            int placed = 0;
            var sizes = new[] { (1, 1), (2, 1), (1, 2), (2, 2), (3, 1), (1, 3) };
            foreach (var c0 in cells)
            {
                if (placed >= want) break;
                var (w, h) = rng.Choice(sizes);
                var block = new List<Cell>();
                for (int dx = 0; dx < w; dx++) for (int dy = 0; dy < h; dy++) block.Add(c0.Offset(dx, dy));
                var ring = new List<Cell>();
                for (int dx = -1; dx < w + 1; dx++) for (int dy = -1; dy < h + 1; dy++) ring.Add(c0.Offset(dx, dy));
                if (!ctx.Free(ring)) continue;
                int height = rng.Random() < 0.25 ? 64 : 40;
                foreach (var c in block) ctx.SetFloor(c, r.Floor + height);
                foreach (var c in ring) ctx.Used.Add(c);
                placed++;
            }
            if (placed > 0) r.Features.Add("crates");
            return placed > 0;
        }

        public static readonly Dictionary<string, double> CrateChance = new Dictionary<string, double>
            { ["combat"] = 0.8, ["arena"] = 0.7, ["hub"] = 0.6, ["supply"] = 0.7, ["exit"] = 0.5, ["start"] = 0.4 };

        static List<Feature> Opt(params Feature[] fs) => fs.ToList();

        public static readonly Dictionary<string, List<List<Feature>>> FeaturesByKind = new Dictionary<string, List<List<Feature>>>
        {
            ["start"] = new List<List<Feature>> { Opt(FPillars), Opt() },
            ["arena"] = new List<List<Feature>> { Opt(FBalcony, FPillars), Opt(FPit, FPillars), Opt(FPlatform, FPillars), Opt(FBalcony, FPlatform) },
            ["hub"] = new List<List<Feature>> { Opt(FBalcony), Opt(FPlatform), Opt(FPillars, FPlatform) },
            ["combat"] = new List<List<Feature>> { Opt(FPillars), Opt(FPlatform), Opt(FPit), Opt(FBalcony), Opt() },
            ["supply"] = new List<List<Feature>> { Opt(), Opt(FPillars) },
            ["key"] = new List<List<Feature>> { Opt(FPedestal) },
            ["secret"] = new List<List<Feature>> { Opt() },
            ["exit"] = new List<List<Feature>> { Opt() },
        };

        static readonly Dictionary<string, Feature> FeatureNames = new Dictionary<string, Feature>
        {
            ["f_pillars"] = FPillars, ["f_platform"] = FPlatform, ["f_pit"] = FPit, ["f_balcony"] = FBalcony,
            ["f_pedestal"] = FPedestal, ["f_crates"] = FCrates, ["f_hazard"] = FHazard, ["f_tower"] = FTower,
            ["f_chasm"] = FChasm,
        };

        /// <summary>A feature's Python name (<c>f.__name__</c>, e.g. "f_pillars"): the feature-weight key is
        /// this without the "f_" prefix.</summary>
        public static string FeatureName(Feature f)
        {
            foreach (var kv in FeatureNames)
                if (kv.Value.Method == f.Method) return kv.Key;
            return "f_" + f.Method.Name.TrimStart('F').ToLowerInvariant();
        }

        // ------------------------------------------------------------------
        // entities
        // ------------------------------------------------------------------
        public static Cell? PickFree(RoomCtx ctx, Rng rng, HashSet<Cell> taken, bool floorOnly = true)
        {
            var r = ctx.Room;
            var cands = new List<Cell>();
            foreach (var c in r.Cells())
            {
                var spans = ctx.Level.RoomSpans(c, r);
                if (spans.Count != 1 || taken.Contains(c) || ctx.Clear.Contains(c) || ctx.Level.Diag.ContainsKey(c)) continue;
                if (floorOnly && spans[0].Floor != r.Floor) continue;
                // pillar rings / platform borders (ctx.Used) are fine to stand on if flat
                cands.Add(c);
            }
            if (cands.Count == 0) return null;
            var pick = rng.Choice(cands);
            taken.Add(pick);
            return pick;
        }

        public static bool FlatCell(Level level, Room room, Cell c)
        {
            var spans = level.RoomSpans(c, room);
            return spans.Count == 1 && spans[0].Floor == room.Floor && !level.Diag.ContainsKey(c) && room.Contains(c);
        }

        /// <summary>2x2 block of flat floor cells nearest to the (fractional) cell point fx, fy.</summary>
        public static List<Cell> FlatBlock(RoomCtx ctx, double fx, double fy, ICollection<Cell> avoid = null)
        {
            (double D, List<Cell> Cells)? best = null;
            foreach (var c0 in ctx.Room.Cells())
            {
                int x = c0.X, y = c0.Y;
                var cells = new List<Cell> { new Cell(x, y), new Cell(x + 1, y), new Cell(x, y + 1), new Cell(x + 1, y + 1) };
                if (cells.All(c => FlatCell(ctx.Level, ctx.Room, c) && (avoid == null || !avoid.Contains(c))))
                {
                    double d = Math.Pow(x + 1 - fx, 2) + Math.Pow(y + 1 - fy, 2);
                    if (best == null || d < best.Value.D) best = (d, cells);
                }
            }
            return best?.Cells;
        }

        public static Cell NearestOpen(Level level, Room room, double fx, double fy, bool single = false)
        {
            Cell? best = null;
            double bd = 0;
            foreach (var c in room.Cells())
            {
                if (level.Diag.ContainsKey(c)) continue;
                var rs = level.RoomSpans(c, room);
                if (rs.Count == 0 || (single && rs.Count != 1)) continue;
                double d = Math.Pow(c.X + 0.5 - fx, 2) + Math.Pow(c.Y + 0.5 - fy, 2);
                if (best == null || d < bd) { best = c; bd = d; }
            }
            if (best == null) throw new InvalidOperationException($"room {room.Id} has no open cell");   // Python: min() of nothing
            return best.Value;
        }

        public static Vec3 At(Level level, Room room, Cell c, bool top = false, double zOff = 24.0)
        {
            var spans = level.RoomSpans(c, room);
            int f = top ? spans[spans.Count - 1].Floor : spans[0].Floor;
            return new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, f + zOff);
        }

        public static Entity DoorEntity(Level level, Corridor corr, string kind)
        {
            var cfg = level.Config;
            int r = corr.Radius;
            var path = corr.Path;
            var floors = corr.Floors;
            (int Score, int I)? best = null;
            for (int i = r + 2; i < path.Count - r - 2; i++)
            {
                if (Layout.Straight(path, i, r, true) && floors[i - 1] == floors[i] && floors[i] == floors[i + 1])
                {
                    // secret doors hug the visible room side, locked doors sit mid way
                    int score = kind == "secret_door" ? i : Math.Abs(i - path.Count / 2);
                    if (best == null || score < best.Value.Score) best = (score, i);
                }
            }
            if (best == null)
            {
                // a short wide corridor (5 wide between close rooms) leaves no room for the full margins:
                // any level cell on a straight run will do (the validator still checks the door seals it)
                for (int i = 2; i < path.Count - 2; i++)
                    if (Layout.Straight(path, i, 1, true) && floors[i - 1] == floors[i] && floors[i] == floors[i + 1])
                    {
                        int score = kind == "secret_door" ? i : Math.Abs(i - path.Count / 2);
                        if (best == null || score < best.Value.Score) best = (score, i);
                    }
            }
            if (best == null && cfg.Packing >= 0.5)
            {
                // packed: the corridor is a short straight passage through a wall; the door goes on any
                // level cell of it
                for (int i = 0; i < path.Count; i++)
                {
                    var nb = new List<int>();
                    foreach (var j in new[] { i - 1, i + 1 }) if (0 <= j && j < path.Count) nb.Add(floors[j]);
                    if ((0 < i && i < path.Count - 1 && nb.All(f => f == floors[i])) || (path.Count == 2 && i == 0))
                    {
                        best = (Math.Abs(i - path.Count / 2), i);
                        break;
                    }
                }
            }
            if (best == null) throw new LayoutException($"no spot for {kind} in corridor {corr.Id}");
            int idx = best.Value.I;
            var c = path[idx];
            int fl = floors[idx];
            int dx = path[idx + 1].X - path[idx].X;
            int s = level.CellSize;
            Vec3 lo, hi;
            if (dx != 0)   // corridor runs along X -> door spans Y
            {
                lo = new Vec3(c.X * s, (c.Y - r) * s, fl);
                hi = new Vec3((c.X + 1) * s, (c.Y + r + 1) * s, fl + corr.Height);
            }
            else
            {
                lo = new Vec3((c.X - r) * s, c.Y * s, fl);
                hi = new Vec3((c.X + r + 1) * s, (c.Y + 1) * s, fl + corr.Height);
            }
            // a door fills the whole opening it closes: where the carved corridor is taller than its
            // nominal height (stair corridors), the door reaches its real ceiling
            double top = hi.Z;
            for (int x = (int)Math.Floor(lo.X / s); x <= (int)Math.Floor((hi.X - 1) / s); x++)
                for (int y = (int)Math.Floor(lo.Y / s); y <= (int)Math.Floor((hi.Y - 1) / s); y++)
                    foreach (var sp in level.SpansIn(new Cell(x, y), fl, fl + 1)) top = Math.Max(top, sp.Ceil);
            hi.Z = top;
            var pos = new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2);
            var e = new Entity(kind, pos) { Bounds = (lo, hi) }.With("corridor", corr.Id);
            if (corr.Key != null) e.With("key", corr.Key);
            return e;
        }

        /// <summary>Underground levels start at the surface: the start room is open to the sky (a hatch or
        /// pit you came down into), where nothing is stacked above it.</summary>
        public static void SurfaceEntrance(Level level)
        {
            var start = level.Rooms.FirstOrDefault(r => r.Kind == "start");
            if (start == null) return;
            int opened = 0;
            foreach (var c in start.Cells())
            {
                var spans = level.RoomSpans(c, start);
                if (spans.Count > 0 && level.Spans[c][level.Spans[c].Count - 1].Equals(spans[spans.Count - 1]))
                {
                    level.Sky.Add(c);
                    opened++;
                }
            }
            if (opened > 0)
            {
                start.Meta["surface"] = true;
                start.Features.Add("surface");
            }
        }

        /// <summary>decorate(): each room's space type adds its features, then its contents; then the
        /// corridors' contents. (Two passes over the rooms, in this order, so the random sequence is
        /// stable.) With an ArchStyle, a room's cross-section puts its piers in before the features and
        /// shapes its ceilings after them (Architecture; it has its own random stream).</summary>
        public static void Run(Level level, Rng rng)
        {
            var cfg = level.Config;
            var ctxs = new Dictionary<int, RoomCtx>();
            if (cfg.Underground) SurfaceEntrance(level);
            if (cfg.Programs) Programs.Assign(level);                 // facility: sectors and room programs
            var sections = Architecture.Plan(level);                  // empty without an arch_style
            var tiers = sections.Count > 0 || !string.IsNullOrEmpty(cfg.ArchStyle)
                ? Architecture.RankTiers(level) : new Dictionary<int, string>();
            // one deliberate landmark per level, in its biggest space (Landmark)
            var grand = level.Rooms.Where(r => tiers.TryGetValue(r.Id, out var t) && t == "grand").ToList();
            int? landmarkRoom = null;
            if (grand.Count > 0 && cfg.ArchLandmark)
            {
                Room big = null;
                long bv = 0;
                foreach (var r in grand)
                {
                    long v = (long)r.Cells().Count() * (r.Ceil - r.Floor);
                    if (big == null || v > bv) { big = r; bv = v; }
                }
                landmarkRoom = big.Id;
            }
            foreach (var room in level.Rooms)
            {
                var ctx = new RoomCtx(level, room);
                ctxs[room.Id] = ctx;
                sections.TryGetValue(room.Id, out var sec);
                int piers = sec != null && level.Config.Pillars ? Architecture.PlacePiers(level, room, sec, ctx) : 0;
                if (tiers.TryGetValue(room.Id, out var tier))
                {
                    var wall = Walls.Articulate(level, room, ctx, sec, tier);
                    if (wall != null && wall.Count > 0) room.Meta["walls"] = wall;
                }
                if (room.Id == landmarkRoom)
                    Landmark.Place(level, room, ctx, sec, cfg.ArchStyle,
                                   Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/landmark/{room.Id}"));
                Spaces.SpaceFor(room.Kind).Decorate(level, room, ctx, rng);
                if (WallsDress(room)) Walls.TidyDress(level, room);
                if (sec != null)
                {
                    var lowered = Architecture.ApplyCeilings(level, room, sec);
                    Walls.ApplySplays(level, room, sec);
                    var arch = new Dictionary<string, object>();
                    foreach (var kv in sec.Info)
                        if (kv.Key != "placed" && kv.Key != "splays") arch[kv.Key] = kv.Value;
                    arch["style"] = cfg.ArchStyle;
                    arch["profile"] = sec.Name;
                    arch["axis"] = sec.Frame.AlongX ? "x" : "y";
                    arch["piers"] = piers;
                    arch["shaped_cells"] = lowered;
                    room.Meta["arch"] = arch;
                    room.Features.Add(sec.Name);
                }
            }
            int maxDepth = level.Graph.Nodes.Count > 0 ? level.Graph.Nodes.Max(n => n.Depth) : 0;
            var info = new Dictionary<string, object> { ["max_depth"] = maxDepth != 0 ? maxDepth : 1 };
            foreach (var room in level.Rooms)
                Spaces.SpaceFor(room.Kind).Populate(level, room, ctxs[room.Id], rng, info);
            foreach (var corr in level.Corridors)
                CorridorSpace.Populate(level, corr, rng, info);
            if (cfg.Mode == "arena")
                Arena.Populate(level, ctxs, rng);                     // deathmatch: spawns instead
            Dungeon.AddSkylights(level);                              // grates to the sky (cfg.Skylights; after the vaults)
            Dungeon.AddTraps(level);                                  // trap markers in corridors (cfg.Traps)
            if (tiers.Count > 0)
            {
                Portals.Build(level, tiers);                          // frames the openings, last: it sees doors and entities
                foreach (var room in level.Rooms)                     // a jamb may have taken a base cell
                    if (WallsDress(room)) Walls.TidyDress(level, room);
            }
        }

        static bool WallsDress(Room room)
        {
            if (!room.Meta.TryGetValue("walls", out var w) || !(w is IDictionary d) || !d.Contains("dress")) return false;
            var v = d["dress"];
            return v switch { null => false, bool b => b, int i => i != 0, double x => x != 0, _ => true };
        }
    }
}
