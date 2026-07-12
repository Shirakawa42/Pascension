using System;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Pascension.Game.View;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// A single Shards of Infinity card visual, built entirely in code: art (when the
    /// index has it), name, cost disc, type line, rules text, shield/defense badges and
    /// a damage-marks overlay. Click and hover surface as events; renders from a def id
    /// plus optional instance state.
    /// </summary>
    public sealed class SoiCardWidget : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public RectTransform Rect { get; private set; }
        public string DefId { get; private set; }
        public int InstanceId { get; private set; } = -1;

        public event Action<SoiCardWidget> Clicked;
        public static event Action<SoiCardWidget, bool> AnyHovered;

        private Image _frame;
        private Image _art;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _typeLine;
        private TextMeshProUGUI _rules;
        private TextMeshProUGUI _cost;
        private RectTransform _costDisc;
        private TextMeshProUGUI _shield;
        private RectTransform _shieldBadge;
        private TextMeshProUGUI _defense;
        private RectTransform _defenseBadge;
        private TextMeshProUGUI _status;
        private CanvasGroup _group;
        private UiTheme _theme;

        public static SoiCardWidget Create(Transform parent, UiTheme theme, Vector2 size)
        {
            var rect = UiFactory.CreateRect("SoiCard", parent);
            rect.sizeDelta = size;
            var widget = rect.gameObject.AddComponent<SoiCardWidget>();
            widget.Build(theme, size);
            return widget;
        }

        private void Build(UiTheme theme, Vector2 size)
        {
            _theme = theme;
            Rect = (RectTransform)transform;
            _group = gameObject.AddComponent<CanvasGroup>();

            _frame = UiFactory.CreateImage("Frame", transform, theme.Rounded, UiPalette.Panel);
            _frame.type = Image.Type.Sliced;
            UiFactory.Stretch((RectTransform)_frame.transform);
            _frame.raycastTarget = true;

            _art = UiFactory.CreateImage("Art", transform, null, new Color(1f, 1f, 1f, 0f));
            UiFactory.Stretch((RectTransform)_art.transform, 4f, 4f, -4f, -4f);
            _art.preserveAspect = false;
            _art.raycastTarget = false;

            // Readability scrim over art for the text blocks.
            var scrimTop = UiFactory.CreateImage("ScrimTop", transform, null, new Color(0f, 0f, 0f, 0.55f));
            UiFactory.Place((RectTransform)scrimTop.transform, new Vector2(0.5f, 1f), new Vector2(0f, -13f), new Vector2(size.x - 8f, 22f));
            scrimTop.raycastTarget = false;
            var scrimBottom = UiFactory.CreateImage("ScrimBottom", transform, null, new Color(0f, 0f, 0f, 0.62f));
            UiFactory.Place((RectTransform)scrimBottom.transform, new Vector2(0.5f, 0f), new Vector2(0f, size.y * 0.26f), new Vector2(size.x - 8f, size.y * 0.52f));
            scrimBottom.raycastTarget = false;

            _name = UiFactory.CreateText(theme, "Name", transform, "", 12f, UiPalette.TextMain,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -13f), new Vector2(size.x - 14f, 24f));
            _name.enableAutoSizing = true;
            _name.fontSizeMin = 8f;
            _name.fontSizeMax = 13f;
            _name.raycastTarget = false;

            _typeLine = UiFactory.CreateText(theme, "Type", transform, "", 9f, UiPalette.TextDim,
                TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(_typeLine.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, size.y * 0.135f), new Vector2(size.x - 12f, 14f));
            _typeLine.raycastTarget = false;

            _rules = UiFactory.CreateText(theme, "Rules", transform, "", 9.5f, UiPalette.TextMain,
                TextAlignmentOptions.Top);
            UiFactory.Place(_rules.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, size.y * 0.42f), new Vector2(size.x - 14f, size.y * 0.40f));
            _rules.enableAutoSizing = true;
            _rules.fontSizeMin = 7f;
            _rules.fontSizeMax = 11f;
            _rules.raycastTarget = false;

            _costDisc = UiFactory.CreateRect("CostDisc", transform);
            var costBg = UiFactory.CreateImage("Bg", _costDisc, theme.Circle, new Color(0.16f, 0.32f, 0.55f, 0.95f));
            UiFactory.Stretch((RectTransform)costBg.transform);
            costBg.raycastTarget = false;
            UiFactory.Place(_costDisc, new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(26f, 26f));
            _cost = UiFactory.CreateText(theme, "Cost", _costDisc, "", 14f, Color.white,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_cost.rectTransform);
            _cost.raycastTarget = false;

            _shieldBadge = UiFactory.CreateRect("ShieldBadge", transform);
            var shieldBg = UiFactory.CreateImage("Bg", _shieldBadge, theme.Circle, new Color(0.42f, 0.42f, 0.46f, 0.95f));
            UiFactory.Stretch((RectTransform)shieldBg.transform);
            shieldBg.raycastTarget = false;
            UiFactory.Place(_shieldBadge, new Vector2(0f, 0f), new Vector2(16f, 14f), new Vector2(24f, 24f));
            _shield = UiFactory.CreateText(theme, "Shield", _shieldBadge, "", 12f, Color.white,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_shield.rectTransform);
            _shield.raycastTarget = false;

            _defenseBadge = UiFactory.CreateRect("DefenseBadge", transform);
            var defBg = UiFactory.CreateImage("Bg", _defenseBadge, theme.Circle, new Color(0.24f, 0.5f, 0.25f, 0.95f));
            UiFactory.Stretch((RectTransform)defBg.transform);
            defBg.raycastTarget = false;
            UiFactory.Place(_defenseBadge, new Vector2(1f, 0f), new Vector2(-16f, 14f), new Vector2(24f, 24f));
            _defense = UiFactory.CreateText(theme, "Defense", _defenseBadge, "", 12f, Color.white,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_defense.rectTransform);
            _defense.raycastTarget = false;

            _status = UiFactory.CreateText(theme, "Status", transform, "", 12f, new Color(1f, 0.55f, 0.4f),
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_status.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Vector2(size.x, 20f));
            _status.raycastTarget = false;
        }

        /// <summary>Bind to a def (+ optional live instance state).</summary>
        public void Show(string defId, int instanceId = -1, bool exhausted = false, int damage = 0,
            bool faceDown = false)
        {
            DefId = defId;
            InstanceId = instanceId;

            if (faceDown || defId == null || !ShardsCardDatabase.TryGet(defId, out var def))
            {
                _name.text = faceDown ? "" : "?";
                _typeLine.text = "";
                _rules.text = "";
                _art.color = new Color(1f, 1f, 1f, 0f);
                _frame.color = new Color(0.16f, 0.15f, 0.2f, 1f);
                _costDisc.gameObject.SetActive(false);
                _shieldBadge.gameObject.SetActive(false);
                _defenseBadge.gameObject.SetActive(false);
                _status.text = "";
                _group.alpha = 1f;
                return;
            }

            _name.text = def.Name;
            _typeLine.text = TypeLine(def);
            _rules.text = def.RulesText;
            _frame.color = FactionColor(def.Faction);

            var art = _theme != null ? _theme.Art(def.Id) : null;
            _art.sprite = art;
            _art.color = art != null ? Color.white : new Color(1f, 1f, 1f, 0f);

            bool showCost = def.Type != ShardsCardType.Destiny && def.Type != ShardsCardType.Monster &&
                            def.Type != ShardsCardType.Relic && def.Type != ShardsCardType.Starter;
            _costDisc.gameObject.SetActive(showCost);
            _cost.text = def.Cost.ToString();

            _shieldBadge.gameObject.SetActive(def.Shield > 0 || def.DynamicShield != null);
            _shield.text = def.DynamicShield != null ? "M" : def.Shield.ToString();

            bool champion = def.IsChampion || def.IsMonster;
            _defenseBadge.gameObject.SetActive(champion && def.Defense > 0);
            _defense.text = damage > 0 ? (def.Defense - damage).ToString() : def.Defense.ToString();
            ((Image)_defenseBadge.GetChild(0).GetComponent<Image>()).color =
                damage > 0 ? new Color(0.62f, 0.28f, 0.2f, 0.95f) : new Color(0.24f, 0.5f, 0.25f, 0.95f);

            _status.text = exhausted ? "EXHAUSTED" : "";
            _group.alpha = exhausted ? 0.55f : 1f;
        }

        public void SetInteractable(bool value)
        {
            _group.interactable = value;
            _frame.color = new Color(_frame.color.r, _frame.color.g, _frame.color.b, value ? 1f : 0.75f);
        }

        private static string TypeLine(ShardsCardDef def)
        {
            string faction = def.Faction == ShardsFaction.None || def.Faction == ShardsFaction.Monster
                ? "" : def.Faction + " ";
            return def.Type switch
            {
                ShardsCardType.Monster => "Ingeminex",
                ShardsCardType.Starter => "Item — Ally",
                ShardsCardType.Mercenary => faction + "Mercenary Ally",
                ShardsCardType.Relic => faction + "Relic" + (def.IsChampion ? " Champion" : " Ally"),
                _ => faction + def.Type
            };
        }

        private static Color FactionColor(ShardsFaction faction) => faction switch
        {
            ShardsFaction.Homodeus => new Color(0.45f, 0.4f, 0.24f, 1f),
            ShardsFaction.Order => new Color(0.2f, 0.32f, 0.5f, 1f),
            ShardsFaction.Undergrowth => new Color(0.2f, 0.42f, 0.24f, 1f),
            ShardsFaction.Wraethe => new Color(0.36f, 0.24f, 0.46f, 1f),
            ShardsFaction.Aion => new Color(0.55f, 0.28f, 0.2f, 1f),
            ShardsFaction.Monster => new Color(0.5f, 0.16f, 0.16f, 1f),
            _ => new Color(0.26f, 0.26f, 0.3f, 1f)
        };

        public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(this);
        public void OnPointerEnter(PointerEventData eventData) => AnyHovered?.Invoke(this, true);
        public void OnPointerExit(PointerEventData eventData) => AnyHovered?.Invoke(this, false);
    }
}
