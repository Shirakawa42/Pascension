using Pascension.Game.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>Small building blocks for the SoI stats screen: stat tiles, winrate
    /// bars, section headers, captioned cards and result pills. Pure layout — every
    /// number is computed in Shards.Stats before it gets here.</summary>
    internal static class SoiStatsWidgets
    {
        public const float TileW = 250f, TileH = 120f;

        /// <summary>Anchor + pivot top-left, explicit rect — the stats screen lays
        /// everything out with a y-cursor from the content's top-left corner.</summary>
        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        public static RectTransform StatTile(UiTheme theme, Transform parent, float x, float y,
            string caption, string value, string sub = null)
        {
            var panel = UiFactory.CreatePanel(theme, "Tile", parent, UiPalette.PanelLight);
            Place(panel.rectTransform, x, y, TileW, TileH);

            var cap = UiFactory.CreateText(theme, "Caption", panel.transform, caption, 13f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(cap.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -10f),
                new Vector2(TileW - 16f, 18f));

            var val = UiFactory.CreateText(theme, "Value", panel.transform, value, 34f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(val.rectTransform, new Vector2(0.5f, 0.5f),
                new Vector2(0f, sub != null ? 0f : -8f), new Vector2(TileW - 16f, 44f));
            val.enableAutoSizing = true;
            val.fontSizeMin = 14f;
            val.fontSizeMax = 34f;
            if (theme.Icons != null) val.spriteAsset = theme.Icons;

            if (sub != null)
            {
                var subText = UiFactory.CreateText(theme, "Sub", panel.transform, sub, 13f,
                    UiPalette.TextDim, TextAlignmentOptions.Center);
                UiFactory.Place(subText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 8f),
                    new Vector2(TileW - 16f, 18f));
            }
            return panel.rectTransform;
        }

        /// <summary>Gold section title over a 2px hairline (CardListModal header idiom).
        /// Occupies ~28px; callers advance their y-cursor themselves.</summary>
        public static void SectionHeader(UiTheme theme, Transform parent, float y, string text,
            float width = 1640f)
        {
            var label = UiFactory.CreateText(theme, "Section", parent, text, 18f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            Place(label.rectTransform, 24f, y, width, 24f);
            var line = UiFactory.CreateImage("Hairline", parent, null,
                UiPalette.WithAlpha(UiPalette.PanelLight, 0.9f));
            Place(line.rectTransform, 24f, y - 26f, width, 2f);
        }

        /// <summary>Track + fill only, for custom rows.</summary>
        public static void Bar(UiTheme theme, Transform parent, float x, float y, float w, float h,
            float fraction, Color fillColor)
        {
            var track = UiFactory.CreateImage("Track", parent, theme.Rounded, UiPalette.PanelLight);
            Place(track.rectTransform, x, y, w, h);
            float clamped = Mathf.Clamp01(fraction);
            if (clamped <= 0f) return;
            var fill = UiFactory.CreateImage("Fill", track.transform, theme.Rounded, fillColor);
            fill.rectTransform.anchorMin = fill.rectTransform.anchorMax =
                fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            fill.rectTransform.anchoredPosition = new Vector2(2f, 0f);
            fill.rectTransform.sizeDelta = new Vector2(Mathf.Max(4f, clamped * (w - 4f)), h - 4f);
        }

        /// <summary>Standard labeled winrate row: label 16f, 900-wide track, right text.</summary>
        public static void BarRow(UiTheme theme, Transform parent, float y, string label,
            float fraction, Color fillColor, string rightText)
        {
            var text = UiFactory.CreateText(theme, "Label", parent, label, 16f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            Place(text.rectTransform, 24f, y, 300f, 24f);
            Bar(theme, parent, 340f, y - 3f, 900f, 18f, fraction, fillColor);
            var right = UiFactory.CreateText(theme, "Right", parent, rightText, 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            Place(right.rectTransform, 1260f, y, 400f, 24f);
        }

        /// <summary>Card bound to a SoI def (or "soichar:" portrait) with up to two
        /// caption lines below. Null captions are skipped. Returns the CardView (its
        /// container box is the positioned rect, view.Rect.parent).</summary>
        public static CardView CardWithCaption(UiTheme theme, Transform parent, float x, float y,
            string defId, float scale, string caption1, string caption2)
        {
            float cardW = CardView.Width * scale, cardH = CardView.Height * scale;
            float captions = (caption1 != null ? 20f : 0f) + (caption2 != null ? 18f : 0f);
            var box = UiFactory.CreateRect("CaptionedCard", parent);
            Place(box, x, y, cardW, cardH + captions + 6f);

            var view = CardViewFactory.Create(box, theme, scale);
            view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0f, 1f);
            view.Rect.anchoredPosition = Vector2.zero;
            view.BindDef(defId);
            view.SetRaycastable(false);
            if (view.Group != null) view.Group.blocksRaycasts = false;

            float cy = -cardH - 4f;
            if (caption1 != null)
            {
                var c1 = UiFactory.CreateText(theme, "Caption1", box, caption1, 13f,
                    UiPalette.TextMain, TextAlignmentOptions.Center);
                var rt = c1.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, cy);
                rt.sizeDelta = new Vector2(cardW + 28f, 18f);
                cy -= 20f;
            }
            if (caption2 != null)
            {
                var c2 = UiFactory.CreateText(theme, "Caption2", box, caption2, 13f,
                    UiPalette.TextDim, TextAlignmentOptions.Center);
                var rt = c2.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, cy);
                rt.sizeDelta = new Vector2(cardW + 28f, 18f);
            }
            return view;
        }

        /// <summary>WIN/LOSS/TIE pill for history rows.</summary>
        public static void ResultPill(UiTheme theme, Transform parent, float x, float y,
            string text, Color fill, Color textColor)
        {
            var pill = UiFactory.CreateImage("Pill", parent, theme.Rounded, fill);
            Place(pill.rectTransform, x, y, 64f, 26f);
            var label = UiFactory.CreateText(theme, "Label", pill.transform, text, 14f,
                textColor, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(label.rectTransform, 2, 1, 2, 1);
            label.enableAutoSizing = true;
            label.fontSizeMin = 8f;
            label.fontSizeMax = 14f;
        }
    }
}
