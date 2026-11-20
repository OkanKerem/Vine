using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Base for every shared production station. The station object holds the host-authoritative state
    /// (SyncVars); the visual model lives elsewhere in the scene and is driven from the synced state.
    /// Keeping the NetworkIdentity off the visual models matters: Mirror disables networked scene objects
    /// until the network starts, and the village must still look complete in the menu.
    /// </summary>
    public abstract class WineryStation : NetworkBehaviour
    {
        public string displayName = "Station";
        [Tooltip("Every clickable part of this station.")]
        public WineryInteractable[] interactables;

        /// <summary>Synced, server-driven clock (identical on host and clients).</summary>
        protected static double Now => NetworkTime.time;

        public WineryInteractable Find(InteractionPart part, int index)
        {
            if (interactables == null) return null;
            foreach (var i in interactables)
                if (i && i.part == part && i.index == index) return i;
            return null;
        }

        /// <summary>Distance from <paramref name="p"/> to the closest part that serves (part, index).</summary>
        public float DistanceTo(InteractionPart part, int index, Vector3 p)
        {
            float best = float.MaxValue;
            if (interactables != null)
                foreach (var i in interactables)
                    if (i && i.part == part && i.index == index) best = Mathf.Min(best, i.DistanceFrom(p));
            return best;
        }

        /// <summary>Client side: what to show for the aimed part. Must not change any state.</summary>
        public abstract InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket);

        /// <summary>
        /// Host side: validate and apply the request. Returns the feedback message for the player.
        /// Called only from <see cref="PlayerInteractor"/>'s command, after the distance check.
        /// </summary>
        public abstract string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText);

        // ------------------------------------------------------------------ helpers for subclasses

        protected static InteractionPrompt Blocked(string title, string status, string reason, float progress = -1f) =>
            new InteractionPrompt { title = title, status = status, action = reason, blockReason = reason, actionable = false, progress = progress };

        protected static InteractionPrompt Action(string title, string status, string action, float progress = -1f) =>
            new InteractionPrompt { title = title, status = status, action = action, actionable = true, progress = progress };

        /// <summary>Why a bucket cannot take <paramref name="t"/>, or null if it can take at least one unit.</summary>
        protected static string CannotReceive(PlayerBucket bucket, ResourceType t)
        {
            if (bucket == null) return "No Bucket";
            if (!bucket.CanHold(t)) return $"Wrong Resource Type — bucket holds {bucket.Type.Label()}";
            if (bucket.Free <= 0) return "Bucket Full";
            return null;
        }

        protected static float Ease(float t) => t * t * (3f - 2f * t);

        /// <summary>
        /// Wraps <paramref name="target"/> in a new parent placed at <paramref name="worldPivot"/>, so it can be
        /// rotated / scaled around that point. Runtime only: the scene asset is never modified.
        /// </summary>
        protected static Transform MakePivot(Transform target, Vector3 worldPivot, string name)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(target.parent, false);
            pivot.position = worldPivot;
            pivot.rotation = target.parent ? target.parent.rotation : Quaternion.identity;
            target.SetParent(pivot, true);
            return pivot;
        }

        protected static Bounds RendererBounds(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }
    }
}
