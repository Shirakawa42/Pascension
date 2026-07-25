using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// Star-twinkle overlay for the SoI "condition met" channel: a handful of 4-point
    /// stars scattered inside the card rect, each fading in/out on its own sin phase and
    /// re-seating at a fresh random position each cycle (at zero alpha, so the move is
    /// never seen). Built once per CardView by CardViewFactory; driven by
    /// CardView.SetSparkle. Purely decorative — never blocks raycasts.
    /// </summary>
    public sealed class SparkleOverlay : MonoBehaviour
    {
        private const int StarCount = 5;
        private const float EdgeInset = 24f;

        private readonly StarGraphic[] _stars = new StarGraphic[StarCount];
        private readonly float[] _speed = new float[StarCount];
        private readonly float[] _phase = new float[StarCount];
        private readonly int[] _cycle = new int[StarCount];
        private Color _color = Color.white;

        public static SparkleOverlay Create(RectTransform parent)
        {
            var rect = UiFactory.CreateRect("Sparkles", parent);
            UiFactory.Stretch(rect);
            var overlay = rect.gameObject.AddComponent<SparkleOverlay>();
            for (int i = 0; i < StarCount; i++)
            {
                var starRect = UiFactory.CreateRect("Star" + i, rect);
                starRect.gameObject.AddComponent<CanvasRenderer>();
                var star = starRect.gameObject.AddComponent<StarGraphic>();
                star.raycastTarget = false;
                star.color = Color.clear;
                overlay._stars[i] = star;
                overlay._speed[i] = Random.Range(1.5f, 2.7f);
                overlay._phase[i] = Random.Range(0f, Mathf.PI * 2f);
            }
            rect.gameObject.SetActive(false);
            return overlay;
        }

        public void Show(Color tint)
        {
            // Faction frame colors are dark — pull the twinkle toward white so it reads
            // on top of busy card art while keeping the faction hue.
            _color = Color.Lerp(tint, Color.white, 0.65f);
            for (int i = 0; i < StarCount; i++)
                _cycle[i] = int.MinValue; // force a fresh scatter on the first frame
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        private void Update()
        {
            var area = ((RectTransform)transform).rect;
            for (int i = 0; i < StarCount; i++)
            {
                float t = Time.time * _speed[i] + _phase[i];
                int cycle = Mathf.FloorToInt(t / Mathf.PI);
                if (cycle != _cycle[i])
                {
                    _cycle[i] = cycle;
                    Reseat(_stars[i], area);
                }
                // 0 → peak → 0 per half-period, squared for a soft twinkle; zero at the
                // cycle boundary, so the re-seat above is invisible.
                float wave = Mathf.Abs(Mathf.Sin(t));
                _stars[i].color = new Color(_color.r, _color.g, _color.b, wave * wave);
            }
        }

        private static void Reseat(StarGraphic star, Rect area)
        {
            // Sized for readability at board scales (~0.44-0.62) — cards render the
            // overlay scaled down with the rest of the CardView.
            float size = Random.Range(14f, 26f);
            star.rectTransform.sizeDelta = new Vector2(size, size);
            star.rectTransform.anchoredPosition = new Vector2(
                Random.Range(area.xMin + EdgeInset, area.xMax - EdgeInset),
                Random.Range(area.yMin + EdgeInset, area.yMax - EdgeInset));
        }
    }
}
