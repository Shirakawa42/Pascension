using Pascension.Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>End-of-game overlay: winner name + hero art + back to menu.</summary>
    public sealed class GameOverPanel : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private bool _built;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _subtitle;
        private Image _heroArt;
        private TextMeshProUGUI _heroInitial;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            UiFactory.CreateDimmer("Dimmer", Container);

            var panel = UiFactory.CreatePanel(Theme, "Panel", Container);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 520f));

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "VICTORY", 44f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.characterSpacing = 8f;
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.anchoredPosition = new Vector2(0f, -24f);
            _title.rectTransform.sizeDelta = new Vector2(-40f, 52f);

            _subtitle = UiFactory.CreateText(Theme, "Subtitle", panel.transform, "", 24f,
                UiPalette.TextMain, TextAlignmentOptions.Center);
            _subtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            _subtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            _subtitle.rectTransform.pivot = new Vector2(0.5f, 1f);
            _subtitle.rectTransform.anchoredPosition = new Vector2(0f, -82f);
            _subtitle.rectTransform.sizeDelta = new Vector2(-40f, 32f);

            var artFrame = UiFactory.CreateImage("ArtFrame", panel.transform, Theme.Rounded, UiPalette.Border);
            UiFactory.Place(artFrame.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(260f, 260f));
            _heroArt = UiFactory.CreateImage("HeroArt", artFrame.transform, null, UiPalette.PanelLight);
            UiFactory.Stretch(_heroArt.rectTransform, 4, 4, 4, 4);
            _heroInitial = UiFactory.CreateText(Theme, "Initial", artFrame.transform, "", 96f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_heroInitial.rectTransform);

            var back = UiFactory.CreateButton(Theme, "BackButton", panel.transform, "BACK TO MENU", 22f,
                UiPalette.Gold, UiPalette.Background);
            var backRt = (RectTransform)back.transform;
            backRt.anchorMin = new Vector2(0.5f, 0f);
            backRt.anchorMax = new Vector2(0.5f, 0f);
            backRt.pivot = new Vector2(0.5f, 0f);
            backRt.anchoredPosition = new Vector2(0f, 24f);
            backRt.sizeDelta = new Vector2(280f, 54f);
            back.onClick.AddListener(SceneFlow.LoadMenu);

            Container.gameObject.SetActive(false);
        }

        public void Show(string winnerName, string heroId, bool localWon)
        {
            if (!_built) return;
            _title.text = localWon ? "VICTORY" : "GAME OVER";
            _title.color = localWon ? UiPalette.Gold : UiPalette.Danger;
            _subtitle.text = string.IsNullOrEmpty(winnerName) ? "It is over." : $"{winnerName} wins!";

            var art = Theme.Art(heroId);
            if (art != null)
            {
                _heroArt.sprite = art;
                _heroArt.color = Color.white;
                _heroInitial.text = "";
            }
            else
            {
                _heroArt.sprite = null;
                _heroArt.color = UiPalette.PanelLight;
                _heroInitial.text = !string.IsNullOrEmpty(heroId) ? heroId.Substring(0, 1).ToUpperInvariant() : "";
            }

            Container.gameObject.SetActive(true);
        }
    }
}
