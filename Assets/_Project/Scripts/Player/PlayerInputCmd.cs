namespace Vineyard.Player
{
    /// <summary>
    /// One fixed-tick worth of player input. Sent from the owning client to the
    /// host; the host simulates movement from it. Mirror's weaver generates
    /// the serializer for this struct automatically.
    /// </summary>
    public struct PlayerInputCmd
    {
        public uint seq;      // increasing sequence number, used for reconciliation
        public float moveX;   // strafe  [-1..1]
        public float moveY;   // forward [-1..1]
        public float yaw;     // look yaw in degrees (client-driven, as in most FPS games)
        public bool jump;
        public bool sprint;
    }
}
