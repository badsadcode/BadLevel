using System.Linq;
using System.Collections.Generic;

// STUBS for modules ported in wave 2, called by Decorate.cs, Recipes.cs and Validate.cs. Each is a
// harmless no-op that keeps levels valid. Whoever ports a module deletes its block here (the classes
// are partial so they merge with stubs of the same class made elsewhere).
namespace NoorLevel
{
    /// <summary>STUB: programs.py.</summary>
    public static partial class Programs
    {
        public static void Assign(Level level) { }   // STUB: wave 2
    }

    /// <summary>STUB: dungeon.py.</summary>
    public static partial class Dungeon
    {
        public static int AddSkylights(Level level) => 0;   // STUB: wave 2
        public static int AddTraps(Level level) => 0;   // STUB: wave 2
    }

    /// <summary>STUB: floods.py.</summary>
    public static partial class Floods
    {
        public static List<string> BreathReport(Level level) => new List<string>();   // STUB: wave 2
    }

    /// <summary>STUB: arena.py. Not a pure no-op: the arena validator needs at least two spawn points,
    /// so this puts one "spawn" on a free floor cell of each ordinary room (up to 12), facing nowhere
    /// in particular. No weapons, power-ups or towers (the real port places all of that).</summary>
    public static partial class Arena
    {
        public static void Populate(Level level, Dictionary<int, RoomCtx> ctxs, Rng rng)   // STUB: wave 2
        {
            int n = 0;
            foreach (var r in level.Rooms)
            {
                if (r.IsConnector || r.Kind == "overlook" || r.Kind == "secret" || n >= 12) continue;
                var taken = new HashSet<Cell>(ctxs[r.Id].Pads);
                var c = Decorate.PickFree(ctxs[r.Id], rng, taken);
                if (c == null) continue;
                level.Entities.Add(new Entity("spawn", Decorate.At(level, r, c.Value, zOff: 32.0)).With("angle", 0).With("room", r.Id));
                n++;
            }
            // arena.py: a player_start on the first spawn too (engines and tools want one)
            level.Entities.RemoveAll(e => e.Kind == "player_start");
            var first = level.Entities.FirstOrDefault(e => e.Kind == "spawn");
            if (first != null) level.Entities.Add(new Entity("player_start", first.Pos).With("angle", 0));
        }
    }
}
