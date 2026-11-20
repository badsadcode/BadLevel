using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Rooms: the classic enclosed space, in all its gameplay roles (spaces/room.py). One space
    /// type covers start, combat, arena, hub, supply, key, secret and exit rooms: same geometry (a
    /// footprint shape on a floor height, optionally tall), different features and contents per role.
    /// (No enemies or pickups in NoorLevel: the game fills the space.)</summary>
    public class RoomSpace : SpaceType
    {
        public static readonly string[] Roles = { "start", "combat", "arena", "hub", "supply", "key", "secret", "exit" };

        protected virtual int? LightMax => null;       // cap light height above the floor (open areas)
        protected virtual int? LightPower => null;

        public RoomSpace()
        {
            Name = "room";
            Kinds = Roles;
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            int H = cfg.StoreyHeight;
            var (w, h, height) = Layout.RoomSize(cfg, node.Size, rng);
            if (multi)
            {
                if ((node.Kind == "arena" || node.Kind == "hub") && rng.Random() < cfg.TallRoomChance)
                    height = rng.Range(H + 96, 2 * H - 32 + 1, 16);     // reaches into the next storey
                else
                    height = Math.Min(height, H - 64);                 // leaves room for a storey above
            }
            return new Room { Id = -1, Node = node.Id, Kind = node.Kind, X = 0, Y = 0, W = w, H = h,
                              Floor = floor, Ceil = floor + height, Storey = node.Storey };
        }

        public override void AssignShape(Room room, Node node, GenConfig cfg, Rng rng, bool merged)
        {
            if (merged) return;                  // merged rooms stay rectangular so their seams line up
            var shape = Shape.Pick(room.W, room.H, node.Size, node.Kind, rng, cfg.ShapeWeights);
            room.Shape = shape.Name;
            if (shape.Name != "rect" || shape.Diag.Count > 0) { room.Mask = shape.Cells; room.Diag = shape.Diag; }
        }

        internal override List<List<Func<RoomCtx, Rng, bool>>> FeatureOptions(Room room) =>
            // TODO(wave 1b): DecorateStubs.FeaturesByKind -> Decorate.FeaturesByKind
            DecorateStubs.FeaturesByKind.TryGetValue(room.Kind, out var o) ? o
                : new List<List<Func<RoomCtx, Rng, bool>>> { new List<Func<RoomCtx, Rng, bool>>() };

        public override double CrateChance(Room room) =>
            DecorateStubs.CrateChance.TryGetValue(room.Kind, out var c) ? c : 0.0;

        internal override void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info)
        {
            var g = level.Graph;
            var ents = level.Entities;
            var node = g.Nodes[room.Node];
            var taken = new HashSet<Cell>();
            double cx = room.X + room.W / 2.0, cy = room.Y + room.H / 2.0;

            Spaces.PlaceLights(level, room, LightMax, LightPower);

            // TODO(wave 1b): DecorateStubs.* -> Decorate.*
            if (room.Kind == "start")
            {
                var flat = room.Cells().Where(c => DecorateStubs.FlatCell(level, room, c)).ToList();
                if (flat.Count == 0) throw new LayoutException("no spot for the start");
                var c0 = flat.OrderBy(c => Math.Pow(c.X + 0.5 - cx, 2) + Math.Pow(c.Y + 0.5 - cy, 2)).First();
                taken.Add(c0);
                int angle = 0;
                if (ctx.Doors.Count > 0)
                {
                    double dx = ctx.Doors.Sum(d => d.X) / (double)ctx.Doors.Count + 0.5 - (c0.X + 0.5);
                    double dy = ctx.Doors.Sum(d => d.Y) / (double)ctx.Doors.Count + 0.5 - (c0.Y + 0.5);
                    angle = ((int)Math.Round(Math.Atan2(dy, dx) * 180.0 / Math.PI / 45.0) * 45 % 360 + 360) % 360;
                }
                // 32 up, not 24: exactly 24 puts the origin on the boundary of qbsp's player and monster
                // hulls ("no filling performed"). The player drops 8 units at spawn.
                ents.Add(new Entity("player_start", DecorateStubs.At(level, room, c0, zOff: 32.0)).With("angle", angle));
            }
            else if (room.Kind == "exit")
            {
                var block = DecorateStubs.FlatBlock(ctx, cx, cy);
                if (block == null) throw new LayoutException("no spot for the exit");
                int ex = block[0].X, ey = block[0].Y, s = level.CellSize;
                var lo = new Vec3(ex * s, ey * s, room.Floor);
                var hi = new Vec3((ex + 2) * s, (ey + 2) * s, room.Floor + 64);
                ents.Add(new Entity("exit", new Vec3((ex + 1) * s, (ey + 1) * s, room.Floor + 32)) { Bounds = (lo, hi) });
                for (int x = ex; x <= ex + 1; x++)
                    for (int y = ey; y <= ey + 1; y++) taken.Add(new Cell(x, y));
            }
            else if (room.Kind == "key")
            {
                var ped = DecorateStubs.Pedestal(ctx);
                Cell? c = ped != null && ped.Count > 0 ? ped[0] : DecorateStubs.PickFree(ctx, rng, taken);
                if (c == null) throw new LayoutException("no spot for key");
                if (ped != null && ped.Count > 0) taken.UnionWith(ped); else taken.Add(c.Value);
                ents.Add(new Entity("key", DecorateStubs.At(level, room, c.Value, zOff: 24)).With("color", node.Key));
            }
            // (enemies, snipers on the high ground and pickups: left to the game, see PORTING.md)
        }
    }

    /// <summary>An observation booth: a small room that exists to look into a hall through glass.</summary>
    public sealed class OverlookSpace : RoomSpace
    {
        public OverlookSpace()
        {
            Name = "overlook";
            Kinds = new[] { "overlook" };
        }

        internal override List<List<Func<RoomCtx, Rng, bool>>> FeatureOptions(Room room) =>
            new List<List<Func<RoomCtx, Rng, bool>>> { new List<Func<RoomCtx, Rng, bool>>() };

        public override double CrateChance(Room room) => 0.0;
    }
}
