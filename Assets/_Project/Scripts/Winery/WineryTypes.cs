namespace Vineyard.Winery
{
    /// <summary>What a bucket (or a machine slot) holds. A bucket holds one type at a time.</summary>
    public enum ResourceType : byte { Empty = 0, Grapes = 1, GrapeJuice = 2, Wine = 3 }

    /// <summary>Which part of a station the player is aiming at.</summary>
    public enum InteractionPart : byte
    {
        None = 0,
        Harvest = 1,
        PressInput = 10, PressHandle = 11, PressOutput = 12,
        FermenterInput = 20, FermenterOutput = 21,
        BottleFunnel = 30, BottleBody = 31, BottleTap = 32,
        BucketPickup = 40,
        HammerPickup = 41,
    }

    /// <summary>Anything that shows bucket contents (the carried bucket, or one standing on the ground).</summary>
    public interface IBucketContents
    {
        ResourceType Type { get; }
        int Amount { get; }
        float Fill { get; }
        /// <summary>0..1 through a tip-out animation, or -1 when not pouring.</summary>
        float PourProgress { get; }
        ResourceType PourType { get; }
    }

    /// <summary>One line of the upgrade panel: "Tank size   20 L → 30 L". <c>next</c> is null at max level.</summary>
    public struct UpgradeStat
    {
        public string label, now, next;
        public UpgradeStat(string label, string now, string next) { this.label = label; this.now = now; this.next = next; }
    }

    /// <summary>A station that can be upgraded on its own (aim at it, press U). Levels 0..MaxUpgradeLevel.</summary>
    public interface IUpgradeable
    {
        int UpgradeLevel { get; }
        int MaxUpgradeLevel { get; }
        /// <summary>Price of the next level (0 when maxed).</summary>
        float NextUpgradeCost { get; }
        /// <summary>Panel heading, e.g. "Fermentation Machine".</summary>
        string UpgradeTitle { get; }
        /// <summary>What upgrading does, in a sentence or two.</summary>
        string UpgradeDescription { get; }
        /// <summary>Live state of the machine ("Fermenting 12 L — 45%").</summary>
        string UpgradeStatus { get; }
        /// <summary>Current values and what the next level changes them to.</summary>
        UpgradeStat[] UpgradeStats { get; }
        /// <summary>Host: pay and apply the next level. Returns the message for the buyer.</summary>
        string ServerUpgrade(out bool success, out float paid);
    }

    /// <summary>What the HUD shows for the targeted object (computed on the client from synced state).</summary>
    public struct InteractionPrompt
    {
        public string title;       // "Grape Press"
        public string action;      // "Add Grapes — 6/20"   (shown with the click hint when actionable)
        public string status;      // "Grapes 6/20 · Juice 0 L"
        public float progress;     // 0..1, or < 0 for no bar
        public bool actionable;    // false: clicking shows 'blockReason' instead of sending a request
        public string blockReason; // "Bucket Full", "Processing...", ...
    }

    public static class ResourceTypeExtensions
    {
        public static string Label(this ResourceType t)
        {
            switch (t)
            {
                case ResourceType.Grapes: return "Grapes";
                case ResourceType.GrapeJuice: return "Grape Juice";
                case ResourceType.Wine: return "Wine";
                default: return "Empty";
            }
        }

        public static bool IsLiquid(this ResourceType t) => t == ResourceType.GrapeJuice || t == ResourceType.Wine;

        /// <summary>"8 Grapes", "10 L Wine".</summary>
        public static string Amount(this ResourceType t, int n) => t.IsLiquid() ? $"{n} L {t.Label()}" : $"{n} {t.Label()}";
    }
}
