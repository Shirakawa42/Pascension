using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>Collapsible scrolling game log (bottom-right).</summary>
    public sealed class LogPanel : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private const int MaxLines = 250;

        private bool _built;
        private bool _expanded;
        private RectTransform _body;
        private ScrollRect _scroll;
        private TextMeshProUGUI _text;
        private TextMeshProUGUI _headerLabel;
        private readonly List<string> _lines = new List<string>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var header = UiFactory.CreateButton(Theme, "Header", Container, "LOG  ^", 15f,
                UiPalette.WithAlpha(UiPalette.Panel, 0.95f), UiPalette.TextDim);
            var headerRt = (RectTransform)header.transform;
            headerRt.anchorMin = new Vector2(0f, 0f);
            headerRt.anchorMax = new Vector2(1f, 0f);
            headerRt.pivot = new Vector2(0.5f, 0f);
            headerRt.anchoredPosition = Vector2.zero;
            headerRt.sizeDelta = new Vector2(0f, 32f);
            header.onClick.AddListener(Toggle);
            _headerLabel = UiFactory.ButtonLabel(header);

            var scroll = UiFactory.CreateScrollView(Theme, "Body", Container, out var content);
            _body = (RectTransform)scroll.transform;
            _body.anchorMin = new Vector2(0f, 0f);
            _body.anchorMax = new Vector2(1f, 0f);
            _body.pivot = new Vector2(0.5f, 0f);
            _body.anchoredPosition = new Vector2(0f, 34f);
            _body.sizeDelta = new Vector2(0f, 240f);
            _scroll = scroll;

            _text = UiFactory.CreateText(Theme, "Text", content, "", 13f,
                UiPalette.TextMain, TextAlignmentOptions.TopLeft);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(8, 8, 6, 6);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _expanded = false;
            _body.gameObject.SetActive(false);
        }

        public void Append(string line)
        {
            if (!_built || string.IsNullOrEmpty(line)) return;
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
            _text.text = string.Join("\n", _lines);
            if (_expanded && isActiveAndEnabled)
                StartCoroutine(ScrollToBottom());
        }

        private void Toggle()
        {
            _expanded = !_expanded;
            _body.gameObject.SetActive(_expanded);
            _headerLabel.text = _expanded ? "LOG  v" : "LOG  ^";
            if (_expanded && isActiveAndEnabled)
                StartCoroutine(ScrollToBottom());
        }

        private IEnumerator ScrollToBottom()
        {
            yield return null; // wait one frame for layout
            if (_scroll != null)
                _scroll.verticalNormalizedPosition = 0f;
        }
    }
}
