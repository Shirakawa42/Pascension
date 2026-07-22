using System;
using System.Collections.Generic;
using Pascension.Game.UI;
using Pascension.Game.View;
using Shards.Stats;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Modal list for the stats screen's opponent filter: ALL OPPONENTS plus one row
    /// per known opponent (most games first, then latest), each with a games/last-played
    /// caption. Pure picker — the stats screen owns the selection state and re-renders
    /// on pick. Dimmer click dismisses without picking.
    /// </summary>
    public sealed class SoiStatsOpponentPicker : MonoBehaviour
    {
        private UiTheme _theme;
        private RectTransform _root;
        private RectTransform _content;
        private Action<string> _onPick;

        public static SoiStatsOpponentPicker Create(Transform parent, UiTheme theme)
        {
            var rect = UiFactory.CreateRect("SoiStatsOpponentPicker", parent);
            UiFactory.Stretch(rect);
            var picker = rect.gameObject.AddComponent<SoiStatsOpponentPicker>();
            picker.Build(theme, rect);
            return picker;
        }

        private void Build(UiTheme theme, RectTransform rect)
        {
            _theme = theme;
            _root = rect;

            var dimmer = UiFactory.CreateDimmer("Dimmer", rect);
            var dimButton = dimmer.gameObject.AddComponent<Button>();
            dimButton.targetGraphic = dimmer;
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(Hide);

            var panel = UiFactory.CreatePanel(theme, "Panel", rect);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 720f));

            var title = UiFactory.CreateText(theme, "Title", panel.transform, Loc.T("CHOOSE AN OPPONENT"), 24f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(520f, 32f));

            var scroll = UiFactory.CreateScrollView(theme, "List", panel.transform, out _content);
            UiFactory.Stretch((RectTransform)scroll.transform, 16, 20, 16, 62);

            rect.gameObject.SetActive(false);
        }

        public void Show(List<OpponentAgg> opponents, string currentKey, Action<string> onPick)
        {
            _onPick = onPick;
            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            float y = -10f;
            AddRow(Loc.T("ALL OPPONENTS"), null, currentKey == null, null, ref y);
            foreach (var opp in SoiStatsScreen.SortOpponents(opponents))
            {
                string caption = Loc.T("Games: ") + opp.Games + " · " +
                    Loc.T("Last: ") + SoiStatsScreen.DateOnly(opp.LastPlayedUtc);
                AddRow(SoiStatsScreen.OpponentDisplayName(opp),
                    opp.IdentityKey, opp.IdentityKey == currentKey, caption, ref y);
            }
            _content.sizeDelta = new Vector2(0f, -y + 10f);
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
        }

        private void AddRow(string label, string key, bool active, string caption, ref float y)
        {
            var button = UiFactory.CreateButton(_theme, "Pick", _content, label, 18f,
                active ? UiPalette.Gold : UiPalette.PanelLight,
                active ? UiPalette.Background : UiPalette.TextMain);
            var rt = (RectTransform)button.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(490f, caption != null ? 58f : 44f);

            var text = UiFactory.ButtonLabel(button);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            if (caption != null)
            {
                UiFactory.Stretch(text.rectTransform, 14, 20, 14, 4);
                var cap = UiFactory.CreateText(_theme, "Caption", button.transform, caption, 12f,
                    active ? UiPalette.WithAlpha(UiPalette.Background, 0.8f) : UiPalette.TextDim,
                    TextAlignmentOptions.MidlineLeft);
                UiFactory.Stretch(cap.rectTransform, 14, 2, 14, 36);
            }
            else
                UiFactory.Stretch(text.rectTransform, 14, 4, 14, 4);

            button.onClick.AddListener(() =>
            {
                Hide();
                _onPick?.Invoke(key);
            });
            y -= caption != null ? 64f : 50f;
        }
    }
}
