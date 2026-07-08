using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Static arrows drawn while the stack is non-empty, from each item's controller
    /// (their sheet) to its target (market slot / boss / player), so every player can
    /// see exactly what is being attacked or targeted and respond to the right thing.
    /// Pooled lines + tips; never blocks raycasts.
    /// </summary>
    public sealed class StackArrows : MonoBehaviour
    {
        public readonly struct Entry
        {
            public readonly Vector2 From;
            public readonly Vector2 To;
            public readonly Color Color;

            public Entry(Vector2 from, Vector2 to, Color color)
            {
                From = from;
                To = to;
                Color = color;
            }
        }

        private UiTheme _theme;
        private RectTransform _container;
        private readonly List<(RectTransform line, Image lineImage, RectTransform tip, Image tipImage)> _pool = new();

        public RectTransform Container => _container;

        public static StackArrows Create(Transform canvasRoot, UiTheme theme, int siblingIndex)
        {
            var rt = UiFactory.CreateRect("StackArrows", canvasRoot);
            UiFactory.Stretch(rt);
            rt.SetSiblingIndex(siblingIndex);
            var arrows = rt.gameObject.AddComponent<StackArrows>();
            arrows._theme = theme;
            arrows._container = rt;
            return arrows;
        }

        public void Render(List<Entry> entries)
        {
            while (_pool.Count < entries.Count)
                _pool.Add(BuildArrow(_pool.Count));

            for (int i = 0; i < _pool.Count; i++)
            {
                var (line, lineImage, tip, tipImage) = _pool[i];
                bool active = i < entries.Count;
                line.gameObject.SetActive(active);
                tip.gameObject.SetActive(active);
                if (!active) continue;

                var e = entries[i];
                var delta = e.To - e.From;
                line.anchoredPosition = e.From;
                line.sizeDelta = new Vector2(Mathf.Max(0f, delta.magnitude - 14f), 7f);
                line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
                lineImage.color = UiPalette.WithAlpha(e.Color, 0.85f);
                tip.anchoredPosition = e.To;
                tipImage.color = e.Color;
            }
        }

        public void Clear() => Render(_empty);

        private static readonly List<Entry> _empty = new();

        private (RectTransform, Image, RectTransform, Image) BuildArrow(int index)
        {
            var lineImage = UiFactory.CreateImage($"Line{index}", _container, null, UiPalette.Danger);
            var line = lineImage.rectTransform;
            line.pivot = new Vector2(0f, 0.5f);
            line.anchorMin = line.anchorMax = new Vector2(0.5f, 0.5f);
            line.sizeDelta = new Vector2(0f, 7f);
            lineImage.raycastTarget = false;

            var tipImage = UiFactory.CreateImage($"Tip{index}", _container, _theme.Circle, UiPalette.Danger);
            var tip = tipImage.rectTransform;
            tip.anchorMin = tip.anchorMax = new Vector2(0.5f, 0.5f);
            tip.sizeDelta = new Vector2(24f, 24f);
            tipImage.raycastTarget = false;

            var tipOutline = tipImage.gameObject.AddComponent<Outline>();
            tipOutline.effectColor = new Color(0f, 0f, 0f, 0.7f);
            tipOutline.effectDistance = new Vector2(1.5f, -1.5f);

            return (line, lineImage, tip, tipImage);
        }
    }
}
