using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>Transient messages: rejected actions, info notes and turn banners.</summary>
    public sealed class ToastView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private bool _built;
        private Image _panel;
        private TextMeshProUGUI _text;
        private CanvasGroup _group;
        private Coroutine _routine;
        private readonly Queue<(string message, bool banner)> _pending = new Queue<(string, bool)>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            _panel = UiFactory.CreatePanel(Theme, "Toast", Container, UiPalette.WithAlpha(Color.black, 0.85f));
            UiFactory.Place(_panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 54f));
            _text = UiFactory.CreateText(Theme, "Text", _panel.transform, "", 20f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_text.rectTransform, 16, 4, 16, 4);
            _text.enableAutoSizing = true;
            _text.fontSizeMin = 12f;
            _text.fontSizeMax = 24f;

            _group = UiFactory.AddGroup(_panel.gameObject);
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
            _panel.raycastTarget = false;
        }

        public void Show(string message) => Enqueue(message, banner: false);

        /// <summary>Large accent message (turn changes and similar).</summary>
        public void ShowBanner(string message) => Enqueue(message, banner: true);

        private void Enqueue(string message, bool banner)
        {
            if (!_built || string.IsNullOrEmpty(message)) return;
            _pending.Enqueue((message, banner));
            if (_routine == null && isActiveAndEnabled)
                _routine = StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            while (_pending.Count > 0)
            {
                var (message, banner) = _pending.Dequeue();
                _text.text = message;
                _text.color = banner ? UiPalette.Gold : UiPalette.TextMain;

                yield return Presentation.Tween.Fade(_group, 1f, 0.15f);
                yield return new WaitForSeconds(banner ? 1.1f : 1.6f);
                yield return Presentation.Tween.Fade(_group, 0f, 0.3f);
            }
            _routine = null;
        }
    }
}
