namespace Vineyard.UI
{
    /// <summary>
    /// Set while a modal gameplay panel (e.g. the upgrade panel) is open: the cursor stays free for clicking,
    /// and the player's look / movement / click-to-lock handling stands down until the panel closes.
    /// </summary>
    public static class UiFocus
    {
        public static bool Modal;
    }
}
