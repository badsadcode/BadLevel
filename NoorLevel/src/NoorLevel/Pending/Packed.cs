using System;
using System.Collections.Generic;

namespace NoorLevel
{
    /// <summary>STUB: wave 2 (packed.py). Returns null: the layout places rooms the normal way.</summary>
    public static class Packed
    {
        /// <summary>place_packed(g, cfg, rng, make, LayoutError): node id -> placed Room, or null to fall back.
        /// make(node, x, y, floor, parentFloor) proposes a room (LayoutError is LayoutException in C#).</summary>
        public static Dictionary<int, Room> PlacePacked(MissionGraph g, GenConfig cfg, Rng rng, Func<Node, int, int, int, int, Room> make)
        {
            /* STUB: wave 2 */
            return null;
        }
    }
}
