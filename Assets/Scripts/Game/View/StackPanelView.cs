using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Serialization;
using Pascension.Game.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// The spell stack (right side). Slides in while non-empty; items are listed
    /// top-of-stack first with the controller's color, description and mini art.
    /// Items can be clicked as targets while a ChooseTargets decision is up.
    /// </summary>
    public sealed class StackPanelView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Raised with the clicked stack item id.</summary>
        public event Action<int> ItemClicked;

        private const float HiddenX = 340f;
        private const float ShownX = -268f;

        private bool _built;
        private RectTransform _content;
        private TextMeshProUGUI _title;
        private bool _shown;
        private Coroutine _slide;
        private HashSet<int> _targetable = new HashSet<int>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var panel = UiFactory.CreatePanel(Theme, "StackPanel", Container, UiPalette.WithAlpha(UiPalette.Panel, 0.97f));
            UiFactory.Stretch(panel.rectTransform);

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "THE STACK", 18f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.characterSpacing = 4f;
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.anchoredPosition = new Vector2(0f, -8f);
            _title.rectTransform.sizeDelta = new Vector2(0f, 24f);

            _content = UiFactory.CreateRect("Items", panel.transform);
            UiFactory.Stretch(_content, 8, 8, 8, 40);
            var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Container.anchoredPosition = new Vector2(HiddenX, Container.anchoredPosition.y);
            _shown = false;
        }

        public void Render(ClientSnapshot snap, HashSet<int> targetableItemIds)
        {
            if (!_built || snap == null) return;
            _targetable = targetableItemIds ?? new HashSet<int>();

            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            // Top of stack renders first.
            for (int i = snap.Stack.Count - 1; i >= 0; i--)
                BuildItem(snap.Stack[i], i == snap.Stack.Count - 1);

            SetShown(snap.Stack.Count > 0);
        }

        private void BuildItem(StackItemSnap item, bool isTop)
        {
            var row = UiFactory.CreateImage("Item", _content, Theme.Rounded,
                isTop ? UiPalette.PanelLight : UiPalette.WithAlpha(UiPalette.PanelLight, 0.7f), raycast: true);
            var rowRt = row.rectTransform;
            rowRt.sizeDelta = new Vector2(0f, 64f);
            var element = row.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = 64f;

            bool targetable = _targetable.Contains(item.Id);
            var outline = row.gameObject.AddComponent<Outline>();
            outline.effectColor = targetable ? UiPalette.TargetBlue : UiPalette.WithAlpha(UiPalette.Border, 0.8f);
            outline.effectDistance = targetable ? new Vector2(2f, -2f) : new Vector2(1f, -1f);

            var bar = UiFactory.CreateImage("ControllerBar", row.transform, null,
                UiPalette.PlayerColor(item.ControllerIndex));
            bar.rectTransform.anchorMin = new Vector2(0f, 0f);
            bar.rectTransform.anchorMax = new Vector2(0f, 1f);
            bar.rectTransform.pivot = new Vector2(0f, 0.5f);
            bar.rectTransform.anchoredPosition = Vector2.zero;
            bar.rectTransform.sizeDelta = new Vector2(5f, 0f);

            var art = UiFactory.CreateImage("Art", row.transform, null, UiPalette.Background);
            UiFactory.Place(art.rectTransform, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(44f, 52f));
            var sprite = Theme.Art(item.DefId);
            if (sprite != null)
            {
                art.sprite = sprite;
                art.color = Color.white;
            }

            var text = UiFactory.CreateText(Theme, "Description", row.transform,
                string.IsNullOrEmpty(item.Description) ? EventText.CardName(item.DefId) : item.Description,
                13f, UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Stretch(text.rectTransform, 60, 4, 8, 4);
            text.enableAutoSizing = true;
            text.fontSizeMin = 9f;
            text.fontSizeMax = 14f;

            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row;
            int id = item.Id;
            button.onClick.AddListener(() => ItemClicked?.Invoke(id));
        }

        private void SetShown(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            if (!isActiveAndEnabled)
            {
                Container.anchoredPosition = new Vector2(shown ? ShownX : HiddenX, Container.anchoredPosition.y);
                return;
            }
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(Tween.Move(Container,
                new Vector2(shown ? ShownX : HiddenX, Container.anchoredPosition.y), 0.25f));
        }
    }
}
