using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Vertical connectors as space types: switchback stairwells, lift shafts, ladder shafts,
    /// spiral stairs (spaces/connector.py). The geometry lives in <see cref="Connectors"/>.</summary>
    public sealed class ConnectorSpace : SpaceType
    {
        public ConnectorSpace(string kind)
        {
            Name = kind;
            Kinds = new[] { kind };
            Vertical = true;
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi)
        {
            int top = node.Top ?? floor;
            bool wide = cfg.Spaciousness >= 0.5 && (node.Kind == "stairwell" || node.Kind == "lift" || node.Kind == "ladder");
            var size = Connectors.ConnectorSize(node.Kind, top - floor, rng, wide);
            if (size == null) return null;
            var (w, h, meta) = size.Value;
            var room = new Room { Id = -1, Node = node.Id, Kind = node.Kind, X = 0, Y = 0, W = w, H = h, Floor = floor,
                                  Ceil = top + Connectors.CeilAboveTop, Storey = node.Storey, TopFloor = top, Meta = meta };
            if (node.Kind == "spiral")                       // a round tower round a solid newel
            {
                room.Mask = new HashSet<Cell>(Connectors.SpiralCells(w).Keys);
                room.Shape = "round";
            }
            return room;
        }

        public override void Build(Level level, Room room, GenConfig cfg) =>
            Connectors.BuildConnector(level, room, cfg.StepHeight);

        public override (HashSet<Cell> Port, int Floor) Attachment(Room room, Room other)
        {
            var p = Connectors.Ports(room);
            int target = other.IsConnector ? (other.TopFloor ?? other.Floor) : other.Floor;
            int top = room.TopFloor ?? room.Floor;
            string key = Math.Abs(target - top) < Math.Abs(target - room.Floor) ? "high" : "low";
            return p[key];
        }

        internal override void Populate(Level level, Room room, RoomCtx ctx, Rng rng, Dictionary<string, object> info) =>
            Spaces.PlaceLights(level, room);        // no fights or pickups on the stairs
    }
}
