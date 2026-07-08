using Pascension.Engine.Core;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>Dark tavern-table palette. Single source of truth for all UI colors.</summary>
    public static class UiPalette
    {
        public static readonly Color Background = Rgb(0x1A, 0x15, 0x12);
        public static readonly Color Panel = Rgb(0x26, 0x20, 0x1B);
        public static readonly Color PanelLight = Rgb(0x33, 0x2B, 0x24);
        public static readonly Color Border = Rgb(0x4A, 0x3F, 0x33);
        public static readonly Color Gold = Rgb(0xD4, 0xAF, 0x37);
        public static readonly Color GoldDim = Rgb(0x8A, 0x74, 0x2E);
        public static readonly Color TextMain = Rgb(0xED, 0xE4, 0xD3);
        public static readonly Color TextDim = Rgb(0xA9, 0x9F, 0x8C);
        public static readonly Color Danger = Rgb(0xC0, 0x39, 0x2B);
        public static readonly Color Good = Rgb(0x3E, 0x8E, 0x5A);
        public static readonly Color TargetBlue = Rgb(0x4F, 0xA3, 0xD1);

        public static readonly Color TierDefault = Rgb(0x8A, 0x8A, 0x8A);
        public static readonly Color TierBasic = Rgb(0x5A, 0x7D, 0x9A);
        public static readonly Color TierAdvanced = Rgb(0x8B, 0x5C, 0xF6);
        public static readonly Color TierElite = Rgb(0xD4, 0xAF, 0x37);
        public static readonly Color TierBoss = Rgb(0xB0, 0x1E, 0x2E);

        private static readonly Color[] PlayerColors =
        {
            Rgb(0xE4, 0xC0, 0x5A), // P0 gold
            Rgb(0x6F, 0xA8, 0xDC), // P1 blue
            Rgb(0xD4, 0x6A, 0x6A), // P2 red
            Rgb(0x93, 0xC4, 0x7D)  // P3 green
        };

        public static Color TierColor(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return TierBasic;
                case CardTier.Advanced: return TierAdvanced;
                case CardTier.Elite: return TierElite;
                case CardTier.Boss: return TierBoss;
                default: return TierDefault;
            }
        }

        public static Color PlayerColor(int index) =>
            PlayerColors[Mathf.Abs(index) % PlayerColors.Length];

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        private static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);
    }
}
