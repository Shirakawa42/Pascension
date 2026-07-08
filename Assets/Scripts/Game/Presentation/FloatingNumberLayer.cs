using System.Collections;
using System.Collections.Generic;
using Pascension.Game.View;
using TMPro;
using UnityEngine;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Pooled floating rich-text numbers ("-3 dmg", "+2 xp") that rise and fade over a
    /// point of interest. Supports TMP inline icon sprites once the icon asset exists.
    /// Fire-and-forget.
    /// </summary>
    public sealed class FloatingNumberLayer : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private readonly List<TextMeshProUGUI> _pool = new List<TextMeshProUGUI>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
        }

        public Vector2 ToLocal(RectTransform rt)
        {
            if (rt == null) return Vector2.zero;
            return Container.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
        }

        public void Spawn(Vector2 local, string richText, Color color, float size = 30f)
        {
            var tmp = Rent();
            if (tmp == null) return;

            tmp.text = richText;
            tmp.color = color;
            tmp.fontSize = size;
            if (Theme != null && Theme.Icons != null)
                tmp.spriteAsset = Theme.Icons;
            tmp.rectTransform.anchoredPosition = local + new Vector2(Random.Range(-14f, 14f), 6f);
            tmp.gameObject.SetActive(true);
            if (isActiveAndEnabled)
                StartCoroutine(Rise(tmp));
            else
                tmp.gameObject.SetActive(false);
        }

        private IEnumerator Rise(TextMeshProUGUI tmp)
        {
            var start = tmp.rectTransform.anchoredPosition;
            float t = 0f;
            const float duration = 0.85f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (tmp == null) yield break;
                float x = Mathf.Clamp01(t / duration);
                tmp.rectTransform.anchoredPosition = start + new Vector2(0f, Tween.EaseOutCubic(x) * 70f);
                var c = tmp.color;
                tmp.color = new Color(c.r, c.g, c.b, x < 0.55f ? 1f : 1f - (x - 0.55f) / 0.45f);
                yield return null;
            }
            tmp.gameObject.SetActive(false);
        }

        private TextMeshProUGUI Rent()
        {
            foreach (var tmp in _pool)
                if (tmp != null && !tmp.gameObject.activeSelf)
                    return tmp;
            if (_pool.Count >= 10) return null;
            var fresh = UiFactory.CreateText(Theme, $"Float{_pool.Count}", Container, "", 30f,
                Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
            fresh.rectTransform.sizeDelta = new Vector2(260f, 44f);
            var outline = fresh.gameObject.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            fresh.gameObject.SetActive(false);
            _pool.Add(fresh);
            return fresh;
        }
    }
}
