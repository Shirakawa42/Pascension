using System.Collections;
using System.Collections.Generic;
using Pascension.Game.View;
using UnityEngine;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Card zone-flight animations: pooled proxy CardViews fly between UI anchors on a
    /// full-screen overlay. Real zone views are hidden by the caller during a flight and
    /// restored by the post-drain refresh — proxies never touch game state.
    /// </summary>
    public sealed class FlightLayer : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private readonly List<CardView> _pool = new List<CardView>();
        private readonly HashSet<CardView> _busy = new HashSet<CardView>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
        }

        /// <summary>Convert any rect's visual center into this layer's local space.
        /// Resolve ONCE at flight start — source rects may be destroyed mid-flight.</summary>
        public Vector2 ToLocal(RectTransform rt)
        {
            if (rt == null) return Vector2.zero;
            var world = rt.TransformPoint(rt.rect.center);
            return Container.InverseTransformPoint(world);
        }

        /// <summary>Fly a card proxy between two layer-local points. Null/hidden defs render
        /// as a card back (faceDown forces it). Paces itself and snaps on fast-forward.</summary>
        public IEnumerator Fly(PresentationQueue queue, string defId, Vector2 from, Vector2 to,
            float fromScale, float toScale, float duration, bool faceDown = false, Color? tint = null)
        {
            var proxy = Rent();
            if (proxy == null) yield break;

            proxy.BindDef(faceDown ? null : defId);
            proxy.Rect.anchoredPosition = from;
            proxy.Rect.localScale = Vector3.one * fromScale;
            proxy.Rect.localRotation = Quaternion.identity;
            if (tint.HasValue) proxy.Frame.color = tint.Value;
            proxy.gameObject.SetActive(true);
            proxy.Rect.SetAsLastSibling();

            float arc = Mathf.Min(90f, Vector2.Distance(from, to) * 0.18f);
            float wobble = Random.Range(-5f, 5f);
            float t = 0f;
            while (t < duration && !(queue != null && queue.FastForwarding))
            {
                t += Time.deltaTime;
                float x = Tween.EaseOutCubic(Mathf.Clamp01(t / duration));
                var pos = Vector2.LerpUnclamped(from, to, x);
                pos.y += Mathf.Sin(x * Mathf.PI) * arc;
                if (proxy == null) yield break;
                proxy.Rect.anchoredPosition = pos;
                proxy.Rect.localScale = Vector3.one * Mathf.LerpUnclamped(fromScale, toScale, x);
                proxy.Rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(x * Mathf.PI) * wobble);
                yield return null;
            }

            Release(proxy);
        }

        /// <summary>Several staggered flights of the same kind (draws, cleanup discards).
        /// Caps the visible proxies so big batches stay readable.</summary>
        public IEnumerator FlyMany(PresentationQueue queue, string defId, int count, Vector2 from, Vector2 to,
            float fromScale, float toScale, float duration, bool faceDown, float stagger)
        {
            int visible = Mathf.Min(count, 4);
            for (int i = 0; i < visible; i++)
            {
                StartCoroutine(Fly(queue, defId, from, to, fromScale, toScale, duration, faceDown));
                if (i < visible - 1)
                    yield return queue.Wait(stagger);
            }
            yield return queue.Wait(duration * 0.8f);
        }

        private CardView Rent()
        {
            foreach (var view in _pool)
                if (!_busy.Contains(view) && view != null)
                {
                    _busy.Add(view);
                    return view;
                }
            if (_pool.Count >= 10) return null; // pool exhausted: skip the visual, never grow unbounded
            var proxy = CardViewFactory.Create(Container, Theme, 1f);
            proxy.SetRaycastable(false);
            proxy.Group.blocksRaycasts = false;
            proxy.gameObject.SetActive(false);
            _pool.Add(proxy);
            _busy.Add(proxy);
            return proxy;
        }

        private void Release(CardView proxy)
        {
            if (proxy == null) return;
            proxy.gameObject.SetActive(false);
            proxy.Frame.color = UiPalette.TierDefault;
            _busy.Remove(proxy);
        }
    }
}
