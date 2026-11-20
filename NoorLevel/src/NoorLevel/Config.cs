using System.Collections.Generic;

namespace NoorLevel
{
    /// <summary>Generation parameters. Distances in Quake-style units unless the name says cells;
    /// one cell is <see cref="Cell"/> units (32, the width of a player). Recipes (dials) fill these in.</summary>
    public sealed class GenConfig
    {
        public int Seed = 1;
        public string Recipe = "";

        // mission graph
        public int MainRooms = 6;            // rooms on the critical path, incl. start and exit
        public int SideRooms = 3;
        public int Loops = 2;
        public double LongLoopChance = 0.25;
        public int Keys = 1;                 // 0..2 key / locked door pairs
        public bool Secret = true;
        public string Composition = "tree";  // tree | hub | central | linear

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
        public double MergeChance = 0.0;     // (merged halls are a later port)

        // player (validation)
        public int PlayerHeight = 56;
        public int MaxStep = 18;
        public bool CheckClearance = true;

        public int MaxAttempts = 40;

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
        public int Windows = 0;

        /// <summary>Room size table: kind -> (min cells, max cells, min height, max height).</summary>
        public Dictionary<string, (int Lo, int Hi, int HMin, int HMax)> RoomSizes =
            new Dictionary<string, (int, int, int, int)>
            {
                ["small"] = (6, 8, 128, 160),
                ["medium"] = (9, 12, 160, 224),
                ["large"] = (14, 18, 256, 320),
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
            return c;
        }
    }
}
