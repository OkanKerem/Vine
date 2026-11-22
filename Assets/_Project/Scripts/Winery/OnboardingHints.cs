using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// One-time contextual tips for the winemaking loop (local player only, drawn by <see cref="WineryHUD"/>).
    /// Each tip fires once, at the moment it becomes relevant (e.g. the first time the bucket holds grapes),
    /// and is remembered in PlayerPrefs so it never repeats. A newer tip replaces the one on screen.
    /// Reset with Tools > Vineyard > Reset Onboarding Hints.
    /// </summary>
    public class OnboardingHints
    {
        const string PrefPrefix = "Vineyard.Hint.";
        static readonly string[] AllIds = { "start", "grapes", "press_handle", "press_collect", "juice", "ferment_wait", "wine" };

        public const float Duration = 8f;
        const float StartDelay = 1.5f;
        /// <summary>How long after looking away a part still counts as "what the player just used" (s).</summary>
        const float AimMemory = 1.5f;

        public string Text { get; private set; }
        public float ShownAt { get; private set; } = -999f;
        public bool Visible => Text != null && Time.unscaledTime - ShownAt < Duration;

        bool initialized;
        float startTime;
        ResourceType lastType;
        int lastAmount;
        InteractionPart lastPart;
        float lastPartTime = -999f;

        public void Tick(PlayerInteractor interactor, PlayerBucket bucket)
        {
            if (interactor == null || bucket == null) return;
            float now = Time.unscaledTime;
            if (!initialized)
            {
                initialized = true;
                startTime = now;
                lastType = bucket.Type;
                lastAmount = bucket.Amount;
            }

            var current = interactor.Current;
            if (current != null) { lastPart = current.part; lastPartTime = now; }
            InteractionPart recentPart = now - lastPartTime <= AimMemory ? lastPart : InteractionPart.None;

            if (now - startTime >= StartDelay && bucket.HasBucket && bucket.Type == ResourceType.Empty)
                Show("start", "Walk up to a grapevine and click it to harvest grapes into your bucket.");

            ResourceType type = bucket.Type;
            int amount = bucket.Amount;

            // The bucket just started holding something new.
            if (type != lastType && amount > 0)
            {
                switch (type)
                {
                    case ResourceType.Grapes:
                        Show("grapes", "Take the grapes to the Grape Press and click its hopper on top to pour them in.");
                        break;
                    case ResourceType.GrapeJuice:
                        Show("juice", "Carry the juice to the Fermentation Machine and pour it into the input.");
                        break;
                    case ResourceType.Wine:
                        Show("wine", "Pour the wine into the Giant Wine Bottle through its copper funnel.");
                        break;
                }
            }

            // The bucket was just poured into a station.
            if (amount < lastAmount && !bucket.IsPouring)
            {
                if (lastType == ResourceType.Grapes && recentPart == InteractionPart.PressInput)
                    Show("press_handle", "Grapes are in. Click the press handle to squeeze them.");
                else if (lastType == ResourceType.GrapeJuice && recentPart == InteractionPart.FermenterInput)
                    Show("ferment_wait", "Fermenting takes a while. When the wine is ready, collect it from the tap.");
            }

            // The player just started the press.
            if (current != null && current.station is GrapePressController press &&
                press.State == GrapePressController.PressState.Pressing && recentPart == InteractionPart.PressHandle)
                Show("press_collect", "Juice drips into the bucket under the spout. Click it when pressing is done to collect the juice.");

            lastType = type;
            lastAmount = amount;
        }

        void Show(string id, string text)
        {
            string key = PrefPrefix + id;
            try
            {
                if (PlayerPrefs.GetInt(key, 0) == 1) return;
                PlayerPrefs.SetInt(key, 1);
                PlayerPrefs.Save();
            }
            catch (System.Exception) { /* prefs unavailable: still show, may repeat next session */ }
            Text = text;
            ShownAt = Time.unscaledTime;
        }

        public static void ResetAll()
        {
            foreach (var id in AllIds) PlayerPrefs.DeleteKey(PrefPrefix + id);
            PlayerPrefs.Save();
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Tools/Vineyard/Reset Onboarding Hints")]
        static void ResetMenu()
        {
            ResetAll();
            Debug.Log("[Vineyard] Onboarding hints reset — they will show again.");
        }
#endif
    }
}
