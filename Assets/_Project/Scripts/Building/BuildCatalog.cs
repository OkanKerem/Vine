using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vineyard.Building
{
    /// <summary>
    /// Everything the player can build with the hammer, in the order shown in the build bar.
    /// Lives at Resources/Buildables/BuildCatalog.asset (created by Tools > Vineyard > Setup Build System);
    /// names, prices and descriptions can be tuned there — re-running the setup keeps them.
    /// Host and clients read the same asset: the host spends the money, clients only show the price.
    /// </summary>
    [CreateAssetMenu(menuName = "Vineyard/Build Catalog", fileName = "BuildCatalog")]
    public class BuildCatalog : ScriptableObject
    {
        public const string ResourcePath = "Buildables/BuildCatalog";

        [Serializable]
        public class Entry
        {
            [Tooltip("Stable key used by the setup tool to find this entry again.")]
            public string id;
            public string displayName;
            [TextArea] public string description;
            [Min(0f)] public float cost = 1f;
            [Tooltip("Icon shown in the build bar (transparent background).")]
            public Texture2D icon;
            [Tooltip("Networked prefab spawned by the host (NetworkIdentity + station on the root, pivot at the bottom centre).")]
            public GameObject prefab;
        }

        public List<Entry> entries = new List<Entry>();

        [Header("Placement preview")]
        [Tooltip("Transparent white: the spot is free.")]
        public Material validMaterial;
        [Tooltip("Transparent red: the spot is blocked / too steep / not enough money.")]
        public Material invalidMaterial;

        static BuildCatalog cached;
        public static BuildCatalog Instance => cached != null ? cached : cached = Resources.Load<BuildCatalog>(ResourcePath);

        public Entry Get(int index) => index >= 0 && index < entries.Count ? entries[index] : null;
    }
}
