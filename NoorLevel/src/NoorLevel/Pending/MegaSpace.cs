namespace NoorLevel
{
    /// <summary>STUB: wave 2 (spaces/mega.py, Python class MegaRoomSpace). Until it is ported a mega-room
    /// is a plain room of 'mega' size: registered so graphs with mega_rooms still build.</summary>
    public class MegaSpace : RoomSpace
    {
        public MegaSpace()
        {
            Name = "mega";
            Kinds = new[] { "megaroom" };
        }
    }
}
