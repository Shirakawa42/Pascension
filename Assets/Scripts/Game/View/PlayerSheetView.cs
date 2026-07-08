using System;
using System.Collections;
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
    /// The local player's character sheet — a tall 400×550 left column: portrait,
    /// name/hero/level, tweened XP bar, hero passives, AP + damage crystals,
    /// equipment mini-cards, relic strip, and the hero active/ultimate buttons.
    /// Zone piles (deck/discard/exile/played) live in the corner PileWidgets now.
    /// </summary>
    public sealed class PlayerSheetView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action<int> EquipmentClicked;
        public event Action<bool> HeroAbilityClicked;
        /// <summary>Only "Relics" remains (piles moved to PileWidgets).</summary>
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
        private Image _apDisc;
        private Image _damageDisc;
        private readonly CardView[] _equipment = new CardView[3];
        private RectTransform _relicRow;
        private TextMeshProUGUI _relicCount;
        private Button _activeButton;
        private Button _ultimateButton;
        private TextMeshProUGUI _activeNote;
        private TextMeshProUGUI _ultimateNote;

        private float _lastXpFraction = -1f;
        private int _lastAp = -1, _lastDamage = -1;
        private Coroutine _xpTween;

        private static readonly string[] SlotShort = { "W", "A", "T" };

        public void Init(UiTheme theme, GameRules rules)
        {
            Theme = theme;
            _rules = rules;
            if (_built) return;
            _built = true;

            var panel = UiFactory.CreatePanel(Theme, "Sheet", Container);
            UiFactory.Stretch(panel.rectTransform);

            // ---- portrait (top-left) ----
            var portraitFrame = UiFactory.CreateImage("PortraitFrame", panel.transform, Theme.Rounded, UiPalette.Border);
            UiFactory.Place(portraitFrame.rectTransform, new Vector2(0f, 1f), new Vector2(10f, -10f), new Vector2(120f, 120f));
            _portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.PanelLight);
            UiFactory.Stretch(_portrait.rectTransform, 3, 3, 3, 3);
            _portraitInitial = UiFactory.CreateText(Theme, "Initial", portraitFrame.transform, "?", 52f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_portraitInitial.rectTransform);

            // ---- identity block (right of portrait) ----
            _nameText = UiFactory.CreateText(Theme, "Name", panel.transform, "You", 23f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_nameText.rectTransform, new Vector2(0f, 1f), new Vector2(142f, -12f), new Vector2(168f, 28f));

            _heroText = UiFactory.CreateText(Theme, "Hero", panel.transform, "", 14f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_heroText.rectTransform, new Vector2(0f, 1f), new Vector2(142f, -42f), new Vector2(168f, 20f));

            _levelText = UiFactory.CreateText(Theme, "Level", panel.transform, "Lv 1", 20f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_levelText.rectTransform, new Vector2(0f, 1f), new Vector2(142f, -68f), new Vector2(100f, 24f));

            var xpBack = UiFactory.CreateImage("XpBack", panel.transform, Theme.Rounded, UiPalette.Background);
            UiFactory.Place(xpBack.rectTransform, new Vector2(0f, 1f), new Vector2(142f, -98f), new Vector2(158f, 14f));
            _xpFill = UiFactory.CreateImage("XpFill", xpBack.transform, Theme.Rounded, UiPalette.Good);
            _xpFill.rectTransform.anchorMin = Vector2.zero;
            _xpFill.rectTransform.anchorMax = new Vector2(0.02f, 1f);
            _xpFill.rectTransform.offsetMin = new Vector2(1f, 1f);
            _xpFill.rectTransform.offsetMax = new Vector2(-1f, -1f);
            _xpText = UiFactory.CreateText(Theme, "XpText", panel.transform, "0/2 XP", 12f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_xpText.rectTransform, new Vector2(0f, 1f), new Vector2(142f, -116f), new Vector2(158f, 14f));

            // ---- AP / DMG crystals (right column) ----
            _apDisc = BuildCrystal(panel.transform, "Ap", new Vector2(-36f, -14f), UiPalette.Gold, "AP", out _apText);
            _damageDisc = BuildCrystal(panel.transform, "Damage", new Vector2(-36f, -90f), UiPalette.Danger, "DMG", out _damageText);

            // ---- hero passives ----
            _passiveText = UiFactory.CreateText(Theme, "Passives", panel.transform, "", 12f,
                UiPalette.TextDim, TextAlignmentOptions.TopLeft, FontStyles.Italic);
            UiFactory.Place(_passiveText.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -142f), new Vector2(372f, 100f));
            _passiveText.enableAutoSizing = true;
            _passiveText.fontSizeMin = 9f;
            _passiveText.fontSizeMax = 13f;

            // ---- equipment (middle band, bottom-anchored) ----
            for (int i = 0; i < 3; i++)
            {
                float x = 22f + i * 132f;
                var slotFrame = UiFactory.CreateImage($"EquipSlot{i}", panel.transform, Theme.Rounded,
                    UiPalette.WithAlpha(UiPalette.Background, 0.8f));
                UiFactory.Place(slotFrame.rectTransform, new Vector2(0f, 0f), new Vector2(x, 172f), new Vector2(94f, 132f));
                var slotLabel = UiFactory.CreateText(Theme, "SlotLabel", slotFrame.transform, SlotShort[i], 30f,
                    UiPalette.WithAlpha(UiPalette.TextDim, 0.45f), TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Stretch(slotLabel.rectTransform);

                var card = CardViewFactory.Create(panel.transform, Theme, 0.42f);
                card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0f, 0f);
                card.Rect.anchoredPosition = new Vector2(x + 1f, 173f);
                card.RotateWhenTapped = false;
                card.Clicked += v => { if (v.InstanceId >= 0) EquipmentClicked?.Invoke(v.InstanceId); };
                card.gameObject.SetActive(false);
                _equipment[i] = card;
            }

            // ---- relic strip ----
            _relicRow = UiFactory.CreateRect("RelicRow", panel.transform);
            UiFactory.Place(_relicRow, new Vector2(0f, 0f), new Vector2(20f, 132f), new Vector2(300f, 32f));
            var relicLayout = _relicRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            relicLayout.spacing = 4f;
            relicLayout.childAlignment = TextAnchor.MiddleLeft;
            relicLayout.childControlWidth = false;
            relicLayout.childControlHeight = false;
            relicLayout.childForceExpandWidth = false;
            relicLayout.childForceExpandHeight = false;
            _relicCount = UiFactory.CreateText(Theme, "RelicCount", panel.transform, "", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(_relicCount.rectTransform, new Vector2(0f, 0f), new Vector2(324f, 132f), new Vector2(64f, 32f));

            // ---- hero active / ultimate ----
            _activeButton = UiFactory.CreateButton(Theme, "ActiveButton", panel.transform, "Active", 15f);
            UiFactory.Place((RectTransform)_activeButton.transform, new Vector2(0f, 0f), new Vector2(12f, 66f), new Vector2(376f, 48f));
            _activeButton.onClick.AddListener(() => HeroAbilityClicked?.Invoke(false));
            _activeNote = BuildNote(_activeButton, "L3");
            var activeLabel = UiFactory.ButtonLabel(_activeButton);
            activeLabel.enableAutoSizing = true;
            activeLabel.fontSizeMin = 10f;
            activeLabel.fontSizeMax = 15f;

            _ultimateButton = UiFactory.CreateButton(Theme, "UltimateButton", panel.transform, "Ultimate", 15f,
                UiPalette.WithAlpha(UiPalette.TierAdvanced, 0.35f));
            UiFactory.Place((RectTransform)_ultimateButton.transform, new Vector2(0f, 0f), new Vector2(12f, 12f), new Vector2(376f, 48f));
            _ultimateButton.onClick.AddListener(() => HeroAbilityClicked?.Invoke(true));
            _ultimateNote = BuildNote(_ultimateButton, "L9");
            var ultLabel = UiFactory.ButtonLabel(_ultimateButton);
            ultLabel.enableAutoSizing = true;
            ultLabel.fontSizeMin = 10f;
            ultLabel.fontSizeMax = 15f;
        }

        private Image BuildCrystal(Transform parent, string name, Vector2 pos, Color color, string label,
            out TextMeshProUGUI value)
        {
            var disc = UiFactory.CreateImage(name + "Disc", parent, Theme.Circle, color);
            UiFactory.Place(disc.rectTransform, new Vector2(1f, 1f), pos, new Vector2(60f, 60f));
            var outline = disc.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var iconSprite = label == "AP" ? Theme.IconAp : Theme.IconDmg;
            if (iconSprite != null)
            {
                var icon = UiFactory.CreateImage("Icon", disc.transform, iconSprite, new Color(1f, 1f, 1f, 0.4f));
                UiFactory.Stretch(icon.rectTransform, 6, 6, 6, 6);
            }
            value = UiFactory.CreateText(Theme, "Value", disc.transform, "0", 30f,
                UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(value.rectTransform);
            var caption = UiFactory.CreateText(Theme, "Caption", disc.transform, label, 12f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            caption.rectTransform.anchorMin = new Vector2(0f, 0f);
            caption.rectTransform.anchorMax = new Vector2(1f, 0f);
            caption.rectTransform.pivot = new Vector2(0.5f, 1f);
            caption.rectTransform.anchoredPosition = new Vector2(0f, -2f);
            caption.rectTransform.sizeDelta = new Vector2(0f, 14f);
            return disc;
        }

        private TextMeshProUGUI BuildNote(Button button, string text)
        {
            var note = UiFactory.CreateText(Theme, "Note", button.transform, text, 10f,
                UiPalette.TextDim, TextAlignmentOptions.TopRight);
            UiFactory.Stretch(note.rectTransform, 4, 2, 6, 1);
            return note;
        }

        /// <summary>Anchor rect for flight animations landing on an equipment slot (0..2).</summary>
        public RectTransform EquipmentRect(int index) =>
            index >= 0 && index < 3 && _equipment[index] != null ? _equipment[index].Rect : Container;

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
            _passiveText.text = HeroTextUtil.PassiveSummary(hero, me.Level);
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
            float fraction = xpNeeded > 0 ? Mathf.Clamp01((float)me.Xp / xpNeeded) : 1f;
            _xpText.text = xpNeeded > 0 ? $"{me.Xp}/{xpNeeded} XP" : "MAX LEVEL";
            SetXpFill(fraction);

            // Crystal punch when the value changed (cheap feedback even outside animations).
            _apText.text = me.Ap.ToString();
            _damageText.text = me.DamagePool.ToString();
            if (_lastAp >= 0 && me.Ap > _lastAp && isActiveAndEnabled)
                StartCoroutine(Presentation.Tween.Punch(_apDisc.transform, 0.18f, 0.22f));
            if (_lastDamage >= 0 && me.DamagePool > _lastDamage && isActiveAndEnabled)
                StartCoroutine(Presentation.Tween.Punch(_damageDisc.transform, 0.18f, 0.22f));
            _lastAp = me.Ap;
            _lastDamage = me.DamagePool;

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

            RenderHeroButton(_activeButton, _activeNote, hero?.Active,
                hero != null ? hero.ActiveUnlockLevel : 3, me.Level, me.HeroActiveUsed, heroActiveLegal);
            RenderHeroButton(_ultimateButton, _ultimateNote, hero?.Ultimate,
                hero != null ? hero.UltimateUnlockLevel : 9, me.Level, me.HeroUltimateUsed, heroUltimateLegal);
        }

        private void SetXpFill(float fraction)
        {
            fraction = Mathf.Max(0.02f, fraction);
            if (_lastXpFraction < 0f || !isActiveAndEnabled)
            {
                _xpFill.rectTransform.anchorMax = new Vector2(fraction, 1f);
            }
            else if (!Mathf.Approximately(fraction, _lastXpFraction))
            {
                if (_xpTween != null) StopCoroutine(_xpTween);
                _xpTween = StartCoroutine(TweenXp(_lastXpFraction, fraction));
            }
            _lastXpFraction = fraction;
        }

        private IEnumerator TweenXp(float from, float to)
        {
            float t = 0f;
            const float duration = 0.35f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float x = Mathf.Lerp(from, to, Presentation.Tween.EaseOutCubic(Mathf.Clamp01(t / duration)));
                _xpFill.rectTransform.anchorMax = new Vector2(x, 1f);
                yield return null;
            }
            _xpFill.rectTransform.anchorMax = new Vector2(to, 1f);
        }

        private void RebuildRelics(PlayerSnap me)
        {
            for (int i = _relicRow.childCount - 1; i >= 0; i--)
                Destroy(_relicRow.GetChild(i).gameObject);

            int shown = 0;
            foreach (var relic in me.Relics)
            {
                if (shown >= 9) break;
                var icon = UiFactory.CreateImage("Relic", _relicRow, Theme.Rounded, UiPalette.PanelLight, raycast: true);
                icon.rectTransform.sizeDelta = new Vector2(28f, 28f);
                var art = Theme.Art(relic.DefId);
                if (art != null)
                {
                    icon.sprite = art;
                    icon.type = Image.Type.Simple;
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
