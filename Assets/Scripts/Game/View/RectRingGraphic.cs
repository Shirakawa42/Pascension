using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// A thin rectangular outline drawn as quads along this RectTransform's edges — no
    /// sprite needed. The RIGHT edge leaves a vertical gap (RightGapHeight, centered)
    /// where the SoI mercenary triangle sits, so the line reads as starting from the
    /// triangle's sides. Used for the mercenary inset line on cards.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class RectRingGraphic : MaskableGraphic
    {
        /// <summary>Line thickness in local units.</summary>
        public float Thickness = 2f;

        /// <summary>Vertical gap centered on the right edge (0 = closed ring).</summary>
        public float RightGapHeight = 60f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            float t = Thickness;

            void Quad(float x0, float y0, float x1, float y1)
            {
                int i = vh.currentVertCount;
                vh.AddVert(new Vector2(x0, y0), color, Vector2.zero);
                vh.AddVert(new Vector2(x0, y1), color, Vector2.zero);
                vh.AddVert(new Vector2(x1, y1), color, Vector2.zero);
                vh.AddVert(new Vector2(x1, y0), color, Vector2.zero);
                vh.AddTriangle(i, i + 1, i + 2);
                vh.AddTriangle(i, i + 2, i + 3);
            }

            Quad(r.xMin, r.yMax - t, r.xMax, r.yMax); // top
            Quad(r.xMin, r.yMin, r.xMax, r.yMin + t); // bottom
            Quad(r.xMin, r.yMin + t, r.xMin + t, r.yMax - t); // left

            // Right edge in two segments around the centered gap.
            float centerY = (r.yMin + r.yMax) * 0.5f;
            float gapHalf = Mathf.Max(0f, RightGapHeight * 0.5f);
            float lowTop = Mathf.Max(r.yMin + t, centerY - gapHalf);
            float highBottom = Mathf.Min(r.yMax - t, centerY + gapHalf);
            if (lowTop > r.yMin + t)
                Quad(r.xMax - t, r.yMin + t, r.xMax, lowTop);
            if (highBottom < r.yMax - t)
                Quad(r.xMax - t, highBottom, r.xMax, r.yMax - t);
        }
    }
}
