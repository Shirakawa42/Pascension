using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// A 4-point twinkle star drawn straight from vertices (no star sprite exists in the
    /// theme): points up/right/down/left at the rect's extents, waist pulled in on the
    /// diagonals. Used by SparkleOverlay for the SoI "condition met" twinkle.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class StarGraphic : MaskableGraphic
    {
        /// <summary>Waist radius as a fraction of the point radius (smaller = spikier).</summary>
        public float Waist = 0.32f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            var c = r.center;
            float outer = Mathf.Min(r.width, r.height) * 0.5f;
            float inner = outer * Waist;

            vh.AddVert(c, color, Vector2.zero);
            for (int i = 0; i < 8; i++)
            {
                // Even = star point (N/E/S/W), odd = waist (diagonals).
                float angle = (90f - i * 45f) * Mathf.Deg2Rad;
                float radius = (i & 1) == 0 ? outer : inner;
                vh.AddVert(c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius,
                    color, Vector2.zero);
            }
            for (int i = 0; i < 8; i++)
                vh.AddTriangle(0, 1 + i, 1 + (i + 1) % 8);
        }
    }
}
