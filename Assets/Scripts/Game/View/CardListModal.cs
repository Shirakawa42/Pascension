using System.Collections.Generic;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>Scrollable card-grid modal used for browsing discard/exile/relics/etc.</summary>
    public sealed class CardListModal : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private bool _built;
        private TextMeshProUGUI _title;
        private RectTransform _content;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var dimmer = UiFactory.CreateDimmer("Dimmer", Container);
            var dimButton = dimmer.gameObject.AddComponent<Button>();
            dimButton.targetGraphic = dimmer;
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(Hide);

            var panel = UiFactory.CreatePanel(Theme, "Panel", Container);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 600f));

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "Cards", 24f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.anchoredPosition = new Vector2(0f, -12f);
            _title.rectTransform.sizeDelta = new Vector2(-40f, 30f);

            var scroll = UiFactory.CreateScrollView(Theme, "Grid", panel.transform, out _content);
            UiFactory.Stretch((RectTransform)scroll.transform, 16, 70, 16, 52);

            var grid = _content.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(140f, 194f);
            grid.spacing = new Vector2(10f, 10f);
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 5;
            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var close = UiFactory.CreateButton(Theme, "Close", panel.transform, "CLOSE", 18f);
            var closeRt = (RectTransform)close.transform;
            closeRt.anchorMin = new Vector2(0.5f, 0f);
            closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0f, 10f);
            closeRt.sizeDelta = new Vector2(160f, 40f);
            close.onClick.AddListener(Hide);

            Container.gameObject.SetActive(false);
        }

        public void Show(string title, IReadOnlyList<CardSnap> cards)
        {
            if (!_built) return;
            _title.text = cards == null || cards.Count == 0 ? $"{title} — empty" : $"{title} ({cards.Count})";

            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            if (cards != null)
            {
                foreach (var snap in cards)
                {
                    var cell = UiFactory.CreateRect("Cell", _content);
                    var card = CardViewFactory.Create(cell, Theme, 0.61f);
                    card.Bind(snap);
                    card.SetRaycastable(false);
                }
            }

            Container.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_built)
                Container.gameObject.SetActive(false);
        }
    }
}
