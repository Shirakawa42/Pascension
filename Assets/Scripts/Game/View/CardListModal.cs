using System.Collections.Generic;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>Scrollable card-grid modal used for browsing discard/exile/relics/etc.
    /// Supports flat lists and GROUPED lists (per-player banished cards…): each group
    /// renders a separator + header line above its own card grid. SoI mercenaries show
    /// their red "M" triangle here automatically — it is baked into the CardView.</summary>
    public sealed class CardListModal : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private const int Columns = 5;
        private const float CellW = 140f, CellH = 194f, Gap = 10f;

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

            var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(14, 14, 10, 10);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
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
            var groups = new List<(string, IReadOnlyList<CardSnap>)>();
            if (cards != null && cards.Count > 0)
                groups.Add((null, cards));
            ShowGroups(title, groups);
        }

        /// <summary>Grouped browse: one separator + header per group, empty groups are
        /// skipped. Total count for the title sums every group.</summary>
        public void ShowGroups(string title, IReadOnlyList<(string Header, IReadOnlyList<CardSnap> Cards)> groups)
        {
            if (!_built) return;

            int total = 0;
            if (groups != null)
                foreach (var group in groups)
                    total += group.Cards?.Count ?? 0;
            _title.text = total == 0 ? $"{title} — empty" : $"{title} ({total})";

            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            if (groups == null) { Container.gameObject.SetActive(true); return; }

            foreach (var group in groups)
            {
                var cards = group.Cards;
                if (cards == null || cards.Count == 0) continue;

                if (!string.IsNullOrEmpty(group.Header))
                {
                    var header = UiFactory.CreateRect("Header", _content);
                    header.sizeDelta = new Vector2(0f, 30f); // childControlHeight=false positions by RECT height
                    var he = header.gameObject.AddComponent<LayoutElement>();
                    he.preferredHeight = 30f;
                    var line = UiFactory.CreateImage("Line", header, null,
                        UiPalette.WithAlpha(UiPalette.PanelLight, 0.9f), raycast: false);
                    line.rectTransform.anchorMin = new Vector2(0f, 0f);
                    line.rectTransform.anchorMax = new Vector2(1f, 0f);
                    line.rectTransform.pivot = new Vector2(0.5f, 0f);
                    line.rectTransform.anchoredPosition = new Vector2(0f, 2f);
                    line.rectTransform.sizeDelta = new Vector2(0f, 2f);
                    var label = UiFactory.CreateText(Theme, "Label", header,
                        $"{group.Header}  ({cards.Count})", 14f, UiPalette.TextDim,
                        TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                    UiFactory.Stretch(label.rectTransform);
                }

                int rows = (cards.Count + Columns - 1) / Columns;
                var grid = UiFactory.CreateRect("Group", _content);
                grid.sizeDelta = new Vector2(0f, rows * (CellH + Gap) - Gap);
                var ge = grid.gameObject.AddComponent<LayoutElement>();
                ge.preferredHeight = rows * (CellH + Gap) - Gap;
                for (int i = 0; i < cards.Count; i++)
                {
                    var cell = UiFactory.CreateRect("Cell", grid);
                    cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 1f);
                    cell.anchoredPosition = new Vector2(i % Columns * (CellW + Gap), -(i / Columns) * (CellH + Gap));
                    cell.sizeDelta = new Vector2(CellW, CellH);
                    var card = CardViewFactory.Create(cell, Theme, 0.61f);
                    card.Bind(cards[i]);
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
