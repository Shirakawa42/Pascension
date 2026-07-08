using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// Maps card/hero definition ids to art sprites. The asset lives at
    /// Assets/Art/CardArtIndex.asset and is populated by the art pipeline tooling.
    /// ⚠ The art tool depends on this exact shape (Entry { id, sprite }, entries,
    /// GetSprite) — do not rename members.
    /// </summary>
    public sealed class CardArtIndex : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string id;
            public Sprite sprite;
        }

        public List<Entry> entries = new List<Entry>();

        public Sprite GetSprite(string id)
        {
            if (string.IsNullOrEmpty(id) || entries == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && e.id == id)
                    return e.sprite;
            }
            return null;
        }
    }
}
