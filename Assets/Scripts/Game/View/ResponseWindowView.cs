using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// "Respond?" banner shown when the local player receives priority while the stack
    /// is non-empty: timer bar (host enforces the actual timeout) and a PASS button.
    /// </summary>
    public sealed class ResponseWindowView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action PassClicked;

        private bool _built;
        private Image _timerFill;
        private TextMeshProUGUI _title;
        private Coroutine _timer;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var panel = UiFactory.CreatePanel(Theme, "ResponsePanel", Container, UiPalette.WithAlpha(Color.black, 0.85f));
            UiFactory.Stretch(panel.rectTransform);

            _title = UiFactory.CreateText(Theme, "Title", panel.transform, "Respond?", 26f,
                UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(_title.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, 8f), new Vector2(220f, 32f));

            var hint = UiFactory.CreateText(Theme, "Hint", panel.transform, "Instants in hand are highlighted", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(hint.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, -14f), new Vector2(260f, 18f));

            var timerBack = UiFactory.CreateImage("TimerBack", panel.transform, Theme.Rounded, UiPalette.Background);
            UiFactory.Place(timerBack.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(0f, 8f));
            timerBack.rectTransform.anchorMin = new Vector2(0f, 0f);
            timerBack.rectTransform.anchorMax = new Vector2(1f, 0f);
            timerBack.rectTransform.pivot = new Vector2(0.5f, 0f);
            timerBack.rectTransform.offsetMin = new Vector2(12f, 6f);
            timerBack.rectTransform.offsetMax = new Vector2(-12f, 14f);

            _timerFill = UiFactory.CreateImage("TimerFill", timerBack.transform, Theme.Rounded, UiPalette.Gold);
            _timerFill.rectTransform.anchorMin = Vector2.zero;
            _timerFill.rectTransform.anchorMax = Vector2.one;
            _timerFill.rectTransform.offsetMin = new Vector2(1f, 1f);
            _timerFill.rectTransform.offsetMax = new Vector2(-1f, -1f);

            var pass = UiFactory.CreateButton(Theme, "PassButton", panel.transform, "PASS", 22f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)pass.transform, new Vector2(1f, 0.5f), new Vector2(-14f, 4f), new Vector2(130f, 46f));
            pass.onClick.AddListener(() => PassClicked?.Invoke());

            Container.gameObject.SetActive(false);
        }

        public void Show(float seconds)
        {
            if (!_built) return;
            if (!Container.gameObject.activeSelf)
                Container.gameObject.SetActive(true);
            if (_timer != null) StopCoroutine(_timer);
            _timer = StartCoroutine(RunTimer(seconds));
        }

        public void Hide()
        {
            if (!_built) return;
            if (_timer != null)
            {
                StopCoroutine(_timer);
                _timer = null;
            }
            Container.gameObject.SetActive(false);
        }

        public bool IsShown => _built && Container.gameObject.activeSelf;

        private IEnumerator RunTimer(float seconds)
        {
            float remaining = seconds;
            while (remaining > 0f)
            {
                remaining -= Time.deltaTime;
                float frac = seconds > 0f ? Mathf.Clamp01(remaining / seconds) : 0f;
                _timerFill.rectTransform.anchorMax = new Vector2(frac, 1f);
                _timerFill.color = frac < 0.25f ? UiPalette.Danger : UiPalette.Gold;
                yield return null;
            }
        }
    }
}
