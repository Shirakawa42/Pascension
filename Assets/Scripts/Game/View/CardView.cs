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

            if (def == null)
            {
                // Hidden / unknown card: card back.
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

        public void SetGlow(bool on) => SetGlow(on, UiPalette.Gold);

        public void SetGlow(bool on, Color color)
        {
            if (Glow == null) return;
            Glow.gameObject.SetActive(on);
            if (on) Glow.color = UiPalette.WithAlpha(color, 0.85f);
        }

        public void SetGlowAlpha(float alpha)
        {
            if (Glow != null && Glow.gameObject.activeSelf)
            {
                var c = Glow.color;
                Glow.color = new Color(c.r, c.g, c.b, alpha);
            }
        }

        public void SetRaycastable(bool on)
        {
            if (Frame != null) Frame.raycastTarget = on;
        }

        // ------------------------------------------------------------------ pointer

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
