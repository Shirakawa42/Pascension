using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Pascension.Game.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Generic modal for any DecisionRequest: options render as buttons or a card grid,
    /// Min/Max selection with a live counter, Ordered mode adds an up/down reorder list,
    /// InnChoice gets big illustrated buttons. Single-pick decisions submit on click.
    /// </summary>
    public sealed class DecisionModalView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private bool _built;
        private Image _dimmer;
        private RectTransform _panel;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _counter;
        private RectTransform _optionsContent;
        private GridLayoutGroup _optionsGrid;
        private RectTransform _orderPanel;
        private RectTransform _orderContent;
        private Button _confirm;

        private DecisionRequest _request;
        private Action<List<int>> _onConfirm;
        private readonly List<int> _selected = new List<int>();
        private readonly Dictionary<int, CardView> _cardWidgets = new Dictionary<int, CardView>();
        private readonly Dictionary<int, Image> _buttonWidgets = new Dictionary<int, Image>();

        public int ShownDecisionId => _request != null && Container.gameObject.activeSelf ? _request.Id : -1;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            _dimmer = UiFactory.CreateDimmer("Dimmer", Container);

            var panel = UiFactory.CreatePanel(Theme, "Panel", Container);
            _panel = panel.rectTransform;
            UiFactory.Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 560f));

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "Choose", 26f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.anchoredPosition = new Vector2(0f, -14f);
            _title.rectTransform.sizeDelta = new Vector2(-40f, 34f);

            _counter = UiFactory.CreateText(Theme, "Counter", panel.transform, "", 15f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            _counter.rectTransform.anchorMin = new Vector2(0f, 1f);
            _counter.rectTransform.anchorMax = new Vector2(1f, 1f);
            _counter.rectTransform.pivot = new Vector2(0.5f, 1f);
            _counter.rectTransform.anchoredPosition = new Vector2(0f, -50f);
            _counter.rectTransform.sizeDelta = new Vector2(-40f, 20f);

            var scroll = UiFactory.CreateScrollView(Theme, "Options", panel.transform, out _optionsContent);
            var scrollRt = (RectTransform)scroll.transform;
            UiFactory.Stretch(scrollRt, 16, 76, 16, 76);

            _optionsGrid = _optionsContent.gameObject.AddComponent<GridLayoutGroup>();
            _optionsGrid.spacing = new Vector2(10f, 10f);
            _optionsGrid.childAlignment = TextAnchor.UpperCenter;
            var fitter = _optionsContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Ordered-mode reorder column (hidden unless request.Ordered).
            var orderImg = UiFactory.CreatePanel(Theme, "OrderPanel", Container, UiPalette.PanelLight);
            _orderPanel = orderImg.rectTransform;
            UiFactory.Place(_orderPanel, new Vector2(0.5f, 0.5f), new Vector2(500f, 0f), new Vector2(230f, 460f));
            var orderTitle = UiFactory.CreateText(Theme, "OrderTitle", _orderPanel, "ORDER (top first)", 13f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            orderTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            orderTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            orderTitle.rectTransform.pivot = new Vector2(0.5f, 1f);
            orderTitle.rectTransform.anchoredPosition = new Vector2(0f, -8f);
            orderTitle.rectTransform.sizeDelta = new Vector2(0f, 18f);
            _orderContent = UiFactory.CreateRect("OrderContent", _orderPanel);
            UiFactory.Stretch(_orderContent, 8, 8, 8, 32);
            var orderLayout = _orderContent.gameObject.AddComponent<VerticalLayoutGroup>();
            orderLayout.spacing = 6f;
            orderLayout.childAlignment = TextAnchor.UpperCenter;
            orderLayout.childControlWidth = true;
            orderLayout.childControlHeight = false;
            orderLayout.childForceExpandWidth = true;
            orderLayout.childForceExpandHeight = false;

            _confirm = UiFactory.CreateButton(Theme, "Confirm", panel.transform, "CONFIRM", 22f,
                UiPalette.Gold, UiPalette.Background);
            var confirmRt = (RectTransform)_confirm.transform;
            confirmRt.anchorMin = new Vector2(0.5f, 0f);
            confirmRt.anchorMax = new Vector2(0.5f, 0f);
            confirmRt.pivot = new Vector2(0.5f, 0f);
            confirmRt.anchoredPosition = new Vector2(0f, 14f);
            confirmRt.sizeDelta = new Vector2(220f, 50f);
            _confirm.onClick.AddListener(Confirm);

            Container.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ show/hide

        /// <param name="blocking">
        /// False for single-pick ChooseTargets: no dimmer, panel docked at the far left
        /// so the actual board/market/stack targets stay clickable as a shortcut.
        /// </param>
        public void Show(DecisionRequest request, ClientGameView view, Action<List<int>> onConfirm,
            bool blocking = true)
        {
            if (!_built) return;
            if (_request != null && _request.Id == request.Id && Container.gameObject.activeSelf)
                return; // already showing this decision

            _request = request;
            _onConfirm = onConfirm;
            _selected.Clear();
            _cardWidgets.Clear();
            _buttonWidgets.Clear();
            Container.gameObject.SetActive(true);

            _dimmer.gameObject.SetActive(blocking);
            _panel.anchoredPosition = blocking ? Vector2.zero : new Vector2(-720f, 0f);
            _panel.sizeDelta = blocking ? new Vector2(760f, 560f) : new Vector2(390f, 500f);

            _title.text = string.IsNullOrEmpty(request.Title) ? "Choose" : request.Title;
            _orderPanel.gameObject.SetActive(request.Ordered);

            for (int i = _optionsContent.childCount - 1; i >= 0; i--)
                Destroy(_optionsContent.GetChild(i).gameObject);

            bool anyCards = false;
            foreach (var option in request.Options)
                if (!string.IsNullOrEmpty(option.DefId) ||
                    (option.CardInstanceId >= 0 && view != null && view.FindCard(option.CardInstanceId) != null))
                    anyCards = true;

            bool innStyle = request.Kind == DecisionKind.InnChoice;
            _optionsGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            if (anyCards)
            {
                _optionsGrid.cellSize = new Vector2(134f, 186f);
                _optionsGrid.constraintCount = blocking ? 5 : 2;
            }
            else if (!blocking)
            {
                _optionsGrid.cellSize = new Vector2(340f, 52f);
                _optionsGrid.constraintCount = 1;
            }
            else
            {
                _optionsGrid.cellSize = innStyle ? new Vector2(660f, 78f) : new Vector2(340f, 56f);
                _optionsGrid.constraintCount = innStyle ? 1 : 2;
            }

            foreach (var option in request.Options)
                BuildOption(option, view, anyCards, innStyle);

            RefreshSelection();
        }

        public void Hide()
        {
            if (!_built) return;
            Container.gameObject.SetActive(false);
            _request = null;
            _onConfirm = null;
        }

        // ------------------------------------------------------------------ options

        private void BuildOption(DecisionOption option, ClientGameView view, bool cardGrid, bool innStyle)
        {
            var snap = option.CardInstanceId >= 0 && view != null ? view.FindCard(option.CardInstanceId) : null;
            // Cards outside every client-visible zone (market exile, deck reveals)
            // still render as real cards via the option's DefId.
            if (snap == null && !string.IsNullOrEmpty(option.DefId))
                snap = new CardSnap { DefId = option.DefId, InstanceId = option.CardInstanceId, EffectiveCost = -1 };

            if (cardGrid && snap != null)
            {
                var cell = UiFactory.CreateRect($"Option{option.Id}", _optionsContent);
                var card = CardViewFactory.Create(cell, Theme, 0.59f);
                card.Bind(snap);
                int id = option.Id;
                card.Clicked += _ => OnOptionClicked(id);
                _cardWidgets[option.Id] = card;
                return;
            }

            string label = option.Label;
            if (string.IsNullOrEmpty(label) && option.Target.HasValue && view != null)
                label = EventText.TargetName(view.Snapshot, option.Target.Value);
            if (string.IsNullOrEmpty(label) && snap != null)
                label = EventText.CardName(snap.DefId);
            if (string.IsNullOrEmpty(label))
                label = "Option " + option.Id;

            var button = UiFactory.CreateButton(Theme, $"Option{option.Id}", _optionsContent, label,
                innStyle ? 21f : 17f);
            var text = UiFactory.ButtonLabel(button);
            text.enableAutoSizing = true;
            text.fontSizeMin = 11f;
            text.fontSizeMax = innStyle ? 21f : 17f;
            int optId = option.Id;
            button.onClick.AddListener(() => OnOptionClicked(optId));
            _buttonWidgets[option.Id] = button.image;
        }

        private void OnOptionClicked(int optionId)
        {
            if (_request == null) return;

            if (_request.Min == 1 && _request.Max == 1 && !_request.Ordered)
            {
                _selected.Clear();
                _selected.Add(optionId);
                Confirm();
                return;
            }

            if (_selected.Contains(optionId))
                _selected.Remove(optionId);
            else if (_selected.Count < _request.Max)
                _selected.Add(optionId);

            RefreshSelection();
        }

        private void Confirm()
        {
            if (_request == null) return;
            if (_selected.Count < _request.Min || _selected.Count > _request.Max) return;
            var handler = _onConfirm;
            var picks = new List<int>(_selected);
            Hide();
            handler?.Invoke(picks);
        }

        // ------------------------------------------------------------------ selection state

        private void RefreshSelection()
        {
            if (_request == null) return;

            foreach (var pair in _cardWidgets)
                pair.Value.SetGlow(_selected.Contains(pair.Key), UiPalette.TargetBlue);
            foreach (var pair in _buttonWidgets)
                pair.Value.color = _selected.Contains(pair.Key)
                    ? UiPalette.WithAlpha(UiPalette.TargetBlue, 0.75f)
                    : UiPalette.PanelLight;

            bool single = _request.Min == 1 && _request.Max == 1 && !_request.Ordered;
            _counter.text = single
                ? "Choose one"
                : _request.Min == _request.Max
                    ? $"Choose {_request.Min} (selected {_selected.Count})"
                    : $"Choose {_request.Min}-{_request.Max} (selected {_selected.Count})";

            _confirm.gameObject.SetActive(!single);
            _confirm.interactable = _selected.Count >= _request.Min && _selected.Count <= _request.Max;

            if (_request.Ordered)
                RebuildOrderList();
        }

        private void RebuildOrderList()
        {
            for (int i = _orderContent.childCount - 1; i >= 0; i--)
                Destroy(_orderContent.GetChild(i).gameObject);

            for (int i = 0; i < _selected.Count; i++)
            {
                int index = i;
                int optionId = _selected[i];
                var option = _request.Options.Find(o => o.Id == optionId);

                var row = UiFactory.CreateImage("OrderRow", _orderContent, Theme.Rounded, UiPalette.Panel);
                row.rectTransform.sizeDelta = new Vector2(0f, 34f);
                var element = row.gameObject.AddComponent<LayoutElement>();
                element.preferredHeight = 34f;

                var label = UiFactory.CreateText(Theme, "Label", row.transform,
                    $"{i + 1}. {(option != null ? option.Label : optionId.ToString())}", 13f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
                UiFactory.Stretch(label.rectTransform, 8, 2, 62, 2);
                label.enableAutoSizing = true;
                label.fontSizeMin = 9f;
                label.fontSizeMax = 13f;

                var up = UiFactory.CreateButton(Theme, "Up", row.transform, "^", 15f);
                UiFactory.Place((RectTransform)up.transform, new Vector2(1f, 0.5f), new Vector2(-32f, 0f), new Vector2(26f, 26f));
                up.interactable = index > 0;
                up.onClick.AddListener(() => MoveSelected(index, -1));

                var down = UiFactory.CreateButton(Theme, "Down", row.transform, "v", 15f);
                UiFactory.Place((RectTransform)down.transform, new Vector2(1f, 0.5f), new Vector2(-4f, 0f), new Vector2(26f, 26f));
                down.interactable = index < _selected.Count - 1;
                down.onClick.AddListener(() => MoveSelected(index, 1));
            }
        }

        private void MoveSelected(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= _selected.Count || target < 0 || target >= _selected.Count) return;
            (_selected[index], _selected[target]) = (_selected[target], _selected[index]);
            RefreshSelection();
        }
    }
}
