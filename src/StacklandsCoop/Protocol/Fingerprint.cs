using System.Collections.Generic;
using System.Text;

namespace StacklandsCoop.Protocol
{
    /// <summary>
    /// Handshake fingerprint: the sorted "id@version" list of every loaded mod.
    /// Both sides must match, otherwise a mod that adds cards or changes rules would
    /// silently desync the world.
    /// </summary>
    public static class Fingerprint
    {
        public static string ModsHash()
        {
            List<string> entries = ModsList();
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < entries.Count; i++)
                {
                    string entry = entries[i];
                    for (int c = 0; c < entry.Length; c++)
                    {
                        hash ^= entry[c];
                        hash *= 16777619u;
                    }

                    hash ^= (byte)'\n';
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }

        /// <summary>Sorted "id@version" lines; also used to explain a mismatch.</summary>
        public static List<string> ModsList()
        {
            var entries = new List<string>();
            List<Mod> loaded = ModManager.LoadedMods;
            if (loaded == null)
            {
                return entries;
            }

            for (int i = 0; i < loaded.Count; i++)
            {
                Mod mod = loaded[i];
                if (mod == null || mod.Manifest == null)
                {
                    continue;
                }

                entries.Add(mod.Manifest.Id + "@" + mod.Manifest.Version);
            }

            entries.Sort(string.CompareOrdinal);
            return entries;
        }

        public static string Describe()
        {
            return string.Join(", ", ModsList().ToArray());
        }

        /// <summary>
        /// Content fingerprint of the loaded card and booster definitions (DLC included).
        /// Empty when no world is loaded yet, in which case the host cannot check it.
        /// </summary>
        public static string DefsHash()
        {
            WorldManager wm = WorldManager.instance;
            if (wm == null || wm.CardDataPrefabs == null)
            {
                return string.Empty;
            }

            var ids = new List<string>();
            for (int i = 0; i < wm.CardDataPrefabs.Count; i++)
            {
                CardData prefab = wm.CardDataPrefabs[i];
                if (prefab != null)
                {
                    ids.Add(prefab.Id);
                }
            }

            if (wm.BoosterPackDatas != null)
            {
                for (int i = 0; i < wm.BoosterPackDatas.Count; i++)
                {
                    BoosterpackData pack = wm.BoosterPackDatas[i];
                    if (pack != null)
                    {
                        ids.Add("pack:" + pack.BoosterId);
                    }
                }
            }

            ids.Sort(string.CompareOrdinal);
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    for (int c = 0; c < id.Length; c++)
                    {
                        hash ^= id[c];
                        hash *= 16777619u;
                    }

                    hash ^= (byte)'\n';
                    hash *= 16777619u;
                }

                return hash.ToString("x8");
            }
        }
    }
}
