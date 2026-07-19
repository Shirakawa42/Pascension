using System;
using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// A Slay-the-Spire-style corner pile: a small stack of offset card backs (or the
    /// face-up top card), a gold count badge, and a title. Clickable to browse contents.
    /// Face-down piles (the draw pile) never reveal a top card.
    /// </summary>
    public sealed class PileWidget : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        public event Action Clicked;

        private bool _built;
        private bool _faceDown;
        private Image[] _stack;
        private CardView _topCard;
        private Image _emptyOutline;
        private Image _badge;
        private TextMeshProUGUI _badgeText;
        private TextMeshProUGUI _title;
        private int _lastCount = -1;
        // Punch/Flash capture their start state as "base" — overlapping calls on this
        // persistent widget would ratchet the badge bigger forever. Restore before restart.
        private Coroutine _pulseCo;
        private Coroutine _flashCo;
        private Vector3 _badgeBaseScale = Vector3.one;
        private Color _stackTopBase;

        /// <summary>Flight-animation anchor (the pile's visual center).</summary>
        public RectTransform AnchorRect => Container;

        public void Init(UiTheme theme, string title, bool faceDown)
        {
            Theme = theme;
            _faceDown = faceDown;
            if (_built) return;
            _built = true;

            // Empty-slot outline (visible when the pile has no cards).
            _emptyOutline = UiFactory.CreateImage("Empty", Container, Theme.Rounded,
                UiPalette.WithAlpha(UiPalette.Border, 0.35f));
            UiFactory.Place(_emptyOutline.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Vector2(96f, 134f));

            // Offset stack of card backs (depth illusion).
            _stack = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                var back = UiFactory.CreateImage($"Stack{i}", Container, Theme.Rounded, BackColor(i));
                UiFactory.Place(back.rectTransform, new Vector2(0.5f, 0.5f),
                    new Vector2(-6f + i * 6f, 2f + i * 6f), new Vector2(96f, 134f));
                var outline = back.gameObject.AddComponent<Outline>();
                outline.effectColor = UiPalette.WithAlpha(Color.black, 0.5f);
                outline.effectDistance = new Vector2(1f, -1f);
                if (i == 2 && _faceDown)
                {
                    // Card-back emblem on the top of a face-down pile.
                    var emblem = UiFactory.CreateImage("Emblem", back.transform, Theme.Circle,
                        UiPalette.WithAlpha(UiPalette.Gold, 0.55f));
                    UiFactory.Place(emblem.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44f, 44f));
                }
                _stack[i] = back;
            }

            // Face-up top card (face-up piles only).
            if (!_faceDown)
            {
                _topCard = CardViewFactory.Create(Container, Theme, 0.42f);
                _topCard.Rect.anchorMin = _topCard.Rect.anchorMax = _topCard.Rect.pivot = new Vector2(0.5f, 0.5f);
                _topCard.Rect.anchoredPosition = new Vector2(6f, 14f);
                _topCard.SetRaycastable(false);
                _topCard.Group.blocksRaycasts = false;
                _topCard.gameObject.SetActive(false);
            }

            _stackTopBase = BackColor(2);

            // Count badge.
            _badge = UiFactory.CreateImage("Badge", Container, Theme.Circle, UiPalette.Gold);
            UiFactory.Place(_badge.rectTransform, new Vector2(1f, 1f), new Vector2(-6f, -2f), new Vector2(34f, 34f));
            var badgeOutline = _badge.gameObject.AddComponent<Outline>();
            badgeOutline.effectColor = UiPalette.WithAlpha(Color.black, 0.7f);
            badgeOutline.effectDistance = new Vector2(1.5f, -1.5f);
            _badgeText = UiFactory.CreateText(Theme, "Count", _badge.transform, "0", 18f,
                UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(_badgeText.rectTransform);
            // Compound labels like "12(3)" (banish pile: total + yours) must shrink to fit.
            _badgeText.enableAutoSizing = true;
            _badgeText.fontSizeMin = 9f;
            _badgeText.fontSizeMax = 18f;

            // Title under the stack.
            _title = UiFactory.CreateText(Theme, "Title", Container, title.ToUpperInvariant(), 13f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.characterSpacing = 2f;
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, -2f), new Vector2(140f, 18f));

            // Whole widget clickable.
            var hit = UiFactory.CreateImage("Hit", Container, null, new Color(0f, 0f, 0f, 0.001f), raycast: true);
            UiFactory.Stretch(hit.rectTransform);
            var button = hit.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => Clicked?.Invoke());
        }

        private Color BackColor(int depth)
        {
            float shade = 0.75f + depth * 0.12f;
            var basec = _faceDown ? new Color(0.18f, 0.16f, 0.26f) : UiPalette.PanelLight;
            return new Color(basec.r * shade, basec.g * shade, basec.b * shade, 1f);
        }

        public void Render(int count, CardSnap top, string badgeLabel = null)
        {
            if (!_built) return;
            bool any = count > 0;
            _emptyOutline.gameObject.SetActive(!any);
            for (int i = 0; i < _stack.Length; i++)
                _stack[i].gameObject.SetActive(any && count > i);
            _badgeText.text = badgeLabel ?? count.ToString();
            _badge.gameObject.SetActive(true);

            if (_topCard != null)
            {
                bool showTop = any && top != null && !string.IsNullOrEmpty(top.DefId);
                _topCard.gameObject.SetActive(showTop);
                if (showTop) _topCard.Bind(top);
            }

            if (count != _lastCount && _lastCount >= 0 && count > _lastCount)
                Pulse();
            _lastCount = count;
        }

        /// <summary>Badge punch + brief glow — call when the pile receives cards.
        /// Safe to spam: the previous tween is stopped and its base state restored first.</summary>
        public void Pulse()
        {
            if (!isActiveAndEnabled) return;
            if (_pulseCo != null)
            {
                StopCoroutine(_pulseCo);
                _badge.transform.localScale = _badgeBaseScale;
            }
            _pulseCo = StartCoroutine(Presentation.Tween.Punch(_badge.transform, 0.35f, 0.3f));

            var top = _stack[_stack.Length - 1];
            if (top.gameObject.activeSelf)
            {
                if (_flashCo != null)
                {
                    StopCoroutine(_flashCo);
                    top.color = _stackTopBase;
                }
                _flashCo = StartCoroutine(Presentation.Tween.Flash(top, UiPalette.Gold, 0.4f));
            }
        }
    }
}
