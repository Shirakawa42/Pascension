using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Pascension.Game.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Generic decision UI for Shards of Infinity. Two modes:
    /// - list (default): one toggle-button per option; CONFIRM enables once the picked
    ///   count is within [Min, Max]; a SKIP shortcut appears when Min == 0.
    /// - split ("soi.split"): one stepper per opponent; CONFIRM enables when the total
    ///   equals the mandatory full assignment.
    /// Built entirely at runtime; one instance per screen, reshown per request.
    /// </summary>
    public sealed class SoiDecisionModal : MonoBehaviour
    {
        private UiTheme _theme;
        private RectTransform _root;
        private Image _dimmer;
        private RectTransform _panel;
        private TextMeshProUGUI _title;
        private RectTransform _body;
        private Button _confirm;
        private TextMeshProUGUI _confirmLabel;
        private Button _skip;

        private DecisionRequest _request;
        private Action<List<int>> _onConfirm;
        private Func<int, string> _captionFor;
        private readonly List<int> _picked = new List<int>();
        private readonly Dictionary<int, int> _splitCounts = new Dictionary<int, int>();
        private readonly List<(Button button, Image bg, int optionId)> _optionButtons = new();
        private readonly List<(CardView card, int optionId)> _optionCards = new();
        private readonly List<TextMeshProUGUI> _splitLabels = new();

        public bool Visible => _root != null && _root.gameObject.activeSelf;

        public static SoiDecisionModal Create(Transform parent, UiTheme theme)
        {
            var rect = UiFactory.CreateRect("SoiDecisionModal", parent);
            UiFactory.Stretch(rect);
            var modal = rect.gameObject.AddComponent<SoiDecisionModal>();
            modal.Build(theme, rect);
            return modal;
        }

        private void Build(UiTheme theme, RectTransform rect)
        {
            _theme = theme;
            _root = rect;

            _dimmer = UiFactory.CreateDimmer("Dimmer", rect);

            var panelImg = UiFactory.CreatePanel(theme, "Panel", rect, UiPalette.Panel);
            _panel = (RectTransform)panelImg.transform;
            UiFactory.Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(660f, 560f));

            _title = UiFactory.CreateText(theme, "Title", _panel, "", 22f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -32f), new Vector2(620f, 56f));

            _body = UiFactory.CreateRect("Body", _panel);
            UiFactory.Place(_body, new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(620f, 400f));

            _confirm = UiFactory.CreateButton(theme, "Confirm", _panel, UI.Loc.T("CONFIRM"), 18f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_confirm.transform, new Vector2(0.5f, 0f), new Vector2(-110f, 42f), new Vector2(190f, 52f));
            _confirm.onClick.AddListener(Confirm);
            _confirmLabel = UiFactory.ButtonLabel(_confirm);

            _skip = UiFactory.CreateButton(theme, "Skip", _panel, UI.Loc.T("SKIP"), 16f);
            UiFactory.Place((RectTransform)_skip.transform, new Vector2(0.5f, 0f), new Vector2(110f, 42f), new Vector2(190f, 52f));
            _skip.onClick.AddListener(() =>
            {
                _picked.Clear();
                _splitCounts.Clear();
                Confirm();
            });

            rect.gameObject.SetActive(false);
        }

        /// <summary>Show a decision. `defIdResolver` maps a card instance id to its def id
        /// so options that reference cards render as the real card face (null = no card).
        /// `captionFor` maps a card instance id to a small caption under the card —
        /// typically its source zone ("your hand" vs "your discard"), so multi-zone
        /// choices like banish are unambiguous.</summary>
        public void Show(DecisionRequest request, Func<int, string> optionLabel, Action<List<int>> onConfirm,
            Func<int, string> defIdResolver = null, Func<int, string> captionFor = null)
        {
            _captionFor = captionFor;
            _request = request;
            _onConfirm = onConfirm;
            _picked.Clear();
            _splitCounts.Clear();
            _optionButtons.Clear();
            _optionCards.Clear();
            _splitLabels.Clear();
            foreach (Transform child in _body)
                Destroy(child.gameObject);

            _title.text = UI.Loc.DecisionTitle(request.Title);
            _skip.gameObject.SetActive(request.Min == 0);
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();

            if (request.Context == "soi.split")
                BuildSplit(request);
            else
                BuildList(request, optionLabel, defIdResolver);
            RefreshConfirm();
        }

        /// <summary>The card def id an option renders as: explicit DefId, else resolved
        /// from the option's card instance id via the caller's zone lookup.</summary>
        private static string OptionDefId(DecisionOption option, Func<int, string> defIdResolver)
        {
            if (!string.IsNullOrEmpty(option.DefId)) return option.DefId;
            if (option.CardInstanceId > 0 && defIdResolver != null) return defIdResolver(option.CardInstanceId);
            return null;
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
            _request = null;
        }

        // ------------------------------------------------------------------ list mode

        private void BuildList(DecisionRequest request, Func<int, string> optionLabel, Func<int, string> defIdResolver)
        {
            // Card mode when any option references a card: render real card faces in a
            // scrollable grid (works for both games via CardView's external face resolver).
            bool anyCard = false;
            foreach (var option in request.Options)
                if (OptionDefId(option, defIdResolver) != null) { anyCard = true; break; }

            if (anyCard)
                BuildCardGrid(request, optionLabel, defIdResolver);
            else
                BuildTextList(request, optionLabel);
        }

        private void BuildCardGrid(DecisionRequest request, Func<int, string> optionLabel, Func<int, string> defIdResolver)
        {
            var scroll = UiFactory.CreateScrollView(_theme, "Options", _body, out var content);
            UiFactory.Stretch((RectTransform)scroll.transform);

            // Cells carry a caption band under the card (source zone); height covers
            // the 0.6-scale card (~185) + a 22px caption line.
            var grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(140f, 220f);
            grid.spacing = new Vector2(12f, 10f);
            grid.padding = new RectOffset(10, 10, 10, 10);
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var option in request.Options)
            {
                int id = option.Id;
                var cell = UiFactory.CreateRect("Opt_" + id, content);
                string defId = OptionDefId(option, defIdResolver);
                if (defId != null)
                {
                    var card = CardViewFactory.Create(cell, _theme, 0.6f);
                    // Pin to the cell TOP (pivot-scaled) so the caption band below stays clear.
                    card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0.5f, 1f);
                    card.Rect.anchoredPosition = Vector2.zero;
                    card.Clicked += _ => TogglePick(id);
                    card.Bind(new CardSnap { DefId = defId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                    _optionCards.Add((card, id));

                    string caption = _captionFor != null && option.CardInstanceId > 0
                        ? _captionFor(option.CardInstanceId) : null;
                    if (!string.IsNullOrEmpty(caption))
                    {
                        var text = UiFactory.CreateText(_theme, "Caption", cell, caption, 12f,
                            UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
                        UiFactory.Place(text.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(138f, 20f));
                    }
                }
                else
                {
                    // Non-card option in a mixed list (e.g. "Leave it on top") — text button.
                    string label = UI.Loc.OptionLabel(optionLabel != null ? optionLabel(id) : option.Label);
                    var button = UiFactory.CreateButton(_theme, "OptTxt_" + id, cell, label, 13f);
                    UiFactory.Stretch((RectTransform)button.transform);
                    var bg = button.GetComponent<Image>();
                    button.onClick.AddListener(() => TogglePick(id));
                    _optionButtons.Add((button, bg, id));
                }
            }
        }

        private void BuildTextList(DecisionRequest request, Func<int, string> optionLabel)
        {
            var scroll = UiFactory.CreateScrollView(_theme, "Options", _body, out var content);
            UiFactory.Stretch((RectTransform)scroll.transform);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var option in request.Options)
            {
                string label = UI.Loc.OptionLabel(optionLabel != null ? optionLabel(option.Id) : option.Label);
                var button = UiFactory.CreateButton(_theme, "Opt_" + option.Id, content, label, 15f);
                var lrect = (RectTransform)button.transform;
                lrect.sizeDelta = new Vector2(0f, 44f);
                var le = button.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 44f;
                int id = option.Id;
                var bg = button.GetComponent<Image>();
                button.onClick.AddListener(() => TogglePick(id));
                _optionButtons.Add((button, bg, id));
            }
        }

        private void TogglePick(int id)
        {
            if (_picked.Contains(id))
            {
                _picked.Remove(id);
            }
            else
            {
                if (_request.Max == 1)
                    _picked.Clear(); // radio behavior for single-choice
                if (_picked.Count >= _request.Max)
                    return;
                _picked.Add(id);
            }
            foreach (var (_, image, optionId) in _optionButtons)
                image.color = _picked.Contains(optionId)
                    ? new Color(0.5f, 0.42f, 0.2f, 1f)
                    : UiPalette.PanelLight;
            foreach (var (card, optionId) in _optionCards)
                card.SetGlow(_picked.Contains(optionId), UiPalette.Gold);
            RefreshConfirm();
        }

        // ------------------------------------------------------------------ split mode

        private void BuildSplit(DecisionRequest request)
        {
            // Rows live in a scroll view (opponents + every attackable champion can
            // exceed the panel); the ALL shortcuts stay pinned below the scroll area.
            var scroll = UiFactory.CreateScrollView(_theme, "SplitRows", _body, out var content);
            var srect = (RectTransform)scroll.transform;
            srect.anchorMin = Vector2.zero;
            srect.anchorMax = Vector2.one;
            srect.pivot = new Vector2(0.5f, 0.5f);
            srect.offsetMin = new Vector2(0f, 56f); // room for the shortcut row
            srect.offsetMax = Vector2.zero;

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var option in request.Options)
            {
                _splitCounts[option.Id] = 0;
                var row = UiFactory.CreateRect("Split_" + option.Id, content);
                row.sizeDelta = new Vector2(0f, 64f);
                var le = row.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 64f;

                var name = UiFactory.CreateText(_theme, "Name", row, option.Label, 18f, UiPalette.TextMain,
                    TextAlignmentOptions.Left, FontStyles.Bold);
                UiFactory.Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(330f, 40f));

                var minus = UiFactory.CreateButton(_theme, "Minus", row, "−", 22f);
                UiFactory.Place((RectTransform)minus.transform, new Vector2(1f, 0.5f), new Vector2(-160f, 0f), new Vector2(52f, 52f));

                var count = UiFactory.CreateText(_theme, "Count", row, "0", 24f, UiPalette.Gold,
                    TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(count.rectTransform, new Vector2(1f, 0.5f), new Vector2(-100f, 0f), new Vector2(60f, 44f));
                _splitLabels.Add(count);

                var plus = UiFactory.CreateButton(_theme, "Plus", row, "+", 22f);
                UiFactory.Place((RectTransform)plus.transform, new Vector2(1f, 0.5f), new Vector2(-40f, 0f), new Vector2(52f, 52f));

                int id = option.Id;
                var countLabel = count;
                minus.onClick.AddListener(() => Bump(id, -1, countLabel));
                plus.onClick.AddListener(() => Bump(id, +1, countLabel));
            }

            // "All on X" shortcuts save clicks for big pools — PLAYER targets only
            // (champion options carry the champion's CardInstanceId and can be many).
            var playerOptions = request.Options.FindAll(o => o.CardInstanceId <= 0);
            float x = -(playerOptions.Count - 1) * 100f;
            foreach (var option in playerOptions)
            {
                var all = UiFactory.CreateButton(_theme, "All_" + option.Id, _body,
                    UI.Loc.T("ALL → ") + option.Label.ToUpperInvariant(), 12f);
                UiFactory.Place((RectTransform)all.transform, new Vector2(0.5f, 0f), new Vector2(x, 24f), new Vector2(190f, 40f));
                x += 200f;
                int id = option.Id;
                all.onClick.AddListener(() =>
                {
                    foreach (var key in new List<int>(_splitCounts.Keys))
                        _splitCounts[key] = key == id ? _request.Max : 0;
                    RefreshSplitLabels();
                    RefreshConfirm();
                });
            }
        }

        private void Bump(int id, int delta, TextMeshProUGUI label)
        {
            int total = 0;
            foreach (var kv in _splitCounts) total += kv.Value;
            if (delta > 0 && total >= _request.Max) return;
            _splitCounts[id] = Mathf.Max(0, _splitCounts[id] + delta);
            label.text = _splitCounts[id].ToString();
            RefreshConfirm();
        }

        private void RefreshSplitLabels()
        {
            int i = 0;
            foreach (var option in _request.Options)
                _splitLabels[i++].text = _splitCounts[option.Id].ToString();
        }

        // ------------------------------------------------------------------ confirm

        private void RefreshConfirm()
        {
            int count;
            if (_request.Context == "soi.split")
            {
                count = 0;
                foreach (var kv in _splitCounts) count += kv.Value;
                _confirmLabel.text = $"{UI.Loc.T("CONFIRM")} ({count}/{_request.Max})";
            }
            else
            {
                count = _picked.Count;
                _confirmLabel.text = _request.Max > 1 ? $"{UI.Loc.T("CONFIRM")} ({count})" : UI.Loc.T("CONFIRM");
            }
            _confirm.interactable = count >= _request.Min && count <= _request.Max;
        }

        private void Confirm()
        {
            var chosen = new List<int>();
            if (_request.Context == "soi.split")
            {
                foreach (var option in _request.Options)
                    for (int i = 0; i < _splitCounts[option.Id]; i++)
                        chosen.Add(option.Id);
            }
            else
            {
                chosen.AddRange(_picked);
            }
            var callback = _onConfirm;
            Hide();
            callback?.Invoke(chosen);
        }
    }
}
