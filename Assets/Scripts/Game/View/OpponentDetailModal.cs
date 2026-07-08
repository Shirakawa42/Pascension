using System;
using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Heroes;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Full opponent inspection: hero + passives (with unlock levels), active/ultimate
    /// state, level/XP, position, resources, equipment cards, relics, and buttons to
    /// browse every pile (deck alphabetical — full-transparency rule). Opened by
    /// clicking an opponent's top-bar sheet.
    /// </summary>
    public sealed class OpponentDetailModal : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Raised with (title, cards) when a pile browse is requested.</summary>
        public event Action<string, List<CardSnap>> BrowseRequested;

        private bool _built;
        private Image _portrait;
        private TextMeshProUGUI _portraitInitial;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _heroLine;
        private TextMeshProUGUI _statsLine;
        private TextMeshProUGUI _passives;
        private TextMeshProUGUI _activeLine;
        private TextMeshProUGUI _ultimateLine;
        private Image _xpFill;
        private TextMeshProUGUI _xpText;
        private readonly CardView[] _equipment = new CardView[3];
        private RectTransform _relicRow;
        private Button _deckButton, _discardButton, _exileButton, _playedButton;

        private PlayerSnap _shown;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var dimmer = UiFactory.CreateDimmer("Dimmer", Container);
            dimmer.gameObject.AddComponent<Button>().onClick.AddListener(Hide);

            var panel = UiFactory.CreatePanel(Theme, "Panel", Container);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 640f));

            // ---- header ----
            var portraitFrame = UiFactory.CreateImage("PortraitFrame", panel.transform, Theme.Rounded, UiPalette.Border);
            UiFactory.Place(portraitFrame.rectTransform, new Vector2(0f, 1f), new Vector2(20f, -20f), new Vector2(140f, 140f));
            _portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.PanelLight);
            UiFactory.Stretch(_portrait.rectTransform, 3, 3, 3, 3);
            _portraitInitial = UiFactory.CreateText(Theme, "Initial", portraitFrame.transform, "?", 56f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_portraitInitial.rectTransform);

            _name = UiFactory.CreateText(Theme, "Name", panel.transform, "", 30f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_name.rectTransform, new Vector2(0f, 1f), new Vector2(178f, -22f), new Vector2(420f, 34f));

            _heroLine = UiFactory.CreateText(Theme, "Hero", panel.transform, "", 17f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_heroLine.rectTransform, new Vector2(0f, 1f), new Vector2(178f, -58f), new Vector2(420f, 22f));

            _statsLine = UiFactory.CreateText(Theme, "Stats", panel.transform, "", 16f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_statsLine.rectTransform, new Vector2(0f, 1f), new Vector2(178f, -86f), new Vector2(560f, 22f));

            var xpBack = UiFactory.CreateImage("XpBack", panel.transform, Theme.Rounded, UiPalette.Background);
            UiFactory.Place(xpBack.rectTransform, new Vector2(0f, 1f), new Vector2(178f, -116f), new Vector2(300f, 14f));
            _xpFill = UiFactory.CreateImage("XpFill", xpBack.transform, Theme.Rounded, UiPalette.Good);
            _xpFill.rectTransform.anchorMin = Vector2.zero;
            _xpFill.rectTransform.anchorMax = new Vector2(0.02f, 1f);
            _xpFill.rectTransform.offsetMin = new Vector2(1f, 1f);
            _xpFill.rectTransform.offsetMax = new Vector2(-1f, -1f);
            _xpText = UiFactory.CreateText(Theme, "XpText", panel.transform, "", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_xpText.rectTransform, new Vector2(0f, 1f), new Vector2(486f, -116f), new Vector2(120f, 16f));

            // ---- abilities ----
            _passives = UiFactory.CreateText(Theme, "Passives", panel.transform, "", 14f,
                UiPalette.TextDim, TextAlignmentOptions.TopLeft, FontStyles.Italic);
            UiFactory.Place(_passives.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -176f), new Vector2(520f, 84f));
            _passives.enableAutoSizing = true;
            _passives.fontSizeMin = 10f;
            _passives.fontSizeMax = 14f;

            _activeLine = UiFactory.CreateText(Theme, "Active", panel.transform, "", 14f,
                UiPalette.TextMain, TextAlignmentOptions.TopLeft);
            UiFactory.Place(_activeLine.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -266f), new Vector2(520f, 26f));

            _ultimateLine = UiFactory.CreateText(Theme, "Ultimate", panel.transform, "", 14f,
                UiPalette.TextMain, TextAlignmentOptions.TopLeft);
            UiFactory.Place(_ultimateLine.rectTransform, new Vector2(0f, 1f), new Vector2(24f, -296f), new Vector2(520f, 26f));

            // ---- equipment (right column) ----
            var equipLabel = UiFactory.CreateText(Theme, "EquipLabel", panel.transform, "EQUIPMENT", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(equipLabel.rectTransform, new Vector2(1f, 1f), new Vector2(-310f, -158f), new Vector2(200f, 18f));
            for (int i = 0; i < 3; i++)
            {
                var card = CardViewFactory.Create(panel.transform, Theme, 0.45f);
                card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(1f, 1f);
                card.Rect.anchoredPosition = new Vector2(-310f + i * 102f, -180f);
                card.RotateWhenTapped = false;
                card.gameObject.SetActive(false);
                _equipment[i] = card;
            }

            // ---- relics ----
            var relicLabel = UiFactory.CreateText(Theme, "RelicLabel", panel.transform, "RELICS", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(relicLabel.rectTransform, new Vector2(0f, 0f), new Vector2(24f, 174f), new Vector2(200f, 18f));
            _relicRow = UiFactory.CreateRect("RelicRow", panel.transform);
            UiFactory.Place(_relicRow, new Vector2(0f, 0f), new Vector2(24f, 128f), new Vector2(600f, 40f));
            var relicLayout = _relicRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            relicLayout.spacing = 6f;
            relicLayout.childAlignment = TextAnchor.MiddleLeft;
            relicLayout.childControlWidth = false;
            relicLayout.childControlHeight = false;
            relicLayout.childForceExpandWidth = false;
            relicLayout.childForceExpandHeight = false;

            // ---- pile browse buttons ----
            _deckButton = BuildPileButton(panel.transform, 0, () => Browse("Deck (alphabetical, order hidden)", _shown?.Deck));
            _discardButton = BuildPileButton(panel.transform, 1, () => Browse("Discard", _shown?.Discard));
            _exileButton = BuildPileButton(panel.transform, 2, () => Browse("Exile", _shown?.Exile));
            _playedButton = BuildPileButton(panel.transform, 3, () => Browse("Played this turn", _shown?.PlayedThisTurn));

            var close = UiFactory.CreateButton(Theme, "Close", panel.transform, "CLOSE", 18f);
            UiFactory.Place((RectTransform)close.transform, new Vector2(1f, 0f), new Vector2(-16f, 14f), new Vector2(130f, 44f));
            close.onClick.AddListener(Hide);

            Container.gameObject.SetActive(false);
        }

        private Button BuildPileButton(Transform parent, int index, Action onClick)
        {
            var button = UiFactory.CreateButton(Theme, $"Pile{index}", parent, "", 14f,
                UiPalette.WithAlpha(UiPalette.Background, 0.9f), UiPalette.TextMain);
            UiFactory.Place((RectTransform)button.transform, new Vector2(0f, 0f),
                new Vector2(24f + index * 152f, 66f), new Vector2(144f, 44f));
            button.onClick.AddListener(() => onClick());
            return button;
        }

        private void Browse(string title, List<CardSnap> cards)
        {
            if (cards == null || _shown == null) return;
            BrowseRequested?.Invoke($"{_shown.Name} — {title} ({cards.Count})", cards);
        }

        public void Show(PlayerSnap p, GameRules rules)
        {
            if (!_built || p == null) return;
            _shown = p;
            Container.gameObject.SetActive(true);

            HeroDefinition hero = null;
            if (!string.IsNullOrEmpty(p.HeroId))
                try { hero = HeroDatabase.Get(p.HeroId); } catch (KeyNotFoundException) { }

            _name.text = p.Name + (p.Conceded ? "  (conceded)" : "");
            _heroLine.text = hero != null ? $"{hero.Name} — {hero.Archetype}" : p.HeroId;
            _statsLine.text = $"Level {p.Level}   ·   Step {p.Position}/50   ·   {p.Ap} AP   ·   {p.DamagePool} DMG   ·   Hand {p.HandCount}";

            var portrait = Theme.Art(p.HeroId);
            if (portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.color = Color.white;
                _portraitInitial.text = "";
            }
            else
            {
                _portrait.sprite = null;
                _portrait.color = UiPalette.PanelLight;
                _portraitInitial.text = !string.IsNullOrEmpty(p.HeroId) ? p.HeroId.Substring(0, 1).ToUpperInvariant() : "?";
            }

            int xpNeeded = p.Level >= 1 && p.Level <= rules.XpToNextLevel.Length
                ? rules.XpToNextLevel[p.Level - 1] : -1;
            _xpFill.rectTransform.anchorMax = new Vector2(
                Mathf.Max(0.02f, xpNeeded > 0 ? Mathf.Clamp01((float)p.Xp / xpNeeded) : 1f), 1f);
            _xpText.text = xpNeeded > 0 ? $"{p.Xp}/{xpNeeded} XP" : "MAX";

            _passives.text = HeroTextUtil.PassiveSummary(hero, p.Level);
            _activeLine.text = AbilityLine("Active", hero?.Active, hero?.ActiveUnlockLevel ?? 3, p.Level, p.HeroActiveUsed);
            _ultimateLine.text = AbilityLine("Ultimate", hero?.Ultimate, hero?.UltimateUnlockLevel ?? 9, p.Level, p.HeroUltimateUsed);

            for (int i = 0; i < 3; i++)
            {
                var snap = p.Equipment != null && i < p.Equipment.Length ? p.Equipment[i] : null;
                _equipment[i].gameObject.SetActive(snap != null);
                if (snap != null)
                {
                    _equipment[i].Bind(snap);
                    _equipment[i].SetGreyed(snap.Tapped);
                }
            }

            for (int i = _relicRow.childCount - 1; i >= 0; i--)
                Destroy(_relicRow.GetChild(i).gameObject);
            foreach (var relic in p.Relics)
            {
                var icon = UiFactory.CreateImage("Relic", _relicRow, Theme.Rounded, UiPalette.PanelLight);
                icon.rectTransform.sizeDelta = new Vector2(36f, 36f);
                var art = Theme.Art(relic.DefId);
                if (art != null)
                {
                    icon.sprite = art;
                    icon.type = Image.Type.Simple;
                    icon.color = Color.white;
                }
                var hover = icon.gameObject.AddComponent<HistoryHover>();
                hover.DefId = relic.DefId;
                icon.raycastTarget = true;
            }

            UiFactory.ButtonLabel(_deckButton).text = $"Deck {p.DeckCount}";
            UiFactory.ButtonLabel(_discardButton).text = $"Discard {p.Discard.Count}";
            UiFactory.ButtonLabel(_exileButton).text = $"Exile {p.Exile.Count}";
            UiFactory.ButtonLabel(_playedButton).text = $"Played {p.PlayedThisTurn.Count}";
        }

        private static string AbilityLine(string kind, ActivatedAbility ability, int unlock, int level, bool used)
        {
            if (ability == null) return "";
            string state = level < unlock ? $"(unlocks L{unlock})" : used ? "(used this turn)" : $"({ability.ApCost} AP, ready)";
            return $"<b>{kind}</b> — {ability.Description}  <color=#A99F8C>{state}</color>";
        }

        public void Hide()
        {
            if (_built) Container.gameObject.SetActive(false);
            _shown = null;
        }

        public bool IsShown => _built && Container.gameObject.activeSelf;
    }
}
