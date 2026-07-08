using UnityEngine;
using UnityEngine.EventSystems;

namespace Pascension.Game.View
{
    /// <summary>
    /// Drag surface for a hand card: converts pointer positions into the hand
    /// container's local space and forwards begin/drag/end to the HandView, which owns
    /// the fan/reorder/play-line logic. Double-click plays the card directly.
    /// </summary>
    public sealed class HandCardDrag : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        private HandView _hand;
        private CardView _card;

        public void Bind(HandView hand, CardView card)
        {
            _hand = hand;
            _card = card;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_hand == null || _card == null) return;
            if (eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
            if (eventData.clickCount >= 2)
                _hand.RequestDoubleClickPlay(_card);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_hand == null || _card == null) return;
            _hand.BeginDrag(_card);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_hand == null || _card == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _hand.Container, eventData.position, eventData.pressEventCamera, out var local))
                _hand.Drag(_card, local);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_hand == null || _card == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _hand.Container, eventData.position, eventData.pressEventCamera, out var local);
            _hand.EndDrag(_card, local);
        }
    }
}
