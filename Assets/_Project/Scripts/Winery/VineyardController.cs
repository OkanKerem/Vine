using Mirror;
using UnityEngine;

namespace Vineyard.Winery
{
    /// <summary>
    /// Host-authoritative grape counts for every grapevine (one synced list instead of one network object
    /// per vine). Regrowth runs only on the host; clients just display the replicated counts.
    /// </summary>
    public class VineyardController : WineryStation
    {
        public GrapevineController[] vines;

        readonly SyncList<byte> grapes = new SyncList<byte>();
        float[] regrowTimers;
        double[] lastHarvest;

        public int Available(int i) => i >= 0 && i < grapes.Count ? grapes[i] : (vines != null && i < vines.Length && vines[i] ? vines[i].maxGrapes : 0);

        public override void OnStartServer()
        {
            grapes.Clear();
            foreach (var v in vines) grapes.Add((byte)Mathf.Clamp(v ? v.maxGrapes : 0, 0, 255));
            regrowTimers = new float[vines.Length];
            lastHarvest = new double[vines.Length];
        }

        void Update()
        {
            if (isServer && regrowTimers != null)
            {
                for (int i = 0; i < grapes.Count; i++)
                {
                    var v = vines[i];
                    if (!v || grapes[i] >= v.maxGrapes) { regrowTimers[i] = 0f; continue; }
                    regrowTimers[i] += Time.deltaTime;
                    if (regrowTimers[i] >= v.regrowSeconds)
                    {
                        regrowTimers[i] -= v.regrowSeconds;
                        grapes[i] = (byte)(grapes[i] + 1);      // one unit at a time, never all at once
                    }
                }
            }
            for (int i = 0; i < vines.Length; i++)
                if (vines[i]) vines[i].ShowGrapes(Available(i));
        }

        public override InteractionPrompt GetPrompt(WineryInteractable target, PlayerBucket bucket)
        {
            int i = target.index;
            var v = vines[i];
            int avail = Available(i);
            string title = "Grapevine";
            string status = avail >= v.maxGrapes ? $"{avail} / {v.maxGrapes} grapes" : $"{avail} / {v.maxGrapes} grapes · regrowing";
            float progress = avail / (float)v.maxGrapes;
            if (avail <= 0) return Blocked(title, status, "No Grapes — Regrowing", progress);
            string why = CannotReceive(bucket, ResourceType.Grapes);
            if (why != null) return Blocked(title, status, why, progress);
            return Action(title, status, $"Harvest Grapes — {avail} Available", progress);
        }

        [Server]
        public override string ServerInteract(WineryInteractable target, PlayerBucket bucket, out bool success, out string floatText)
        {
            success = false; floatText = null;
            int i = target.index;
            if (i < 0 || i >= grapes.Count || target.part != InteractionPart.Harvest) return null;
            var v = vines[i];
            if (Now - lastHarvest[i] < v.harvestCooldown) return null;            // duplicate request
            int avail = grapes[i];
            if (avail <= 0) return "No Grapes — Regrowing";
            if (!bucket.CanHold(ResourceType.Grapes)) return $"Wrong Resource Type — bucket holds {bucket.Type.Label()}";
            if (bucket.Free <= 0) return "Bucket Full";

            int n = bucket.ServerAdd(ResourceType.Grapes, avail);                  // MIN(available, free space)
            if (n <= 0) return "Bucket Full";
            grapes[i] = (byte)(avail - n);
            lastHarvest[i] = Now;
            RpcHarvested(i);
            success = true;
            floatText = $"+{n} Grapes";
            return bucket.Free == 0 ? $"Harvested {n} grapes — Bucket Full" : $"Harvested {n} grapes";
        }

        [ClientRpc]
        void RpcHarvested(int i)
        {
            if (i >= 0 && i < vines.Length && vines[i]) vines[i].PlayHarvestFeedback();
        }
    }
}
