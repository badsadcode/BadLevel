namespace NoorLevel
{
    /// <summary>STUB: wave 2 (spaces/tower_space.py). Vertical (a connector to placement and routing);
    /// it proposes nothing, and no tower node is made while tower.py is a stub.</summary>
    public class TowerSpace : SpaceType
    {
        public TowerSpace()
        {
            Name = "tower";
            Kinds = new[] { "tower" };
            Vertical = true;
        }

        public override Room Propose(Node node, int floor, GenConfig cfg, Rng rng, bool multi) { /* STUB: wave 2 */ return null; }
    }
}
