using System.Collections.Generic;
using Pascension.Engine.Cards;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Hearthstone-style play history: the last 10 cards played by anyone, newest at the
    /// top, each with a player-color stripe. Event-driven (Push), never rebuilt by
    /// RefreshAll. Hovering an entry shows the big preview via CardView.AnyHovered.
    /// </summary>
    public sealed class PlayHistoryBar : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        private const int MaxEntries = 10;
        private const float EntryHeight = 44f;
        private const float TopOffset = -26f;

        private bool _built;
        private readonly List<RectTransform> _entries = new List<RectTransform>();

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var title = UiFactory.CreateText(Theme, "Title", Container, "RECENT", 12f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 3f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -2f), new Vector2(96f, 18f));
        }

        /// <summary>Record a play. defId must be non-null (plays are always public).</summary>
        public void Push(string defId, int playerIndex)
        {
            if (!_built || string.IsNullOrEmpty(defId)) return;

            var entry = UiFactory.CreateRect("Entry", Container);
            entry.anchorMin = entry.anchorMax = entry.pivot = new Vector2(0.5f, 1f);
            entry.sizeDelta = new Vector2(88f, EntryHeight - 4f);
            entry.anchoredPosition = new Vector2(0f, TopOffset);

            var stripe = UiFactory.CreateImage("Stripe", entry, Theme.Rounded, UiPalette.PlayerColor(playerIndex));
            stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
            stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
            stripe.rectTransform.pivot = new Vector2(0f, 0.5f);
            stripe.rectTransform.anchoredPosition = Vector2.zero;
            stripe.rectTransform.sizeDelta = new Vector2(4f, 0f);

            // Mini art tile + name — a full CardView at 0.18 is unreadable; a tile reads better.
            var tile = UiFactory.CreateImage("Art", entry, null, UiPalette.PanelLight, raycast: true);
            tile.rectTransform.anchorMin = new Vector2(0f, 0f);
            tile.rectTransform.anchorMax = new Vector2(0f, 1f);
            tile.rectTransform.pivot = new Vector2(0f, 0.5f);
            tile.rectTransform.anchoredPosition = new Vector2(6f, 0f);
            tile.rectTransform.sizeDelta = new Vector2(38f, 0f);
            var art = Theme.Art(defId);
            if (art != null)
            {
                tile.sprite = art;
                tile.color = Color.white;
            }

            CardDatabase.TryGet(defId, out var def);
            var name = UiFactory.CreateText(Theme, "Name", entry, def?.Name ?? defId, 10f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Stretch(name.rectTransform, 48, 2, 2, 2);
            name.enableAutoSizing = true;
            name.fontSizeMin = 7f;
            name.fontSizeMax = 10f;

            // A hidden CardView drives the hover preview (raycast via the tile).
            var hoverCard = tile.gameObject.AddComponent<HistoryHover>();
            hoverCard.DefId = defId;

            _entries.Insert(0, entry);
            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries[_entries.Count - 1];
                _entries.RemoveAt(_entries.Count - 1);
                if (oldest != null) Destroy(oldest.gameObject);
            }

            // Slide everything to its slot (newest at the top).
            for (int i = 0; i < _entries.Count; i++)
            {
                var target = new Vector2(0f, TopOffset - i * EntryHeight);
                if (i == 0)
                    _entries[i].anchoredPosition = target;
                else if (isActiveAndEnabled)
                    StartCoroutine(Presentation.Tween.Move(_entries[i], target, 0.2f));
                else
                    _entries[i].anchoredPosition = target;
            }
        }
    }

    /// <summary>Forwards history-entry hovers into the global card-preview feed.</summary>
    public sealed class HistoryHover : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public string DefId;
        private CardView _proxy;

        private CardView Proxy()
        {
            if (_proxy == null)
            {
                // An invisible zero-size CardView whose only job is carrying DefId + position
                // for the preview system.
                var go = new GameObject("HoverProxy", typeof(RectTransform));
                go.transform.SetParent(transform, false);
                ((RectTransform)go.transform).sizeDelta = Vector2.zero;
                _proxy = go.AddComponent<CardView>();
            }
            return _proxy;
        }

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
        {
            var proxy = Proxy();
            proxy.SetPreviewDef(DefId);
            proxy.RaiseHover(true);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_proxy != null) _proxy.RaiseHover(false);
        }
    }
}
