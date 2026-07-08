using System.Collections.Generic;
using Pascension.Engine.Core;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// Runtime-generated vertical gradient sprites used as card-art fallback when a
    /// definition has no sprite in the CardArtIndex. Cached per tier.
    /// </summary>
    public static class TierGradients
    {
        private static readonly Dictionary<CardTier, Sprite> Cache = new Dictionary<CardTier, Sprite>();

        public static Sprite Sprite(CardTier tier)
        {
            if (Cache.TryGetValue(tier, out var cached) && cached != null)
                return cached;

            var top = UiPalette.TierColor(tier) * 0.75f;
            top.a = 1f;
            var bottom = UiPalette.TierColor(tier) * 0.22f;
            bottom.a = 1f;

            const int h = 64;
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int y = 0; y < h; y++)
                tex.SetPixel(0, y, Color.Lerp(bottom, top, (float)y / (h - 1)));
            tex.Apply();

            var sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, 1, h), new Vector2(0.5f, 0.5f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Cache[tier] = sprite;
            return sprite;
        }
    }
}
