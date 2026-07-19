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
    /// Every entry is also kept in an unbounded record; the small ALL button above the
    /// bar opens a scrollable window with the whole game's log.
    /// Effects that touch other cards attach them to their causing entry
    /// (AttachAffected); hovering such an entry shows "→ [the affected cards]" beside
    /// the big preview. While a bar entry is hovered a grey spotlight mask dims
    /// everything except the log, the preview and the affected panel.
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
            public string DefId;
            public int PlayerIndex;
            public string Note;
            public bool Attachable;
            public readonly List<string> Affected = new List<string>();
            /// <summary>Live bar row; null once the entry scrolled off the bar.</summary>
            public RectTransform Rect;
        }

        private readonly List<Entry> _entries = new List<Entry>();  // on the bar (≤ 10)
        private readonly List<Entry> _all = new List<Entry>();      // whole game, newest first
        private RectTransform _affectedPanel;
        private RectTransform _fullLog;
        private Image _spotlight;
        private int _containerSiblingBackup = -1;

        public void Init(UiTheme theme)
        {
            Theme = theme;
            if (_built) return;
            _built = true;

            var title = UiFactory.CreateText(Theme, "Title", Container, "RECENT", 12f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            title.characterSpacing = 3f;
            UiFactory.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(4f, -2f), new Vector2(60f, 18f));

            // The full-log window trigger. Autosize keeps ALL/TOUT on one line.
            var all = UiFactory.CreateButton(Theme, "AllLogs", Container, UI.Loc.T("ALL"), 10f);
            UiFactory.Place((RectTransform)all.transform, new Vector2(1f, 1f), new Vector2(-2f, -2f), new Vector2(40f, 18f));
            var allLabel = UiFactory.ButtonLabel(all);
            allLabel.enableAutoSizing = true;
            allLabel.fontSizeMin = 6f;
            allLabel.fontSizeMax = 10f;
            all.onClick.AddListener(ShowFullLog);
        }

        /// <summary>Record an event. defId must be non-null (logged cards are always
        /// public). `note` renders as a dim second line; `attachable` marks entries that
        /// CAUSE things (plays, activations) so later effect events can attach to them.</summary>
        public void Push(string defId, int playerIndex, string note = null, bool attachable = false)
        {
            if (!_built || string.IsNullOrEmpty(defId)) return;

            var entry = new Entry { DefId = defId, PlayerIndex = playerIndex, Note = note, Attachable = attachable };
            _all.Insert(0, entry);
            entry.Rect = BuildRow(Container, entry, 88f, EntryHeight - 4f, compact: true);
            entry.Rect.anchorMin = entry.Rect.anchorMax = entry.Rect.pivot = new Vector2(0.5f, 1f);
            entry.Rect.anchoredPosition = new Vector2(0f, TopOffset);

            _entries.Insert(0, entry);
            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries[_entries.Count - 1];
                _entries.RemoveAt(_entries.Count - 1);
                if (oldest.Rect != null) Destroy(oldest.Rect.gameObject);
                oldest.Rect = null; // the record itself lives on in _all
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

        /// <summary>One log row (shared by the bar and the full-log window): stripe,
        /// art tile, name and note. The tile carries the hover proxy (preview +
        /// affected panel + spotlight).</summary>
        private RectTransform BuildRow(Transform parent, Entry entry, float width, float height, bool compact)
        {
            var rect = UiFactory.CreateRect("Entry", parent);
            rect.sizeDelta = new Vector2(width, height);

            var stripe = UiFactory.CreateImage("Stripe", rect, Theme.Rounded,
                entry.PlayerIndex < 0 ? UiPalette.Danger : UiPalette.PlayerColor(entry.PlayerIndex));
            stripe.rectTransform.anchorMin = new Vector2(0f, 0f);
            stripe.rectTransform.anchorMax = new Vector2(0f, 1f);
            stripe.rectTransform.pivot = new Vector2(0f, 0.5f);
            stripe.rectTransform.anchoredPosition = Vector2.zero;
            stripe.rectTransform.sizeDelta = new Vector2(4f, 0f);

            var tile = UiFactory.CreateImage("Art", rect, null, UiPalette.PanelLight, raycast: true);
            tile.rectTransform.anchorMin = new Vector2(0f, 0f);
            tile.rectTransform.anchorMax = new Vector2(0f, 1f);
            tile.rectTransform.pivot = new Vector2(0f, 0.5f);
            tile.rectTransform.anchoredPosition = new Vector2(6f, 0f);
            tile.rectTransform.sizeDelta = new Vector2(38f, 0f);

            // Non-Pascension ids (Shards of Infinity, character portraits) resolve name
            // AND art through the shared external-face hook.
            CardDatabase.TryGet(entry.DefId, out var def);
            string displayName = def?.Name;
            var external = displayName == null ? CardView.ExternalFaceResolver?.Invoke(entry.DefId) : null;
            if (displayName == null)
                displayName = external?.Name ?? entry.DefId;
            var art = Theme.Art(entry.DefId);
            if (art == null && external?.ArtId != null)
                art = Theme.Art(external.Value.ArtId);
            if (art != null)
            {
                tile.sprite = art;
                tile.color = Color.white;
            }

            bool hasNote = !string.IsNullOrEmpty(entry.Note);
            var name = UiFactory.CreateText(Theme, "Name", rect, displayName, compact ? 10f : 13f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Stretch(name.rectTransform, 48, hasNote ? 18 : 2, 2, 2);
            name.enableAutoSizing = true;
            name.fontSizeMin = 7f;
            name.fontSizeMax = compact ? 10f : 13f;
            if (hasNote)
            {
                var note = UiFactory.CreateText(Theme, "Note", rect, entry.Note, compact ? 8f : 11f,
                    UiPalette.GoldDim, TextAlignmentOptions.MidlineLeft);
                note.rectTransform.anchorMin = new Vector2(0f, 0f);
                note.rectTransform.anchorMax = new Vector2(1f, 0f);
                note.rectTransform.pivot = new Vector2(0.5f, 0f);
                note.rectTransform.offsetMin = new Vector2(48f, 1f);
                note.rectTransform.offsetMax = new Vector2(-2f, 17f);
                note.enableAutoSizing = true;
                note.fontSizeMin = 6f;
                note.fontSizeMax = compact ? 8f : 11f;
            }

            var hover = tile.gameObject.AddComponent<HistoryHover>();
            hover.DefId = entry.DefId;
            hover.Bar = this;
            hover.Affected = entry.Affected; // same list instance — later attaches show up
            hover.SuppressSpotlight = !compact; // the window brings its own dimmer
            return rect;
        }

        /// <summary>Attach a card an effect touched (banished, revealed, fetched…) to the
        /// newest ATTACHABLE entry of that player — its cause. False when no cause entry
        /// exists yet (caller may push a standalone entry instead).</summary>
        public bool AttachAffected(int playerIndex, string affectedDefId)
        {
            if (string.IsNullOrEmpty(affectedDefId)) return false;
            foreach (var entry in _all)
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

        // ------------------------------------------------------------------ full log window

        /// <summary>Scrollable window with EVERY log entry of the game, newest first.</summary>
        public void ShowFullLog()
        {
            HideFullLog();
            HideSpotlight();

            _fullLog = UiFactory.CreateRect("FullLog", Container.parent);
            UiFactory.Stretch(_fullLog);
            _fullLog.SetAsLastSibling();

            var dimmer = UiFactory.CreateDimmer("Dimmer", _fullLog);
            var dismiss = dimmer.gameObject.AddComponent<Button>();
            dismiss.targetGraphic = dimmer;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(HideFullLog);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _fullLog, UiPalette.Panel);
            var panelRect = (RectTransform)panel.transform;
            UiFactory.Place(panelRect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 980f));

            var title = UiFactory.CreateText(Theme, "Title", panelRect,
                UI.Loc.T("GAME LOG") + "  ·  " + _all.Count, 20f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(400f, 30f));

            var close = UiFactory.CreateButton(Theme, "Close", panelRect, "X", 16f);
            UiFactory.Place((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(-30f, -28f), new Vector2(44f, 36f));
            close.onClick.AddListener(HideFullLog);

            var scroll = UiFactory.CreateScrollView(Theme, "Entries", panelRect, out var content);
            var scrollRect = (RectTransform)scroll.transform;
            UiFactory.Stretch(scrollRect, 14, 14, 14, 56);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var entry in _all)
            {
                var row = BuildRow(content, entry, 0f, 46f, compact: false);
                var le = row.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 46f;
            }
        }

        public void HideFullLog()
        {
            if (_fullLog != null)
            {
                Destroy(_fullLog.gameObject);
                _fullLog = null;
            }
            HideAffected();
        }

        // ------------------------------------------------------------------ hover chrome

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

        /// <summary>Grey mask over everything except the log while a bar entry is
        /// hovered: mask on top, then the bar above it; the preview and the affected
        /// panel raise themselves above both.</summary>
        internal void ShowSpotlight()
        {
            if (_fullLog != null) return; // the window already dims the table
            if (_spotlight == null)
            {
                _spotlight = UiFactory.CreateImage("LogSpotlight", Container.parent, null,
                    new Color(0.06f, 0.06f, 0.07f, 0.72f), raycast: false);
                UiFactory.Stretch(_spotlight.rectTransform);
            }
            _spotlight.gameObject.SetActive(true);
            _spotlight.rectTransform.SetAsLastSibling();
            if (_containerSiblingBackup < 0)
                _containerSiblingBackup = Container.GetSiblingIndex();
            Container.SetAsLastSibling();
        }

        internal void HideSpotlight()
        {
            if (_spotlight != null)
                _spotlight.gameObject.SetActive(false);
            if (_containerSiblingBackup >= 0)
            {
                Container.SetSiblingIndex(_containerSiblingBackup);
                _containerSiblingBackup = -1;
            }
        }
    }

    /// <summary>Forwards log-entry hovers into the global card-preview feed, pops the
    /// affected-cards panel when the entry has any, and (bar entries only) raises the
    /// grey spotlight mask over the rest of the table.</summary>
    public sealed class HistoryHover : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public string DefId;
        public PlayHistoryBar Bar;
        public List<string> Affected;
        public bool SuppressSpotlight;
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
            // Order matters: the mask (and the bar above it) go up FIRST so the
            // preview's own SetAsLastSibling lands above both.
            if (Bar != null && !SuppressSpotlight)
                Bar.ShowSpotlight();
            var proxy = Proxy();
            proxy.SetPreviewDef(DefId);
            proxy.RaiseHover(true);
            if (Bar != null && Affected != null && Affected.Count > 0)
                Bar.ShowAffected(Affected);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (_proxy != null) _proxy.RaiseHover(false);
            if (Bar != null)
            {
                Bar.HideAffected();
                Bar.HideSpotlight();
            }
        }

        private void OnDisable()
        {
            if (Bar != null)
            {
                Bar.HideAffected();
                Bar.HideSpotlight();
            }
        }
    }
}
