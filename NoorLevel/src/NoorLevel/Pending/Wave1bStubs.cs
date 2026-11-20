using System.Linq;
using System.Collections.Generic;

// STUBS for modules ported in wave 2, called by Decorate.cs, Recipes.cs and Validate.cs. Each is a
// harmless no-op that keeps levels valid. Whoever ports a module deletes its block here (the classes
// are partial so they merge with stubs of the same class made elsewhere).
namespace NoorLevel
{
    /// <summary>STUB (architecture.py Section): a room's cross-section.</summary>
    public sealed partial class Section
    {
        public string Name = "";
        public Frame Frame = new Frame();
        public Dictionary<string, object> Info = new Dictionary<string, object>();
    }

    /// <summary>STUB (architecture.py Frame): a room's local frame.</summary>
    public sealed partial class Frame
    {
        public bool AlongX = true;
    }

    /// <summary>STUB: architecture.py.</summary>
    public static partial class Architecture
    {
        public static readonly string[] Styles = { "gothic", "industrial", "bunker", "compound", "egyptian", "scifi", "castle" };

        public static Dictionary<int, Section> Plan(Level level) => new Dictionary<int, Section>();   // STUB: wave 2
        public static Dictionary<int, string> RankTiers(Level level) => new Dictionary<int, string>();   // STUB: wave 2
        public static int PlacePiers(Level level, Room room, Section sec, RoomCtx ctx, string tag = "piers") => 0;   // STUB: wave 2
        public static int ApplyCeilings(Level level, Room room, Section sec) => 0;   // STUB: wave 2
    }

    /// <summary>STUB: walls.py.</summary>
    public static partial class Walls
    {
        public static Dictionary<string, int> Articulate(Level level, Room room, RoomCtx ctx, Section sec, string tier) => new Dictionary<string, int>();   // STUB: wave 2
        public static void TidyDress(Level level, Room room) { }   // STUB: wave 2
        public static void ApplySplays(Level level, Room room, Section sec) { }   // STUB: wave 2
    }

    /// <summary>STUB: landmark.py.</summary>
    public static partial class Landmark
    {
        public static Dictionary<string, object> Place(Level level, Room room, RoomCtx ctx, Section sec, string style, Rng rng) => null;   // STUB: wave 2
    }

    /// <summary>STUB: programs.py.</summary>
    public static partial class Programs
    {
        public static void Assign(Level level) { }   // STUB: wave 2
    }

    /// <summary>STUB: portals.py.</summary>
    public static partial class Portals
    {
        public static int Build(Level level, Dictionary<int, string> tiers) => 0;   // STUB: wave 2
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
