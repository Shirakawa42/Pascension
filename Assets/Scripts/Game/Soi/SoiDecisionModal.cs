using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Pascension.Game.View;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Generic decision UI for Shards of Infinity. Modes:
    /// - text list: one toggle-button per option.
    /// - card grid: options that reference cards render as REAL card faces, grouped in
    ///   sections by source zone (separator + zone name per group).
    /// - split ("soi.split"): a target picker grouped BY PLAYER — the hero portrait
    ///   takes a freeform amount via 0/−/+/MAX buttons; champions show their live HP
    ///   and toggle their EXACT remaining HP (kill-or-nothing); a Taunt champion
    ///   (Zetta) must be selected before its owner's other targets unlock.
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
        private readonly List<(Button button, Image bg, int optionId)> _optionButtons = new();
        private readonly List<(CardView card, int optionId)> _optionCards = new();

        // Split-mode state.
        private readonly Dictionary<int, int> _heroAssign = new();     // player option id -> amount
        private readonly HashSet<int> _champPicked = new();            // champion option ids
        private readonly List<(CardView card, DecisionOption option)> _champViews = new();
        private readonly Dictionary<int, TextMeshProUGUI> _heroAssignLabels = new(); // player option id -> label

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
            UiFactory.Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(720f, 620f));

            _title = UiFactory.CreateText(theme, "Title", _panel, "", 22f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -32f), new Vector2(680f, 56f));

            _body = UiFactory.CreateRect("Body", _panel);
            UiFactory.Place(_body, new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(680f, 430f));

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
                _heroAssign.Clear();
                _champPicked.Clear();
                Confirm();
            });

            rect.gameObject.SetActive(false);
        }

        /// <summary>Show a decision. `defIdResolver` maps a card instance id to its def
        /// id so options render as real card faces; `captionFor` maps an instance id to
        /// its source zone (drives the zone SECTIONS of the card grid); `playerInfo`
        /// supplies name/health/portrait per player index for the damage split.</summary>
        public void Show(DecisionRequest request, Func<int, string> optionLabel, Action<List<int>> onConfirm,
            Func<int, string> defIdResolver = null, Func<int, string> captionFor = null,
            Func<int, (string Name, int Health, int MaxHealth, string PortraitDefId)> playerInfo = null)
        {
            _captionFor = captionFor;
            _request = request;
            _onConfirm = onConfirm;
            _picked.Clear();
            _heroAssign.Clear();
            _champPicked.Clear();
            _champViews.Clear();
            _heroAssignLabels.Clear();
            _optionButtons.Clear();
            _optionCards.Clear();
            foreach (Transform child in _body)
                Destroy(child.gameObject);

            _title.text = UI.Loc.DecisionTitle(request.Title);
            // Reveal mode ("soi.defiant") owns its buttons: clicking one submits
            // immediately, so the shared CONFIRM/SKIP row hides entirely.
            bool reveal = request.Context == "soi.defiant";
            _confirm.gameObject.SetActive(!reveal);
            _skip.gameObject.SetActive(!reveal && request.Min == 0);
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();

            if (request.Context == "soi.split")
                BuildSplit(request, playerInfo);
            else if (reveal)
                BuildReveal(request, defIdResolver);
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

        // ------------------------------------------------------------------ shared chrome

        private RectTransform ScrollContent(out ScrollRect scroll)
        {
            var view = UiFactory.CreateScrollView(_theme, "Options", _body, out var content);
            scroll = view;
            UiFactory.Stretch((RectTransform)view.transform);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return content;
        }

        private void SectionHeader(RectTransform content, string label)
        {
            var header = UiFactory.CreateRect("Header", content);
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
            var text = UiFactory.CreateText(_theme, "Label", header, label, 14f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Stretch(text.rectTransform);
        }

        // ------------------------------------------------------------------ list mode

        private void BuildList(DecisionRequest request, Func<int, string> optionLabel, Func<int, string> defIdResolver)
        {
            bool anyCard = false;
            foreach (var option in request.Options)
                if (OptionDefId(option, defIdResolver) != null) { anyCard = true; break; }

            if (anyCard)
                BuildCardGrid(request, optionLabel, defIdResolver);
            else
                BuildTextList(request, optionLabel);
        }

        /// <summary>Card options grouped by SOURCE ZONE: a separator + zone-name header
        /// per section instead of a caption under every card.</summary>
        private void BuildCardGrid(DecisionRequest request, Func<int, string> optionLabel, Func<int, string> defIdResolver)
        {
            const int columns = 4;
            const float cellW = 140f, cellH = 198f, gap = 12f;

            var content = ScrollContent(out _);

            // Group options by zone caption, preserving first-seen order. Options
            // without a caption (or non-card options) collect in a tail group.
            var order = new List<string>();
            var groups = new Dictionary<string, List<DecisionOption>>();
            foreach (var option in request.Options)
            {
                string zone = _captionFor != null && option.CardInstanceId > 0
                    ? _captionFor(option.CardInstanceId) : null;
                zone ??= "";
                if (!groups.TryGetValue(zone, out var list))
                {
                    list = new List<DecisionOption>();
                    groups[zone] = list;
                    order.Add(zone);
                }
                list.Add(option);
            }

            foreach (string zone in order)
            {
                if (zone.Length > 0)
                    SectionHeader(content, zone);

                var list = groups[zone];
                int rows = (list.Count + columns - 1) / columns;
                var grid = UiFactory.CreateRect("Group", content);
                grid.sizeDelta = new Vector2(0f, rows * (cellH + gap) - gap);
                var ge = grid.gameObject.AddComponent<LayoutElement>();
                ge.preferredHeight = rows * (cellH + gap) - gap;
                for (int i = 0; i < list.Count; i++)
                {
                    var option = list[i];
                    int id = option.Id;
                    var cell = UiFactory.CreateRect("Opt_" + id, grid);
                    cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 1f);
                    cell.anchoredPosition = new Vector2(10f + i % columns * (cellW + gap), -(i / columns) * (cellH + gap));
                    cell.sizeDelta = new Vector2(cellW, cellH);

                    string defId = OptionDefId(option, defIdResolver);
                    if (defId != null)
                    {
                        var card = CardViewFactory.Create(cell, _theme, 0.6f);
                        card.Clicked += _ => TogglePick(id);
                        card.Bind(new CardSnap { DefId = defId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                        _optionCards.Add((card, id));
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
        }

        /// <summary>Reveal mode ("soi.defiant"): the revealed card itself, big and
        /// readable (mercenaries carry their red "M" triangle intrinsically), with one
        /// large action button per option below it. The choice is mandatory and a click
        /// submits immediately — no confirm/skip row.</summary>
        private void BuildReveal(DecisionRequest request, Func<int, string> defIdResolver)
        {
            string defId = null;
            foreach (var option in request.Options)
            {
                defId = OptionDefId(option, defIdResolver);
                if (defId != null) break;
            }

            if (defId != null)
            {
                var card = CardViewFactory.Create(_body, _theme, 1.02f);
                card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0.5f, 1f);
                card.Rect.anchoredPosition = new Vector2(0f, -6f);
                card.Bind(new CardSnap { DefId = defId, InstanceId = 0, EffectiveCost = -1 });
                card.SetRaycastable(false);
                if (card.Group != null) card.Group.blocksRaycasts = false;
            }

            int count = request.Options.Count;
            const float buttonWidth = 200f, gap = 24f;
            float x0 = -((count - 1) * (buttonWidth + gap)) / 2f;
            for (int i = 0; i < count; i++)
            {
                var option = request.Options[i];
                bool primary = i == 0; // first option (Keep) gold, the rest (Banish) red
                var button = UiFactory.CreateButton(_theme, "Reveal_" + option.Id, _body,
                    UI.Loc.OptionLabel(option.Label).ToUpperInvariant(), 19f,
                    primary ? UiPalette.Gold : UiPalette.Danger,
                    primary ? UiPalette.Background : UiPalette.TextMain);
                UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 0f),
                    new Vector2(x0 + i * (buttonWidth + gap), 34f), new Vector2(buttonWidth, 58f));
                int id = option.Id;
                button.onClick.AddListener(() =>
                {
                    _picked.Clear();
                    _picked.Add(id);
                    Confirm();
                });
            }
        }

        private void BuildTextList(DecisionRequest request, Func<int, string> optionLabel)
        {
            var content = ScrollContent(out _);
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

        /// <summary>One section per opponent (separator + name + live health). The hero
        /// portrait takes any amount via 0/−/+/MAX; champions display their remaining
        /// HP and toggle exactly that amount. A Required (Taunt) champion locks every
        /// other target of its owner until selected.</summary>
        private void BuildSplit(DecisionRequest request,
            Func<int, (string Name, int Health, int MaxHealth, string PortraitDefId)> playerInfo)
        {
            var content = ScrollContent(out _);
            const float scale = 0.6f; // normal decision-card size (same as the card grid)
            const float cardWidth = CardView.Width * scale;
            const float rowHeight = 198f;

            // Section order = order of first option per owner.
            var owners = new List<int>();
            foreach (var option in request.Options)
                if (!owners.Contains(option.OwnerIndex))
                    owners.Add(option.OwnerIndex);

            foreach (int owner in owners)
            {
                var info = playerInfo != null ? playerInfo(owner) : (Name: "P" + owner, Health: 0, MaxHealth: 0, PortraitDefId: null);
                SectionHeader(content, info.MaxHealth > 0
                    ? $"{info.Name}   <color=#6FDF8F>{info.Health}/{info.MaxHealth}</color>"
                    : info.Name);

                DecisionOption playerOption = null;
                int targets = 0;
                foreach (var option in request.Options)
                {
                    if (option.OwnerIndex != owner) continue;
                    targets++;
                    if (option.CardInstanceId <= 0)
                        playerOption = option;
                }

                // The hero row is taller: the 0/−/+/MAX strip sits BELOW the portrait.
                float ownerRowHeight = playerOption != null ? rowHeight + 48f : rowHeight;
                var row = UiFactory.CreateRect("Owner_" + owner, content);
                row.sizeDelta = new Vector2(0f, ownerRowHeight);
                var le = row.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = ownerRowHeight;
                // Compress the step when a row would overflow (cards overlap, none drop).
                float step = targets > 1 ? Mathf.Min(cardWidth + 14f, (656f - cardWidth) / (targets - 1)) : cardWidth + 14f;

                float x = 8f;
                if (playerOption != null)
                {
                    var portrait = CardViewFactory.Create(row, _theme, scale);
                    portrait.Rect.anchorMin = portrait.Rect.anchorMax = new Vector2(0f, 1f);
                    portrait.Rect.pivot = new Vector2(0f, 1f);
                    portrait.Rect.anchoredPosition = new Vector2(x, 0f);
                    if (!string.IsNullOrEmpty(info.PortraitDefId))
                        portrait.BindDef(info.PortraitDefId);
                    portrait.SetRaycastable(false); // the face must not eat the button clicks

                    _heroAssign[playerOption.Id] = 0;
                    // Assigned amount centered ON the portrait, over a backdrop so it
                    // never disappears into a bright card art.
                    var assignedBg = UiFactory.CreateImage("AssignedBg", portrait.Rect, _theme.Rounded,
                        UiPalette.WithAlpha(UiPalette.Background, 0.72f));
                    UiFactory.Place(assignedBg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(112f, 78f));
                    assignedBg.raycastTarget = false;
                    var assigned = UiFactory.CreateText(_theme, "Assigned", portrait.Rect, "0", 64f,
                        UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
                    UiFactory.Place(assigned.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(150f, 84f));
                    var assignedOutline = assigned.gameObject.AddComponent<Outline>();
                    assignedOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
                    assignedOutline.effectDistance = new Vector2(2f, -2f);
                    assigned.raycastTarget = false;
                    _heroAssignLabels[playerOption.Id] = assigned;

                    // 0/−/+/MAX strip BELOW the portrait (row-local, unscaled).
                    int pid = playerOption.Id;
                    float cardHeight = CardView.Height * scale;
                    string[] labels = { "0", "−", "+", "MAX" };
                    for (int b = 0; b < 4; b++)
                    {
                        var button = UiFactory.CreateButton(_theme, "H" + labels[b], row, labels[b], 16f);
                        UiFactory.Place((RectTransform)button.transform, new Vector2(0f, 1f),
                            new Vector2(x + b * 36f, -(cardHeight + 4f)), new Vector2(34f, 40f));
                        // NoWrap + autosize: "MAX" shrinks to fit instead of wrapping.
                        var bumpLabel = UiFactory.ButtonLabel(button);
                        bumpLabel.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                        bumpLabel.enableAutoSizing = true;
                        bumpLabel.fontSizeMin = 9f;
                        bumpLabel.fontSizeMax = 18f;
                        int kind = b;
                        button.onClick.AddListener(() => HeroBump(pid, kind));
                    }
                    x += step;
                }

                // Champions: live HP pill ON the card, exact-HP toggle.
                foreach (var option in request.Options)
                {
                    if (option.OwnerIndex != owner || option.CardInstanceId <= 0) continue;
                    var card = CardViewFactory.Create(row, _theme, scale);
                    card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0f, 1f);
                    card.Rect.pivot = new Vector2(0f, 1f);
                    card.Rect.anchoredPosition = new Vector2(x, 0f);
                    card.RotateWhenTapped = false;
                    int id = option.Id;
                    card.Clicked += _ => ToggleChampion(id);
                    card.Bind(new CardSnap { DefId = option.DefId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                    _champViews.Add((card, option));

                    // Modifier-adjusted HP on the card's own red disc: green when
                    // buffed above the printed defense, red when below.
                    int printed = Shards.Engine.ShardsCardDatabase.TryGet(option.DefId, out var champDef)
                        ? champDef.Defense : option.Amount;
                    card.SetBadge(option.Amount.ToString(),
                        option.Amount > printed ? UiPalette.HealthyGreen
                        : option.Amount < printed ? UiPalette.WoundedRed
                        : Color.white);
                    x += step;
                }
            }

            RefreshSplitVisuals();
        }

        private int SplitTotal()
        {
            int total = 0;
            foreach (var kv in _heroAssign) total += kv.Value;
            foreach (var (_, option) in _champViews)
                if (_champPicked.Contains(option.Id))
                    total += option.Amount;
            return total;
        }

        /// <summary>A Taunt (Required) champion locks its owner's OTHER targets until
        /// it is selected (it will die to the same split).</summary>
        private bool OwnerUnlocked(int ownerIndex)
        {
            foreach (var (_, option) in _champViews)
                if (option.Required && option.OwnerIndex == ownerIndex && !_champPicked.Contains(option.Id))
                    return false;
            return true;
        }

        private void HeroBump(int playerOptionId, int kind)
        {
            int owner = playerOptionId; // player option id == player index
            if (!OwnerUnlocked(owner) && kind != 0) return;
            int current = _heroAssign[playerOptionId];
            int remaining = _request.Max - SplitTotal();
            _heroAssign[playerOptionId] = kind switch
            {
                0 => 0,
                1 => Mathf.Max(0, current - 1),
                2 => remaining > 0 ? current + 1 : current,
                _ => current + Mathf.Max(0, remaining), // MAX = take everything left
            };
            RefreshSplitVisuals();
        }

        private void ToggleChampion(int optionId)
        {
            DecisionOption option = null;
            foreach (var (_, o) in _champViews)
                if (o.Id == optionId)
                    option = o;
            if (option == null) return;

            if (_champPicked.Contains(optionId))
            {
                _champPicked.Remove(optionId);
                // Deselecting the Taunt champion re-locks the owner: clear everything
                // that depended on it.
                if (option.Required)
                {
                    foreach (var (_, other) in _champViews)
                        if (other.OwnerIndex == option.OwnerIndex && other.Id != optionId)
                            _champPicked.Remove(other.Id);
                    if (_heroAssign.ContainsKey(option.OwnerIndex))
                        _heroAssign[option.OwnerIndex] = 0;
                }
            }
            else
            {
                if (!option.Required && !OwnerUnlocked(option.OwnerIndex)) return;
                if (option.Amount > _request.Max - SplitTotal()) return; // not enough damage left
                _champPicked.Add(optionId);
            }
            RefreshSplitVisuals();
        }

        private void RefreshSplitVisuals()
        {
            int remaining = _request.Max - SplitTotal();
            foreach (var (card, option) in _champViews)
            {
                bool picked = _champPicked.Contains(option.Id);
                bool selectable = picked ||
                    ((option.Required || OwnerUnlocked(option.OwnerIndex)) && option.Amount <= remaining);
                // Picked = red kill glow; an unpicked Taunt champion glows gold ("kill
                // me first"); everything else unlit. Unselectable targets grey out.
                if (picked) card.SetGlow(true, UiPalette.WoundedRed);
                else if (option.Required && selectable) card.SetGlow(true, UiPalette.Gold);
                else card.SetGlow(false);
                card.SetGreyed(!selectable);
            }
            foreach (var kv in _heroAssignLabels)
                kv.Value.text = _heroAssign.TryGetValue(kv.Key, out int amount) ? amount.ToString() : "0";
            RefreshConfirm();
        }

        // ------------------------------------------------------------------ confirm

        private void RefreshConfirm()
        {
            int count;
            if (_request.Context == "soi.split")
            {
                count = SplitTotal();
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
            if (_request != null && _request.Context == "soi.split")
            {
                // Required (Taunt) champions first so the engine's rule guard sees the
                // lethal before its dependents; then other champions, then heroes.
                foreach (var (_, option) in _champViews)
                    if (option.Required && _champPicked.Contains(option.Id))
                        for (int i = 0; i < option.Amount; i++)
                            chosen.Add(option.Id);
                foreach (var (_, option) in _champViews)
                    if (!option.Required && _champPicked.Contains(option.Id))
                        for (int i = 0; i < option.Amount; i++)
                            chosen.Add(option.Id);
                foreach (var kv in _heroAssign)
                    for (int i = 0; i < kv.Value; i++)
                        chosen.Add(kv.Key);
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
