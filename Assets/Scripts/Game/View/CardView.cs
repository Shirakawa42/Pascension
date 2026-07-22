using System;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// One card, MTG full-art style: full-bleed art, top name bar with AP-cost disc
    /// (red HP badge for monsters), translucent rules box, tier-colored frame.
    /// Configured from a CardSnap or a def id; hierarchy built by CardViewFactory.
    /// </summary>
    public sealed class CardView : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public const float Width = 220f;
        public const float Height = 308f;

        [Header("Wired by CardViewFactory")]
        public UiTheme Theme;
        public Image Glow;
        public Image OuterGlow;
        public Image HoverGlow;
        public Image Frame;
        public Image Art;
        public Image TopBar;
        public TextMeshProUGUI NameText;
        public GameObject CostGroup;
        public TextMeshProUGUI CostText;
        public GameObject HpGroup;
        public TextMeshProUGUI HpText;
        public Image RulesBox;
        public TextMeshProUGUI TypeText;
        public TextMeshProUGUI RulesText;
        public GameObject DamageGroup;
        public TextMeshProUGUI DamageText;
        public GameObject ShieldGroup;
        public TextMeshProUGUI ShieldText;
        public GameObject MercenaryMarker;
        public CanvasGroup Group;

        public int InstanceId { get; private set; } = -1;
        public string DefId { get; private set; }
        public bool Tapped { get; private set; }

        /// <summary>False for mini-cards in tight slots (equipment): tapped shows as
        /// greyed instead of rotating out of the slot's bounds.</summary>
        public bool RotateWhenTapped = true;

        public event Action<CardView> Clicked;
        public event Action<CardView, bool> Hovered;

        /// <summary>Global hover feed — drives the large card preview.</summary>
        public static event Action<CardView, bool> AnyHovered;

        /// <summary>Face data for def ids OUTSIDE Pascension's CardDatabase (the Shards
        /// of Infinity table plugs its database in through this). Only consulted when a
        /// CardDatabase lookup misses, so Pascension rendering is untouched.</summary>
        public struct ExternalFace
        {
            public string Name;
            public string TypeLine;
            public string RulesText;
            public string ArtId;
            public Color FrameColor;
            /// <summary>Cost disc (blue, top-left).</summary>
            public bool ShowCost;
            public string CostText;
            /// <summary>Red badge (Pascension: monster HP; SoI: champion defense).</summary>
            public bool ShowBadge;
            public string BadgeText;
            /// <summary>Shield badge on the card face (count inside the shield icon).</summary>
            public bool ShowShield;
            public string ShieldValueText;
            /// <summary>SoI mercenary: shows the red "M" triangle on the right edge.</summary>
            public bool IsMercenary;
        }

        /// <summary>Set by non-Pascension tables (SoiGameScreen). Return null for unknown ids.</summary>
        public static Func<string, ExternalFace?> ExternalFaceResolver;

        // Rules-box defaults captured on first bind so external faces (which use a fixed
        // font size and grow the box instead) can be fully undone when a pooled view is
        // later re-bound to a Pascension card.
        private bool _rulesDefaultsCaptured;
        private float _rulesBoxDefaultTop;
        private Vector2 _costDefaultPos;
        private bool _rulesAutoSizeDefault;
        private float _rulesFontMinDefault, _rulesFontMaxDefault, _rulesFontSizeDefault;

        private void CaptureRulesDefaults()
        {
            if (_rulesDefaultsCaptured || RulesBox == null || RulesText == null) return;
            _rulesDefaultsCaptured = true;
            _rulesBoxDefaultTop = RulesBox.rectTransform.offsetMax.y;
            _costDefaultPos = ((RectTransform)CostGroup.transform).anchoredPosition;
            _rulesAutoSizeDefault = RulesText.enableAutoSizing;
            _rulesFontMinDefault = RulesText.fontSizeMin;
            _rulesFontMaxDefault = RulesText.fontSizeMax;
            _rulesFontSizeDefault = RulesText.fontSize;
        }

        private void RestoreRulesDefaults()
        {
            if (!_rulesDefaultsCaptured) return;
            ((RectTransform)CostGroup.transform).anchoredPosition = _costDefaultPos;
            if (ShieldGroup != null) ShieldGroup.SetActive(false);
            RulesText.enableAutoSizing = _rulesAutoSizeDefault;
            RulesText.fontSizeMin = _rulesFontMinDefault;
            RulesText.fontSizeMax = _rulesFontMaxDefault;
            RulesText.fontSize = _rulesFontSizeDefault;
            var offsetMax = RulesBox.rectTransform.offsetMax;
            RulesBox.rectTransform.offsetMax = new Vector2(offsetMax.x, _rulesBoxDefaultTop);
        }

        public RectTransform Rect => (RectTransform)transform;

        // ------------------------------------------------------------------ binding

        public void Bind(CardSnap snap)
        {
            if (snap == null)
            {
                InstanceId = -1;
                ApplyDef(null);
                SetTapped(false);
                SetMarkedDamage(0);
                return;
            }
            InstanceId = snap.InstanceId;
            ApplyDef(snap.DefId);
            SetTapped(snap.Tapped);
            SetMarkedDamage(snap.MarkedDamage);
            ApplyLiveNumbers(snap);
        }

        /// <summary>Show CURRENT values from the snapshot: monsters display effective HP
        /// minus damage (green above base / red below / white equal); market cards display
        /// the viewer's effective buy cost (green cheaper / red pricier).</summary>
        private void ApplyLiveNumbers(CardSnap snap)
        {
            if (string.IsNullOrEmpty(snap.DefId) || !CardDatabase.TryGet(snap.DefId, out var def))
                return;

            if (def.IsMonster && snap.EffectiveHp > 0)
            {
                int current = snap.EffectiveHp - snap.MarkedDamage;
                HpText.text = current.ToString();
                HpText.color = current > def.MonsterHp ? UiPalette.HealthyGreen
                    : current < def.MonsterHp ? UiPalette.WoundedRed
                    : Color.white;
            }
            else if (!def.IsMonster && snap.EffectiveCost >= 0)
            {
                CostText.text = snap.EffectiveCost.ToString();
                CostText.color = snap.EffectiveCost < def.Cost ? UiPalette.DiscountGreen
                    : snap.EffectiveCost > def.Cost ? UiPalette.WoundedRed
                    : UiPalette.Background;
            }
        }

        public void BindDef(string defId, int instanceId = -1)
        {
            InstanceId = instanceId;
            ApplyDef(defId);
            SetTapped(false);
            SetMarkedDamage(0);
        }

        private void ApplyDef(string defId)
        {
            DefId = defId;
            CardDefinition def = null;
            if (!string.IsNullOrEmpty(defId))
                CardDatabase.TryGet(defId, out def);

            CaptureRulesDefaults();
            SetMercenaryMarker(false); // ApplyExternalFace re-enables it for SoI mercenaries
            if (def == null)
            {
                // Not a Pascension card — another game's database may know this id.
                var external = !string.IsNullOrEmpty(defId) ? ExternalFaceResolver?.Invoke(defId) : null;
                if (external.HasValue)
                {
                    ApplyExternalFace(external.Value);
                    return;
                }

                // Hidden / unknown card: card back.
                RestoreRulesDefaults();
                NameText.text = "";
                CostGroup.SetActive(false);
                HpGroup.SetActive(false);
                TypeText.text = "";
                RulesText.text = "";
                Frame.color = UiPalette.TierDefault;
                Art.sprite = null;
                Art.color = UiPalette.PanelLight;
                return;
            }

            RestoreRulesDefaults();
            var tierColor = UiPalette.TierColor(def.Tier);
            Frame.color = tierColor;
            NameText.text = def.Name;
            TypeText.text = def.TypeLine;
            RulesText.text = def.RulesText;

            bool monster = def.IsMonster;
            CostGroup.SetActive(!monster);
            HpGroup.SetActive(monster);
            if (monster)
                HpText.text = def.MonsterHp.ToString();
            else
                CostText.text = def.Cost.ToString();

            var sprite = Theme != null ? Theme.Art(defId) : null;
            if (sprite != null)
            {
                Art.sprite = sprite;
                Art.color = Color.white;
            }
            else
            {
                Art.sprite = TierGradients.Sprite(def.Tier);
                Art.color = Color.white;
            }
        }

        private void ApplyExternalFace(ExternalFace face)
        {
            Frame.color = face.FrameColor;
            NameText.text = face.Name;
            TypeText.text = face.TypeLine;

            // Fixed, readable font size — the rules box GROWS for long texts instead of
            // shrinking the text (Shards cards can be wordy).
            RulesText.enableAutoSizing = false;
            RulesText.fontSize = 13.5f;
            if (Theme != null && Theme.Icons != null)
                RulesText.spriteAsset = Theme.Icons;
            RulesText.text = face.RulesText;
            float textWidth = 210f - 16f; // frame-relative rules width minus padding
            float preferred = RulesText.GetPreferredValues(face.RulesText ?? "", textWidth, 0f).y;
            float boxTop = Mathf.Max(_rulesDefaultsCaptured ? _rulesBoxDefaultTop : 102f, preferred + 34f);
            var offsetMax = RulesBox.rectTransform.offsetMax;
            RulesBox.rectTransform.offsetMax = new Vector2(offsetMax.x, boxTop);
            CostGroup.SetActive(face.ShowCost);
            if (face.ShowCost) CostText.text = face.CostText;
            HpGroup.SetActive(face.ShowBadge);
            if (face.ShowBadge) HpText.text = face.BadgeText;
            // Champions show BOTH: the defense badge keeps the right slot, the cost
            // disc slides left beside it (both live in the top bar).
            ((RectTransform)CostGroup.transform).anchoredPosition =
                face.ShowBadge && face.ShowCost ? _costDefaultPos + new Vector2(-36f, 0f) : _costDefaultPos;
            if (ShieldGroup != null)
            {
                ShieldGroup.SetActive(face.ShowShield);
                if (face.ShowShield && ShieldText != null) ShieldText.text = face.ShieldValueText;
            }
            SetMercenaryMarker(face.IsMercenary);

            var sprite = Theme != null ? Theme.Art(face.ArtId ?? DefId) : null;
            Art.sprite = sprite;
            if (sprite != null)
            {
                Art.color = Color.white;
            }
            else
            {
                // No art yet: a dimmed take on the frame color reads as intentional.
                Art.color = new Color(face.FrameColor.r * 0.45f, face.FrameColor.g * 0.45f,
                    face.FrameColor.b * 0.45f, 1f);
            }
        }

        // ------------------------------------------------------------------ state

        public void SetTapped(bool tapped)
        {
            Tapped = tapped;
            transform.localRotation = Quaternion.Euler(0f, 0f, tapped && RotateWhenTapped ? -90f : 0f);
        }

        public void SetMarkedDamage(int amount)
        {
            bool show = amount > 0;
            DamageGroup.SetActive(show);
            if (show) DamageText.text = amount.ToString();
        }

        public void SetGreyed(bool greyed)
        {
            if (Group != null)
                Group.alpha = greyed ? 0.45f : 1f;
        }

        /// <summary>Override the HP badge value/color after a bind — the SoI split
        /// window shows each champion's modifier-adjusted HP on its own red disc
        /// (green above printed, red below) instead of a separate label.</summary>
        public void SetBadge(string text, Color color)
        {
            if (HpGroup != null) HpGroup.SetActive(true);
            if (HpText == null) return;
            HpText.text = text;
            HpText.color = color;
        }

        /// <summary>SoI mercenary flag — the red "M" triangle on the card's right edge.
        /// Set intrinsically from the bound face (see ApplyExternalFace), so it follows a
        /// mercenary into every zone (river, hand, piles, showcase, reveals).</summary>
        public void SetMercenaryMarker(bool on)
        {
            if (MercenaryMarker != null) MercenaryMarker.SetActive(on);
        }

        public void SetGlow(bool on) => SetGlow(on, UiPalette.Gold);

        public void SetGlow(bool on, Color color)
        {
            if (Glow == null) return;
            Glow.gameObject.SetActive(on);
            if (on) Glow.color = UiPalette.WithAlpha(color, 0.85f);
            ApplyGlowLayout();
        }

        public void SetGlowAlpha(float alpha)
        {
            if (Glow != null && Glow.gameObject.activeSelf)
            {
                var c = Glow.color;
                Glow.color = new Color(c.r, c.g, c.b, alpha);
            }
        }

        /// <summary>Second halo — independent of SetGlow so both can show at once
        /// (river slots: faction "condition met" inner + gold "affordable" outer).</summary>
        public void SetOuterGlow(bool on) => SetOuterGlow(on, UiPalette.Gold);

        public void SetOuterGlow(bool on, Color color)
        {
            if (OuterGlow == null) return;
            OuterGlow.gameObject.SetActive(on);
            if (on) OuterGlow.color = UiPalette.WithAlpha(color, 0.5f);
            ApplyGlowLayout();
        }

        /// <summary>Hearthstone hover halo (the color of whoever is pointing at the
        /// card) — an independent third ring stacking OUTSIDE the other two.</summary>
        public void SetHoverGlow(bool on) => SetHoverGlow(on, UiPalette.Gold);

        public void SetHoverGlow(bool on, Color color)
        {
            if (HoverGlow == null) return;
            HoverGlow.gameObject.SetActive(on);
            if (on) HoverGlow.color = UiPalette.WithAlpha(color, 0.6f);
            ApplyGlowLayout();
        }

        public void SetHoverGlowAlpha(float alpha)
        {
            if (HoverGlow != null && HoverGlow.gameObject.activeSelf)
            {
                var c = HoverGlow.color;
                HoverGlow.color = new Color(c.r, c.g, c.b, alpha);
            }
        }

        /// <summary>Halo stacking: each active ring sits one 5px step outside the ones
        /// beneath it; unused slots collapse so the visible rings always hug the card.
        /// Order inward→out: Glow (condition/selection), OuterGlow (affordable),
        /// HoverGlow (pointing player).</summary>
        private void ApplyGlowLayout()
        {
            bool innerOn = Glow != null && Glow.gameObject.activeSelf;
            bool outerOn = OuterGlow != null && OuterGlow.gameObject.activeSelf;
            if (OuterGlow != null)
            {
                float extent = innerOn ? 9f : 4f;
                OuterGlow.rectTransform.offsetMin = new Vector2(-extent, -extent);
                OuterGlow.rectTransform.offsetMax = new Vector2(extent, extent);
            }
            if (HoverGlow != null)
            {
                float extent = 4f + (innerOn ? 5f : 0f) + (outerOn ? 5f : 0f);
                HoverGlow.rectTransform.offsetMin = new Vector2(-extent, -extent);
                HoverGlow.rectTransform.offsetMax = new Vector2(extent, extent);
            }
        }

        public void SetOuterGlowAlpha(float alpha)
        {
            if (OuterGlow != null && OuterGlow.gameObject.activeSelf)
            {
                var c = OuterGlow.color;
                OuterGlow.color = new Color(c.r, c.g, c.b, alpha);
            }
        }

        public void SetRaycastable(bool on)
        {
            if (Frame != null) Frame.raycastTarget = on;
        }

        // ------------------------------------------------------------------ pointer

        /// <summary>Configure this view as a bare hover-preview proxy: sets DefId only,
        /// without binding any visuals (safe on a factory-less CardView).</summary>
        public void SetPreviewDef(string defId) => DefId = defId;

        /// <summary>Raise the global hover feed for this view (used by proxy hover sources).</summary>
        public void RaiseHover(bool entered) => AnyHovered?.Invoke(this, entered);

        public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(this);

        public void OnPointerEnter(PointerEventData eventData)
        {
            Hovered?.Invoke(this, true);
            AnyHovered?.Invoke(this, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Hovered?.Invoke(this, false);
            AnyHovered?.Invoke(this, false);
        }

        private void OnDisable() => AnyHovered?.Invoke(this, false);
    }
}
