using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Cosmetic targeting line shown while a ChooseTargets decision is pending: an Image
    /// stretched and rotated between an origin (the hand/sheet area) and the cursor.
    /// Never blocks raycasts.
    /// </summary>
    public sealed class TargetingArrow : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private bool _built;
        private RectTransform _line;
        private Image _lineImage;
        private RectTransform _tip;
        private Vector2 _originLocal;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            _lineImage = UiFactory.CreateImage("Line", Container, null, UiPalette.WithAlpha(UiPalette.TargetBlue, 0.9f));
            _line = _lineImage.rectTransform;
            _line.pivot = new Vector2(0f, 0.5f);
            _line.anchorMin = _line.anchorMax = new Vector2(0.5f, 0.5f);
            _line.sizeDelta = new Vector2(0f, 6f);

            var tipImage = UiFactory.CreateImage("Tip", Container, Theme.Circle, UiPalette.TargetBlue);
            _tip = tipImage.rectTransform;
            _tip.anchorMin = _tip.anchorMax = new Vector2(0.5f, 0.5f);
            _tip.sizeDelta = new Vector2(20f, 20f);

            Container.gameObject.SetActive(false);
        }

        /// <summary>Origin in the Container's local space (center-anchored canvas units).</summary>
        public void Show(Vector2 originLocal)
        {
            if (!_built) return;
            _originLocal = originLocal;
            Container.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_built && Container.gameObject.activeSelf)
                Container.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_built || !Container.gameObject.activeSelf) return;
            var mouse = Mouse.current;
            if (mouse == null) return;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                Container, mouse.position.ReadValue(), null, out var cursorLocal);

            var delta = cursorLocal - _originLocal;
            _line.anchoredPosition = _originLocal;
            _line.sizeDelta = new Vector2(delta.magnitude, 6f);
            _line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            _tip.anchoredPosition = cursorLocal;
        }
    }
}
