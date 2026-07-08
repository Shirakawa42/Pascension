using System;
using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// The shared market: 3 tier rows × 5 card slots, pile counts, level-gate locks.
    /// Buyable slots glow gold, killable monsters glow red, targetable slots glow blue.
    /// Clicks report (tierIndex, slotIndex) upward.
    /// </summary>
    public sealed class MarketView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action<int, int> SlotClicked;

        private const float CardScale = 0.5f;
        private const float SlotSpacing = 122f;
        private const float RowSpacing = 166f;

        private CardView[][] _slots;
        private Image[][] _slotFrames;
        private TextMeshProUGUI[] _pileCounts;
        private TextMeshProUGUI[] _lockLabels;
        private GameRules _rules;
        private bool _built;

        private static readonly string[] TierNames = { "BASIC", "ADVANCED", "ELITE" };

        public void Init(UiTheme theme, GameRules rules)
        {
            Theme = theme;
            _rules = rules;
            if (_built) return;
            _built = true;

            _slots = new CardView[3][];
            _slotFrames = new Image[3][];
            _pileCounts = new TextMeshProUGUI[3];
            _lockLabels = new TextMeshProUGUI[3];

            for (int t = 0; t < 3; t++)
            {
                // Row order top→bottom: Elite, Advanced, Basic.
                float y = (t - 1) * RowSpacing;

                var label = UiFactory.CreateText(Theme, $"TierLabel{t}", Container, TierNames[t], 18f,
                    UiPalette.WithAlpha(UiPalette.TierColor((CardTier)(t + 1)), 0.95f),
                    TextAlignmentOptions.MidlineRight, FontStyles.Bold);
                UiFactory.Place(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(-46f, y + 46f), new Vector2(180f, 26f));
                label.characterSpacing = 2f;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Overflow;

                var pile = UiFactory.CreateText(Theme, $"PileCount{t}", Container, "×0", 17f,
                    UiPalette.TextDim, TextAlignmentOptions.MidlineRight);
                UiFactory.Place(pile.rectTransform, new Vector2(0f, 0.5f), new Vector2(-46f, y + 20f), new Vector2(180f, 22f));
                _pileCounts[t] = pile;

                var lockLabel = UiFactory.CreateText(Theme, $"Lock{t}", Container, "", 15f,
                    UiPalette.Danger, TextAlignmentOptions.MidlineRight, FontStyles.Bold);
                UiFactory.Place(lockLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(-46f, y - 6f), new Vector2(180f, 22f));
                _lockLabels[t] = lockLabel;

                _slots[t] = new CardView[5];
                _slotFrames[t] = new Image[5];
                for (int s = 0; s < 5; s++)
                {
                    float x = 150f + s * SlotSpacing;

                    var frame = UiFactory.CreateImage($"Slot{t}_{s}", Container, Theme.Rounded,
                        UiPalette.WithAlpha(UiPalette.PanelLight, 0.35f));
                    UiFactory.Place(frame.rectTransform, new Vector2(0f, 0.5f), new Vector2(x, y),
                        new Vector2(CardView.Width * CardScale, CardView.Height * CardScale));
                    _slotFrames[t][s] = frame;

                    var card = CardViewFactory.Create(Container, Theme, CardScale);
                    card.Rect.anchorMin = card.Rect.anchorMax = card.Rect.pivot = new Vector2(0f, 0.5f);
                    card.Rect.anchoredPosition = new Vector2(x, y);
                    int tierIndex = t, slotIndex = s;
                    card.Clicked += _ => SlotClicked?.Invoke(tierIndex, slotIndex);
                    card.gameObject.SetActive(false);
                    _slots[t][s] = card;
                }
            }
        }

        /// <summary>Anchor rect of a market slot (for stack-target arrows). Null before Init.</summary>
        public RectTransform SlotRect(int tierIndex, int slotIndex)
        {
            if (!_built || tierIndex < 0 || tierIndex > 2 || slotIndex < 0 || slotIndex > 4) return null;
            return _slotFrames[tierIndex][slotIndex].rectTransform;
        }

        /// <summary>Anchor rect of a tier's face-down pile label (refill-flight source).</summary>
        public RectTransform PileLabelRect(int tierIndex)
        {
            if (!_built || tierIndex < 0 || tierIndex > 2) return null;
            return _pileCounts[tierIndex].rectTransform;
        }

        /// <summary>Hide a slot's card view while a flight animates it (cleared by Render).</summary>
        public void SetSlotHidden(int tierIndex, int slotIndex, bool hidden)
        {
            if (!_built || tierIndex < 0 || tierIndex > 2 || slotIndex < 0 || slotIndex > 4) return;
            var card = _slots[tierIndex][slotIndex];
            if (card != null && card.Group != null)
                card.Group.alpha = hidden ? 0f : 1f;
        }

        public void Render(ClientSnapshot snap, int viewerLevel,
            HashSet<(int, int)> buyable, HashSet<(int, int)> attackable, HashSet<(int, int)> targetable)
        {
            if (!_built || snap == null) return;

            for (int t = 0; t < 3; t++)
            {
                _pileCounts[t].text = "×" + snap.PileCounts[t];

                int requirement = t == 1 ? _rules.AdvancedLevelRequirement
                    : t == 2 ? _rules.EliteLevelRequirement : 0;
                bool locked = viewerLevel < requirement;
                _lockLabels[t].text = locked ? $"Lv {requirement} req." : "";

                var row = snap.MarketRows[t];
                for (int s = 0; s < 5; s++)
                {
                    var card = _slots[t][s];
                    var cardSnap = row != null && s < row.Length ? row[s] : null;
                    if (cardSnap == null)
                    {
                        card.gameObject.SetActive(false);
                        continue;
                    }
                    card.gameObject.SetActive(true);
                    card.Bind(cardSnap);

                    if (targetable != null && targetable.Contains((t, s)))
                        card.SetGlow(true, UiPalette.TargetBlue);
                    else if (attackable != null && attackable.Contains((t, s)))
                        card.SetGlow(true, UiPalette.Danger);
                    else if (buyable != null && buyable.Contains((t, s)))
                        card.SetGlow(true, UiPalette.Gold);
                    else
                        card.SetGlow(false);

                    // Locked rows read as unavailable, but monsters stay vivid — they can
                    // always be fought regardless of the buy-level gate.
                    bool isMonster = cardSnap.DefId != null &&
                                     Engine.Cards.CardDatabase.TryGet(cardSnap.DefId, out var def) &&
                                     def.IsMonster;
                    card.SetGreyed(locked && !isMonster);
                }
            }
        }

        /// <summary>Locate the CardView for a slot (animation hooks).</summary>
        public CardView Slot(int tierIndex, int slotIndex)
        {
            if (!_built) return null;
            if (tierIndex < 0 || tierIndex > 2 || slotIndex < 0 || slotIndex > 4) return null;
            return _slots[tierIndex][slotIndex];
        }

        public void FlashSlot(int tierIndex, int slotIndex, Color color)
        {
            var card = Slot(tierIndex, slotIndex);
            if (card != null && card.gameObject.activeSelf && isActiveAndEnabled)
                StartCoroutine(Presentation.Tween.Flash(card.Frame, color));
        }

        public void PunchSlot(int tierIndex, int slotIndex)
        {
            var card = Slot(tierIndex, slotIndex);
            if (card != null && card.gameObject.activeSelf && isActiveAndEnabled)
                StartCoroutine(Presentation.Tween.Punch(card.transform));
        }
    }
}
