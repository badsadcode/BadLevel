using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Mega-rooms: a level within the level (spaces/mega.py, Python class MegaRoomSpace).
    ///
    /// A mega-room is a huge, tall space that runs its own small generation inside its footprint. Instead of
    /// rooms and corridors it builds <i>sub-areas</i> joined by open transitions, all in one continuous volume:
    /// platform (a raised block, reached by stairs), deck (a platform on stilts: you can walk underneath it),
    /// catwalk (a bridge between two decks at the same height, open floor below), structure (a large solid
    /// mass: machinery, cargo, a monolith), tier (a terrace along a wall, several stairs up).
    ///
    /// Each sub-area is a semantic region (<c>room.Meta["regions"]</c>) for encounters, diagnostics and editors.
    ///
    /// Not every mega-room is an arena. Its <i>character</i> picks the vocabulary: hangar (vast and mostly
    /// empty: a few big structures, maybe a loading dock), works (an industrial hall: decks, catwalks,
    /// stairs, clutter), terraces (an atrium or great hall: tiers stepping up from the walls).
    ///
    /// Everything is placed incrementally with its stairs; a placement that would cut the floor into islands
    /// or block a doorway is undone, so every region stays reachable.</summary>
    public class MegaSpace : RoomSpace
    {
        public static readonly string[] Styles = { "hangar", "works", "terraces" };

        public MegaSpace()
        {
            Name = "mega";
            Kinds = new[] { "megaroom" };
        }

        /// <summary>A sub-area: name, kind, height, rect (x0, y0, x1, y1) and cells.</summary>
        public sealed class Region
        {
            public string Name, Kind;
            public int Height;
            public int[] Rect;
            public List<Cell> Cells;
        }

        /// <summary>Places sub-areas inside one mega-room, with undo.</summary>
        public sealed class Builder
        {
            readonly RoomCtx ctx;
            readonly Rng rng;
            readonly Room room;
            readonly Level level;
            readonly int f0, top;
            public readonly HashSet<Cell> Occupied = new HashSet<Cell>();   // platform / stair / structure cells
            public readonly List<Region> Regions = new List<Region>();
            public readonly List<Cell> Tops = new List<Cell>();              // walkable tops (snipers, rewards)

            public Builder(RoomCtx ctx, Rng rng)
            {
                this.ctx = ctx;
                this.rng = rng;
                room = ctx.Room;
                level = ctx.Level;
                f0 = room.Floor;
                top = room.Ceil;
            }

            // -- bookkeeping -------------------------------------------------------------------
            Dictionary<Cell, List<Span>> Snapshot(IEnumerable<Cell> cells)
            {
                var snap = new Dictionary<Cell, List<Span>>();
                foreach (var c in cells)
                    snap[c] = level.Spans.TryGetValue(c, out var s) ? new List<Span>(s) : new List<Span>();
                return snap;
            }

            void Restore(Dictionary<Cell, List<Span>> snap)
            {
                foreach (var kv in snap)
                {
                    if (kv.Value.Count > 0) level.Spans[kv.Key] = kv.Value;
                    else level.Spans.Remove(kv.Key);
                }
            }

            bool Ok(IEnumerable<Cell> cells) =>
                cells.All(c => room.Contains(c) && !room.IsDiag(c.X, c.Y) && !level.Diag.ContainsKey(c)   // (any 45-degree cell)
                               && !ctx.Clear.Contains(c) && !ctx.Used.Contains(c) && !Occupied.Contains(c));

            /// <summary>The room's base floor (cells walkable at f0) must remain one connected area.</summary>
            bool FloorConnected()
            {
                var basis = room.Cells().Where(c => level.RoomSpans(c, room).Any(sp => sp.Floor == f0)).ToList();
                if (basis.Count == 0) return false;
                var baseSet = new HashSet<Cell>(basis);
                var seen = new HashSet<Cell> { basis[0] };
                var stack = new Stack<Cell>();
                stack.Push(basis[0]);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    foreach (var n in new[] { c.Offset(1, 0), c.Offset(-1, 0), c.Offset(0, 1), c.Offset(0, -1) })
                        if (baseSet.Contains(n) && seen.Add(n)) stack.Push(n);
                }
                return seen.Count == baseSet.Count;
            }

            void Set(Cell c, params Span[] spans) => level.ReplaceIn(c, room.Slot.Lo, room.Slot.Hi, spans);

            // -- pieces ----------------------------------------------------------------------------
            /// <summary>A 2-wide stair from the base floor up to one side of rect: [(cell, floor)].</summary>
            public List<(Cell Cell, int Floor)> StairsFor(int[] rect, int height, bool deck)
            {
                int x0 = rect[0], y0 = rect[1], x1 = rect[2], y1 = rect[3];
                int n = height / 16;
                var sides = new List<char> { 'n', 's', 'e', 'w' };
                rng.Shuffle(sides);
                foreach (var side in sides)
                {
                    int len = side == 'n' || side == 's' ? x1 - x0 : y1 - y0;
                    int span = Math.Max(1, len - 1);
                    foreach (var off in rng.Sample(Enumerable.Range(0, span).ToList(), Math.Min(3, span)))
                    {
                        var steps = new List<(Cell, int)>();
                        for (int j = 0; j < n; j++)        // j = 0 next to the platform; j = n-1: the landing
                            for (int w = 0; w < 2; w++)
                            {
                                Cell c = side switch
                                {
                                    's' => new Cell(x0 + off + w, y0 - 1 - j),
                                    'n' => new Cell(x0 + off + w, y1 + j),
                                    'w' => new Cell(x0 - 1 - j, y0 + off + w),
                                    _ => new Cell(x1 + j, y0 + off + w),
                                };
                                steps.Add((c, f0 + height - 16 * (j + 1)));
                            }
                        var cells = steps.Select(s => s.Item1).ToList();
                        // the last row is the landing at floor level: kept clear, so nothing blocks the stairs
                        if (Ok(cells) && cells.Distinct().Count() == cells.Count)
                            return steps;
                    }
                }
                return null;
            }

            /// <summary>A raised block (or a deck on stilts) with its stairs, placed somewhere free.
            /// <paramref name="near"/>: try first to line up with that rect (same band, a catwalk's gap away).</summary>
            public int[] Platform(int w, int h, int height, bool deck, string name, bool againstWall = false, int[] near = null)
            {
                var r = room;
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    int x0, y0;
                    if (near != null && attempt < 15)
                    {
                        int nx0 = near[0], ny0 = near[1], nx1 = near[2], ny1 = near[3];
                        int gap = rng.Int(4, 9);
                        if (rng.Random() < 0.5)
                        {
                            x0 = rng.Random() < 0.5 ? nx1 + gap : nx0 - gap - w;
                            y0 = ny0 + rng.Int(-1, Math.Max(-1, ny1 - ny0 - h + 1));
                        }
                        else
                        {
                            y0 = rng.Random() < 0.5 ? ny1 + gap : ny0 - gap - h;
                            x0 = nx0 + rng.Int(-1, Math.Max(-1, nx1 - nx0 - w + 1));
                        }
                    }
                    else if (againstWall)
                    {
                        char side = rng.Choice("nsew".ToCharArray());
                        if (side == 'n' || side == 's')
                        {
                            x0 = rng.Int(r.X + 1, r.X + r.W - 1 - w);
                            y0 = side == 's' ? r.Y + 1 : r.Y + r.H - 1 - h;
                        }
                        else
                        {
                            y0 = rng.Int(r.Y + 1, r.Y + r.H - 1 - h);
                            x0 = side == 'w' ? r.X + 1 : r.X + r.W - 1 - w;
                        }
                    }
                    else
                    {
                        x0 = rng.Int(r.X + 2, Math.Max(r.X + 2, r.X + r.W - 2 - w));
                        y0 = rng.Int(r.Y + 2, Math.Max(r.Y + 2, r.Y + r.H - 2 - h));
                    }
                    var rect = new[] { x0, y0, x0 + w, y0 + h };
                    var cells = new List<Cell>();
                    for (int x = x0; x < x0 + w; x++)
                        for (int y = y0; y < y0 + h; y++) cells.Add(new Cell(x, y));
                    var ring = new List<Cell>();
                    for (int x = x0 - 1; x < x0 + w + 1; x++)
                        for (int y = y0 - 1; y < y0 + h + 1; y++) ring.Add(new Cell(x, y));
                    if (!Ok(cells) || ring.Any(Occupied.Contains)) continue;
                    var steps = StairsFor(rect, height, deck);
                    if (steps == null) continue;
                    var snap = Snapshot(cells.Concat(steps.Select(s => s.Cell)));
                    foreach (var c in cells)
                    {
                        if (deck) Set(c, new Span(f0, f0 + height - 16), new Span(f0 + height, top));
                        else Set(c, new Span(f0 + height, top));
                    }
                    foreach (var (c, f) in steps)
                        if (f > f0) Set(c, new Span(f, top));          // (the landing row stays floor)
                    if (!FloorConnected())
                    {
                        Restore(snap);
                        continue;
                    }
                    Occupied.UnionWith(cells);
                    Occupied.UnionWith(steps.Select(s => s.Cell));
                    Tops.AddRange(cells);
                    Regions.Add(new Region { Name = name, Kind = deck ? "deck" : "platform", Height = height, Rect = rect, Cells = cells });
                    return rect;
                }
                return null;
            }

            /// <summary>A 2-wide bridge between two decks of the same height, if they face each other.</summary>
            public bool Catwalk(Region a, Region b)
            {
                if (a.Height != b.Height || a.Kind != "deck" || b.Kind != "deck") return false;
                int ax0 = a.Rect[0], ay0 = a.Rect[1], ax1 = a.Rect[2], ay1 = a.Rect[3];
                int bx0 = b.Rect[0], by0 = b.Rect[1], bx1 = b.Rect[2], by1 = b.Rect[3];
                int h = a.Height;
                var cells = new List<Cell>();
                int lo = Math.Max(ay0, by0), hi = Math.Min(ay1, by1);      // overlap in y: bridge runs along x
                if (hi - lo >= 2 && (ax1 <= bx0 || bx1 <= ax0))
                {
                    var (xa, xb) = ax1 <= bx0 ? (ax1, bx0) : (bx1, ax0);
                    int yc = FloorDiv(lo + hi, 2) - 1;
                    for (int x = xa; x < xb; x++) { cells.Add(new Cell(x, yc)); cells.Add(new Cell(x, yc + 1)); }
                }
                else
                {
                    lo = Math.Max(ax0, bx0);
                    hi = Math.Min(ax1, bx1);
                    if (hi - lo >= 2 && (ay1 <= by0 || by1 <= ay0))
                    {
                        var (ya, yb) = ay1 <= by0 ? (ay1, by0) : (by1, ay0);
                        int xc = FloorDiv(lo + hi, 2) - 1;
                        for (int y = ya; y < yb; y++) { cells.Add(new Cell(xc, y)); cells.Add(new Cell(xc + 1, y)); }
                    }
                }
                if (cells.Count == 0 || cells.Count > 40) return false;
                if (!cells.All(c => room.Contains(c) && !Occupied.Contains(c) && !room.IsDiag(c.X, c.Y))) return false;
                foreach (var c in cells)   // the floor stays walkable underneath
                    Set(c, new Span(f0, f0 + h - 16), new Span(f0 + h, top));
                Occupied.UnionWith(cells);
                Regions.Add(new Region { Name = $"catwalk {Regions.Count}", Kind = "catwalk", Height = h, Cells = cells });
                return true;
            }

            public bool Structure(int w, int h, int height, string name)
            {
                var r = room;
                for (int t = 0; t < 30; t++)
                {
                    int x0 = rng.Int(r.X + 3, Math.Max(r.X + 3, r.X + r.W - 3 - w));
                    int y0 = rng.Int(r.Y + 3, Math.Max(r.Y + 3, r.Y + r.H - 3 - h));
                    var cells = new List<Cell>();
                    for (int x = x0; x < x0 + w; x++)
                        for (int y = y0; y < y0 + h; y++) cells.Add(new Cell(x, y));
                    var ring = new List<Cell>();
                    for (int x = x0 - 2; x < x0 + w + 2; x++)
                        for (int y = y0 - 2; y < y0 + h + 2; y++) ring.Add(new Cell(x, y));
                    if (!Ok(ring)) continue;
                    var snap = Snapshot(cells);
                    foreach (var c in cells)
                    {
                        if (height < top - f0 - 64) Set(c, new Span(f0 + height, top));
                        else Set(c);
                    }
                    if (!FloorConnected())
                    {
                        Restore(snap);
                        continue;
                    }
                    Occupied.UnionWith(ring);
                    Regions.Add(new Region { Name = name, Kind = "structure", Height = height, Cells = cells });
                    return true;
                }
                return false;
            }

            /// <summary>Terraces rising towards one wall (an amphitheatre / great hall): the band against the
            /// wall is the highest. Built front to back; each band's two stairs stand on the band in front of it
            /// (or on the floor), so every tier is reachable.</summary>
            public int Tiers(int levels)
            {
                var r = room;
                int made = 0;
                var prev = new HashSet<Cell>();                  // the band in front: this band's stairs stand on it
                char side = rng.Choice("nsew".ToCharArray());
                int depth = rng.Choice(new[] { 3, 4 });
                for (int t = 0; t < levels; t++)                 // t = 0: frontmost and lowest
                {
                    int k = levels - 1 - t;                      // bands from the wall: k = 0 against it
                    int height = 64 * (t + 1);
                    int[] rect;
                    if (side == 'n' || side == 's')
                    {
                        int yy = side == 's' ? r.Y + 1 + k * depth : r.Y + r.H - 1 - (k + 1) * depth;
                        rect = new[] { r.X + 1, yy, r.X + r.W - 1, yy + depth };
                    }
                    else
                    {
                        int xx = side == 'w' ? r.X + 1 + k * depth : r.X + r.W - 1 - (k + 1) * depth;
                        rect = new[] { xx, r.Y + 1, xx + depth, r.Y + r.H - 1 };
                    }
                    int x0 = rect[0], y0 = rect[1], x1 = rect[2], y1 = rect[3];
                    var cells = new List<Cell>();
                    for (int x = x0; x < x1; x++)
                        for (int y = y0; y < y1; y++)
                        {
                            var c = new Cell(x, y);
                            if (r.Contains(c) && !r.IsDiag(x, y) && !ctx.Clear.Contains(c)) cells.Add(c);
                        }
                    if (cells.Count < 12) break;
                    // the stairs come up from the front (towards the room's middle)
                    var stairCells = new List<(Cell Cell, int Floor)>();
                    bool ok = true;
                    foreach (var frac in new[] { 0.25, 0.75 })
                        for (int j = 0; j < 4; j++)              // 3 steps of 16 and the landing below them
                            for (int w = 0; w < 2; w++)
                            {
                                int x, y;
                                if (side == 'n' || side == 's')
                                {
                                    x = x0 + (int)((x1 - x0) * frac) + w;
                                    y = side == 's' ? y1 + j : y0 - 1 - j;
                                }
                                else
                                {
                                    y = y0 + (int)((y1 - y0) * frac) + w;
                                    x = side == 'w' ? x1 + j : x0 - 1 - j;
                                }
                                var c = new Cell(x, y);
                                if (!r.Contains(c) || ctx.Clear.Contains(c) || (Occupied.Contains(c) && !prev.Contains(c)))
                                    ok = false;
                                stairCells.Add((c, f0 + height - 16 * (j + 1)));
                            }
                    if (!ok) break;
                    var snap = Snapshot(cells.Concat(stairCells.Select(s => s.Cell)));
                    foreach (var c in cells) Set(c, new Span(f0 + height, top));
                    foreach (var (c, f) in stairCells)
                        if (f > f0 + height - 64)                // the landing keeps the height of the band in front
                            Set(c, new Span(f, top));
                    if (!FloorConnected())
                    {
                        Restore(snap);
                        break;
                    }
                    Occupied.UnionWith(cells);
                    Occupied.UnionWith(stairCells.Select(s => s.Cell));
                    prev = new HashSet<Cell>(cells);
                    Tops.AddRange(cells);
                    Regions.Add(new Region { Name = $"tier {t + 1}", Kind = "tier", Height = height, Rect = rect, Cells = cells });
                    made++;
                }
                return made;
            }

            static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            var (w, h, height) = Layout.RoomSize(cfg, "mega", rng);
            w = Math.Min(w, 40);
            h = Math.Min(h, 40);
            height = Math.Max(384, Math.Min(576, height));
            var room = new Room { Id = -1, Node = node.Id, Kind = node.Kind, X = 0, Y = 0, W = w, H = h,
                                  Floor = floor, Ceil = floor + height, Storey = node.Storey };
            room.Meta["style"] = !string.IsNullOrEmpty(node.Style) ? node.Style : rng.Choice(Styles);
            return room;
        }

        public override void AssignShape(Room room, Node node, GenConfig cfg, Rng rng, bool merged)
        {
            room.Shape = "rect";          // the interior carries the complexity, the outline stays legible
        }

        internal override void Decorate(Level level, Room room, RoomCtx ctx, Rng rng)
        {
            string style = room.Get("style", "works");
            var b = new Builder(ctx, rng);
            b.Regions.Add(new Region { Name = "floor", Kind = "floor", Height = 0 });
            int area = room.W * room.H;
            if (style == "hangar")
            {
                int nk = rng.Int(1, 2);
                for (int k = 0; k < nk; k++)
                    b.Structure(rng.Int(4, 8), rng.Int(4, 8), rng.Choice(new[] { 128, 192, 256, 9999 }), $"structure {k + 1}");
                if (rng.Random() < 0.7)
                    b.Platform(rng.Int(6, 10), rng.Int(3, 4), 64, false, "loading dock", againstWall: true);
            }
            else if (style == "terraces")
            {
                b.Tiers(rng.Int(2, 3));
                if (rng.Random() < 0.5)
                    b.Structure(rng.Int(3, 4), rng.Int(3, 4), 9999, "monolith");
            }
            else  // works
            {
                var decks = new List<Region>();
                int deckHeight = rng.Choice(new[] { 160, 176, 192 });        // one deck level per hall: catwalks line up
                int count = Math.Max(3, area / 160);
                for (int k = 0; k < count; k++)
                {
                    bool deck = rng.Random() < 0.7;
                    int height = deck ? deckHeight : rng.Choice(new[] { 64, 96, 128 });
                    int[] near = deck && decks.Count > 0 && rng.Random() < 0.75 ? rng.Choice(decks).Rect : null;
                    var rect = b.Platform(rng.Int(4, 7), rng.Int(4, 7), height, deck,
                                          $"{(deck ? "deck" : "platform")} {k + 1}", near: near);
                    if (rect != null && deck)
                        decks.Add(b.Regions[b.Regions.Count - 1]);
                }
                for (int i = 0; i < decks.Count; i++)
                    for (int j = i + 1; j < decks.Count; j++)
                        b.Catwalk(decks[i], decks[j]);
            }
            ctx.Used.UnionWith(b.Occupied);
            ctx.TopCells.AddRange(b.Tops);
            room.Meta["regions"] = b.Regions.Select(rg =>
            {
                var d = new Dictionary<string, object> { ["name"] = rg.Name, ["kind"] = rg.Kind, ["height"] = rg.Height };
                if (rg.Rect != null) d["rect"] = rg.Rect.ToList();
                d["size"] = rg.Cells?.Count ?? 0;
                return d;
            }).ToList();
            var regionCells = new Dictionary<string, List<Cell>>();
            foreach (var rg in b.Regions)
                if (rg.Cells != null && rg.Cells.Count > 0) regionCells[rg.Name] = rg.Cells;
            room.Meta["region_cells"] = regionCells;
            room.Features.Add(style);
            // clutter and cover on the floor, as in any room
            if (rng.Random() < Math.Min(1.0, (style == "hangar" ? 0.3 : 0.8) * level.Config.CrateScale))
                NoorLevel.Decorate.FCrates(ctx, rng);
        }

        public override List<int> Regions(Room room) => new List<int> { room.Id };
    }
}
