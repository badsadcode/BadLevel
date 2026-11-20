using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Open areas: courtyards, plazas, yards. Spaces under the sky (spaces/open_area.py).
    ///
    /// What makes an open area different from a big room:
    /// <list type="bullet">
    /// <item><b>sky instead of a ceiling</b>: the column is open up to sky height and its top face is sky
    /// (a Quake <c>sky</c> texture in the .map, <c>bl_sky</c> in the .glb).</item>
    /// <item><b>the perimeter reads as building fronts</b>: pilasters in a regular rhythm along the walls,
    /// doorways where corridors arrive, windows from neighbouring spaces.</item>
    /// <item><b>contents are a choice, including nothing</b>: an empty yard (breathing room), a kiosk or
    /// monument in the middle, a colonnade, a sunken plaza, a raised terrace, or an arcade with a walkway
    /// on top. Recipes weight these like any room feature.</item>
    /// </list>
    /// It reuses the room machinery (placement, shapes, gameplay contents) and only changes what makes it
    /// outdoors. Turned on by <see cref="GenConfig.OpenAreas"/> (recipes: openness), off by default.</summary>
    public class OpenAreaSpace : RoomSpace
    {
        protected override int? LightMax => 224;       // lamps on posts and walls, not up at the sky
        protected override int? LightPower => 400;

        public OpenAreaSpace()
        {
            Name = "open_area";
            Kinds = new[] { "courtyard" };
        }

        static readonly (int, int)[] D4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Pilasters every few cells along the yard's walls: building fronts, not room walls.
        /// Never in front of a doorway, never on a 45-degree corner.</summary>
        public static bool FFacade(RoomCtx ctx, Rng rng)
        {
            var room = ctx.Room;
            var level = ctx.Level;
            int step = rng.Choice(new[] { 3, 4 });
            int placed = 0;
            foreach (var c in room.Cells().ToList())
            {
                if (ctx.Used.Contains(c) || ctx.Clear.Contains(c) || room.IsDiag(c.X, c.Y)) continue;
                // a cell against the outer wall, on one of the yard's straight edges
                var outside = D4.Where(d => !room.Contains(c.X + d.Item1, c.Y + d.Item2)
                                            && !level.SpansIn(c.Offset(d.Item1, d.Item2), room.Floor, room.Ceil).Any()).ToList();
                if (outside.Count != 1) continue;
                var (dx, dy) = outside[0];
                int along = dx != 0 ? c.Y : c.X;
                if (Mod(along, step) != 0) continue;
                // keep a walkway: the cells beside and in front must stay open
                var front = c.Offset(-dx, -dy);
                if (!room.Contains(front) || ctx.Used.Contains(front)) continue;
                ctx.MakeSolid(c);
                placed++;
            }
            if (placed > 0) room.Features.Add("facade");
            return placed > 0;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;      // Python's % (never negative)

        /// <summary>Free-standing columns / planters in a grid. Outdoors there is no ceiling to hold up,
        /// so they stop at 5-7 m with open sky above (indoor pillars run floor to ceiling).</summary>
        public static bool FColonnade(RoomCtx ctx, Rng rng)
        {
            var room = ctx.Room;
            if (Math.Min(room.W, room.H) < 9) return false;
            int spacing = rng.Choice(new[] { 4, 5 });
            int height = rng.Choice(new[] { 160, 192, 224 });
            int size = rng.Choice(new[] { 1, 2 });
            int placed = 0;
            for (int py = room.Y + 2; py < room.Y + room.H - 3; py += spacing)
                for (int px = room.X + 2; px < room.X + room.W - 3; px += spacing)
                {
                    var cells = new List<Cell>();
                    for (int i = 0; i < size; i++)
                        for (int j = 0; j < size; j++) cells.Add(new Cell(px + i, py + j));
                    var ring = new List<Cell>();
                    for (int dx = -1; dx < size + 1; dx++)
                        for (int dy = -1; dy < size + 1; dy++) ring.Add(new Cell(px + dx, py + dy));
                    if (ctx.Free(ring))
                    {
                        foreach (var c in cells) ctx.SetFloor(c, room.Floor + height);
                        foreach (var c in ring) ctx.Used.Add(c);
                        placed++;
                    }
                }
            if (placed > 0) room.Features.Add("colonnade");
            return placed > 0;
        }

        /// <summary>A solid structure in the middle of the yard: kiosk, monument, guard post. A landmark
        /// and cover, not a room (rooms inside open areas come with mega-rooms).</summary>
        public static bool FKiosk(RoomCtx ctx, Rng rng)
        {
            var room = ctx.Room;
            for (int t = 0; t < 20; t++)
            {
                int w = rng.Int(3, 6), h = rng.Int(3, 6);
                int x = room.X + room.W / 2 - w / 2 + rng.Int(-2, 2);
                int y = room.Y + room.H / 2 - h / 2 + rng.Int(-2, 2);
                var block = new List<Cell>();
                for (int i = 0; i < w; i++)
                    for (int j = 0; j < h; j++) block.Add(new Cell(x + i, y + j));
                var ring = new List<Cell>();
                for (int i = -2; i < w + 2; i++)
                    for (int j = -2; j < h + 2; j++) ring.Add(new Cell(x + i, y + j));
                if (!ctx.Free(ring)) continue;
                int height = rng.Choice(new[] { 96, 128, 160, 192 });
                foreach (var c in block) ctx.SetFloor(c, room.Floor + height);    // its roof: no way up (yet)
                foreach (var c in ring) ctx.Used.Add(c);
                room.Features.Add("kiosk");
                return true;
            }
            return false;
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            var (w, h, height) = Layout.RoomSize(cfg, node.Size, rng);   // 'yard' size: where the sky starts
            height = Math.Max(288, Math.Min(448, height));                // 9-14 m, whatever the recipe's ceilings
            if (cfg.YardHeight != 0)
                height = cfg.YardHeight;                                  // a compound: low buildings, a wide sky
            return new Room { Id = -1, Node = node.Id, Kind = node.Kind, X = 0, Y = 0, W = w, H = h,
                              Floor = floor, Ceil = floor + height, Storey = node.Storey };
        }

        public override void Build(Level level, Room room, GenConfig cfg)
        {
            base.Build(level, room, cfg);
            level.Sky.UnionWith(room.Cells());
        }

        internal override List<List<Func<RoomCtx, Rng, bool>>> FeatureOptions(Room room)
        {
            Func<RoomCtx, Rng, bool> kiosk = FKiosk, colonnade = FColonnade, pit = NoorLevel.Decorate.FPit,
                platform = NoorLevel.Decorate.FPlatform, balcony = NoorLevel.Decorate.FBalcony, buildings = Compound.FBuildings;
            List<Func<RoomCtx, Rng, bool>> L(params Func<RoomCtx, Rng, bool>[] fs) => fs.ToList();
            return new List<List<Func<RoomCtx, Rng, bool>>>
            {
                L(), L(), L(kiosk), L(colonnade), L(pit), L(platform), L(balcony), L(kiosk, colonnade),
                L(buildings), L(buildings, colonnade),
            };
        }

        public override double CrateChance(Room room) => 0.45;

        internal override void Decorate(Level level, Room room, RoomCtx ctx, Rng rng)
        {
            var cfg = level.Config;
            if (cfg.OpenRoofscape)
                Compound.Roofscape(level, room, Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/roofs/{room.Id}"));
            if (rng.Random() < 0.8)
                FFacade(ctx, rng);
            double mk = cfg.Market;
            if (mk > 0)
            {
                var mrng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/market/{room.Id}");
                if (mrng.Random() < mk)
                {
                    if (Market.FMarket(ctx, mrng))                    // streets of stalls; nothing else in the yard
                    {
                        level.Sky.ExceptWith(room.Cells().Where(c => !level.Spans.ContainsKey(c)).ToList());
                        return;
                    }
                }
            }
            double yb = cfg.OpenBuildings;
            if (yb > 0)
            {
                var brng = Rng.FromLabel($"{cfg.Seed}/{cfg.Variant}/buildings/{room.Id}");
                if (brng.Random() < yb)
                    Compound.FBuildings(ctx, brng);                   // a compound's yards are places between buildings
            }
            base.Decorate(level, room, ctx, rng);
            if (room.Meta.ContainsKey("roofscape"))
            {
                // a pilaster against a roofscape stops at the roof beside it, not at the sky
                string roofTag = $"roof:{room.Id}";
                foreach (var c in room.Cells().ToList())
                {
                    if (level.Spans.ContainsKey(c)) continue;
                    var roofs = new List<int>();
                    foreach (var (dx, dy) in D4)
                    {
                        var n = c.Offset(dx, dy);
                        if (!level.Spans.TryGetValue(n, out var sps)) continue;
                        foreach (var sp in sps)
                            if (level.OwnerOf(n, sp) == roofTag) roofs.Add(sp.Floor);
                    }
                    if (roofs.Count > 0)
                        level.AddSpan(c, new Span(roofs.Min(), room.Ceil), $"room:{room.Id}", room.Slot);
                }
            }
            // pilasters and other solid cells are no longer open to the sky
            level.Sky.ExceptWith(room.Cells().Where(c => !level.Spans.ContainsKey(c)).ToList());
        }
    }
}
