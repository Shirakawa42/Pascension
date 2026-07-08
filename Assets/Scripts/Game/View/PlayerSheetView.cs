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
    /// The local player's sheet (bottom-left): portrait, level/XP, AP + damage-pool
    /// crystals, equipment mini-cards, relic strip, zone counters and the hero
    /// active/ultimate buttons.
    /// </summary>
    public sealed class PlayerSheetView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action<int> EquipmentClicked;
        public event Action<bool> HeroAbilityClicked;
        /// <summary>"Deck" | "Discard" | "Exile" | "Relics" | "Played".</summary>
        public event Action<string> ZoneClicked;

        private GameRules _rules;
        private bool _built;

        private Image _portrait;
        private TextMeshProUGUI _portraitInitial;
        private TextMeshProUGUI _nameText;
        private TextMeshProUGUI _heroText;
        private TextMeshProUGUI _passiveText;
        private TextMeshProUGUI _levelText;
        private Image _xpFill;
        private TextMeshProUGUI _xpText;
        private TextMeshProUGUI _apText;
        private TextMeshProUGUI _damageText;
        private readonly CardView[] _equipment = new CardView[3];
        private RectTransform _relicRow;
        private TextMeshProUGUI _relicCount;
        private Button _deckButton;
        private Button _discardButton;
        private Button _exileButton;
        private Button _playedButton;
        private Button _activeButton;
        private Button _ultimateButton;
        private TextMeshProUGUI _activeNote;
        private TextMeshProUGUI _ultimateNote;

        private static readonly string[] SlotShort = { "W", "A", "T" };

        public void Init(UiTheme theme, GameRules rules)
        {
            Theme = theme;
            _rules = rules;
            if (_built) return;
            _built = true;

            // Grow past the scene-authored 262px so the hero passive block fits between
            // the identity rows (top-anchored) and the equipment slots (bottom-anchored).
            Container.sizeDelta = new Vector2(Container.sizeDelta.x, 306f);

            var panel = UiFactory.CreatePanel(Theme, "Sheet", Container);
            UiFactory.Stretch(panel.rectTransform);

            // ---- portrait ----
            var portraitFrame = UiFactory.CreateImage("PortraitFrame", panel.transform, Theme.Rounded, UiPalette.Border);
            UiFactory.Place(portraitFrame.rectTransform, new Vector2(0f, 1f), new Vector2(10f, -10f), new Vector2(92f, 92f));
            _portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.PanelLight);
            UiFactory.Stretch(_portrait.rectTransform, 3, 3, 3, 3);
            _portraitInitial = UiFactory.CreateText(Theme, "Initial", portraitFrame.transform, "?", 42f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_portraitInitial.rectTransform);

            // ---- identity + xp ----
            _nameText = UiFactory.CreateText(Theme, "Name", panel.transform, "You", 22f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_nameText.rectTransform, new Vector2(0f, 1f), new Vector2(112f, -10f), new Vector2(210f, 26f));

            _heroText = UiFactory.CreateText(Theme, "Hero", panel.transform, "", 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_heroText.rectTransform, new Vector2(0f, 1f), new Vector2(112f, -36f), new Vector2(210f, 20f));

            _levelText = UiFactory.CreateText(Theme, "Level", panel.transform, "Lv 1", 19f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_levelText.rectTransform, new Vector2(0f, 1f), new Vector2(112f, -58f), new Vector2(70f, 24f));

            var xpBack = UiFactory.CreateImage("XpBack", panel.transform, Theme.Rounded, UiPalette.Background);
            UiFactory.Place(xpBack.rectTransform, new Vector2(0f, 1f), new Vector2(112f, -84f), new Vector2(180f, 12f));
            _xpFill = UiFactory.CreateImage("XpFill", xpBack.transform, Theme.Rounded, UiPalette.Good);
            _xpFill.rectTransform.anchorMin = Vector2.zero;
            _xpFill.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            _xpFill.rectTransform.offsetMin = new Vector2(1f, 1f);
            _xpFill.rectTransform.offsetMax = new Vector2(-1f, -1f);
            _xpText = UiFactory.CreateText(Theme, "XpText", panel.transform, "0 / 2 XP", 12f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_xpText.rectTransform, new Vector2(0f, 1f), new Vector2(298f, -84f), new Vector2(90f, 14f));

            // ---- hero passive(s) ----
            _passiveText = UiFactory.CreateText(Theme, "Passives", panel.transform, "", 12f,
                UiPalette.TextDim, TextAlignmentOptions.TopLeft, FontStyles.Italic);
            UiFactory.Place(_passiveText.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -102f), new Vector2(492f, 48f));
            _passiveText.enableAutoSizing = true;
            _passiveText.fontSizeMin = 9f;
            _passiveText.fontSizeMax = 12f;

            // ---- AP / damage crystals ----
            _apText = BuildCrystal(panel.transform, "Ap", new Vector2(-116f, -14f), UiPalette.Gold, "AP");
            _damageText = BuildCrystal(panel.transform, "Damage", new Vector2(-42f, -14f), UiPalette.Danger, "DMG");

            // ---- equipment ----
            for (int i = 0; i < 3; i++)
            {
                float x = 14f + i * 86f;
                var slotFrame = UiFactory.CreateImage($"EquipSlot{i}", panel.transform, Theme.Rounded,
                    UiPalette.WithAlpha(UiPalette.Background, 0.8f));
                UiFactory.Place(slotFrame.rectTransform, new Vector2(0f, 0f), new Vector2(x, 44f), new Vector2(78f, 108f));
                var slotLabel = UiFactory.CreateText(Theme, "SlotLabel", slotFrame.transform, SlotShort[i], 26f,
                    UiPalette.WithAlpha(UiPalette.TextDim, 0.5f), TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Stretch(slotLabel.rectTransform);

                var card = CardViewFactory.Create(panel.transform, Theme, 0.34f);
                card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0f, 0f);
                card.Rect.anchoredPosition = new Vector2(x + 1f, 45f);
                card.RotateWhenTapped = false; // tapped shows as greyed; rotating would leave the slot
                card.Clicked += v => { if (v.InstanceId >= 0) EquipmentClicked?.Invoke(v.InstanceId); };
                card.gameObject.SetActive(false);
                _equipment[i] = card;
            }

            // ---- relic strip ----
            _relicRow = UiFactory.CreateRect("RelicRow", panel.transform);
            UiFactory.Place(_relicRow, new Vector2(0f, 0f), new Vector2(14f, 8f), new Vector2(240f, 30f));
            var relicLayout = _relicRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            relicLayout.spacing = 4f;
            relicLayout.childAlignment = TextAnchor.MiddleLeft;
            relicLayout.childControlWidth = false;
            relicLayout.childControlHeight = false;
            relicLayout.childForceExpandWidth = false;
            relicLayout.childForceExpandHeight = false;
            _relicCount = UiFactory.CreateText(Theme, "RelicCount", panel.transform, "", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_relicCount.rectTransform, new Vector2(0f, 0f), new Vector2(258f, 8f), new Vector2(70f, 26f));

            // ---- zone counters ----
            _deckButton = BuildZoneButton(panel.transform, "Deck", new Vector2(-190f, -110f));
            _discardButton = BuildZoneButton(panel.transform, "Discard", new Vector2(-98f, -110f));
            _exileButton = BuildZoneButton(panel.transform, "Exile", new Vector2(-190f, -146f));
            _playedButton = BuildZoneButton(panel.transform, "Played", new Vector2(-98f, -146f));

            // ---- hero active / ultimate ----
            _activeButton = UiFactory.CreateButton(Theme, "ActiveButton", panel.transform, "Active", 14f);
            UiFactory.Place((RectTransform)_activeButton.transform, new Vector2(1f, 0f), new Vector2(-10f, 50f), new Vector2(184f, 42f));
            _activeButton.onClick.AddListener(() => HeroAbilityClicked?.Invoke(false));
            _activeNote = BuildNote(_activeButton, "L3");
            var activeLabel = UiFactory.ButtonLabel(_activeButton);
            activeLabel.enableAutoSizing = true;
            activeLabel.fontSizeMin = 9f;
            activeLabel.fontSizeMax = 14f;

            _ultimateButton = UiFactory.CreateButton(Theme, "UltimateButton", panel.transform, "Ultimate", 14f,
                UiPalette.WithAlpha(UiPalette.TierAdvanced, 0.35f));
            UiFactory.Place((RectTransform)_ultimateButton.transform, new Vector2(1f, 0f), new Vector2(-10f, 6f), new Vector2(184f, 42f));
            _ultimateButton.onClick.AddListener(() => HeroAbilityClicked?.Invoke(true));
            _ultimateNote = BuildNote(_ultimateButton, "L9");
            var ultLabel = UiFactory.ButtonLabel(_ultimateButton);
            ultLabel.enableAutoSizing = true;
            ultLabel.fontSizeMin = 9f;
            ultLabel.fontSizeMax = 14f;
        }

        private TextMeshProUGUI BuildCrystal(Transform parent, string name, Vector2 pos, Color color, string label)
        {
            var disc = UiFactory.CreateImage(name + "Disc", parent, Theme.Circle, color);
            UiFactory.Place(disc.rectTransform, new Vector2(1f, 1f), pos, new Vector2(58f, 58f));
            var outline = disc.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var value = UiFactory.CreateText(Theme, "Value", disc.transform, "0", 30f,
                UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(value.rectTransform);
            var caption = UiFactory.CreateText(Theme, "Caption", disc.transform, label, 12f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            caption.rectTransform.anchorMin = new Vector2(0f, 0f);
            caption.rectTransform.anchorMax = new Vector2(1f, 0f);
            caption.rectTransform.pivot = new Vector2(0.5f, 1f);
            caption.rectTransform.anchoredPosition = new Vector2(0f, -2f);
            caption.rectTransform.sizeDelta = new Vector2(0f, 14f);
            return value;
        }

        private Button BuildZoneButton(Transform parent, string zone, Vector2 pos)
        {
            var button = UiFactory.CreateButton(Theme, zone + "Button", parent, zone + " 0", 13f,
                UiPalette.WithAlpha(UiPalette.Background, 0.9f), UiPalette.TextDim);
            UiFactory.Place((RectTransform)button.transform, new Vector2(1f, 1f), pos, new Vector2(88f, 30f));
            button.onClick.AddListener(() => ZoneClicked?.Invoke(zone));
            return button;
        }

        private TextMeshProUGUI BuildNote(Button button, string text)
        {
            var note = UiFactory.CreateText(Theme, "Note", button.transform, text, 10f,
                UiPalette.TextDim, TextAlignmentOptions.TopRight);
            UiFactory.Stretch(note.rectTransform, 4, 2, 6, 1);
            return note;
        }

        // ------------------------------------------------------------------ rendering

        public void Render(PlayerSnap me, HashSet<int> activatableEquipment,
            bool heroActiveLegal, bool heroUltimateLegal)
        {
            if (!_built || me == null) return;

            HeroDefinition hero = null;
            if (!string.IsNullOrEmpty(me.HeroId))
                try { hero = HeroDatabase.Get(me.HeroId); } catch (KeyNotFoundException) { }

            _nameText.text = me.Name;
            _heroText.text = hero != null ? hero.Name : me.HeroId;
            _passiveText.text = PassiveSummary(hero, me.Level);
            _levelText.text = "Lv " + me.Level;

            var portrait = Theme.Art(me.HeroId);
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
                _portraitInitial.text = !string.IsNullOrEmpty(me.HeroId)
                    ? me.HeroId.Substring(0, 1).ToUpperInvariant() : "?";
            }

            int xpNeeded = me.Level >= 1 && me.Level <= _rules.XpToNextLevel.Length
                ? _rules.XpToNextLevel[me.Level - 1] : -1;
            if (xpNeeded > 0)
            {
                float frac = Mathf.Clamp01((float)me.Xp / xpNeeded);
                _xpFill.rectTransform.anchorMax = new Vector2(Mathf.Max(0.02f, frac), 1f);
                _xpText.text = $"{me.Xp}/{xpNeeded} XP";
            }
            else
            {
                _xpFill.rectTransform.anchorMax = new Vector2(1f, 1f);
                _xpText.text = "MAX";
            }

            _apText.text = me.Ap.ToString();
            _damageText.text = me.DamagePool.ToString();

            for (int i = 0; i < 3; i++)
            {
                var snap = me.Equipment != null && i < me.Equipment.Length ? me.Equipment[i] : null;
                var card = _equipment[i];
                if (snap == null)
                {
                    card.gameObject.SetActive(false);
                    continue;
                }
                card.gameObject.SetActive(true);
                card.Bind(snap);
                bool usable = activatableEquipment != null && activatableEquipment.Contains(snap.InstanceId);
                card.SetGlow(usable);
                card.SetGreyed(snap.Tapped && !usable);
            }

            RebuildRelics(me);

            UiFactory.ButtonLabel(_deckButton).text = "Deck " + me.DeckCount;
            UiFactory.ButtonLabel(_discardButton).text = "Discard " + me.Discard.Count;
            UiFactory.ButtonLabel(_exileButton).text = "Exile " + me.Exile.Count;
            UiFactory.ButtonLabel(_playedButton).text = "Played " + me.PlayedThisTurn.Count;

            RenderHeroButton(_activeButton, _activeNote, hero?.Active,
                hero != null ? hero.ActiveUnlockLevel : 3, me.Level, me.HeroActiveUsed, heroActiveLegal);
            RenderHeroButton(_ultimateButton, _ultimateNote, hero?.Ultimate,
                hero != null ? hero.UltimateUnlockLevel : 9, me.Level, me.HeroUltimateUsed, heroUltimateLegal);
        }

        /// <summary>All hero passives; not-yet-unlocked ones are prefixed with their level.</summary>
        private static string PassiveSummary(HeroDefinition hero, int level)
        {
            if (hero == null) return "";
            var parts = new List<string>();
            foreach (var (minLevel, ability) in hero.PassiveStatics)
                parts.Add(Describe(ability.Description, minLevel, level));
            foreach (var (minLevel, ability) in hero.PassiveTriggers)
                parts.Add(Describe(ability.Description, minLevel, level));
            return parts.Count == 0 ? "" : "Passive — " + string.Join("  ·  ", parts);
        }

        private static string Describe(string description, int minLevel, int level) =>
            level >= minLevel ? description : $"(unlocks L{minLevel}) {description}";

        private void RebuildRelics(PlayerSnap me)
        {
            for (int i = _relicRow.childCount - 1; i >= 0; i--)
                Destroy(_relicRow.GetChild(i).gameObject);

            int shown = 0;
            foreach (var relic in me.Relics)
            {
                if (shown >= 8) break;
                var icon = UiFactory.CreateImage("Relic", _relicRow, Theme.Rounded, UiPalette.PanelLight, raycast: true);
                icon.rectTransform.sizeDelta = new Vector2(26f, 26f);
                var art = Theme.Art(relic.DefId);
                if (art != null)
                {
                    icon.sprite = art;
                    icon.type = UnityEngine.UI.Image.Type.Simple;
                    icon.color = Color.white;
                }
                var button = icon.gameObject.AddComponent<Button>();
                button.targetGraphic = icon;
                button.onClick.AddListener(() => ZoneClicked?.Invoke("Relics"));
                shown++;
            }
            _relicCount.text = me.Relics.Count > 0 ? "×" + me.Relics.Count : "";
        }

        private void RenderHeroButton(Button button, TextMeshProUGUI note,
            ActivatedAbility ability, int unlockLevel, int level, bool used, bool legal)
        {
            var label = UiFactory.ButtonLabel(button);
            if (ability == null)
            {
                button.gameObject.SetActive(false);
                return;
            }
            button.gameObject.SetActive(true);
            label.text = ability.Description;

            bool locked = level < unlockLevel;
            note.text = locked ? $"unlocks L{unlockLevel}"
                : used ? "used this turn"
                : $"{ability.ApCost} AP";
            note.color = locked || used ? UiPalette.Danger : UiPalette.TextDim;

            button.interactable = legal;
            var group = UiFactory.AddGroup(button.gameObject);
            group.alpha = legal ? 1f : 0.55f;
        }
    }
}
