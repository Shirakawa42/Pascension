using System.Collections.Generic;
using Pascension.Game.View;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Lightweight UI "particles": pooled circle Images flung outward with fade, driven
    /// by one Update loop (no ParticleSystem — works in the ScreenSpaceOverlay canvas).
    /// Fire-and-forget; each burst lives well under a second.
    /// </summary>
    public sealed class GlowBurstLayer : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private struct Particle
        {
            public RectTransform Rect;
            public Image Image;
            public Vector2 Velocity;
            public float Life;
            public float MaxLife;
            public float Size;
        }

        private readonly List<Image> _pool = new List<Image>();
        private readonly List<Particle> _active = new List<Particle>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
        }

        public Vector2 ToLocal(RectTransform rt)
        {
            if (rt == null) return Vector2.zero;
            return Container.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
        }

        /// <summary>Radial burst. directionBias (optional) skews the spray (attack slashes).</summary>
        public void Burst(Vector2 local, Color color, int count = 12, float speed = 260f, Vector2? directionBias = null)
        {
            for (int i = 0; i < count; i++)
            {
                var image = Rent();
                if (image == null) return;

                float angle = Random.value * Mathf.PI * 2f;
                var dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                if (directionBias.HasValue)
                    dir = (dir + directionBias.Value.normalized * 1.4f).normalized;

                float size = Random.Range(8f, 16f);
                var rect = image.rectTransform;
                rect.anchoredPosition = local;
                rect.sizeDelta = new Vector2(size, size);
                image.color = UiPalette.WithAlpha(color, 0.95f);
                image.gameObject.SetActive(true);

                _active.Add(new Particle
                {
                    Rect = rect,
                    Image = image,
                    Velocity = dir * speed * Random.Range(0.55f, 1.25f),
                    Life = 0f,
                    MaxLife = Random.Range(0.4f, 0.7f),
                    Size = size
                });
            }
        }

        private void Update()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var p = _active[i];
                p.Life += Time.deltaTime;
                if (p.Life >= p.MaxLife || p.Rect == null)
                {
                    if (p.Image != null) p.Image.gameObject.SetActive(false);
                    _active.RemoveAt(i);
                    continue;
                }
                float x = p.Life / p.MaxLife;
                p.Velocity *= 1f - Time.deltaTime * 2.2f; // drag
                p.Rect.anchoredPosition += p.Velocity * Time.deltaTime;
                float size = p.Size * (1f - Tween.EaseOutCubic(x));
                p.Rect.sizeDelta = new Vector2(size, size);
                var c = p.Image.color;
                p.Image.color = new Color(c.r, c.g, c.b, 0.95f * (1f - x));
                _active[i] = p;
            }
        }

        private Image Rent()
        {
            foreach (var image in _pool)
                if (image != null && !image.gameObject.activeSelf)
                    return image;
            if (_pool.Count >= 48) return null;
            var fresh = UiFactory.CreateImage($"Spark{_pool.Count}", Container, Theme.Circle, Color.white);
            fresh.raycastTarget = false;
            fresh.gameObject.SetActive(false);
            _pool.Add(fresh);
            return fresh;
        }
    }
}
