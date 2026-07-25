using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Pascension.Game.View;
using UnityEngine.EventSystems;
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
        /// <summary>Hearthstone-style sizing: cards big enough to read across the table,
        /// titles and buttons to match.</summary>
        private const float TitleFontSize = 40f;
        private const float ButtonFontSize = 26f;
        private const float CardScale = 0.82f;   // was 0.6 inside the old boxed panel
        private const float ButtonRowY = 330f;   // clears the hand fan behind the window
        private const float RowWidth = 1400f;    // usable width of a split row before cards compress

        private UiTheme _theme;
        private RectTransform _root;
        private RectTransform _content;   // everything that HIDE parks
        private Image _dimmer;
        private RectTransform _panel;
        private TextMeshProUGUI _title;
        private RectTransform _body;
        private Button _confirm;
        private TextMeshProUGUI _confirmLabel;
        private Button _skip;
        private Button _hideToggle;
        private TextMeshProUGUI _hideToggleLabel;
        // Reorder mode ("soi.reorder"): the row IS the answer, left to right.
        private readonly List<int> _reorderIds = new();
        private readonly Dictionary<int, CardView> _reorderCards = new();
        private readonly Dictionary<int, TextMeshProUGUI> _reorderBadges = new();
        private RectTransform _reorderRow;
        private int _reorderDragging = -1;

        private DecisionRequest _request;
        private Action<List<int>> _onConfirm;
        private Func<int, string> _captionFor;
        private readonly List<int> _picked = new List<int>();
        private readonly List<(Button button, Image bg, int optionId)> _optionButtons = new();
        private readonly List<(CardView card, int optionId)> _optionCards = new();

        // Split-mode state.
        private readonly Dictionary<int, int> _heroAssign = new();     // player option id -> amount
        private readonly HashSet<int> _champPicked = new();            // champion option ids (kill-or-nothing toggle)
        private readonly List<(CardView card, DecisionOption option)> _champViews = new();
        private readonly Dictionary<int, TextMeshProUGUI> _heroAssignLabels = new(); // player option id -> label
        // Testudo (ShieldsProtectChampions) defenders: their champions take FREEFORM
        // amounts like the hero face (over-assign pays through the shields).
        private readonly Dictionary<int, int> _champAssign = new();    // champion option id -> amount
        private readonly Dictionary<int, TextMeshProUGUI> _champAssignLabels = new();

        public bool Visible => _root != null && _root.gameObject.activeSelf;

        public static SoiDecisionModal Create(Transform parent, UiTheme theme)
        {
            var rect = UiFactory.CreateRect("SoiDecisionModal", parent);
            UiFactory.Stretch(rect);
            var modal = rect.gameObject.AddComponent<SoiDecisionModal>();
            modal.Build(theme, rect);
            return modal;
        }

        /// <summary>Hearthstone-style chrome: NO window panel — just a soft dim over the
        /// table, big centered cards, a big title and big buttons. The table stays
        /// readable behind it, and a persistent HIDE toggle (a sibling of the content, so
        /// hiding can never hide the button that brings it back) lets the player go and
        /// study piles or the shop before committing.</summary>
        private void Build(UiTheme theme, RectTransform rect)
        {
            _theme = theme;
            _root = rect;

            _content = UiFactory.CreateRect("Content", rect);
            UiFactory.Stretch(_content);

            // A full-screen dark MASK, but no window panel: the choice floats over a
            // dimmed table rather than inside a box. (The hover preview raises itself
            // above this — see SoiGameScreen.OnAnyCardHovered.) Also the raycast blocker
            // that keeps stray clicks off the board mid-decision; HIDE parks the whole
            // thing, mask included, when the player wants to look around.
            _dimmer = UiFactory.CreateDimmer("Dimmer", _content);
            _dimmer.color = new Color(0f, 0f, 0f, 0.85f); // darker than the shared default

            // The old opaque 720x620 panel is gone; the "panel" is now the full-screen
            // content rect, so cards centre on the SCREEN and get the room they need.
            _panel = _content;

            _title = UiFactory.CreateText(theme, "Title", _content, "", TitleFontSize, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            // Below the opponent strip (which owns the top ~190px) and above the cards.
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -228f), new Vector2(1500f, 76f));
            _title.enableAutoSizing = true;      // long titles shrink instead of clipping
            _title.fontSizeMin = 20f;
            _title.fontSizeMax = TitleFontSize;
            var titleShadow = _title.gameObject.AddComponent<Outline>();
            titleShadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
            titleShadow.effectDistance = new Vector2(2.5f, -2.5f);

            _body = UiFactory.CreateRect("Body", _content);
            UiFactory.Place(_body, new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(1560f, 500f));

            _confirm = UiFactory.CreateButton(theme, "Confirm", _content, UI.Loc.T("CONFIRM"), ButtonFontSize,
                UiPalette.Gold, UiPalette.Background);
            // High enough to clear the hand fan behind the window (Show re-centres it
            // when PASS is hidden).
            UiFactory.Place((RectTransform)_confirm.transform, new Vector2(0.5f, 0f), new Vector2(-160f, ButtonRowY), new Vector2(300f, 76f));
            _confirm.onClick.AddListener(Confirm);
            _confirmLabel = UiFactory.ButtonLabel(_confirm);

            _skip = UiFactory.CreateButton(theme, "Skip", _content, UI.Loc.T("PASS"), ButtonFontSize);
            UiFactory.Place((RectTransform)_skip.transform, new Vector2(0.5f, 0f), new Vector2(160f, ButtonRowY), new Vector2(300f, 76f));
            _skip.onClick.AddListener(() =>
            {
                _picked.Clear();
                _heroAssign.Clear();
                _champPicked.Clear();
                _champAssign.Clear();
                Confirm();
            });

            // Sibling of _content (not a child): hiding the window must never hide this.
            _hideToggle = UiFactory.CreateButton(theme, "HideToggle", rect, UI.Loc.T("HIDE"), 19f);
            UiFactory.Place((RectTransform)_hideToggle.transform, new Vector2(1f, 0f), new Vector2(-150f, 152f), new Vector2(240f, 54f));
            _hideToggle.onClick.AddListener(ToggleHidden);
            _hideToggleLabel = UiFactory.ButtonLabel(_hideToggle);

            rect.gameObject.SetActive(false);
        }

        /// <summary>Park the window so the player can inspect the board/piles, and bring
        /// it back. The decision stays pending either way.</summary>
        private void ToggleHidden()
        {
            bool showing = _content.gameObject.activeSelf;
            _content.gameObject.SetActive(!showing);
            _hideToggleLabel.text = UI.Loc.T(showing ? "BACK TO CHOICE" : "HIDE");
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
            _champAssign.Clear();
            _champAssignLabels.Clear();
            _optionButtons.Clear();
            _optionCards.Clear();
            _reorderIds.Clear();
            _reorderCards.Clear();
            _reorderBadges.Clear();
            _reorderRow = null;
            _reorderDragging = -1;
            foreach (Transform child in _body)
                Destroy(child.gameObject);

            _title.text = UI.Loc.DecisionTitle(request.Title);
            // Reveal mode ("soi.defiant") owns its buttons: clicking one submits
            // immediately, so the shared CONFIRM/SKIP row hides entirely.
            bool reveal = request.Context == "soi.defiant";
            _confirm.gameObject.SetActive(!reveal);
            bool canSkip = !reveal && request.Min == 0;
            _skip.gameObject.SetActive(canSkip);
            // CONFIRM sits centred when it is the only button, and shifts left to make
            // room only when PASS is actually there.
            ((RectTransform)_confirm.transform).anchoredPosition =
                new Vector2(canSkip ? -160f : 0f, ButtonRowY);
            _content.gameObject.SetActive(true);
            _hideToggleLabel.text = UI.Loc.T("HIDE");
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();

            if (request.Context == "soi.split")
                BuildSplit(request, playerInfo);
            else if (reveal)
                BuildReveal(request, defIdResolver);
            else if (request.Context == "soi.reorder")
                BuildReorder(request, defIdResolver);
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
            // NO background: the shared scroll view paints a panel behind its content,
            // which is the last thing standing between these cards and the bare table.
            // Kept as an invisible raycast target so the wheel still scrolls.
            var backdrop = view.GetComponent<Image>();
            if (backdrop != null)
            {
                backdrop.color = new Color(0f, 0f, 0f, 0f);
                backdrop.sprite = null;
            }

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

        /// <summary>Mark an option unselectable WITHOUT making it transparent. The window
        /// has no background, so the usual alpha-grey would let the table show straight
        /// through the card; a dark veil keeps it readable and still clearly out of play.</summary>
        private void VeilCard(CardView card)
        {
            var veil = UiFactory.CreateImage("Disabled", card.Rect, _theme.Rounded,
                new Color(0.02f, 0.02f, 0.03f, 0.66f), raycast: false);
            UiFactory.Stretch(veil.rectTransform);
            veil.rectTransform.SetAsLastSibling();
        }

        private void SectionHeader(RectTransform content, string label)
        {
            var header = UiFactory.CreateRect("Header", content);
            header.sizeDelta = new Vector2(0f, 30f); // childControlHeight=false positions by RECT height
            var he = header.gameObject.AddComponent<LayoutElement>();
            he.preferredHeight = 30f;
            // A short centred rule under the name. Full-width would draw a line clean
            // across the screen now that the window has no panel behind it.
            var line = UiFactory.CreateImage("Line", header, null,
                UiPalette.WithAlpha(UiPalette.PanelLight, 0.55f), raycast: false);
            line.rectTransform.anchorMin = line.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            line.rectTransform.pivot = new Vector2(0.5f, 0f);
            line.rectTransform.anchoredPosition = new Vector2(0f, 2f);
            line.rectTransform.sizeDelta = new Vector2(520f, 2f);
            var text = UiFactory.CreateText(_theme, "Label", header, label, 18f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
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
        /// per section instead of a caption under every card. Cards are CENTERED per row
        /// (a Hearthstone-style spread, not a left-packed grid) and sized to be read from
        /// across the table.</summary>
        private void BuildCardGrid(DecisionRequest request, Func<int, string> optionLabel, Func<int, string> defIdResolver)
        {
            const int columns = 7;
            const float gap = 16f;
            float cellW = CardView.Width * CardScale + 8f;
            float cellH = CardView.Height * CardScale + 10f;

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
                    // Centre each row: the last row of a 7-wide spread sits under the
                    // middle of the one above it, not hard left.
                    int row = i / columns;
                    int inRow = i % columns;
                    int rowCount = Mathf.Min(columns, list.Count - row * columns);
                    float rowWidth = rowCount * cellW + (rowCount - 1) * gap;
                    var cell = UiFactory.CreateRect("Opt_" + id, grid);
                    cell.anchorMin = cell.anchorMax = new Vector2(0.5f, 1f);
                    cell.pivot = new Vector2(0f, 1f);
                    cell.anchoredPosition = new Vector2(-rowWidth / 2f + inRow * (cellW + gap), -row * (cellH + gap));
                    cell.sizeDelta = new Vector2(cellW, cellH);

                    string defId = OptionDefId(option, defIdResolver);
                    if (defId != null)
                    {
                        var card = CardViewFactory.Create(cell, _theme, CardScale);
                        // A greyed option (a reveal's non-qualifying card) is shown but
                        // never selectable.
                        if (!option.Disabled)
                            card.Clicked += _ => TogglePick(id);
                        card.Bind(new CardSnap { DefId = defId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                        if (option.Disabled) VeilCard(card);
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
                var card = CardViewFactory.Create(_body, _theme, 1.35f);
                card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0.5f, 1f);
                card.Rect.anchoredPosition = new Vector2(0f, -6f);
                card.Bind(new CardSnap { DefId = defId, InstanceId = 0, EffectiveCost = -1 });
                card.SetRaycastable(false);
                if (card.Group != null) card.Group.blocksRaycasts = false;
            }

            int count = request.Options.Count;
            const float buttonWidth = 300f, gap = 32f;
            float x0 = -((count - 1) * (buttonWidth + gap)) / 2f;
            for (int i = 0; i < count; i++)
            {
                var option = request.Options[i];
                bool primary = i == 0; // first option (Keep) gold, the rest (Banish) red
                var button = UiFactory.CreateButton(_theme, "Reveal_" + option.Id, _body,
                    UI.Loc.OptionLabel(option.Label).ToUpperInvariant(), ButtonFontSize,
                    primary ? UiPalette.Gold : UiPalette.Danger,
                    primary ? UiPalette.Background : UiPalette.TextMain);
                UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 0f),
                    new Vector2(x0 + i * (buttonWidth + gap), 20f), new Vector2(buttonWidth, 76f));
                int id = option.Id;
                button.onClick.AddListener(() =>
                {
                    _picked.Clear();
                    _picked.Add(id);
                    Confirm();
                });
            }
        }

        /// <summary>Text options (mode choices, target picks) — big centered rows, not a
        /// dense list, so they read like the rest of the Hearthstone-style window.</summary>
        private void BuildTextList(DecisionRequest request, Func<int, string> optionLabel)
        {
            const float rowHeight = 72f;
            var content = ScrollContent(out _);
            // Rows stretch to the content width, so inset it hard to get centred
            // ~900px buttons instead of one 1560px slab.
            var layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.padding = new RectOffset(330, 330, 14, 14);
                layout.spacing = 14f;
            }
            foreach (var option in request.Options)
            {
                string label = UI.Loc.OptionLabel(optionLabel != null ? optionLabel(option.Id) : option.Label);
                var button = UiFactory.CreateButton(_theme, "Opt_" + option.Id, content, label, ButtonFontSize);
                var lrect = (RectTransform)button.transform;
                lrect.sizeDelta = new Vector2(0f, rowHeight);
                var le = button.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = rowHeight;
                var buttonLabel = UiFactory.ButtonLabel(button);
                buttonLabel.enableAutoSizing = true;
                buttonLabel.fontSizeMin = 14f;
                buttonLabel.fontSizeMax = ButtonFontSize;
                int id = option.Id;
                var bg = button.GetComponent<Image>();
                button.interactable = !option.Disabled;
                if (button.interactable)
                    button.onClick.AddListener(() => TogglePick(id));
                _optionButtons.Add((button, bg, id));
            }
        }

        // ------------------------------------------------------------------ reorder mode

        /// <summary>"Put these back in any order" (Index of Futures): the cards lie in a
        /// row in their CURRENT order, each wearing its position number, and you DRAG
        /// them left/right to rearrange. Position 1 is the top of the deck. The answer is
        /// simply the row, left to right — no click-sequence to remember.</summary>
        private void BuildReorder(DecisionRequest request, Func<int, string> defIdResolver)
        {
            _reorderIds.Clear();
            _reorderCards.Clear();
            _reorderBadges.Clear();

            _reorderRow = UiFactory.CreateRect("Reorder", _body);
            UiFactory.Place(_reorderRow, new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(1400f, 300f));

            // On the CONTENT, between the title and the cards — a child of _body would be
            // drawn under the cards created after it.
            var hint = UiFactory.CreateText(_theme, "Hint", _content,
                UI.Loc.T("Drag the cards to reorder them — 1 goes on top."), 20f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(hint.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -296f), new Vector2(1200f, 32f));
            hint.raycastTarget = false;

            foreach (var option in request.Options)
            {
                _reorderIds.Add(option.Id);
                var card = CardViewFactory.Create(_reorderRow, _theme, CardScale);
                card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0.5f, 0.5f);
                card.Rect.pivot = new Vector2(0.5f, 0.5f);
                string defId = OptionDefId(option, defIdResolver);
                if (defId != null)
                    card.Bind(new CardSnap { DefId = defId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                var drag = card.gameObject.AddComponent<ReorderDrag>();
                drag.Modal = this;
                drag.OptionId = option.Id;

                // Position badge — big, gold, top-left, above the art.
                var badgeBg = UiFactory.CreateImage("PosBg", card.Rect, _theme.Circle, UiPalette.Gold);
                UiFactory.Place(badgeBg.rectTransform, new Vector2(0f, 1f), new Vector2(26f, -26f), new Vector2(46f, 46f));
                badgeBg.raycastTarget = false;
                var badge = UiFactory.CreateText(_theme, "Pos", card.Rect, "1", 30f,
                    UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(badge.rectTransform, new Vector2(0f, 1f), new Vector2(26f, -26f), new Vector2(46f, 46f));
                badge.raycastTarget = false;

                _reorderCards[option.Id] = card;
                _reorderBadges[option.Id] = badge;
            }
            LayoutReorder();
        }

        private float ReorderStep => CardView.Width * CardScale + 26f;

        /// <summary>Park every card on its slot and renumber. The card being dragged
        /// keeps following the pointer instead.</summary>
        private void LayoutReorder()
        {
            float step = ReorderStep;
            float x0 = -(_reorderIds.Count - 1) * step / 2f;
            for (int i = 0; i < _reorderIds.Count; i++)
            {
                int id = _reorderIds[i];
                if (_reorderBadges.TryGetValue(id, out var badge)) badge.text = (i + 1).ToString();
                if (!_reorderCards.TryGetValue(id, out var card) || card == null) continue;
                if (id == _reorderDragging) continue;
                card.Rect.anchoredPosition = new Vector2(x0 + i * step, 0f);
            }
        }

        internal void ReorderBegin(int optionId)
        {
            _reorderDragging = optionId;
            if (_reorderCards.TryGetValue(optionId, out var card) && card != null)
                card.transform.SetAsLastSibling(); // dragged card rides above the others
        }

        internal void ReorderMove(int optionId, PointerEventData e)
        {
            if (_reorderRow == null || !_reorderCards.TryGetValue(optionId, out var card) || card == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _reorderRow, e.position, e.pressEventCamera, out var local)) return;
            card.Rect.anchoredPosition = new Vector2(local.x, 0f);

            float step = ReorderStep;
            float x0 = -(_reorderIds.Count - 1) * step / 2f;
            int target = Mathf.Clamp(Mathf.RoundToInt((local.x - x0) / step), 0, _reorderIds.Count - 1);
            int current = _reorderIds.IndexOf(optionId);
            if (target != current && current >= 0)
            {
                _reorderIds.RemoveAt(current);
                _reorderIds.Insert(target, optionId);
                LayoutReorder();
            }
        }

        internal void ReorderEnd()
        {
            _reorderDragging = -1;
            LayoutReorder();
            RefreshConfirm();
        }

        /// <summary>Drag handles for the reorder row (the CardView keeps its own click and
        /// hover handlers — Unity dispatches to every handler on the object).</summary>
        private sealed class ReorderDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public SoiDecisionModal Modal;
            public int OptionId;
            public void OnBeginDrag(PointerEventData eventData) => Modal.ReorderBegin(OptionId);
            public void OnDrag(PointerEventData eventData) => Modal.ReorderMove(OptionId, eventData);
            public void OnEndDrag(PointerEventData eventData) => Modal.ReorderEnd();
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

        /// <summary>Whether this owner has a "shields protect champions" champion
        /// (Testudo Vanguard, Duel) among the split targets: their champions then take
        /// freeform amounts — over-assignment pays through the coming shield reveal.
        /// Pure display affordance from the card database; the engine stays the
        /// authority (its taunt-held rule zeroes under-assignments).</summary>
        private static bool OwnerHasTestudo(DecisionRequest request, int owner)
        {
            foreach (var option in request.Options)
                if (option.OwnerIndex == owner && option.CardInstanceId > 0 &&
                    !string.IsNullOrEmpty(option.DefId) &&
                    Shards.Engine.ShardsCardDatabase.TryGet(option.DefId, out var def) &&
                    def.ShieldsProtectChampions)
                    return true;
            return false;
        }

        private bool Assignable(int optionId) => _champAssign.ContainsKey(optionId);

        /// <summary>One section per opponent (separator + name + live health). The hero
        /// portrait takes any amount via 0/−/+/MAX; champions display their remaining
        /// HP and toggle exactly that amount — except under a Testudo defender, where
        /// they take freeform amounts exactly like the hero face. A Required (Taunt)
        /// champion locks every other target of its owner until selected (Testudo:
        /// until assigned at least its pre-shield lethal).</summary>
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

                bool testudo = OwnerHasTestudo(request, owner);
                if (testudo)
                {
                    var hint = UiFactory.CreateRect("TestudoHint", content);
                    hint.sizeDelta = new Vector2(0f, 20f);
                    var hintLe = hint.gameObject.AddComponent<LayoutElement>();
                    hintLe.preferredHeight = 20f;
                    var hintText = UiFactory.CreateText(_theme, "Label", hint,
                        UI.Loc.T("Their shields will reduce each champion's damage — assign extra to kill through."),
                        15f, UiPalette.GoldDim, TextAlignmentOptions.Center, FontStyles.Italic);
                    UiFactory.Stretch(hintText.rectTransform);
                }

                DecisionOption playerOption = null;
                int targets = 0;
                foreach (var option in request.Options)
                {
                    if (option.OwnerIndex != owner) continue;
                    targets++;
                    if (option.CardInstanceId <= 0)
                        playerOption = option;
                }

                // Rows carrying an assign strip (hero face; Testudo champions) are
                // taller: the 0/−/+/MAX buttons sit BELOW the card.
                float ownerRowHeight = playerOption != null || testudo ? rowHeight + 48f : rowHeight;
                var row = UiFactory.CreateRect("Owner_" + owner, content);
                row.sizeDelta = new Vector2(0f, ownerRowHeight);
                var le = row.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = ownerRowHeight;
                // Compress the step when a row would overflow (cards overlap, none drop),
                // then CENTRE the run: the hero and their champions sit in the middle of
                // the window, not packed against its left edge.
                float step = targets > 1 ? Mathf.Min(cardWidth + 14f, (RowWidth - cardWidth) / (targets - 1)) : cardWidth + 14f;
                float x = -((targets - 1) * step + cardWidth) / 2f;

                if (playerOption != null)
                {
                    var portrait = CardViewFactory.Create(row, _theme, scale);
                    portrait.Rect.anchorMin = portrait.Rect.anchorMax = new Vector2(0.5f, 1f);
                    portrait.Rect.pivot = new Vector2(0f, 1f);
                    portrait.Rect.anchoredPosition = new Vector2(x, 0f);
                    if (!string.IsNullOrEmpty(info.PortraitDefId))
                        portrait.BindDef(info.PortraitDefId);
                    portrait.SetRaycastable(false); // the face must not eat the button clicks

                    _heroAssign[playerOption.Id] = 0;
                    _heroAssignLabels[playerOption.Id] = AssignedLabel(portrait);
                    int pid = playerOption.Id;
                    BumpStrip(row, x, "H", kind => HeroBump(pid, kind));
                    x += step;
                }

                // Champions: live HP pill ON the card; kill-or-nothing toggle, or a
                // freeform assign strip under a Testudo defender.
                foreach (var option in request.Options)
                {
                    if (option.OwnerIndex != owner || option.CardInstanceId <= 0) continue;
                    var card = CardViewFactory.Create(row, _theme, scale);
                    card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0.5f, 1f);
                    card.Rect.pivot = new Vector2(0f, 1f);
                    card.Rect.anchoredPosition = new Vector2(x, 0f);
                    card.RotateWhenTapped = false;
                    int id = option.Id;
                    card.Bind(new CardSnap { DefId = option.DefId, InstanceId = option.CardInstanceId, EffectiveCost = -1 });
                    _champViews.Add((card, option));

                    if (testudo)
                    {
                        card.SetRaycastable(false); // strip buttons drive the amount
                        _champAssign[id] = 0;
                        _champAssignLabels[id] = AssignedLabel(card);
                        BumpStrip(row, x, "C", kind => ChampBump(id, kind));
                    }
                    else
                    {
                        card.Clicked += _ => ToggleChampion(id);
                    }

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

        /// <summary>The big assigned-amount number centered on a card, over a backdrop
        /// so it never disappears into bright card art.</summary>
        private TextMeshProUGUI AssignedLabel(CardView card)
        {
            var assignedBg = UiFactory.CreateImage("AssignedBg", card.Rect, _theme.Rounded,
                UiPalette.WithAlpha(UiPalette.Background, 0.72f));
            UiFactory.Place(assignedBg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(112f, 78f));
            assignedBg.raycastTarget = false;
            var assigned = UiFactory.CreateText(_theme, "Assigned", card.Rect, "0", 64f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(assigned.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(150f, 84f));
            var assignedOutline = assigned.gameObject.AddComponent<Outline>();
            assignedOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            assignedOutline.effectDistance = new Vector2(2f, -2f);
            assigned.raycastTarget = false;
            return assigned;
        }

        /// <summary>0/−/+/MAX strip under a card. x is measured from the row's CENTRE,
        /// matching the centred card run above it.</summary>
        private void BumpStrip(RectTransform row, float x, string prefix, Action<int> onBump)
        {
            float cardHeight = CardView.Height * 0.6f;
            string[] labels = { "0", "−", "+", "MAX" };
            for (int b = 0; b < 4; b++)
            {
                var button = UiFactory.CreateButton(_theme, prefix + labels[b], row, labels[b], 16f);
                UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 1f),
                    new Vector2(x + b * 36f, -(cardHeight + 4f)), new Vector2(34f, 40f));
                // NoWrap + autosize: "MAX" shrinks to fit instead of wrapping.
                var bumpLabel = UiFactory.ButtonLabel(button);
                bumpLabel.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                bumpLabel.enableAutoSizing = true;
                bumpLabel.fontSizeMin = 9f;
                bumpLabel.fontSizeMax = 18f;
                int kind = b;
                button.onClick.AddListener(() => onBump(kind));
            }
        }

        private int SplitTotal()
        {
            int total = 0;
            foreach (var kv in _heroAssign) total += kv.Value;
            foreach (var kv in _champAssign) total += kv.Value;
            foreach (var (_, option) in _champViews)
                if (!Assignable(option.Id) && _champPicked.Contains(option.Id))
                    total += option.Amount;
            return total;
        }

        /// <summary>A Taunt (Required) champion locks its owner's OTHER targets until it
        /// is selected — for a freeform (Testudo) taunt, until its assignment reaches
        /// its pre-shield lethal (option.Amount). It may still SURVIVE the shields; the
        /// engine's taunt-held rule then zeroes everything behind it.</summary>
        private bool OwnerUnlocked(int ownerIndex)
        {
            foreach (var (_, option) in _champViews)
            {
                if (!option.Required || option.OwnerIndex != ownerIndex) continue;
                if (Assignable(option.Id))
                {
                    if (_champAssign[option.Id] < option.Amount) return false;
                }
                else if (!_champPicked.Contains(option.Id))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Zero every dependent assignment of an owner whose taunt just
        /// dropped below lethal (the engine would waste those points anyway).</summary>
        private void RelockOwner(int ownerIndex, int exceptOptionId)
        {
            foreach (var (_, other) in _champViews)
            {
                if (other.OwnerIndex != ownerIndex || other.Id == exceptOptionId) continue;
                _champPicked.Remove(other.Id);
                if (_champAssign.ContainsKey(other.Id))
                    _champAssign[other.Id] = 0;
            }
            if (_heroAssign.ContainsKey(ownerIndex))
                _heroAssign[ownerIndex] = 0;
        }

        private int Bumped(int current, int kind, int remaining) => kind switch
        {
            0 => 0,
            1 => Mathf.Max(0, current - 1),
            2 => remaining > 0 ? current + 1 : current,
            _ => current + Mathf.Max(0, remaining), // MAX = take everything left
        };

        private void HeroBump(int playerOptionId, int kind)
        {
            int owner = playerOptionId; // player option id == player index
            if (!OwnerUnlocked(owner) && kind != 0) return;
            _heroAssign[playerOptionId] =
                Bumped(_heroAssign[playerOptionId], kind, _request.Max - SplitTotal());
            RefreshSplitVisuals();
        }

        private void ChampBump(int optionId, int kind)
        {
            DecisionOption option = null;
            foreach (var (_, o) in _champViews)
                if (o.Id == optionId)
                    option = o;
            if (option == null) return;
            // Non-taunt targets stay locked behind an unsatisfied taunt (0 always works).
            if (!option.Required && !OwnerUnlocked(option.OwnerIndex) && kind != 0) return;

            bool wasLethal = option.Required && _champAssign[optionId] >= option.Amount;
            _champAssign[optionId] = Bumped(_champAssign[optionId], kind, _request.Max - SplitTotal());
            // The taunt dropping below lethal re-locks its owner.
            if (wasLethal && option.Required && _champAssign[optionId] < option.Amount)
                RelockOwner(option.OwnerIndex, optionId);
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
                    RelockOwner(option.OwnerIndex, optionId);
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
                bool assignable = Assignable(option.Id);
                bool picked = assignable
                    ? _champAssign[option.Id] >= option.Amount // pre-shield lethal reached
                    : _champPicked.Contains(option.Id);
                bool unlocked = option.Required || OwnerUnlocked(option.OwnerIndex);
                bool selectable = picked || (assignable
                    ? unlocked                                  // freeform: any amount, when unlocked
                    : unlocked && option.Amount <= remaining);  // toggle: needs full lethal available
                // Picked/lethal = red kill glow; an unsatisfied Taunt champion glows
                // gold ("kill me first"); everything else unlit. Locked targets grey.
                if (picked) card.SetGlow(true, UiPalette.WoundedRed);
                else if (option.Required && selectable) card.SetGlow(true, UiPalette.Gold);
                else card.SetGlow(false);
                card.SetGreyed(!selectable);
            }
            foreach (var kv in _heroAssignLabels)
                kv.Value.text = _heroAssign.TryGetValue(kv.Key, out int amount) ? amount.ToString() : "0";
            foreach (var kv in _champAssignLabels)
            {
                int amount = _champAssign.TryGetValue(kv.Key, out int a) ? a : 0;
                kv.Value.text = amount.ToString();
                // Green once the assignment overshoots the live HP — the overkill is
                // what pays through the defender's shields.
                int lethal = 0;
                foreach (var (_, option) in _champViews)
                    if (option.Id == kv.Key)
                        lethal = option.Amount;
                kv.Value.color = amount > lethal ? UiPalette.HealthyGreen : UiPalette.Gold;
            }
            RefreshConfirm();
        }

        // ------------------------------------------------------------------ confirm

        private void RefreshConfirm()
        {
            int count;
            if (_reorderIds.Count > 0)
            {
                // Reorder: every card is always in the answer — the ORDER is the choice.
                count = _reorderIds.Count;
                _confirmLabel.text = UI.Loc.T("CONFIRM");
            }
            else if (_request.Context == "soi.split")
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
            if (_reorderIds.Count > 0)
            {
                chosen.AddRange(_reorderIds); // left-to-right IS the answer
            }
            else if (_request != null && _request.Context == "soi.split")
            {
                // Required (Taunt) champions first so the engine's rule guard sees the
                // lethal before its dependents; then other champions, then heroes.
                // Wire format: repeat the option id once per damage point — toggled
                // champions contribute their exact lethal, freeform (Testudo) ones
                // whatever was assigned.
                int AmountFor(DecisionOption option) => Assignable(option.Id)
                    ? _champAssign[option.Id]
                    : _champPicked.Contains(option.Id) ? option.Amount : 0;
                foreach (var (_, option) in _champViews)
                    if (option.Required)
                        for (int i = 0; i < AmountFor(option); i++)
                            chosen.Add(option.Id);
                foreach (var (_, option) in _champViews)
                    if (!option.Required)
                        for (int i = 0; i < AmountFor(option); i++)
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
