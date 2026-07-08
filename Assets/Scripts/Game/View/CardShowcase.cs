using System.Collections;
using Pascension.Game.Presentation;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// The played-card reveal: a large proxy card flies to center screen, glows in the
    /// player's color with a spark flare, holds briefly (click skips via the queue's
    /// fast-forward), then shrinks toward its destination. Fixed duration — never awaits
    /// input, so pending decisions are only delayed by ~a second at most.
    /// </summary>
    public sealed class CardShowcase : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private static readonly Vector2 CenterPos = new Vector2(60f, 60f);
        private const float HoldSeconds = 0.55f;

        private CardView _proxy;
        private GlowBurstLayer _bursts;

        public void Init(UiTheme theme, GlowBurstLayer bursts)
        {
            Theme = theme;
            _bursts = bursts;
        }

        public Vector2 ToLocal(RectTransform rt)
        {
            if (rt == null) return CenterPos;
            return Container.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
        }

        public IEnumerator Play(PresentationQueue queue, string defId, int playerIndex,
            Vector2 from, Vector2 to)
        {
            if (_proxy == null)
            {
                _proxy = CardViewFactory.Create(Container, Theme, 1f);
                _proxy.SetRaycastable(false);
                _proxy.Group.blocksRaycasts = false;
            }

            _proxy.BindDef(defId);
            _proxy.SetGlow(true, UiPalette.PlayerColor(playerIndex));
            _proxy.gameObject.SetActive(true);

            // In: fly + grow.
            yield return Segment(queue, from, CenterPos, 0.65f, 1.06f, 0.16f);
            if (_bursts != null)
                _bursts.Burst(_bursts.ToLocal(_proxy.Rect), UiPalette.PlayerColor(playerIndex), 10, 220f);

            // Hold (click-to-skip through the queue).
            yield return queue.Wait(HoldSeconds);

            // Out: shrink toward the destination.
            yield return Segment(queue, CenterPos, to, 1.06f, 0.35f, 0.2f);

            _proxy.gameObject.SetActive(false);
        }

        private IEnumerator Segment(PresentationQueue queue, Vector2 from, Vector2 to,
            float fromScale, float toScale, float duration)
        {
            float t = 0f;
            while (t < duration && !(queue != null && queue.FastForwarding))
            {
                t += Time.deltaTime;
                float x = Tween.EaseOutCubic(Mathf.Clamp01(t / duration));
                if (_proxy == null) yield break;
                _proxy.Rect.anchoredPosition = Vector2.LerpUnclamped(from, to, x);
                _proxy.Rect.localScale = Vector3.one * Mathf.LerpUnclamped(fromScale, toScale, x);
                yield return null;
            }
            if (_proxy != null)
            {
                _proxy.Rect.anchoredPosition = to;
                _proxy.Rect.localScale = Vector3.one * toScale;
            }
        }
    }
}
