namespace NoorLevel
{
    /// <summary>STUB: wave 2 (spaces/open_area.py). Until it is ported a courtyard is a plain room
    /// (enclosed, 'yard' size): registered so graphs with open_areas still build.</summary>
    public class OpenAreaSpace : RoomSpace
    {
        public OpenAreaSpace()
        {
            Name = "open_area";
            Kinds = new[] { "courtyard" };
        }
    }
}
