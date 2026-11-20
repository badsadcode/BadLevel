using System.Linq;
using System.Collections.Generic;

// STUBS for modules ported in wave 2, called by Decorate.cs, Recipes.cs and Validate.cs. Each is a
// harmless no-op that keeps levels valid. Whoever ports a module deletes its block here (the classes
// are partial so they merge with stubs of the same class made elsewhere).
namespace NoorLevel
{

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
}
