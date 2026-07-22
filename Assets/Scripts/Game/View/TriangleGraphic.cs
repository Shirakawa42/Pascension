using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// A solid triangle drawn straight from three vertices — no sprite needed. The apex
    /// points LEFT (toward the card center) and the vertical base sits on the RIGHT edge
    /// of the RectTransform. Used for the SoI mercenary marker on the right side of cards.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class TriangleGraphic : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            var apex = new Vector2(r.xMin, (r.yMin + r.yMax) * 0.5f); // toward center
            var baseTop = new Vector2(r.xMax, r.yMax);
            var baseBottom = new Vector2(r.xMax, r.yMin);

            vh.AddVert(apex, color, Vector2.zero);
            vh.AddVert(baseTop, color, Vector2.zero);
            vh.AddVert(baseBottom, color, Vector2.zero);
            vh.AddTriangle(0, 1, 2);
        }
    }
}
