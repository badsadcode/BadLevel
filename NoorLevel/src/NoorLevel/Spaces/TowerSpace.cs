using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>The wizard's tower as a space type (spaces/tower_space.py; geometry in <see cref="Tower"/>).</summary>
    public class TowerSpace : SpaceType
    {
        public TowerSpace()
        {
            Name = "tower";
            Kinds = new[] { "tower" };
            Vertical = true;                    // joins storeys: a connector to placement and routing
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            int S = Math.Max(2, cfg.Storeys);
            int H = cfg.StoreyHeight;
            int R = rng.Choice(new[] { 8, 9, 9, 10 });
            int D = 2 * R + 1;
            int top = node.Top ?? floor + (S - 1) * H;
            var room = new Room
            {
                Id = -1, Node = node.Id, Kind = "tower", X = 0, Y = 0, W = D, H = D, Floor = floor,
                Ceil = floor + (S - 1) * H + 320, Storey = node.Storey, TopFloor = top,
                Meta = new Dictionary<string, object> { ["R"] = R, ["storeys"] = S, ["fw"] = 5 },
            };
            var (_, order, _) = Tower.Geom(R);
            room.Mask = new HashSet<Cell>(order);
            room.Shape = "round";
            Tower.Plan(room, H, rng);
            room.Meta["landings"] = Enumerable.Range(0, S).Select(k => floor + k * H).ToList();
            return room;
        }

        public override void Build(Level level, Room room, GenConfig cfg) => Tower.Build(level, room);

        public override (HashSet<Cell> Port, int Floor) Attachment(Room room, Room other)
        {
            int target = other.IsConnector && other.TopFloor != null ? other.TopFloor.Value : other.Floor;
            (HashSet<Cell> Door, int Floor) best = default;
            bool first = true;
            foreach (var p in Tower.Ports(room))       // min(): the first of the nearest
                if (first || Math.Abs(p.Floor - target) < Math.Abs(best.Floor - target)) { best = p; first = false; }
            return (best.Door, best.Floor);
        }

        /// <summary>Light at every landing and half way round each lap, over the helix; one at the top.</summary>
        internal override void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info)
        {
            var m = room.Meta;
            int R = (int)m["R"], S = (int)m["storeys"];
            var hx = new Tower.Helix(R, S, (int)m["H"], (double)m["theta0"]);
            int s = level.CellSize;
            double c = room.W / 2.0;
            double r = R - Tower.Ring / 2;
            for (int k = 0; k < S; k++)
                foreach (var extra in new[] { 0.0, 0.5 })
                {
                    double phi = hx.Theta0 + (k + extra) * hx.Per;
                    var h = hx.Height(phi);
                    if (h == null) continue;
                    double ang = Tower.PyMod(phi, 2 * Math.PI);
                    double x = (room.X + c + r * Math.Cos(ang)) * s;
                    double y = (room.Y + c + r * Math.Sin(ang)) * s;
                    level.Entities.Add(new Entity("light", new Vec3(x, y, room.Floor + h.Value + 120)).With("intensity", 260));
                }
            level.Entities.Add(new Entity("light", new Vec3((room.X + c) * s, (room.Y + c) * s, room.Ceil - 96)).With("intensity", 380));
        }
    }
}
