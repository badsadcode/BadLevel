using System.Collections.Generic;

namespace NoorLevel
{
    /// <summary>Generation parameters. Distances in Quake-style units unless the name says cells;
    /// one cell is <see cref="Cell"/> units (32, the width of a player). Recipes (dials) fill these in.</summary>
    public sealed class GenConfig
    {
        public int Seed = 1;
        public int Variant = 0;              // alternative layouts for the same seed
        public string Recipe = "";

        // mission graph
        public int MainRooms = 6;            // rooms on the critical path, incl. start and exit
        public int SideRooms = 3;
        public int Loops = 2;
        public double LongLoopChance = 0.25;
        public int Keys = 1;                 // 0..2 key / locked door pairs
        public bool Secret = true;
        public bool CorridorBeats = true;    // break long corridors up with chambers, alcoves, vaults
        public int BeatSpacing = 16;         // cells between beats along a long corridor
        public double BeatShapes = 0.0;      // share of beats that are shaped rooms
        public string Mode = "campaign";     // campaign | arena (deathmatch: arena.py)
        public double JumpPads = 0.0;        // weight of jump-pad towers among room features
        public int Vistas = 0;               // big windows in grand halls onto a landscape (vistas)
        public string MonsterSet = "";       // base | keep | dungeon | wizard (kept for the record: no monsters in NoorLevel)
        public double Market = 0.0;          // chance an open area is a goblin market
        public bool Tower = false;           // the wizard's tower: a helix hall as the spine
        public bool Mirror = false;          // arena: a team map, half and its mirror image
        public double Chasms = 0.0;          // weight of chasm bridges among room features
        public double Skylights = 0.0;       // weight of grates open to the sky
        public bool Torches = false;         // wall torches instead of ceiling lights
        public double Floods = 0.0;          // chance a crypt floods
        public double Sumps = 0.0;           // chance a level corridor in a flood dips under the water
        public double Traps = 0.0;           // chance a corridor gets a trap marker
        public string ProgramSet = "facility";   // facility | dungeon | wizard | station
        public bool Programs = false;        // rooms with a job and its furniture (programs)
        public double Vents = 0.0;           // chance a loop becomes a low duct
        public int VentHeight = 64;
        public double EnemyDensity = 1.0;    // (kept for recipes; NoorLevel places no enemies)
        public double PickupDensity = 1.0;   // (kept for recipes; NoorLevel places no pickups)

        // station markers (Dressing, StationEntities): empty marker nodes a game dresses and acts on
        /// <summary>Prop markers: "a console, this big, facing this way, belongs here" (Dressing). Off: nothing changes.</summary>
        public bool Dressing = false;
        /// <summary>How many props Dressing places, 0..1 (scaled per program: storage dense, an atrium sparse).</summary>
        public double DressingDensity = 0.6;
        /// <summary>Gameplay markers: loot, log terminals, creature spawns, hiding spots, power junctions,
        /// hazard zones, the airlock (StationEntities). Off: nothing changes.</summary>
        public bool StationEntities = false;
        /// <summary>Viewport markers: small portholes on outer walls (StationEntities). Off: nothing changes.</summary>
        public bool Viewports = false;

        // composition
        public string Composition = "tree";  // tree | hub | central | linear
        public bool PacingContrast = false;  // compression before release

        // open areas
        public int OpenAreas = 0;            // courtyards / plazas open to the sky
        public double OpenBuildings = 0.0;   // chance a yard gets free-standing buildings
        public int YardHeight = 0;           // fixed height of open areas (0: from the size table)
        public int YardSetback = 0;          // extra cells between open areas and other rooms
        public bool OpenRoofscape = false;   // stepped roofs round yards
        public int MegaRooms = 0;            // levels within the level
        public Dictionary<string, double> MegaStyles = new Dictionary<string, double>();   // hangar / works / terraces

