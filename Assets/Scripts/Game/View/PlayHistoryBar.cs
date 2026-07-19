using System.Collections.Generic;
using Pascension.Engine.Cards;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Hearthstone-style play history: the last 10 notable events, newest at the top,
    /// each with a player-color stripe (red for the game itself, e.g. Ingeminex),
    /// an art tile, the card name and an optional note line ("recruited", "−4 · 29→25").
    /// Event-driven (Push), never rebuilt by RefreshAll.
    /// Effects that touch other cards attach them to their causing entry
    /// (AttachAffected); hovering such an entry shows "→ [the affected cards]" beside
    /// the big preview. Hovering any entry drives the preview via CardView.AnyHovered.
    /// </summary>
    public sealed class PlayHistoryBar : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Where (in Container.parent local space, anchor/pivot top-left) the
        /// affected-cards panel pops on hover — set by the screen so it lands beside its
        /// fixed card preview.</summary>
        public Vector2 AffectedAnchor = new Vector2(420f, -12f);

        private const int MaxEntries = 10;
        private const float EntryHeight = 44f;
        private const float TopOffset = -26f;

        private bool _built;

        private sealed class Entry
        {
            public RectTransform Rect;
            public int PlayerIndex;
            public bool Attachable;
            public readonly List<string> Affected = new List<string>();
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private RectTransform _affectedPanel;

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

        /// <summary>Record an event. defId must be non-null (logged cards are always
        /// public). `note` renders as a dim second line; `attachable` marks entries that
        /// CAUSE things (plays, activations) so later effect events can attach to them.</summary>
        public void Push(string defId, int playerIndex, string note = null, bool attachable = false)
        {
            if (!_built || string.IsNullOrEmpty(defId)) return;

            var rect = UiFactory.CreateRect("Entry", Container);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(88f, EntryHeight - 4f);
            rect.anchoredPosition = new Vector2(0f, TopOffset);

            var stripe = UiFactory.CreateImage("Stripe", rect, Theme.Rounded,
                playerIndex < 0 ? UiPalette.Danger : UiPalette.PlayerColor(playerIndex));
            stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
            stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
            stripe.rectTransform.pivot = new Vector2(0f, 0.5f);
            stripe.rectTransform.anchoredPosition = Vector2.zero;
            stripe.rectTransform.sizeDelta = new Vector2(4f, 0f);

            // Mini art tile + name — a full CardView at 0.18 is unreadable; a tile reads better.
            var tile = UiFactory.CreateImage("Art", rect, null, UiPalette.PanelLight, raycast: true);
            tile.rectTransform.anchorMin = new Vector2(0f, 0f);
            tile.rectTransform.anchorMax = new Vector2(0f, 1f);
            tile.rectTransform.pivot = new Vector2(0f, 0.5f);
            tile.rectTransform.anchoredPosition = new Vector2(6f, 0f);
            tile.rectTransform.sizeDelta = new Vector2(38f, 0f);

            // Non-Pascension ids (Shards of Infinity, character portraits) resolve name
            // AND art through the shared external-face hook.
            CardDatabase.TryGet(defId, out var def);
            string displayName = def?.Name;
            var external = displayName == null ? CardView.ExternalFaceResolver?.Invoke(defId) : null;
            if (displayName == null)
                displayName = external?.Name ?? defId;
            var art = Theme.Art(defId);
            if (art == null && external?.ArtId != null)
                art = Theme.Art(external.Value.ArtId);
            if (art != null)
            {
                tile.sprite = art;
                tile.color = Color.white;
            }

            bool hasNote = !string.IsNullOrEmpty(note);
            var name = UiFactory.CreateText(Theme, "Name", rect, displayName, 10f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Stretch(name.rectTransform, 48, hasNote ? 18 : 2, 2, 2);
            name.enableAutoSizing = true;
            name.fontSizeMin = 7f;
            name.fontSizeMax = 10f;
            if (hasNote)
            {
                var noteText = UiFactory.CreateText(Theme, "Note", rect, note, 8f,
                    UiPalette.GoldDim, TextAlignmentOptions.MidlineLeft);
                noteText.rectTransform.anchorMin = new Vector2(0f, 0f);
                noteText.rectTransform.anchorMax = new Vector2(1f, 0f);
                noteText.rectTransform.pivot = new Vector2(0.5f, 0f);
                noteText.rectTransform.offsetMin = new Vector2(48f, 1f);
                noteText.rectTransform.offsetMax = new Vector2(-2f, 17f);
                noteText.enableAutoSizing = true;
                noteText.fontSizeMin = 6f;
                noteText.fontSizeMax = 8f;
            }

            var entry = new Entry { Rect = rect, PlayerIndex = playerIndex, Attachable = attachable };

            // The hover proxy drives the big preview + the affected-cards panel.
            var hover = tile.gameObject.AddComponent<HistoryHover>();
            hover.DefId = defId;
            hover.Bar = this;
            hover.Affected = entry.Affected; // same list instance — later attaches show up

            _entries.Insert(0, entry);
            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries[_entries.Count - 1];
                _entries.RemoveAt(_entries.Count - 1);
                if (oldest?.Rect != null) Destroy(oldest.Rect.gameObject);
            }

            // Slide everything to its slot (newest at the top).
            for (int i = 0; i < _entries.Count; i++)
            {
                var target = new Vector2(0f, TopOffset - i * EntryHeight);
                if (i == 0 || !isActiveAndEnabled)
                    _entries[i].Rect.anchoredPosition = target;
                else
                    StartCoroutine(Presentation.Tween.Move(_entries[i].Rect, target, 0.2f));
            }
        }

        /// <summary>Attach a card an effect touched (banished, revealed, fetched…) to the
        /// newest ATTACHABLE entry of that player — its cause. False when no cause entry
        /// is on the board (caller may push a standalone entry instead).</summary>
        public bool AttachAffected(int playerIndex, string affectedDefId)
        {
            if (string.IsNullOrEmpty(affectedDefId)) return false;
            foreach (var entry in _entries)
            {
                if (!entry.Attachable || entry.PlayerIndex != playerIndex) continue;
                // Dedupe: one effect often reports the same card twice (revealed AND
                // then recruited) — the panel should show it once.
                if (entry.Affected.Count < 6 && !entry.Affected.Contains(affectedDefId))
                    entry.Affected.Add(affectedDefId);
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ affected panel

        /// <summary>"→ [cards]" beside the big preview while an entry with affected
        /// cards is hovered. Rebuilt per show; purely visual (no raycasts).</summary>
        internal void ShowAffected(List<string> defIds)
        {
            if (defIds == null || defIds.Count == 0) return;
            HideAffected();

            _affectedPanel = UiFactory.CreateRect("AffectedPanel", Container.parent);
            _affectedPanel.anchorMin = _affectedPanel.anchorMax = _affectedPanel.pivot = new Vector2(0f, 1f);
            _affectedPanel.anchoredPosition = AffectedAnchor;
            _affectedPanel.SetAsLastSibling();

            var arrow = UiFactory.CreateText(Theme, "Arrow", _affectedPanel, "→", 44f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(arrow.rectTransform, new Vector2(0f, 1f), new Vector2(0f, -60f), new Vector2(52f, 60f));
            arrow.raycastTarget = false;

            const float scale = 0.55f;
            float x = 62f;
            int shown = 0;
            foreach (string defId in defIds)
            {
                if (shown++ >= 4) break;
                var card = CardViewFactory.Create(_affectedPanel, Theme, scale);
                card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0f, 1f);
                card.Rect.pivot = new Vector2(0f, 1f);
                card.Rect.anchoredPosition = new Vector2(x, 0f);
                card.BindDef(defId);
                card.SetRaycastable(false);
                if (card.Group != null) card.Group.blocksRaycasts = false;
                x += CardView.Width * scale + 10f;
            }
        }

        internal void HideAffected()
        {
            if (_affectedPanel != null)
            {
                Destroy(_affectedPanel.gameObject);
                _affectedPanel = null;
            }
        }
    }

    /// <summary>Forwards history-entry hovers into the global card-preview feed and
    /// pops the affected-cards panel when the entry has any.</summary>
    public sealed class HistoryHover : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public string DefId;
        public PlayHistoryBar Bar;
        public List<string> Affected;
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
            if (Bar != null && Affected != null && Affected.Count > 0)
                Bar.ShowAffected(Affected);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_proxy != null) _proxy.RaiseHover(false);
            if (Bar != null) Bar.HideAffected();
        }

        private void OnDisable()
        {
            if (Bar != null) Bar.HideAffected();
        }
    }
}
