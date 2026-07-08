using UnityEngine;

namespace Pascension.Net
{
    /// <summary>
    /// Persistent per-installation identity. The GUID travels in the connection payload
    /// and is what lets a reconnecting client reclaim its seat mid-game.
    /// </summary>
    public static class ClientIdentity
    {
        private const string GuidKeyPrefix = "pascension.client_guid";
        private const string NameKey = "pascension.player_name";

        private static string GuidKey
        {
            get
            {
#if UNITY_EDITOR
                // MPPM virtual players share the editor PlayerPrefs store (registry keyed by
                // company/product), but each virtual player has its own project data path.
                // Salting the key keeps identities distinct AND stable per virtual player.
                return GuidKeyPrefix + "." + StableHash(Application.dataPath);
#else
                return GuidKeyPrefix;
#endif
            }
        }

        /// <summary>Stable 32-hex-char identity, created on first use.</summary>
        public static string Guid
        {
            get
            {
                string existing = PlayerPrefs.GetString(GuidKey, "");
                if (string.IsNullOrEmpty(existing))
                {
                    existing = System.Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(GuidKey, existing);
                    PlayerPrefs.Save();
                }
                return existing;
            }
        }

        /// <summary>Display name sent to the host; defaults to a GUID-derived tag.</summary>
        public static string PlayerName
        {
            get
            {
                string name = PlayerPrefs.GetString(NameKey, "");
                return string.IsNullOrEmpty(name) ? "Player-" + Guid.Substring(0, 4) : name;
            }
            set
            {
                PlayerPrefs.SetString(NameKey, value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>FNV-1a — string.GetHashCode is not stable across runtimes/processes.</summary>
        private static uint StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in s)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash;
            }
        }
    }
}
