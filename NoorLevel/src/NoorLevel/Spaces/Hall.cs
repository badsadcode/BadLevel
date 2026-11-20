using System;
using System.Collections.Generic;
using System.Linq;

namespace NoorLevel
{
    /// <summary>Merged halls: neighbouring rooms placed touching, one continuous space (spaces/hall.py).
    /// A hall is not a space type but a relationship: its rooms stay separate rooms (semantic regions)
    /// and share a seam instead of a corridor. Seams between rooms at different heights become
    /// full-width terrace steps.</summary>
    public static class HallSpace
    {
        public static readonly (int, int)[] Dirs = { (1, 0), (0, 1), (-1, 0), (0, -1) };

        /// <summary>Can this tree edge become a seam inside one hall instead of a corridor?</summary>
        public static bool Mergeable(Edge e, Room parent, Node node, MissionGraph g)
        {
            if (!e.Tree || e.Kind != "open" || e.Connector != null) return false;
            var kinds = new[] { parent.Kind, node.Kind };
            if (kinds.Any(k => Array.IndexOf(MissionGraph.ConnectorKinds, k) >= 0) || kinds.Any(k => k == "secret" || k == "key"))
                return false;
            return parent.Storey == node.Storey;
        }

        /// <summary>Pairs of touching cells (one in a, one in b) along the shared edge of two rectangles.</summary>
        public static List<(Cell A, Cell B, (int Dx, int Dy) D)> SeamCells(Room a, Room b)
        {
            var outp = new List<(Cell, Cell, (int, int))>();
            foreach (var c in a.Cells())
                foreach (var (dx, dy) in Dirs)
                {
                    var n = c.Offset(dx, dy);
                    if (b.Contains(n)) outp.Add((c, n, (dx, dy)));
                }
            return outp;
        }

        public static bool SeamOk(Room a, Room b) => SeamCells(a, b).Count >= 6;

        /// <summary>Record merged halls, and turn seams between rooms at different heights into terrace
        /// steps across the whole seam (16-unit steps in the higher room, down towards the seam).</summary>
        public static void BuildHalls(Level level, MissionGraph g, GenConfig cfg)
        {
            var seen = new HashSet<string>();
            foreach (var r in level.Rooms)
            {
                var h = r.Get<List<int>>("hall");
                if (h == null || h.Count == 0) continue;
                if (seen.Add(string.Join(",", h))) level.Halls.Add(new List<int>(h));
            }
            foreach (var e in g.Edges)
            {
                if (!e.Merged) continue;
                Room a = level.Rooms[e.A], b = level.Rooms[e.B];
                var (hi, lo) = a.Floor > b.Floor ? (a, b) : (b, a);
                int d = hi.Floor - lo.Floor;
                int rows = d / cfg.StepHeight - 1;
                if (rows <= 0) continue;                    // within one step: seamless
                var terrace = new List<Cell>();
                foreach (var (cHi, _, (dx, dy)) in SeamCells(hi, lo))
                {
                    for (int j = 0; j < rows; j++)
                    {
                        var c = new Cell(cHi.X - dx * j, cHi.Y - dy * j);
                        if (!hi.Contains(c)) break;
                        level.ReplaceIn(c, hi.Slot.Lo, hi.Slot.Hi, new[] { new Span(lo.Floor + cfg.StepHeight * (j + 1), hi.Ceil) });
                        terrace.Add(c);
                    }
                }
                if (!(hi.Meta.TryGetValue("terrace", out var t) && t is List<Cell> list)) hi.Meta["terrace"] = list = new List<Cell>();
                list.AddRange(terrace);
                hi.Features.Add("terrace");
            }
        }
    }
}
