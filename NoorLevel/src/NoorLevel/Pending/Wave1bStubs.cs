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

    /// <summary>STUB: dungeon.py.</summary>
    public static partial class Dungeon
    {
        public static int AddSkylights(Level level) => 0;   // STUB: wave 2
        public static int AddTraps(Level level) => 0;   // STUB: wave 2
    }

    /// <summary>STUB: portals.py.</summary>
    public static partial class Portals
    {
        public static int Build(Level level, Dictionary<int, string> tiers) => 0;   // STUB: wave 2
    }

    /// <summary>STUB: floods.py.</summary>
    public static partial class Floods
    {
        public static List<string> BreathReport(Level level) => new List<string>();   // STUB: wave 2
    }
}