        // architecture
        public string ArchStyle = "";        // "" | gothic | industrial | bunker | compound | castle | egyptian | scifi ...
        public Dictionary<string, double> ArchWeights = new Dictionary<string, double>();
        public bool ArchWalls = true;
        public double Hazards = 0.0;         // weight of hazard pools among room features
        public string HazardKind = "slime";  // slime | lava
        public double LightVariety = 0.0;
        public double Ramps = 0.0;           // chance per stair corridor that its steps become a ramp
        public bool ArchDress = true;
        public bool ArchPortals = true;
        public double ArchAxial = 0.65;
        public int ArchHeadroom = 112;
        public bool ArchLandmark = true;
        public bool ArchSmooth = true;
        /// <summary>Free-standing columns anywhere: room pillars, yard colonnades, architectural piers under
        /// vaults, columns in corridor rotundas / aisles / chambers. False: none of them (NoorLevel, for Tom:
        /// "those pillars can be really annoying"). Wall members (half-columns on walls) are ArchWalls.</summary>
        public bool Pillars = true;

        // merged halls
        public int MaxHallRooms = 3;

        // openings
        public double GlazedChance = 0.4;
        public bool Galleries = true;

        // storeys (more)
        public bool Underground = false;     // in from the surface and down
        /// <summary>Rooms over rooms: 0 = as BadLevel (now and then a room on another storey sits over or under
        /// a neighbour), up to 1 = every room on another storey tries that first. Needs Storeys &gt; 1.</summary>
        public double Stacking = 0.0;
        /// <summary>The level's footprint: the longer side in metres (cells), 0 = whatever the recipe gives.
        /// The generator fits the level to it by changing room counts and sizes (within about 15%).</summary>
        public int MapSize = 0;

        // storeys
        public int Storeys = 1;
        public int StoreyHeight = 256;
        public double StoreyChange = 0.5;
        public Dictionary<string, double> ConnectorWeights = new Dictionary<string, double>
            { ["stairs"] = 0.3, ["stairwell"] = 0.35, ["lift"] = 0.2, ["ladder"] = 0.15 };
        public double DropChance = 0.6;
        public int MaxDrop = 288;
        public double TallRoomChance = 0.4;

        // geometry
        public int Cell = 32;
        public int StepHeight = 16;
        public int LevelStep = 64;
        public int MaxLevel = 4;
        public int CorridorRadius = 1;
        public int CorridorHeight = 128;
        public int MaxCorridorRadius = 3;
        public double DiagonalChance = 0.5;
        public int RoomGap = 4;
        public double MergeChance = 0.3;     // neighbouring rooms joined into one hall
        public double Packing = 0.0;         // >= 0.5: a divided floor plan (packed)
        public double DiagonalPull = 0.0;

        // player (validation)
        public int PlayerHeight = 56;
        public int MaxStep = 18;
        public bool CheckClearance = true;

        public int MaxAttempts = 60;

        // style levers
        public Dictionary<string, double> SizeBias = new Dictionary<string, double>();
        public Dictionary<string, double> ShapeWeights = new Dictionary<string, double>();
        public Dictionary<string, double> FeatureWeights = new Dictionary<string, double>();
        public Dictionary<int, double> CorridorWidthBias = new Dictionary<int, double>();
        public double CrateScale = 1.0;
        public double HeightScale = 1.0;
        public double RoomScale = 1.0;
        public double SizeVariety = 0.0;
        public double Spaciousness = 0.0;
        public int Windows = 5;

        /// <summary>Room size table: kind -> (min cells, max cells, min height, max height).</summary>
        public Dictionary<string, (int Lo, int Hi, int HMin, int HMax)> RoomSizes =
            new Dictionary<string, (int, int, int, int)>
            {
                ["small"] = (6, 8, 128, 160),
                ["medium"] = (9, 12, 160, 224),
                ["large"] = (14, 18, 256, 320),
                ["yard"] = (16, 22, 320, 320),
                ["mega"] = (24, 32, 448, 512),
            };

        public GenConfig Clone()
        {
            var c = (GenConfig)MemberwiseClone();
            c.ConnectorWeights = new Dictionary<string, double>(ConnectorWeights);
            c.SizeBias = new Dictionary<string, double>(SizeBias);
            c.ShapeWeights = new Dictionary<string, double>(ShapeWeights);
            c.FeatureWeights = new Dictionary<string, double>(FeatureWeights);
            c.CorridorWidthBias = new Dictionary<int, double>(CorridorWidthBias);
            c.RoomSizes = new Dictionary<string, (int, int, int, int)>(RoomSizes);
            c.MegaStyles = new Dictionary<string, double>(MegaStyles);
            c.ArchWeights = new Dictionary<string, double>(ArchWeights);
            return c;
        }
    }
}
