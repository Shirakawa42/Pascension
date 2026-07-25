using System;
using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Game.View;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Duel of Doom turn-1 hero draft — a carousel ("wheel") over the available heroes.
    /// Each entry shows the hero portrait, the unique hero ability (independent of Focus —
    /// both blocks render separately), and the hero's relics. A persistent SHOW/HIDE
    /// button lets the player inspect the already-dealt shop before committing; the button
    /// itself never hides, so the draft can always be brought back. Pure presentation:
    /// renders the soi.herodraft decision and submits the chosen option id.
    /// </summary>
    public sealed class SoiHeroDraftPanel : MonoBehaviour
    {
        private UiTheme _theme;
        private RectTransform _root;     // full-screen; active only while a draft is pending
        private RectTransform _content;  // everything EXCEPT the persistent shop toggle
        private TextMeshProUGUI _title, _subtitle, _heroName, _abilityText, _relicLabel;
        private Button _confirm, _shopToggle, _prev, _next;
        private TextMeshProUGUI _confirmLabel, _shopToggleLabel;
        private CardView _portrait;
        private CardView _abilityCard; // the focused hero's ability, as its real card
        private RectTransform _relicRow, _thumbRow, _infoPanel;
        private readonly List<CardView> _relicViews = new List<CardView>();
        private readonly List<CardView> _thumbs = new List<CardView>();

        private DecisionRequest _request;
        private Action<int> _onPick;
        private ShardsDlc _dlc;
        private readonly List<(int OptionId, string HeroId)> _options = new List<(int, string)>();
        private int _focus;

        public bool Visible => _root != null && _root.gameObject.activeSelf;

        public static SoiHeroDraftPanel Create(RectTransform parent, UiTheme theme)
        {
            var root = UiFactory.CreateRect("HeroDraft", parent);
            UiFactory.Stretch(root);
            var panel = root.gameObject.AddComponent<SoiHeroDraftPanel>();
            panel._theme = theme;
            panel._root = root;
            panel.Build();
            root.gameObject.SetActive(false);
            return panel;
        }

        private void Build()
        {
            _content = UiFactory.CreateRect("Content", _root);
            UiFactory.Stretch(_content);

            // Near-opaque dim over the whole table (input-blocking): the draft must read
            // cleanly — the shop is inspected via the VIEW SHOP toggle, not through the dim.
            var dimmer = UiFactory.CreateImage("Dimmer", _content, null, new Color(0.05f, 0.04f, 0.03f, 0.94f), raycast: true);
            UiFactory.Stretch(dimmer.rectTransform);

            _title = UiFactory.CreateText(_theme, "Title", _content, UI.Loc.T("CHOOSE YOUR HERO"), 42f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.characterSpacing = 4f;
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -56f), new Vector2(1200f, 54f));
            _subtitle = UiFactory.CreateText(_theme, "Subtitle", _content,
                UI.Loc.T("The shop is already dealt — check it before you commit."), 19f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(_subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -112f), new Vector2(1200f, 28f));

            // Carousel arrows.
            _prev = UiFactory.CreateButton(_theme, "Prev", _content, "‹", 46f);
            UiFactory.Place((RectTransform)_prev.transform, new Vector2(0.5f, 0.5f), new Vector2(-660f, 40f), new Vector2(72f, 130f));
            _prev.onClick.AddListener(() => Cycle(-1));
            _next = UiFactory.CreateButton(_theme, "Next", _content, "›", 46f);
            UiFactory.Place((RectTransform)_next.transform, new Vector2(0.5f, 0.5f), new Vector2(660f, 40f), new Vector2(72f, 130f));
            _next.onClick.AddListener(() => Cycle(+1));

            // Focused hero: big portrait (its face carries Focus) + an info panel whose
            // details are headed by the hero's ABILITY CARD — the ability is what you are
            // really drafting, so it shows here as the real card, own art and all.
            _portrait = CardViewFactory.Create(_content, _theme, 1.05f);
            _portrait.Rect.anchorMin = _portrait.Rect.anchorMax = _portrait.Rect.pivot = new Vector2(0.5f, 0.5f);
            _portrait.Rect.anchoredPosition = new Vector2(-380f, 40f);
            _portrait.SetRaycastable(false);

            _infoPanel = UiFactory.CreateRect("Info", _content);
            UiFactory.Place(_infoPanel, new Vector2(0.5f, 0.5f), new Vector2(170f, 40f), new Vector2(680f, 470f));
            var backdrop = UiFactory.CreatePanel(_theme, "Backdrop", _infoPanel, UiPalette.WithAlpha(UiPalette.Panel, 0.92f));
            UiFactory.Stretch((RectTransform)backdrop.transform);

            _heroName = UiFactory.CreateText(_theme, "Name", _infoPanel, "", 32f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_heroName.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -20f), new Vector2(620f, 40f));

            _abilityText = UiFactory.CreateText(_theme, "Ability", _infoPanel, "", 19f,
                UiPalette.TextMain, TextAlignmentOptions.TopLeft);
            if (_theme.Icons != null) _abilityText.spriteAsset = _theme.Icons;
            // Narrowed to leave room for the ability card on the right.
            UiFactory.Place(_abilityText.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -72f), new Vector2(470f, 150f));

            _abilityCard = CardViewFactory.Create(_infoPanel, _theme, 0.5f);
            _abilityCard.Rect.anchorMin = _abilityCard.Rect.anchorMax = _abilityCard.Rect.pivot = new Vector2(0f, 1f);
            _abilityCard.Rect.anchoredPosition = new Vector2(520f, -56f);
            _abilityCard.SetRaycastable(false);

            _relicLabel = UiFactory.CreateText(_theme, "RelicLabel", _infoPanel,
                UI.Loc.T("RELICS — recruit one free at Mastery 10"), 16f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_relicLabel.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -226f), new Vector2(620f, 24f));
            _relicRow = UiFactory.CreateRect("Relics", _infoPanel);
            UiFactory.Place(_relicRow, new Vector2(0f, 1f), new Vector2(28f, -252f), new Vector2(620f, 200f));

            _confirm = UiFactory.CreateButton(_theme, "Confirm", _content, "", 24f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_confirm.transform, new Vector2(0.5f, 0.5f), new Vector2(170f, -260f), new Vector2(380f, 64f));
            _confirm.onClick.AddListener(() =>
            {
                if (_request == null || _focus < 0 || _focus >= _options.Count) return;
                var pick = _options[_focus].OptionId;
                var onPick = _onPick;
                Hide();
                onPick?.Invoke(pick);
            });
            _confirmLabel = UiFactory.ButtonLabel(_confirm);

            // The "wheel": one thumbnail per available hero, click to focus.
            _thumbRow = UiFactory.CreateRect("Thumbs", _content);
            UiFactory.Place(_thumbRow, new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(1400f, 130f));

            // Persistent shop toggle — a sibling of Content, so hiding the draft to look
            // at the shop NEVER hides the button that brings the draft back.
            _shopToggle = UiFactory.CreateButton(_theme, "ShopToggle", _root, UI.Loc.T("VIEW SHOP"), 19f);
            UiFactory.Place((RectTransform)_shopToggle.transform, new Vector2(0.5f, 0f), new Vector2(0f, 44f), new Vector2(260f, 54f));
            _shopToggle.onClick.AddListener(ToggleShop);
            _shopToggleLabel = UiFactory.ButtonLabel(_shopToggle);
        }

        public void Show(DecisionRequest request, ShardsDlc dlc, Action<int> onPick)
        {
            _request = request;
            _onPick = onPick;
            _dlc = dlc;
            _options.Clear();
            foreach (var option in request.Options)
                _options.Add((option.Id, string.IsNullOrEmpty(option.DefId) ? option.Label : option.DefId));
            if (_options.Count == 0) return;

            int focus = 0;
            if (request.DefaultOptionIds.Count > 0)
            {
                int byDefault = _options.FindIndex(o => o.OptionId == request.DefaultOptionIds[0]);
                if (byDefault >= 0) focus = byDefault;
            }

            BuildThumbs();
            _root.gameObject.SetActive(true);
            _content.gameObject.SetActive(true);
            _shopToggleLabel.text = UI.Loc.T("VIEW SHOP");
            FocusHero(focus);
        }

        public void Hide()
        {
            _request = null;
            _onPick = null;
            if (_root != null)
                _root.gameObject.SetActive(false);
        }

        /// <summary>Stale-decision guard: hide unless the given decision is the draft this
        /// panel is currently showing.</summary>
        public void HideIfNot(DecisionRequest current)
        {
            if (_request == null) return;
            if (current == null || current.Id != _request.Id)
                Hide();
        }

        private void Cycle(int delta)
        {
            if (_options.Count == 0) return;
            FocusHero((_focus + delta + _options.Count) % _options.Count);
        }

        private void BuildThumbs()
        {
            foreach (var thumb in _thumbs)
                if (thumb != null)
                    Destroy(thumb.gameObject);
            _thumbs.Clear();

            float step = 96f;
            float x = -(_options.Count - 1) * step * 0.5f;
            for (int i = 0; i < _options.Count; i++)
            {
                var thumb = CardViewFactory.Create(_thumbRow, _theme, 0.3f);
                thumb.Rect.anchorMin = thumb.Rect.anchorMax = thumb.Rect.pivot = new Vector2(0.5f, 0.5f);
                thumb.Rect.anchoredPosition = new Vector2(x, 0f);
                thumb.BindDef(SoiCardFaces.CharacterPrefix + _options[i].HeroId);
                int index = i;
                thumb.Clicked += _ => FocusHero(index);
                _thumbs.Add(thumb);
                x += step;
            }
        }

        private void FocusHero(int index)
        {
            if (index < 0 || index >= _options.Count) return;
            _focus = index;
            string heroId = _options[index].HeroId;
            string display = Shards.Content.ShardsContentRegistry.CharacterDisplayName(heroId);

            _portrait.BindDef(SoiCardFaces.CharacterPrefix + heroId);
            _heroName.text = display;

            // The hero's unique ability and Focus, as SEPARATE blocks — they are
            // independent activations and can both be used in the same turn.
            var spec = ShardsEngine.HeroAbilityInfo(heroId);
            string ability = spec.Name == null
                ? ""
                : "<color=#E4C05A><b>" + UI.Loc.T(spec.Name) + "</b></color> — " + UI.Loc.T(spec.Text) + "\n\n";
            _abilityText.text = SoiCardFaces.Iconize(
                ability + "<color=#8A8377>" + SoiCardFaces.FocusText() + "</color>");
            // The ability as its real card (own art). Heroes without one (none today)
            // simply hide it.
            bool hasAbility = spec.Name != null;
            _abilityCard.gameObject.SetActive(hasAbility);
            if (hasAbility)
                _abilityCard.BindDef(SoiCardFaces.AbilityPrefix + heroId);

            // Relics from the same source the engine deals from.
            foreach (var view in _relicViews)
                if (view != null)
                    Destroy(view.gameObject);
            _relicViews.Clear();
            var relics = ShardsEngine.RelicIdsFor(heroId, _dlc);
            float rx = 60f;
            foreach (var relicId in relics)
            {
                var view = CardViewFactory.Create(_relicRow, _theme, 0.52f);
                view.Rect.anchorMin = view.Rect.anchorMax = view.Rect.pivot = new Vector2(0f, 1f);
                view.Rect.anchoredPosition = new Vector2(rx, -8f);
                view.BindDef(relicId);
                _relicViews.Add(view);
                rx += 132f;
            }

            _confirmLabel.text = string.Format(UI.Loc.T("PLAY {0}"), display.ToUpperInvariant());

            for (int i = 0; i < _thumbs.Count; i++)
                _thumbs[i].SetGlow(i == _focus, UiPalette.Gold);
        }

        private void ToggleShop()
        {
            bool showing = _content.gameObject.activeSelf;
            _content.gameObject.SetActive(!showing);
            _shopToggleLabel.text = UI.Loc.T(showing ? "BACK TO DRAFT" : "VIEW SHOP");
        }
    }
}
