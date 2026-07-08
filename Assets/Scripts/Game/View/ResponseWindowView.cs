using System;
using TMPro;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// "Respond?" banner shown when the local player receives priority while the stack
    /// is non-empty. No timer — players may take as long as they like; the banner just
    /// highlights that a response is possible and offers PASS.
    /// </summary>
    public sealed class ResponseWindowView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action PassClicked;

        private bool _built;
        private TextMeshProUGUI _title;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var panel = UiFactory.CreatePanel(Theme, "ResponsePanel", Container, UiPalette.WithAlpha(Color.black, 0.85f));
            UiFactory.Stretch(panel.rectTransform);

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "Respond?", 26f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_title.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, 10f), new Vector2(220f, 32f));

            var hint = UiFactory.CreateText(Theme, "Hint", panel.transform, "Instants in hand are highlighted — take your time", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(hint.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, -14f), new Vector2(300f, 18f));

            var pass = UiFactory.CreateButton(Theme, "PassButton", panel.transform, "PASS", 22f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)pass.transform, new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(130f, 46f));
            pass.onClick.AddListener(() => PassClicked?.Invoke());

            Container.gameObject.SetActive(false);
        }

        public void Show()
        {
            if (!_built) return;
            if (!Container.gameObject.activeSelf)
                Container.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (!_built) return;
            Container.gameObject.SetActive(false);
        }

        public bool IsShown => _built && Container.gameObject.activeSelf;
    }
}
