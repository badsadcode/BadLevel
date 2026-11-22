using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>The station markers' shared contract (Dressing's props, StationEntities' gameplay markers):
    /// which entity kinds they are, how a marker is placed (mount, footprint, facing) and how it reads in a
    /// Noor Interchange GLB (<c>extras.noor</c>, the yaw rotation). The README's consumer contract and
    /// docs/DECISIONS.md describe the same thing in words.
    ///
    /// Map units, Z up, as everywhere in a Level: 32 units = 1 m. A marker's facing is a map yaw in degrees
    /// (<c>Props["angle"]</c>, the convention player_start uses: 0 = +X, 90 = +Y), always a multiple of 90.
    /// A footprint is [width, depth, height] in metres: width along the marker's right, depth along its
    /// forward, height up. The marker's position (its origin) is:
    /// <list type="bullet">
    /// <item><c>floor</c>: the centre of the footprint on the floor;</item>
    /// <item><c>wall</c>: on the wall surface, at the bottom centre of the back face (the piece reaches
    /// <c>depth</c> into the room and <c>height</c> up from there);</item>
    /// <item><c>ceiling</c>: on the ceiling, at the top centre (it hangs <c>height</c> down).</item>
    /// </list></summary>
    public static class Markers
    {
        /// <summary>Entity kinds written by the station passes, in the order they're appended (after every
        /// existing kind, so existing node names stay stable).</summary>
        public static readonly string[] Kinds =
        {
            "prop", "loot", "log_terminal", "creature_spawn", "hiding_spot", "power_junction", "hazard_zone", "airlock", "viewport",
        };

        static readonly HashSet<string> KindSet = new HashSet<string>(Kinds);

        public static bool IsMarker(string kind) => kind != null && KindSet.Contains(kind);

        /// <summary>The Noor Interchange marker type of each kind: an existing one from the spec's table where
        /// it fits (pickup, spawn.enemy, trigger, entrance), else a proposed one (docs/INTERCHANGE_PROPOSAL.md).</summary>
        public static readonly Dictionary<string, string> NoorMarker = new Dictionary<string, string>
        {
            ["prop"] = "prop",                   // proposed
            ["loot"] = "pickup",                 // spec: pickup (class "loot", size)
            ["log_terminal"] = "interact",       // proposed (class "log_terminal")
            ["creature_spawn"] = "spawn.enemy",  // spec: spawn.enemy (class "creature", role)
            ["hiding_spot"] = "hide",            // proposed
            ["power_junction"] = "interact",     // proposed (class "power_junction")
            ["hazard_zone"] = "trigger",         // spec: trigger (size, event "hazard", type)
            ["airlock"] = "entrance",            // spec: entrance (kind "airlock": a proposed kind)
            ["viewport"] = "viewport",           // proposed
        };

        // the noor block's fields per kind, copied from Props when present (the order they're written in)
        static readonly string[] NoorFields =
            { "class", "role", "size", "event", "type", "mount", "footprint", "variant", "on", "room", "corridor", "program", "sector" };

        public const double M = 32.0;            // map units per metre

        public static (int Dx, int Dy) Dir(int angle)
        {
            switch (((angle % 360) + 360) % 360)
            {
                case 0: return (1, 0);
                case 90: return (0, 1);
                case 180: return (-1, 0);
                case 270: return (0, -1);
                default: throw new ArgumentException($"marker angle {angle} is not a multiple of 90");
            }
        }

        public static int Angle(int dx, int dy) => dx > 0 ? 0 : dy > 0 ? 90 : dx < 0 ? 180 : 270;

        /// <summary>The glTF node rotation (x, y, z, w) for a map yaw: the marker's forward is the node's +Z
        /// (Noor Interchange 3.4). Map (x, y, z) is glTF (x, z, -y), so a map facing (cos a, sin a) is glTF
        /// (cos a, 0, -sin a): a turn about +Y by a + 90 degrees. Exact constants, no trigonometry (spec 5.3).</summary>
        public static float[] Rotation(int angle)
        {
            float s = (float)Math.Sqrt(0.5);
            switch (((angle % 360) + 360) % 360)
            {
                case 0: return new[] { 0f, s, 0f, s };
                case 90: return new[] { 0f, 1f, 0f, 0f };
                case 180: return new[] { 0f, -s, 0f, s };
                case 270: return new[] { 0f, 0f, 0f, 1f };
                default: throw new ArgumentException($"marker angle {angle} is not a multiple of 90");
            }
        }

        /// <summary>The axis-aligned box (map units) a marker with this mount, footprint (metres) and facing takes.</summary>
        public static (Vec3 Lo, Vec3 Hi) Box(Vec3 origin, string mount, double w, double d, double h, int angle)
        {
            var (fx, fy) = Dir(angle);
            double ex = (fx != 0 ? d : w) / 2 * M, ey = (fy != 0 ? d : w) / 2 * M;   // half extents along x and y
            double cx = origin.X, cy = origin.Y, z0 = origin.Z, z1 = origin.Z + h * M;
            if (mount == "wall") { cx += fx * d / 2 * M; cy += fy * d / 2 * M; }
            else if (mount == "ceiling") { z0 = origin.Z - h * M; z1 = origin.Z; }
            return (new Vec3(cx - ex, cy - ey, z0), new Vec3(cx + ex, cy + ey, z1));
        }

        /// <summary>A placed marker entity with the common fields (room, program, sector), in Props order.</summary>
        public static Entity Make(string kind, Vec3 pos, int? angle, Room room)
        {
            var e = new Entity(kind, pos);
            if (angle != null) e.With("angle", angle.Value);
            if (room != null)
            {
                e.With("room", room.Id);
                var prog = room.Get<string>("program");
                if (!string.IsNullOrEmpty(prog)) e.With("program", prog);
                var sector = room.Get<string>("sector");
                if (!string.IsNullOrEmpty(sector)) e.With("sector", sector);
            }
            return e;
        }

        /// <summary>The <c>extras.noor</c> block for a marker node named <paramref name="id"/> (Noor Interchange
        /// 3.3/3.4: v, marker, id, then the marker's fields, tags and free-form props).</summary>
        public static Dictionary<string, object> NoorBlock(Entity e, string id)
        {
            var b = new Dictionary<string, object> { ["v"] = 1, ["marker"] = NoorMarker[e.Kind], ["id"] = id };
            if (e.Kind == "airlock") b["kind"] = "airlock";       // an entrance of kind airlock
            if (e.Kind == "hazard_zone") b["event"] = "hazard";   // a trigger that hurts
            foreach (var f in NoorFields)
                if (e.Props.TryGetValue(f, out var v)) b[f] = v;
            if (e.Props.TryGetValue("tags", out var tags)) b["tags"] = tags;
            var free = new Dictionary<string, object>();         // tool-specific extras: the spec's free-form props
            if (e.Props.TryGetValue("text", out var text)) free["text"] = text;
            if (e.Props.TryGetValue("block", out var block)) free["block"] = block;
            if (free.Count > 0) b["props"] = free;
            return b;
        }
    }
}
