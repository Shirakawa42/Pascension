using System.Collections;
using UnityEngine;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Minimal coroutine tween helpers (chosen over LitMotion — the package is declared
    /// in the manifest but was not resolved into this project's Library at the time of
    /// writing, so no code depends on it). All tweens are Lerp-based with ease-out cubic
    /// and are safe against destroyed targets.
    /// </summary>
    public static class Tween
    {
        public static float EaseOutCubic(float x)
        {
            float inv = 1f - Mathf.Clamp01(x);
            return 1f - inv * inv * inv;
        }

        public static IEnumerator Move(RectTransform rt, Vector2 to, float duration)
        {
            if (rt == null) yield break;
            Vector2 from = rt.anchoredPosition;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.LerpUnclamped(from, to, EaseOutCubic(t / duration));
                yield return null;
            }
            if (rt != null) rt.anchoredPosition = to;
        }

        public static IEnumerator Scale(Transform tr, Vector3 to, float duration)
        {
            if (tr == null) yield break;
            Vector3 from = tr.localScale;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (tr == null) yield break;
                tr.localScale = Vector3.LerpUnclamped(from, to, EaseOutCubic(t / duration));
                yield return null;
            }
            if (tr != null) tr.localScale = to;
        }

        public static IEnumerator RotateZ(Transform tr, float toZ, float duration)
        {
            if (tr == null) yield break;
            float from = tr.localEulerAngles.z;
            if (from > 180f) from -= 360f;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (tr == null) yield break;
                float z = Mathf.LerpUnclamped(from, toZ, EaseOutCubic(t / duration));
                tr.localRotation = Quaternion.Euler(0f, 0f, z);
                yield return null;
            }
            if (tr != null) tr.localRotation = Quaternion.Euler(0f, 0f, toZ);
        }

        public static IEnumerator Fade(CanvasGroup group, float to, float duration)
        {
            if (group == null) yield break;
            float from = group.alpha;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (group == null) yield break;
                group.alpha = Mathf.Lerp(from, to, EaseOutCubic(t / duration));
                yield return null;
            }
            if (group != null) group.alpha = to;
        }

        /// <summary>Quick scale punch (up then back) for feedback on slots/badges.</summary>
        public static IEnumerator Punch(Transform tr, float amount = 0.15f, float duration = 0.25f)
        {
            if (tr == null) yield break;
            Vector3 baseScale = tr.localScale;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (tr == null) yield break;
                float k = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                tr.localScale = baseScale * (1f + amount * k);
                yield return null;
            }
            if (tr != null) tr.localScale = baseScale;
        }

        /// <summary>Flash a graphic's color and restore it.</summary>
        public static IEnumerator Flash(UnityEngine.UI.Graphic g, Color flashColor, float duration = 0.35f)
        {
            if (g == null) yield break;
            Color baseColor = g.color;
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (g == null) yield break;
                float k = Mathf.Sin(Mathf.Clamp01(t / duration) * Mathf.PI);
                g.color = Color.Lerp(baseColor, flashColor, k);
                yield return null;
            }
            if (g != null) g.color = baseColor;
        }
    }
}
