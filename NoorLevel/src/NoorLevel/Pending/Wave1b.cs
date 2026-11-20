using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    // STUB (wave 1b)
    // decorate.py helpers the space types call, which the Decorate class doesn't expose yet (it is being
    // ported in parallel). TODO(wave 1b): once Decorate.cs has them, replace every DecorateStubs.X with
    // Decorate.X (callers: Spaces/SpaceType.cs, Spaces/Room.cs, Spaces/Corridor.cs) and delete this file.
    static class DecorateStubs
    {
        // features (decorate.f_*): no-ops here
        public static bool FHazard(RoomCtx ctx, Rng rng) => false;
        public static bool FChasm(RoomCtx ctx, Rng rng) => false;
        public static bool FTower(RoomCtx ctx, Rng rng) => false;
        public static bool FPillars(RoomCtx ctx, Rng rng) => false;
        public static bool FCrates(RoomCtx ctx, Rng rng) => false;

        // decorate.FEATURES_BY_KIND / CRATE_CHANCE
        public static readonly Dictionary<string, List<List<Func<RoomCtx, Rng, bool>>>> FeaturesByKind =
            new Dictionary<string, List<List<Func<RoomCtx, Rng, bool>>>>();
        public static readonly Dictionary<string, double> CrateChance = new Dictionary<string, double>();

        // getattr(ctx, "pedestal", None)
        public static List<Cell> Pedestal(RoomCtx ctx) => null;

        // decorate._door_entity
        public static Entity DoorEntity(Level level, Corridor corr, string kind) => null;

        // decorate._nearest_open
        public static Cell NearestOpen(Level level, Room room, double fx, double fy, bool single = false) =>
            room.Cells().Where(c => !level.Diag.ContainsKey(c) && level.RoomSpans(c, room).Count > 0
                                    && (!single || level.RoomSpans(c, room).Count == 1))
                .OrderBy(c => Math.Pow(c.X + 0.5 - fx, 2) + Math.Pow(c.Y + 0.5 - fy, 2)).First();

        // decorate._flat_cell
        public static bool FlatCell(Level level, Room room, Cell c)
        {
            var spans = level.RoomSpans(c, room);
            return spans.Count == 1 && spans[0].Floor == room.Floor && !level.Diag.ContainsKey(c) && room.Contains(c);
        }

        // decorate._flat_block
        public static List<Cell> FlatBlock(RoomCtx ctx, double fx, double fy, ICollection<Cell> avoid = null)
        {
            List<Cell> best = null; double bestD = 0;
            foreach (var c in ctx.Room.Cells())
            {
                var cells = new List<Cell> { c, c.Offset(1, 0), c.Offset(0, 1), c.Offset(1, 1) };
                if (cells.All(x => FlatCell(ctx.Level, ctx.Room, x) && (avoid == null || !avoid.Contains(x))))
                {
                    double d = Math.Pow(c.X + 1 - fx, 2) + Math.Pow(c.Y + 1 - fy, 2);
                    if (best == null || d < bestD) { best = cells; bestD = d; }
                }
            }
            return best;
        }

        // decorate._pick_free
        public static Cell? PickFree(RoomCtx ctx, Rng rng, HashSet<Cell> taken, bool floorOnly = true)
        {
            var r = ctx.Room;
            var cands = new List<Cell>();
            foreach (var c in r.Cells())
            {
                var spans = ctx.Level.RoomSpans(c, r);
                if (spans.Count != 1 || taken.Contains(c) || ctx.Clear.Contains(c) || ctx.Level.Diag.ContainsKey(c)) continue;
                if (floorOnly && spans[0].Floor != r.Floor) continue;
                cands.Add(c);
            }
            if (cands.Count == 0) return null;
            var pick = rng.Choice(cands);
            taken.Add(pick);
            return pick;
        }

        // decorate._at
        public static Vec3 At(Level level, Room room, Cell c, bool top = false, double zOff = 24.0)
        {
            var spans = level.RoomSpans(c, room);
            int f = top ? spans[spans.Count - 1].Floor : spans[0].Floor;
            return new Vec3((c.X + 0.5) * level.CellSize, (c.Y + 0.5) * level.CellSize, f + zOff);
        }
    }
}
