using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Game.Presentation;
using Pascension.Game.View;
using Pascension.Net;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// The Shards of Infinity table on Pascension's full presentation stack: real
    /// CardViews everywhere (via CardView.ExternalFaceResolver), the drag-to-play
    /// HandView, StS corner piles, zone flights, played-card showcases, floating
    /// numbers, glow bursts and the click-to-skip PresentationQueue. Same architecture
    /// as GameScreen: the hand, stats and pile counts render LIVE on every snapshot;
    /// board zones refresh on queue drain; decisions wait for animations. Zero rules
    /// logic — renders ShardsSnapshot and submits PlayerActions.
    /// </summary>
    public sealed class SoiGameScreen : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform UiRootRect;

        private ISession _session;
        private ShardsSnapshot _snap;
        private int _maxHealth = 50;
        private PendingSnap _pending;
        private DecisionRequest _deferredDecision;
        private bool _gameOverShown;

        // Presentation stack.
        private PresentationQueue _queue;
        private FlightLayer _flights;
        private CardShowcase _showcase;
        private GlowBurstLayer _bursts;
        private FloatingNumberLayer _floats;
        private ToastView _toast;

        // Table views.
        private TextMeshProUGUI _hudTurn, _hudCounts, _statusLine;
        private TextMeshProUGUI _statHealth, _statMastery, _statGems, _statPower;
        private RectTransform _statHealthRect, _statMasteryRect, _statGemsRect, _statPowerRect;
        private Button _endTurn, _relics, _focusButton;
        private TextMeshProUGUI _endTurnLabel;
        private Color _relicsBaseColor;
        private PlayHistoryBar _history;
        private RectTransform _buyPopup;
        private int _buyPopupSlot = -1;
        private RectTransform _opponentStrip, _centerRow, _destinyRow, _monsterRow;
        private RectTransform _championRow, _ownDestinyRow;
        private CardView _portrait;
        private CardView[] _slots;
        private readonly HashSet<int> _hiddenSlots = new HashSet<int>();
        private HandView _hand;
        private RectTransform _handRect;
        private PileWidget _drawPile, _playedPile, _discardPile, _banishPile, _centerDeckPile;
        private SoiDecisionModal _modal;
        private CardListModal _cardList;
        private SoiOpponentDetailModal _opponentDetail;
        private PauseOverlayView _pauseOverlay;
        private CardView _preview;
        private RectTransform _gameOverPanel;
        private TextMeshProUGUI _gameOverText;

        // Event-time lookups (rebuilt on every full refresh).
        private readonly Dictionary<int, CardView> _boardViews = new Dictionary<int, CardView>();
        private readonly Dictionary<int, RectTransform> _opponentPanels = new Dictionary<int, RectTransform>();
        private readonly Dictionary<int, TextMeshProUGUI> _opponentStatTexts = new Dictionary<int, TextMeshProUGUI>();
        private readonly HashSet<int> _pendingReveal = new HashSet<int>();
        private readonly List<CardView> _transient = new List<CardView>();

        // Snapshot-computed glow hints (host truth — see ShardsSnapshotBuilder).
        private readonly HashSet<int> _conditionGlow = new HashSet<int>();
        private readonly HashSet<int> _killable = new HashSet<int>();
        private readonly HashSet<int> _buyable = new HashSet<int>();

        // Remote hover (Hearthstone-style "what is the active player pointing at").
        private int _lastHoverSent = -1;
        private int _remoteHoverSeat = -1, _remoteHoverId = -1;
        private RectTransform _remoteHoverMark;
        private Image _remoteHoverImg;

        // Kill attribution for the play log: a champion destroyed right after taking
        // power damage was ATTACKED (own log entry only); destroyed without a preceding
        // hit was killed by an effect (also attaches to its causing card).
        private int _lastChampionHitId = -1;

        // ------------------------------------------------------------------ bind

        public void Bind(ISession session, object rules)
        {
            _session = session;
            if (rules is ShardsRules shardsRules)
                _maxHealth = shardsRules.MaxHealth;
            SoiCardFaces.Install();

            BuildLayout();
            StartCoroutine(OuterGlowPulseLoop());

            session.SnapshotReceived += OnSnapshot;
            session.EventsReceived += OnEvents;
            session.InputRequested += OnInputRequested;
            session.ActionRejected += OnActionRejected;

            _queue.EventPlayer = PlayEvent;
            _queue.Drained += RefreshAll;

            _pauseOverlay = PauseOverlayView.Create(UiRootRect, Theme);
            session.PauseChanged += OnPauseChanged;
            NetEvents.LocalClientDisconnected += OnLocalClientDisconnected;
            if (session is NetworkSession netSession && netSession.CurrentPause != null &&
                netSession.CurrentPause.Paused)
                OnPauseChanged(netSession.CurrentPause);

            CardView.AnyHovered += OnAnyCardHovered;
            GameNetBridge.CardHoverChanged += OnRemoteHover;
        }

        private void OnDestroy()
        {
            GameNetBridge.CardHoverChanged -= OnRemoteHover;
            CardView.AnyHovered -= OnAnyCardHovered;
            NetEvents.LocalClientDisconnected -= OnLocalClientDisconnected;
            if (_session != null)
                _session.PauseChanged -= OnPauseChanged;
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            var root = UiRootRect;

            // HUD (top-right) + status line.
            _hudTurn = UiFactory.CreateText(Theme, "HudTurn", root, "", 20f, UiPalette.Gold,
                TextAlignmentOptions.Right, FontStyles.Bold);
            UiFactory.Place(_hudTurn.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -26f), new Vector2(500f, 30f));
            _hudCounts = UiFactory.CreateText(Theme, "HudCounts", root, "", 14f, UiPalette.TextDim,
                TextAlignmentOptions.Right);
            UiFactory.Place(_hudCounts.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -54f), new Vector2(500f, 22f));
            _statusLine = UiFactory.CreateText(Theme, "Status", root, "", 16f, UiPalette.GoldDim,
                TextAlignmentOptions.Right, FontStyles.Italic);
            UiFactory.Place(_statusLine.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -80f), new Vector2(620f, 24f));

            // Opponents — centered along the top edge.
            _opponentStrip = UiFactory.CreateRect("Opponents", root);
            UiFactory.Place(_opponentStrip, new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(1180f, 170f));
            _opponentStrip.pivot = new Vector2(0.5f, 1f);

            // Play history (top-left, like Pascension's RECENT bar).
            var historyRect = UiFactory.CreateRect("History", root);
            UiFactory.Place(historyRect, new Vector2(0f, 1f), new Vector2(10f, -8f), new Vector2(100f, 620f));
            historyRect.pivot = new Vector2(0f, 1f);
            _history = historyRect.gameObject.AddComponent<PlayHistoryBar>();
            _history.Container = historyRect;
            _history.Init(Theme);
            // The affected-cards hover panel pops just right of the fixed card preview
            // (preview: x 16..302 at y −192).
            _history.AffectedAnchor = new Vector2(312f, -192f);

            // Destiny row + Ingeminex space (between opponents and the center row).
            _destinyRow = UiFactory.CreateRect("DestinyRow", root);
            UiFactory.Place(_destinyRow, new Vector2(0.5f, 0.5f), new Vector2(-260f, 268f), new Vector2(760f, 126f));
            _monsterRow = UiFactory.CreateRect("MonsterRow", root);
            UiFactory.Place(_monsterRow, new Vector2(0.5f, 0.5f), new Vector2(400f, 268f), new Vector2(520f, 132f));

            // Center row: deck pile + 6 persistent card slots.
            _centerDeckPile = CreatePile("CenterDeck", UI.Loc.T("Center"), faceDown: true,
                new Vector2(0.5f, 0.5f), new Vector2(-590f, 84f));
            _centerDeckPile.Clicked += () => _toast.Show(UI.Loc.T("The shared center deck — row slots refill from here."));

            _centerRow = UiFactory.CreateRect("CenterRow", root);
            UiFactory.Place(_centerRow, new Vector2(0.5f, 0.5f), new Vector2(60f, 90f), new Vector2(900f, 200f));
            _slots = new CardView[6];
            for (int s = 0; s < 6; s++)
            {
                var slot = CardViewFactory.Create(_centerRow, Theme, 0.62f);
                slot.Rect.anchorMin = slot.Rect.anchorMax = new Vector2(0f, 0.5f);
                slot.Rect.pivot = new Vector2(0f, 0.5f);
                slot.Rect.anchoredPosition = new Vector2(s * 148f, 0f);
                int slotIndex = s;
                slot.Clicked += _ => OnRowSlotClicked(slotIndex);
                slot.gameObject.SetActive(false);
                _slots[s] = slot;
            }

            // Character portrait — display only (Focus moved to the "+" mastery button),
            // sitting just below the draw pile.
            _portrait = CardViewFactory.Create(root, Theme, 0.5f);
            _portrait.Rect.anchorMin = _portrait.Rect.anchorMax = new Vector2(0f, 0f);
            _portrait.Rect.anchoredPosition = new Vector2(80f, 112f);
            _portrait.SetRaycastable(false);
            _portrait.Group.blocksRaycasts = false;

            // My champions + owned destinies (above the hand band).
            _championRow = LabeledRow(root, UI.Loc.T("MY CHAMPIONS"), new Vector2(-330f, -122f), new Vector2(620f, 140f));
            _ownDestinyRow = LabeledRow(root, UI.Loc.T("MY DESTINIES"), new Vector2(330f, -122f), new Vector2(560f, 140f));

            // Stats (left edge column) — icons instead of words.
            _statHealth = Stat(root, "soi_health", new Vector2(-690f, -116f), UiPalette.HealthyGreen, out _statHealthRect);
            _statMastery = Stat(root, "soi_mastery", new Vector2(-690f, -178f), UiPalette.Gold, out _statMasteryRect);
            _statGems = Stat(root, "soi_gem", new Vector2(-690f, -240f), new Color(0.45f, 0.68f, 0.95f), out _statGemsRect);
            _statPower = Stat(root, "soi_power", new Vector2(-690f, -302f), UiPalette.WoundedRed, out _statPowerRect);

            // Focus lives on a "+" button beside the mastery counter (tap ability).
            _focusButton = UiFactory.CreateButton(Theme, "FocusPlus", root, "+", 26f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_focusButton.transform, new Vector2(0.5f, 0.5f),
                new Vector2(-690f + 112f, -178f), new Vector2(40f, 40f));
            _focusButton.onClick.AddListener(OnFocusClicked);

            // StS corner piles: played over draw on the left (portrait sits below the
            // draw pile), banish over discard on the right.
            _drawPile = CreatePile("DrawPile", UI.Loc.T("Draw"), faceDown: true, new Vector2(0f, 0f), new Vector2(80f, 300f));
            _playedPile = CreatePile("PlayedPile", UI.Loc.T("Played"), faceDown: false, new Vector2(0f, 0f), new Vector2(80f, 488f));
            _discardPile = CreatePile("DiscardPile", UI.Loc.T("Discard"), faceDown: false, new Vector2(1f, 0f), new Vector2(-80f, 208f));
            _banishPile = CreatePile("BanishPile", UI.Loc.T("Banish"), faceDown: false, new Vector2(1f, 0f), new Vector2(-80f, 396f));
            _drawPile.Clicked += () => _toast.Show(UI.Loc.French
                ? "Votre deck : " + (Me?.DeckCount ?? 0) + " cartes (contenu et ordre cachés)."
                : "Your deck: " + (Me?.DeckCount ?? 0) + " cards (contents and order hidden).");
            _playedPile.Clicked += () => ShowPile(UI.Loc.T("Played this turn"), Me != null ? ZoneSnaps(Me.PlayZone) : null);
            _discardPile.Clicked += () => ShowPile(UI.Loc.T("Discard pile"), Me != null ? ZoneSnaps(Me.Discard) : null);
            _banishPile.Clicked += ShowBanishedByPlayer;

            // END TURN sits at the very bottom, below the discard pile (clear of its
            // title text); RECRUIT RELIC directly to its left, pulsing once usable.
            _endTurn = UiFactory.CreateButton(Theme, "EndTurn", root, UI.Loc.T("END TURN"), 21f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_endTurn.transform, new Vector2(1f, 0f), new Vector2(-108f, 62f), new Vector2(186f, 58f));
            _endTurn.onClick.AddListener(() => Submit(new ShardsEndTurnAction { PlayerIndex = MyIndex }));
            _endTurnLabel = UiFactory.ButtonLabel(_endTurn);
            _relics = UiFactory.CreateButton(Theme, "Relics", root, UI.Loc.T("RECRUIT RELIC"), 14f);
            UiFactory.Place((RectTransform)_relics.transform, new Vector2(1f, 0f), new Vector2(-306f, 62f), new Vector2(186f, 58f));
            _relics.onClick.AddListener(OnRelicsClicked);
            _relics.gameObject.SetActive(false);
            _relicsBaseColor = _relics.image.color;

            // Hand — the real drag-to-play HandView, hugging the bottom edge.
            _handRect = UiFactory.CreateRect("Hand", root);
            UiFactory.Place(_handRect, new Vector2(0.5f, 0f), new Vector2(0f, 62f), new Vector2(1400f, 320f));
            _hand = _handRect.gameObject.AddComponent<HandView>();
            _hand.Theme = Theme;
            _hand.Container = _handRect;
            _hand.PlayRequested += OnHandPlayRequested;
            _hand.GlowResolver = HandGlowFor;

            // Remote-hover marker: a soft player-colored overlay repositioned in
            // LateUpdate over whatever public card the active player is pointing at.
            // Created BELOW the presentation layers so animations/modals draw over it.
            _remoteHoverMark = UiFactory.CreateRect("RemoteHover", root);
            _remoteHoverImg = UiFactory.CreateImage("Fill", _remoteHoverMark, Theme.Rounded,
                UiPalette.WithAlpha(UiPalette.Gold, 0.3f), raycast: false);
            UiFactory.Stretch(_remoteHoverImg.rectTransform);
            _remoteHoverMark.gameObject.SetActive(false);

            // Presentation layers (z: flights below showcase below bursts/floats).
            _flights = Layer<FlightLayer>("Flights", root, out var flightsRect);
            _flights.Container = flightsRect;
            _flights.Init(Theme);
            _bursts = Layer<GlowBurstLayer>("Bursts", root, out var burstsRect);
            _bursts.Container = burstsRect;
            _bursts.Init(Theme);
            _showcase = Layer<CardShowcase>("Showcase", root, out var showcaseRect);
            _showcase.Container = showcaseRect;
            _showcase.Init(Theme, _bursts);
            _floats = Layer<FloatingNumberLayer>("Floats", root, out var floatsRect);
            _floats.Container = floatsRect;
            _floats.Init(Theme);
            _queue = gameObject.AddComponent<PresentationQueue>();

            // Services above the table.
            var toastRect = UiFactory.CreateRect("ToastHost", root);
            UiFactory.Place(toastRect, new Vector2(0.5f, 0f), new Vector2(0f, 390f), new Vector2(600f, 60f));
            _toast = toastRect.gameObject.AddComponent<ToastView>();
            _toast.Container = toastRect;
            _toast.Init(Theme);

            var cardListRect = UiFactory.CreateRect("CardList", root);
            UiFactory.Stretch(cardListRect);
            _cardList = cardListRect.gameObject.AddComponent<CardListModal>();
            _cardList.Container = cardListRect;
            _cardList.Init(Theme);
            _opponentDetail = SoiOpponentDetailModal.Create(root, Theme);
            _modal = SoiDecisionModal.Create(root, Theme);

            // Fixed hover preview (top-left, under the opponent strip).
            _preview = CardViewFactory.Create(root, Theme, 1.3f);
            _preview.Rect.anchorMin = _preview.Rect.anchorMax = new Vector2(0f, 1f);
            _preview.Rect.pivot = new Vector2(0f, 1f);
            _preview.Rect.anchoredPosition = new Vector2(16f, -192f);
            _preview.SetRaycastable(false);
            _preview.Group.blocksRaycasts = false;
            _preview.Group.interactable = false;
            _preview.gameObject.SetActive(false);

            // Game-over panel.
            _gameOverPanel = UiFactory.CreateRect("GameOver", root);
            UiFactory.Stretch(_gameOverPanel);
            UiFactory.CreateDimmer("Dimmer", _gameOverPanel);
            var goPanel = UiFactory.CreatePanel(Theme, "Panel", _gameOverPanel, UiPalette.Panel);
            UiFactory.Place((RectTransform)goPanel.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 300f));
            _gameOverText = UiFactory.CreateText(Theme, "Text", goPanel.transform, "", 30f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_gameOverText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(520f, 90f));
            var menuButton = UiFactory.CreateButton(Theme, "Menu", goPanel.transform, UI.Loc.T("BACK TO MENU"), 18f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)menuButton.transform, new Vector2(0.5f, 0f), new Vector2(0f, 46f), new Vector2(260f, 56f));
            menuButton.onClick.AddListener(() =>
            {
                // Full network teardown, not just Current=null: a still-listening
                // NetworkManager + stale NetLobbyData would rebuild the ONLINE match on
                // the next solo start (NetBootstrap re-spawns HostMatchStarter, which
                // waits its 10 s scene-sync failsafe = the "slow load", then re-seats
                // the old opponents). Shutdown() is a no-op after solo games.
                NetLauncher.Shutdown();
                UnityEngine.SceneManagement.SceneManager.LoadScene(UI.SceneFlow.MenuScene);
            });
            _gameOverPanel.gameObject.SetActive(false);
        }

        private T Layer<T>(string name, RectTransform root, out RectTransform rect) where T : Component
        {
            rect = UiFactory.CreateRect(name, root);
            UiFactory.Stretch(rect);
            return rect.gameObject.AddComponent<T>();
        }

        private PileWidget CreatePile(string goName, string title, bool faceDown, Vector2 anchor, Vector2 pos)
        {
            var rect = UiFactory.CreateRect(goName, UiRootRect);
            UiFactory.Place(rect, anchor, pos, new Vector2(140f, 170f));
            var pile = rect.gameObject.AddComponent<PileWidget>();
            pile.Container = rect;
            pile.Init(Theme, title, faceDown);
            return pile;
        }

        private TextMeshProUGUI Stat(RectTransform root, string iconName, Vector2 pos, Color color, out RectTransform rect)
        {
            rect = UiFactory.CreateRect("Stat_" + iconName, root);
            UiFactory.Place(rect, new Vector2(0.5f, 0.5f), pos, new Vector2(168f, 54f));
            var bg = UiFactory.CreatePanel(Theme, "Bg", rect, UiPalette.WithAlpha(UiPalette.Panel, 0.9f));
            UiFactory.Stretch((RectTransform)bg.transform);
            // Icon instead of a word — fixed left slot, value centered in the rest.
            var icon = UiFactory.CreateText(Theme, "Icon", rect, $"<sprite name=\"{iconName}\">", 28f,
                Color.white, TextAlignmentOptions.Center);
            if (Theme.Icons != null) icon.spriteAsset = Theme.Icons;
            UiFactory.Place(icon.rectTransform, new Vector2(0f, 0.5f), new Vector2(26f, 0f), new Vector2(40f, 44f));
            var value = UiFactory.CreateText(Theme, "Value", rect, "0", 25f, color,
                TextAlignmentOptions.Center, FontStyles.Bold);
            // Pivot sits at the LEFT edge (Place sets pivot=anchor) — the rect must end
            // inside the 168-wide tile or "0/30" runs under the + focus button.
            UiFactory.Place(value.rectTransform, new Vector2(0f, 0.5f), new Vector2(66f, 0f), new Vector2(94f, 44f));
            value.enableAutoSizing = true;
            value.fontSizeMin = 12f;
            value.fontSizeMax = 25f;
            return value;
        }

        private RectTransform LabeledRow(RectTransform root, string label, Vector2 pos, Vector2 size)
        {
            var caption = UiFactory.CreateText(Theme, "Label_" + label, root, label, 11f, UiPalette.TextDim,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(caption.rectTransform, new Vector2(0.5f, 0.5f),
                pos + new Vector2(0f, size.y / 2f + 10f), new Vector2(size.x, 16f));
            var row = UiFactory.CreateRect("Row_" + label, root);
            UiFactory.Place(row, new Vector2(0.5f, 0.5f), pos, size);
            return row;
        }

        /// <summary>Lay out `count` left-anchored (pivot 0,0.5) cards centered inside a
        /// row of the given width. Cards keep `preferredStep` spacing until the run would
        /// overflow, then the step compresses (cards overlap) so nothing is dropped.
        /// Returns the first card's left-edge x and the per-card step.</summary>
        private static void CenterRun(float width, int count, out float startX, out float step,
            float preferredStep = 104f)
        {
            step = preferredStep;
            if (count <= 0) { startX = 0f; return; }
            float cardWidth = preferredStep - 8f; // approx card box (step minus the gap)
            float runWidth = (count - 1) * step + cardWidth;
            if (runWidth > width && count > 1)
            {
                step = (width - cardWidth) / (count - 1); // compress to fit
                runWidth = width;
            }
            startX = (width - runWidth) / 2f;
        }

        private Coroutine _relicPulse;

        /// <summary>Pulse the RECRUIT RELIC button gold while it is usable (Mastery 10,
        /// not yet recruited) so the free once-per-game pick is impossible to miss.</summary>
        private void SetRelicGlow(bool on)
        {
            if (on)
            {
                if (_relicPulse == null && isActiveAndEnabled)
                    _relicPulse = StartCoroutine(RelicPulseLoop());
            }
            else if (_relicPulse != null)
            {
                StopCoroutine(_relicPulse);
                _relicPulse = null;
                if (_relics != null) _relics.image.color = _relicsBaseColor;
            }
        }

        private IEnumerator RelicPulseLoop()
        {
            while (true)
            {
                float t = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4.5f);
                _relics.image.color = Color.Lerp(_relicsBaseColor, UiPalette.Gold, t);
                yield return null;
            }
        }

        // ------------------------------------------------------------------ session plumbing

        private int MyIndex => _session.LocalPlayerIndex;
        private ShardsPlayerSnap Me => _snap != null ? _snap.Players[MyIndex] : null;
        private bool MyPriority => _pending != null && _pending.PlayerIndex == MyIndex &&
                                   _pending.Kind == PendingInputKind.Priority;

        private void OnSnapshot(SnapshotBase snapshotBase)
        {
            _snap = snapshotBase as ShardsSnapshot;
            if (_snap == null) return;
            _conditionGlow.Clear();
            _killable.Clear();
            _buyable.Clear();
            if (_snap.ConditionGlowIds != null) _conditionGlow.UnionWith(_snap.ConditionGlowIds);
            if (_snap.KillableIds != null) _killable.UnionWith(_snap.KillableIds);
            if (_snap.BuyableSlots != null) _buyable.UnionWith(_snap.BuyableSlots);
            RefreshHandLive();
            if (_queue.IsIdle)
                RefreshAll();
        }

        private void OnEvents(List<GameEvent> batch)
        {
            // Drawn cards render hidden until their flight lands (mine only).
            foreach (var e in batch)
                if (e is ShardsCardDrawnEvent drawn && drawn.PlayerIndex == MyIndex && drawn.InstanceId > 0)
                    _pendingReveal.Add(drawn.InstanceId);
            _queue.Enqueue(batch);
        }

        private void OnInputRequested(PendingSnap pending)
        {
            _pending = pending;
            if (pending != null && pending.Kind == PendingInputKind.Decision &&
                pending.PlayerIndex == MyIndex && pending.Decision != null)
            {
                // Decisions wait for animations to finish (Pascension discipline).
                if (_queue.IsIdle)
                    ShowDecision(pending.Decision);
                else
                    _deferredDecision = pending.Decision;
            }
            RefreshInteractivity();
        }

        private void OnActionRejected(string error) => _toast.Show(error);

        private void OnPauseChanged(PauseInfo info)
        {
            if (_pauseOverlay == null) return;
            if (info != null && info.Paused) _pauseOverlay.ShowWaiting(info);
            else _pauseOverlay.HideWaiting();
        }

        private void OnLocalClientDisconnected(string reason)
        {
            if (_pauseOverlay == null || _gameOverShown) return;
            _pauseOverlay.ShowConnectionLost(reason);
        }

        private void Submit(PlayerAction action)
        {
            if (_session == null || action == null) return;
            _session.SubmitAction(action);
        }

        // ------------------------------------------------------------------ input handlers

        private void OnHandPlayRequested(int instanceId)
        {
            if (!MyPriority)
            {
                _toast.Show(UI.Loc.T("Not your turn."));
                return;
            }
            _hand.RemoveCardOptimistic(instanceId);
            Submit(new ShardsPlayCardAction { PlayerIndex = MyIndex, CardInstanceId = instanceId });
        }

        private void OnFocusClicked()
        {
            var me = Me;
            if (!MyPriority || me == null) return;
            if (me.CharacterExhausted) { _toast.Show(UI.Loc.T("Your character is already exhausted.")); return; }
            if (me.FocusedThisTurn) { _toast.Show(UI.Loc.T("You already focused this turn.")); return; }
            if (me.Gems < 1) { _toast.Show(UI.Loc.T("Focus costs 1 gem.")); return; }
            _portrait.SetTapped(true); // the portrait taps visually; the snapshot confirms
            Submit(new ShardsFocusAction { PlayerIndex = MyIndex });
        }

        private void OnRowSlotClicked(int slot)
        {
            if (!MyPriority || _snap == null) return;
            var card = _snap.CenterRow[slot];
            if (card == null) return;
            if (!ShardsCardDatabase.TryGet(card.DefId, out var def)) return;

            if (def.Type == ShardsCardType.Mercenary)
            {
                ShowBuyUsePopup(slot);
                return;
            }
            Submit(new ShardsBuyCardAction { PlayerIndex = MyIndex, SlotIndex = slot });
        }

        /// <summary>Mercenary click: two inline buttons right at the card — BUY (recruit
        /// to your deck) or USE (fast-play once). Clicking anywhere else dismisses.</summary>
        private void ShowBuyUsePopup(int slot)
        {
            if (_buyPopup == null)
            {
                _buyPopup = UiFactory.CreateRect("BuyUsePopup", UiRootRect);
                UiFactory.Stretch(_buyPopup);

                // Full-screen invisible catcher: any outside click closes the popup.
                var catcher = UiFactory.CreateImage("Dismiss", _buyPopup, null, new Color(0f, 0f, 0f, 0.001f), raycast: true);
                UiFactory.Stretch(catcher.rectTransform);
                var dismiss = catcher.gameObject.AddComponent<Button>();
                dismiss.targetGraphic = catcher;
                dismiss.transition = Selectable.Transition.None;
                dismiss.onClick.AddListener(() => _buyPopup.gameObject.SetActive(false));

                var holder = UiFactory.CreateRect("Buttons", _buyPopup);
                holder.sizeDelta = new Vector2(162f, 128f);
                var backdrop = UiFactory.CreatePanel(Theme, "Backdrop", holder, UiPalette.WithAlpha(UiPalette.Background, 0.92f));
                UiFactory.Stretch((RectTransform)backdrop.transform, -6f, -6f, 6f, 6f);

                var buy = UiFactory.CreateButton(Theme, "Buy", holder, UI.Loc.T("BUY"), 19f,
                    UiPalette.Gold, UiPalette.Background);
                UiFactory.Place((RectTransform)buy.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, 31f), new Vector2(150f, 52f));
                buy.onClick.AddListener(() =>
                {
                    _buyPopup.gameObject.SetActive(false);
                    Submit(new ShardsBuyCardAction { PlayerIndex = MyIndex, SlotIndex = _buyPopupSlot });
                });

                var use = UiFactory.CreateButton(Theme, "Use", holder, UI.Loc.T("USE"), 19f,
                    UiPalette.Danger, UiPalette.TextMain);
                UiFactory.Place((RectTransform)use.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, -31f), new Vector2(150f, 52f));
                use.onClick.AddListener(() =>
                {
                    _buyPopup.gameObject.SetActive(false);
                    Submit(new ShardsBuyCardAction { PlayerIndex = MyIndex, SlotIndex = _buyPopupSlot, FastPlay = true });
                });
            }

            _buyPopupSlot = slot;
            var holderRect = (RectTransform)_buyPopup.GetChild(1);
            var slotWorld = _slots[slot].Rect.TransformPoint(_slots[slot].Rect.rect.center);
            Vector2 local = UiRootRect.InverseTransformPoint(slotWorld);
            holderRect.anchoredPosition = local + new Vector2(0f, -10f);
            _buyPopup.gameObject.SetActive(true);
            _buyPopup.SetAsLastSibling();
        }

        private void OnRelicsClicked()
        {
            var me = Me;
            if (me?.SetAside == null || me.SetAside.Count == 0) return;
            var request = new DecisionRequest
            {
                Id = -1,
                PlayerIndex = MyIndex,
                Title = UI.Loc.T("Recruit a relic (free, once per game)"),
                Context = "local.relic",
                Min = 0,
                Max = 1
            };
            foreach (var relic in me.SetAside)
                request.Options.Add(new DecisionOption(relic.InstanceId, DefName(relic.DefId))
                {
                    CardInstanceId = relic.InstanceId,
                    DefId = relic.DefId
                });
            _modal.Show(request, id => OptionLabel(request, id), chosen =>
            {
                if (chosen.Count > 0)
                    Submit(new ShardsRecruitRelicAction { PlayerIndex = MyIndex, CardInstanceId = chosen[0] });
            }, FindDefId, FindZoneName);
        }

        private void ShowDecision(DecisionRequest request)
        {
            _modal.Show(request, id => OptionLabel(request, id), chosen =>
            {
                var answer = new DecisionAnswer { DecisionId = request.Id };
                answer.ChosenOptionIds.AddRange(chosen);
                Submit(new SubmitDecisionAction { PlayerIndex = MyIndex, Answer = answer });
            }, FindDefId, FindZoneName, PlayerInfo);
        }

        /// <summary>Live name/health/portrait per player for the damage-split picker.</summary>
        private (string Name, int Health, int MaxHealth, string PortraitDefId) PlayerInfo(int playerIndex)
        {
            if (_snap == null || playerIndex < 0 || playerIndex >= _snap.Players.Count)
                return ("P" + playerIndex, 0, 0, null);
            var player = _snap.Players[playerIndex];
            return (player.Name, player.Health, _maxHealth, SoiCardFaces.CharacterPrefix + player.CharacterId);
        }

        /// <summary>Where the viewer sees this card instance right now — shown as a
        /// caption under the card in decision grids so multi-zone choices (banish from
        /// hand OR discard) are unambiguous. Null when the zone is unknown/irrelevant.</summary>
        private string FindZoneName(int instanceId)
        {
            if (_snap == null || instanceId <= 0) return null;
            foreach (var c in _snap.CenterRow) if (c != null && c.InstanceId == instanceId) return UI.Loc.T("center row");
            foreach (var c in _snap.DestinyRow) if (c.InstanceId == instanceId) return UI.Loc.T("destiny row");
            foreach (var c in _snap.ActiveMonsters) if (c.InstanceId == instanceId) return UI.Loc.T("ingeminex");
            foreach (var c in _snap.Banished) if (c.InstanceId == instanceId) return UI.Loc.T("banished");
            foreach (var p in _snap.Players)
            {
                bool mine = p.Index == MyIndex;
                if (p.Hand != null)
                    foreach (var c in p.Hand) if (c.InstanceId == instanceId)
                        return mine ? UI.Loc.T("your hand") : NameOf(p.Index) + UI.Loc.T(" — hand");
                if (p.SetAside != null)
                    foreach (var c in p.SetAside) if (c.InstanceId == instanceId) return UI.Loc.T("set aside");
                foreach (var c in p.Discard) if (c.InstanceId == instanceId)
                    return mine ? UI.Loc.T("your discard") : NameOf(p.Index) + UI.Loc.T(" — discard");
                foreach (var c in p.PlayZone) if (c.InstanceId == instanceId)
                    return mine ? UI.Loc.T("played this turn") : NameOf(p.Index) + UI.Loc.T(" — in play");
                foreach (var c in p.Champions) if (c.InstanceId == instanceId)
                    return mine ? UI.Loc.T("your champion") : NameOf(p.Index) + UI.Loc.T(" — champion");
                foreach (var c in p.Destinies) if (c.InstanceId == instanceId)
                    return mine ? UI.Loc.T("your destiny") : NameOf(p.Index) + UI.Loc.T(" — destiny");
            }
            return null;
        }

        /// <summary>Resolve a card instance to its def id through every zone the viewer
        /// can see — fallback for decision options that lack an explicit DefId.</summary>
        private string FindDefId(int instanceId)
        {
            if (_snap == null || instanceId <= 0) return null;
            foreach (var c in _snap.CenterRow) if (c != null && c.InstanceId == instanceId) return c.DefId;
            foreach (var c in _snap.DestinyRow) if (c.InstanceId == instanceId) return c.DefId;
            foreach (var c in _snap.ActiveMonsters) if (c.InstanceId == instanceId) return c.DefId;
            foreach (var c in _snap.Banished) if (c.InstanceId == instanceId) return c.DefId;
            foreach (var p in _snap.Players)
            {
                if (p.Hand != null) foreach (var c in p.Hand) if (c.InstanceId == instanceId) return c.DefId;
                if (p.SetAside != null) foreach (var c in p.SetAside) if (c.InstanceId == instanceId) return c.DefId;
                foreach (var c in p.Discard) if (c.InstanceId == instanceId) return c.DefId;
                foreach (var c in p.PlayZone) if (c.InstanceId == instanceId) return c.DefId;
                foreach (var c in p.Champions) if (c.InstanceId == instanceId) return c.DefId;
                foreach (var c in p.Destinies) if (c.InstanceId == instanceId) return c.DefId;
            }
            return null;
        }

        private static string OptionLabel(DecisionRequest request, int id)
        {
            foreach (var option in request.Options)
                if (option.Id == id)
                    return option.Label;
            return id.ToString();
        }

        // ------------------------------------------------------------------ live rendering (every snapshot)

        private void RefreshHandLive()
        {
            var me = Me;
            if (me == null) return;

            _hand.Render(HandSnaps(me), PlayableIds(me), _pendingReveal.Count > 0 ? _pendingReveal : null);

            _statHealth.text = me.Health + "/" + _maxHealth;
            _statMastery.text = me.Mastery + "/30";
            _statGems.text = me.Gems.ToString();
            _statPower.text = me.Power.ToString();

            _drawPile.Render(me.DeckCount, null);
            _playedPile.Render(me.PlayZone.Count, TopSnap(me.PlayZone));
            _discardPile.Render(me.Discard.Count, TopSnap(me.Discard));
            // Two counters: gold = total banished, green = yours.
            int banishedMine = _snap.Banished.FindAll(c => c.Owner == MyIndex).Count;
            _banishPile.Render(_snap.Banished.Count, TopSnap(_snap.Banished),
                null, banishedMine.ToString());
            _centerDeckPile.Render(_snap.CenterDeckCount, null);

            RefreshControls(); // live turn/button gating (also runs on drain via RefreshInteractivity)
        }

        // ------------------------------------------------------------------ full refresh (on drain)

        private void RefreshAll()
        {
            if (_snap == null) return;
            _pendingReveal.Clear();
            _hiddenSlots.Clear();
            RefreshHandLive();

            string turnOwner = _snap.TurnPlayerIndex == MyIndex
                ? UI.Loc.T("YOUR TURN")
                : UI.Loc.French
                    ? ("TOUR " + UI.Loc.De(NameOf(_snap.TurnPlayerIndex))).ToUpperInvariant()
                    : NameOf(_snap.TurnPlayerIndex).ToUpperInvariant() + "'S TURN";
            _hudTurn.text = (UI.Loc.French ? $"MANCHE {_snap.Round}" : $"ROUND {_snap.Round}") + "  ·  " + turnOwner;
            _hudCounts.text = UI.Loc.French
                ? $"pioche commune {_snap.CenterDeckCount}  ·  bannies {_snap.Banished.Count}"
                : $"center deck {_snap.CenterDeckCount}  ·  banished {_snap.Banished.Count}";

            _boardViews.Clear();
            foreach (var view in _transient)
                if (view != null)
                    Destroy(view.gameObject);
            _transient.Clear();
            foreach (Transform child in _opponentStrip) Destroy(child.gameObject);

            RenderCenterRow();
            RenderOpponents();
            RefreshOpponentDetail();
            RenderDestinyAndMonsters();
            RenderMyBoard();
            RefreshInteractivity();
            RenderGameOver();

            if (_deferredDecision != null && _pending != null &&
                _pending.Kind == PendingInputKind.Decision &&
                _pending.Decision != null && _pending.Decision.Id == _deferredDecision.Id)
            {
                var request = _deferredDecision;
                _deferredDecision = null;
                ShowDecision(request);
            }
            else
            {
                _deferredDecision = null;
            }
        }

        private void RenderCenterRow()
        {
            for (int s = 0; s < _slots.Length && s < _snap.CenterRow.Count; s++)
            {
                var card = _snap.CenterRow[s];
                var slot = _slots[s];
                if (card == null || _hiddenSlots.Contains(s))
                {
                    slot.gameObject.SetActive(false);
                    continue;
                }
                slot.gameObject.SetActive(true);
                slot.BindDef(card.DefId, card.InstanceId);
                ApplyRowGlows(slot, card, s);
                _boardViews[card.InstanceId] = slot;
            }
        }

        /// <summary>Row slot adornments — two independent channels:
        /// inner glow = the card's CONDITION is met right now (faction color; falls
        /// back to the red mercenary marker), outer pulse = the viewer can afford it.</summary>
        private void ApplyRowGlows(CardView slot, ShardsCardSnap card, int slotIndex)
        {
            ShardsCardDatabase.TryGet(card.DefId, out var def);
            bool mercenary = def != null && def.Type == ShardsCardType.Mercenary;
            if (def != null && _conditionGlow.Contains(card.InstanceId))
                slot.SetGlow(true, SoiCardFaces.FactionColor(def.Faction));
            else
                slot.SetGlow(mercenary, UiPalette.Danger);
            slot.SetOuterGlow(_buyable.Contains(slotIndex));
        }

        /// <summary>Gentle shared pulse for every active "affordable" outer halo.</summary>
        private IEnumerator OuterGlowPulseLoop()
        {
            while (true)
            {
                float alpha = 0.3f + 0.28f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                if (_slots != null)
                    foreach (var slot in _slots)
                        if (slot != null && slot.gameObject.activeSelf)
                            slot.SetOuterGlowAlpha(alpha);
                yield return null;
            }
        }

        /// <summary>RowRefilled reveal: bind + pop a slot as its flight lands (the full
        /// render would otherwise pop the whole row in at batch end).</summary>
        private void RevealSlot(int slotIndex)
        {
            _hiddenSlots.Remove(slotIndex);
            if (_snap == null || slotIndex >= _snap.CenterRow.Count) return;
            var card = _snap.CenterRow[slotIndex];
            var slot = _slots[slotIndex];
            if (card == null) { slot.gameObject.SetActive(false); return; }
            slot.gameObject.SetActive(true);
            slot.BindDef(card.DefId, card.InstanceId);
            ApplyRowGlows(slot, card, slotIndex);
            _boardViews[card.InstanceId] = slot;
            if (isActiveAndEnabled)
                StartCoroutine(Tween.Punch(slot.transform, 0.18f, 0.22f));
        }

        private string OpponentStatsLine(ShardsPlayerSnap player) =>
            $"<color=#6FDF8F>{player.Health}/{_maxHealth}</color><sprite name=\"soi_health\">  " +
            $"<color=#D4AF37>{player.Mastery}/30</color><sprite name=\"soi_mastery\">  " +
            $"<color=#73AEF2>{player.Gems}</color><sprite name=\"soi_gem\">  " +
            $"<color=#E06C55>{player.Power}</color><sprite name=\"soi_power\">\n" +
            $"{UI.Loc.T("hand")} {player.HandCount} · {UI.Loc.T("deck")} {player.DeckCount} · {UI.Loc.T("discard")} {player.Discard.Count}";

        /// <summary>Life totals update the instant damage lands — never waiting for the
        /// animation queue to drain.</summary>
        private void UpdateOpponentLine(int playerIndex)
        {
            if (_snap == null || playerIndex < 0 || playerIndex >= _snap.Players.Count) return;
            if (_opponentStatTexts.TryGetValue(playerIndex, out var text) && text != null)
                text.text = OpponentStatsLine(_snap.Players[playerIndex]);
        }

        private void RenderOpponents()
        {
            _opponentPanels.Clear();
            _opponentStatTexts.Clear();
            int count = 0;
            foreach (var player in _snap.Players)
                if (player.Index != MyIndex)
                    count++;
            float x = -(count * 384f - 12f) / 2f; // centered along the top edge
            foreach (var player in _snap.Players)
            {
                if (player.Index == MyIndex) continue;
                var panel = UiFactory.CreatePanel(Theme, "Opp_" + player.Index, _opponentStrip,
                    UiPalette.WithAlpha(UiPalette.Panel, player.Eliminated ? 0.4f : 0.92f));
                var rect = (RectTransform)panel.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, 0f);
                rect.sizeDelta = new Vector2(372f, 168f);
                x += 384f;
                _opponentPanels[player.Index] = rect;

                // Whole panel opens the full detail sheet (portrait, stats, champions,
                // destinies, browsable piles) — the compact strip can't fit everything.
                int detailIndex = player.Index;
                var open = panel.gameObject.AddComponent<Button>();
                open.targetGraphic = panel;
                open.transition = Selectable.Transition.None;
                open.onClick.AddListener(() => ShowOpponentDetail(detailIndex));

                bool theirTurn = _snap.TurnPlayerIndex == player.Index;
                var name = UiFactory.CreateText(Theme, "Name", rect, player.Name +
                        (player.Eliminated ? UI.Loc.T("  · eliminated") : theirTurn ? UI.Loc.T("  ← turn") : ""),
                    16f, theirTurn ? UiPalette.Gold : UiPalette.TextMain, TextAlignmentOptions.Left, FontStyles.Bold);
                UiFactory.Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -15f), new Vector2(350f, 22f));
                name.raycastTarget = false;

                var stats = UiFactory.CreateText(Theme, "Stats", rect, OpponentStatsLine(player),
                    13f, UiPalette.TextDim, TextAlignmentOptions.TopLeft);
                if (Theme.Icons != null) stats.spriteAsset = Theme.Icons;
                UiFactory.Place(stats.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -36f), new Vector2(356f, 40f));
                stats.raycastTarget = false;
                _opponentStatTexts[player.Index] = stats;

                // Mini portrait (same sheet as ours), bottom-left of the panel.
                var portrait = CardViewFactory.Create(rect, Theme, 0.3f);
                portrait.Rect.anchorMin = portrait.Rect.anchorMax = new Vector2(0f, 0f);
                portrait.Rect.pivot = new Vector2(0f, 0f);
                portrait.Rect.anchoredPosition = new Vector2(10f, 4f);
                portrait.BindDef(SoiCardFaces.CharacterPrefix + player.CharacterId);
                portrait.SetTapped(player.CharacterExhausted);
                portrait.SetRaycastable(false);
                if (portrait.Group != null) portrait.Group.blocksRaycasts = false;

                // Champions band, then DESTINIES (smaller) to their right — both
                // compress instead of dropping cards.
                float cx = 84f;
                float championStep = player.Champions.Count > 1
                    ? Mathf.Min(72f, (156f - 66f) / (player.Champions.Count - 1)) : 72f;
                foreach (var champion in player.Champions)
                {
                    var view = CardViewFactory.Create(rect, Theme, 0.3f);
                    view.RotateWhenTapped = false;
                    view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0f);
                    view.Rect.pivot = new Vector2(0f, 0f);
                    view.Rect.anchoredPosition = new Vector2(cx, 4f);
                    cx += championStep;
                    view.BindDef(champion.DefId, champion.InstanceId);
                    view.SetTapped(champion.Exhausted);
                    view.SetMarkedDamage(champion.DamageThisTurn);
                    ApplyBoardGlow(view, champion);
                    int target = player.Index;
                    view.Clicked += w => Submit(new ShardsAttackChampionAction
                    {
                        PlayerIndex = MyIndex,
                        TargetPlayerIndex = target,
                        CardInstanceId = w.InstanceId
                    });
                    _boardViews[champion.InstanceId] = view;
                }

                float dx = 254f;
                float destinyStep = player.Destinies.Count > 1
                    ? Mathf.Min(52f, (110f - 48f) / (player.Destinies.Count - 1)) : 52f;
                foreach (var destiny in player.Destinies)
                {
                    var view = CardViewFactory.Create(rect, Theme, 0.22f);
                    view.RotateWhenTapped = false;
                    view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0f);
                    view.Rect.pivot = new Vector2(0f, 0f);
                    view.Rect.anchoredPosition = new Vector2(dx, 4f);
                    dx += destinyStep;
                    view.BindDef(destiny.DefId, destiny.InstanceId);
                    view.SetTapped(destiny.Exhausted);
                    ApplyBoardGlow(view, destiny);
                    view.Clicked += _ => ShowOpponentDetail(detailIndex); // don't swallow the panel click
                    _boardViews[destiny.InstanceId] = view;
                }
            }
        }

        private void ShowOpponentDetail(int playerIndex)
        {
            if (_snap == null || playerIndex < 0 || playerIndex >= _snap.Players.Count) return;
            _opponentDetail.Show(_snap.Players[playerIndex], _maxHealth, (title, cards) =>
            {
                _cardList.Show(title, cards);
                _cardList.Container.SetAsLastSibling(); // above the detail modal
            });
        }

        /// <summary>Keep an open detail sheet live: re-bind it to the current snapshot
        /// (its stats/rows would otherwise freeze at open time while the compact strip
        /// underneath keeps updating).</summary>
        private void RefreshOpponentDetail()
        {
            if (_opponentDetail == null || !_opponentDetail.Visible) return;
            int shown = _opponentDetail.ShownIndex;
            if (shown < 0 || _snap == null || shown >= _snap.Players.Count) return;
            bool browserOpen = _cardList.Container.gameObject.activeSelf;
            ShowOpponentDetail(shown);
            if (browserOpen)
                _cardList.Container.SetAsLastSibling(); // don't cover an open browser
        }

        private void RenderDestinyAndMonsters()
        {
            foreach (Transform child in _destinyRow) Destroy(child.gameObject);
            foreach (Transform child in _monsterRow) Destroy(child.gameObject);

            bool eligible = MyPriority && Me != null && !Me.DestinyTaken && Me.Mastery >= 5;
            CenterRun(760f, _snap.DestinyRow.Count, out float x, out float xStep, 96f);
            foreach (var destiny in _snap.DestinyRow)
            {
                var view = CardViewFactory.Create(_destinyRow, Theme, 0.4f);
                view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0.5f);
                view.Rect.pivot = new Vector2(0f, 0.5f);
                view.Rect.anchoredPosition = new Vector2(x, 0f);
                x += xStep;
                view.BindDef(destiny.DefId, destiny.InstanceId);
                view.SetGreyed(!eligible);
                view.SetGlow(eligible, UiPalette.HealthyGreen); // takeable right now
                view.Clicked += w =>
                {
                    if (MyPriority && Me != null && !Me.DestinyTaken && Me.Mastery >= 5)
                        Submit(new ShardsTakeDestinyAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                    else
                        _toast.Show(UI.Loc.T("Destinies unlock at Mastery 5 (one per game)."));
                };
                _boardViews[destiny.InstanceId] = view;
            }

            CenterRun(520f, _snap.ActiveMonsters.Count, out x, out xStep, 108f);
            foreach (var monster in _snap.ActiveMonsters)
            {
                var view = CardViewFactory.Create(_monsterRow, Theme, 0.46f);
                view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0.5f);
                view.Rect.pivot = new Vector2(0f, 0.5f);
                view.Rect.anchoredPosition = new Vector2(x, 0f);
                x += xStep;
                view.BindDef(monster.DefId, monster.InstanceId);
                view.SetMarkedDamage(monster.DamageThisTurn);
                ApplyBoardGlow(view, monster);
                view.Clicked += w => Submit(new ShardsAttackMonsterAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                _boardViews[monster.InstanceId] = view;
            }
        }

        private void RenderMyBoard()
        {
            var me = Me;
            if (me == null) return;

            foreach (Transform child in _championRow) Destroy(child.gameObject);
            foreach (Transform child in _ownDestinyRow) Destroy(child.gameObject);

            _portrait.BindDef(SoiCardFaces.CharacterPrefix + me.CharacterId);
            _portrait.SetTapped(me.CharacterExhausted);
            _portrait.SetGreyed(me.CharacterExhausted); // display-only: tapped/greyed when exhausted

            _relics.gameObject.SetActive(me.SetAside != null && me.SetAside.Count > 0 && !me.RelicRecruited);

            // Cards sit centered under the row label; on overflow the spacing
            // compresses (cards overlap) instead of dropping cards.
            CenterRun(620f, me.Champions.Count, out float x, out float xStep);
            foreach (var champion in me.Champions)
            {
                var view = CardViewFactory.Create(_championRow, Theme, 0.44f);
                view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0.5f);
                view.Rect.pivot = new Vector2(0f, 0.5f);
                view.Rect.anchoredPosition = new Vector2(x, 0f);
                x += xStep;
                view.BindDef(champion.DefId, champion.InstanceId);
                view.SetTapped(champion.Exhausted);
                view.SetMarkedDamage(champion.DamageThisTurn);
                ApplyBoardGlow(view, champion);
                view.Clicked += w =>
                {
                    if (MyPriority)
                        Submit(new ShardsExhaustAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                };
                _boardViews[champion.InstanceId] = view;
            }

            CenterRun(560f, me.Destinies.Count, out x, out xStep);
            foreach (var destiny in me.Destinies)
            {
                var view = CardViewFactory.Create(_ownDestinyRow, Theme, 0.44f);
                view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0.5f);
                view.Rect.pivot = new Vector2(0f, 0.5f);
                view.Rect.anchoredPosition = new Vector2(x, 0f);
                x += xStep;
                view.BindDef(destiny.DefId, destiny.InstanceId);
                view.SetTapped(destiny.Exhausted);
                ApplyBoardGlow(view, destiny);
                // Gem-cost-gated exhausts ("Pay N gems, Exhaust:") grey out while
                // unaffordable — the engine rejects the tap as illegal.
                int gemCost = ShardsCardDatabase.TryGet(destiny.DefId, out var destinyDef)
                    ? destinyDef.ExhaustGemCost : 0;
                bool unaffordable = !destiny.Exhausted && gemCost > 0 && me.Gems < gemCost;
                view.SetGreyed(unaffordable);
                view.Clicked += w =>
                {
                    if (!MyPriority) return;
                    if (gemCost > 0 && Me != null && Me.Gems < gemCost)
                    {
                        _toast.Show(UI.Loc.French
                            ? $"Coûte {gemCost} cristaux pour l'activer."
                            : $"Costs {gemCost} gems to activate.");
                        return;
                    }
                    Submit(new ShardsExhaustAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                };
                _boardViews[destiny.InstanceId] = view;
            }
        }

        private void RefreshInteractivity()
        {
            RefreshControls();
            var me = Me;
            if (_snap != null && me != null)
                _hand.Render(HandSnaps(me), PlayableIds(me), _pendingReveal.Count > 0 ? _pendingReveal : null);
        }

        /// <summary>Button/status gating driven by the ALWAYS-FRESH snapshot (turn owner),
        /// not the client's <c>_pending</c> field which goes stale during opponents' turns
        /// (no input is routed to us then). Called live on every snapshot so the END TURN
        /// button disables and reads "NOT YOUR TURN" the instant the turn passes, and
        /// re-enables the instant it returns — not only when the animation queue drains.</summary>
        private void RefreshControls()
        {
            var me = Me;
            bool over = _gameOverShown || (_snap != null && _snap.GameOver);
            bool myTurn = _snap != null && _snap.TurnPlayerIndex == MyIndex;
            bool canAct = myTurn && MyPriority && !over;

            _endTurn.interactable = canAct;
            _endTurnLabel.text = UI.Loc.T(myTurn || over ? "END TURN" : "NOT YOUR TURN");
            _relics.interactable = canAct && me != null && me.Mastery >= 10;
            SetRelicGlow(_relics.gameObject.activeSelf && _relics.interactable);
            _focusButton.interactable = canAct && me != null && me.Gems >= 1 &&
                                        !me.FocusedThisTurn && !me.CharacterExhausted;
            // Snapshot's Pending is fresh + unredacted for all viewers, so this covers
            // both an opponent's whole turn and an opponent's mid-my-turn decision
            // (e.g. shield reveals when I attack).
            bool waitingOther = _snap != null && _snap.Pending != null && _snap.Pending.PlayerIndex != MyIndex;
            _statusLine.text = over || !waitingOther ? "" : UI.Loc.French
                ? "En attente " + UI.Loc.De(NameOf(_snap.Pending.PlayerIndex)) + "…"
                : "Waiting for " + NameOf(_snap.Pending.PlayerIndex) + "…";
        }

        private void RenderGameOver()
        {
            if (!_snap.GameOver || _gameOverShown) return;
            _gameOverShown = true;
            _gameOverPanel.gameObject.SetActive(true);
            _gameOverPanel.SetAsLastSibling();
            _gameOverText.text = _snap.WinnerIndex < 0 ? UI.Loc.T("IT'S A TIE")
                : _snap.WinnerIndex == MyIndex ? UI.Loc.T("VICTORY!")
                : UI.Loc.French
                    ? NameOf(_snap.WinnerIndex).ToUpperInvariant() + " REMPORTE LA PARTIE !"
                    : NameOf(_snap.WinnerIndex).ToUpperInvariant() + " WINS";
        }

        // ------------------------------------------------------------------ event playback

        private IEnumerator PlayEvent(GameEvent e)
        {
            // Kill attribution (see field comment): remember whether the PREVIOUS event
            // was a power hit on a champion — a destroy right after one is an attack.
            int previousChampionHit = _lastChampionHitId;
            _lastChampionHitId = e is ShardsChampionDamagedEvent championHit ? championHit.InstanceId : -1;

            switch (e)
            {
                case ShardsCardPlayedEvent played:
                    return PlayShowcase(played.PlayerIndex, played.DefId);
                case CoalescedDrawEvent draws:
                    return PlayDraws(draws.PlayerIndex, draws.InstanceIds);
                case ShardsCardDrawnEvent drawn:
                    return PlayDraws(drawn.PlayerIndex, new List<int> { drawn.InstanceId });
                case ShardsCardBoughtEvent bought:
                    return PlayBuy(bought);
                case ShardsRowRefilledEvent refilled:
                    return PlayRefill(refilled);
                case ShardsDeckShuffledEvent shuffled:
                    return PlayShuffle(shuffled.PlayerIndex);
                case ShardsMercenaryReturnedEvent merc:
                    return PlayMercReturn(merc);
                case ShardsCleanupEvent cleanup:
                    return PlayCleanup(cleanup.PlayerIndex);
                case ShardsGemsChangedEvent gems:
                    PlayStatFloat(gems.PlayerIndex, gems.Delta, "soi_gem", new Color(0.45f, 0.68f, 0.95f), _statGemsRect);
                    return null;
                case ShardsPowerChangedEvent power:
                    PlayStatFloat(power.PlayerIndex, power.Delta, "soi_power", UiPalette.WoundedRed, _statPowerRect);
                    return null;
                case ShardsMasteryChangedEvent mastery:
                    PlayStatFloat(mastery.PlayerIndex, mastery.Delta, "soi_mastery", UiPalette.Gold, _statMasteryRect);
                    return null;
                case ShardsHealthChangedEvent health:
                    // Damage/health losses get a log entry with old→new HP (gains are
                    // frequent and already visible on the played card + floats).
                    if (health.Delta < 0 && _snap != null &&
                        health.PlayerIndex >= 0 && health.PlayerIndex < _snap.Players.Count)
                        _history.Push(SoiCardFaces.CharacterPrefix + _snap.Players[health.PlayerIndex].CharacterId,
                            health.PlayerIndex,
                            $"−{-health.Delta} · {health.NewValue - health.Delta}→{health.NewValue}");
                    PlayStatFloat(health.PlayerIndex, health.Delta, "soi_health", UiPalette.HealthyGreen, _statHealthRect);
                    return null;
                case ShardsChampionDamagedEvent hit:
                    return PlayChampionHit(hit.InstanceId, hit.Amount);
                case ShardsChampionDestroyedEvent killed:
                    // Destroy WITHOUT a preceding power hit = an effect kill — also show
                    // the victim on its causing card's log entry.
                    if (killed.InstanceId != previousChampionHit && killed.ByPlayerIndex >= 0)
                        _history.AttachAffected(killed.ByPlayerIndex, killed.DefId);
                    _history.Push(killed.DefId,
                        killed.ByPlayerIndex >= 0 ? killed.ByPlayerIndex : killed.OwnerIndex,
                        UI.Loc.T("destroyed"));
                    return PlayChampionDestroyed(killed);
                case ShardsMonsterDamagedEvent monsterHit:
                    return PlayChampionHit(monsterHit.InstanceId, monsterHit.Amount);
                case ShardsMonsterDefeatedEvent defeated:
                    _history.Push(defeated.DefId, defeated.PlayerIndex, UI.Loc.T("defeated"));
                    return PlayMonsterDefeated(defeated);
                case ShardsMonsterRevealedEvent revealed:
                    return PlayMonsterRevealed(revealed);
                case ShardsMonsterAttackedEvent attack:
                    _history.Push(attack.DefId, -1, UI.Loc.T("attacks!"));
                    _toast.ShowBanner(DefName(attack.DefId) + UI.Loc.T(" strikes every player!"));
                    return null;
                case ShardsCardsRevealedEvent reveals:
                    // Reveals are always card-caused (Unify/Dominion, Shard Defiant…):
                    // attach them to their causing entry rather than spamming the log.
                    foreach (string defId in reveals.DefIds)
                        _history.AttachAffected(reveals.PlayerIndex, defId);
                    return null;
                case ShardsDamageAssignedEvent damage:
                    return PlayPlayerDamage(damage);
                case ShardsShieldsRevealedEvent shields:
                    return PlayShields(shields);
                case ShardsCharacterExhaustedEvent exhausted:
                    // Champion/destiny activations are log-worthy causes (their effects
                    // — banish, reveal, fetch — attach to this entry).
                    if (exhausted.CardInstanceId > 0)
                    {
                        string exhaustedDef = FindDefId(exhausted.CardInstanceId);
                        if (exhaustedDef != null)
                            _history.Push(exhaustedDef, exhausted.PlayerIndex, UI.Loc.T("activated"), attachable: true);
                    }
                    return PlayExhaust(exhausted);
                case ShardsFocusedEvent focused:
                    if (focused.PlayerIndex != MyIndex)
                        _toast.Show(NameOf(focused.PlayerIndex) + UI.Loc.T(" focuses."));
                    return null;
                case ShardsDestinyTakenEvent destiny:
                    return PlayShowcase(destiny.PlayerIndex, destiny.DefId);
                case ShardsRelicRecruitedEvent relic:
                    _toast.Show(NameOf(relic.PlayerIndex) + UI.Loc.T(" recruits ") + DefName(relic.DefId));
                    return PlayShowcase(relic.PlayerIndex, relic.DefId);
                case ShardsCardBanishedEvent banished:
                    return PlayBanish(banished);
                case ShardsCardReturnedEvent returned:
                    return PlayReturn(returned);
                case ShardsPlayerEliminatedEvent eliminated:
                    if (_snap != null && eliminated.PlayerIndex >= 0 && eliminated.PlayerIndex < _snap.Players.Count)
                        _history.Push(SoiCardFaces.CharacterPrefix + _snap.Players[eliminated.PlayerIndex].CharacterId,
                            eliminated.PlayerIndex, UI.Loc.T("eliminated"));
                    _toast.ShowBanner(NameOf(eliminated.PlayerIndex) + UI.Loc.T(" has been eliminated!"));
                    return null;
                case ShardsTurnStartedEvent turn:
                    if (turn.PlayerIndex == MyIndex)
                        _toast.ShowBanner(UI.Loc.T("YOUR TURN"));
                    return null;
                default:
                    return null;
            }
        }

        private Vector2 AnchorOf(int playerIndex, RectTransform mine)
        {
            if (playerIndex == MyIndex)
                return _flights.ToLocal(mine != null ? mine : _handRect);
            return _flights.ToLocal(_opponentPanels.TryGetValue(playerIndex, out var panel) ? panel : _opponentStrip);
        }

        private IEnumerator PlayShowcase(int playerIndex, string defId)
        {
            _history.Push(defId, playerIndex, null, attachable: true);
            Vector2 from = AnchorOf(playerIndex, _handRect);
            Vector2 to = playerIndex == MyIndex
                ? _showcase.ToLocal(_playedPile.AnchorRect)
                : AnchorOf(playerIndex, null);
            yield return _showcase.Play(_queue, defId, playerIndex, from, to);
            if (playerIndex == MyIndex) _playedPile.Pulse();
        }

        private IEnumerator PlayDraws(int playerIndex, List<int> instanceIds)
        {
            if (playerIndex != MyIndex)
            {
                Vector2 oppTo = AnchorOf(playerIndex, null);
                yield return _flights.FlyMany(_queue, null, instanceIds.Count,
                    _flights.ToLocal(_drawPile.AnchorRect), oppTo, 0.5f, 0.4f, 0.3f, faceDown: true, stagger: 0.05f);
                yield break;
            }

            Vector2 from = _flights.ToLocal(_drawPile.AnchorRect);
            foreach (int id in instanceIds)
            {
                var target = _hand.CardRect(id);
                Vector2 to = target != null ? _flights.ToLocal(target) : _flights.ToLocal(_handRect);
                StartCoroutine(FlightThenReveal(id, from, to));
                yield return _queue.Wait(0.09f);
            }
            yield return _queue.Wait(0.3f);
        }

        private IEnumerator FlightThenReveal(int instanceId, Vector2 from, Vector2 to)
        {
            yield return _flights.Fly(_queue, null, from, to, 0.5f, 0.75f, 0.32f, faceDown: true);
            _pendingReveal.Remove(instanceId);
            _hand.RevealCard(instanceId);
        }

        private IEnumerator PlayBuy(ShardsCardBoughtEvent bought)
        {
            Vector2 from = bought.SlotIndex >= 0 && bought.SlotIndex < _slots.Length
                ? _flights.ToLocal(_slots[bought.SlotIndex].Rect)
                : _flights.ToLocal(_centerDeckPile.AnchorRect);

            // Free acquisitions are effect-caused (warp, Portal Monk, Shard Defiant's
            // keep…): show the card on its causing entry BEFORE logging its own entry.
            if (bought.CostPaid == 0)
                _history.AttachAffected(bought.PlayerIndex, bought.DefId);

            if (bought.FastPlay)
            {
                _history.Push(bought.DefId, bought.PlayerIndex, null, attachable: true);
                yield return _showcase.Play(_queue, bought.DefId, bought.PlayerIndex, from,
                    bought.PlayerIndex == MyIndex ? _showcase.ToLocal(_playedPile.AnchorRect) : AnchorOf(bought.PlayerIndex, null));
                if (bought.PlayerIndex == MyIndex) _playedPile.Pulse();
                yield break;
            }

            _history.Push(bought.DefId, bought.PlayerIndex, UI.Loc.T("recruited"));

            Vector2 to = bought.PlayerIndex == MyIndex
                ? _flights.ToLocal(_discardPile.AnchorRect)
                : AnchorOf(bought.PlayerIndex, null);
            yield return _flights.Fly(_queue, bought.DefId, from, to, 0.62f, 0.4f, 0.42f);
            if (bought.PlayerIndex == MyIndex) _discardPile.Pulse();
        }

        private IEnumerator PlayRefill(ShardsRowRefilledEvent refilled)
        {
            if (refilled.SlotIndex < 0 || refilled.SlotIndex >= _slots.Length) yield break;
            _hiddenSlots.Add(refilled.SlotIndex);
            _slots[refilled.SlotIndex].gameObject.SetActive(false);
            yield return _flights.Fly(_queue, refilled.DefId,
                _flights.ToLocal(_centerDeckPile.AnchorRect),
                _flights.ToLocal(_slots[refilled.SlotIndex].Rect), 0.45f, 0.62f, 0.3f);
            RevealSlot(refilled.SlotIndex);
        }

        private IEnumerator PlayShuffle(int playerIndex)
        {
            if (playerIndex != MyIndex) yield break;
            yield return _flights.FlyMany(_queue, null, 3,
                _flights.ToLocal(_discardPile.AnchorRect), _flights.ToLocal(_drawPile.AnchorRect),
                0.45f, 0.45f, 0.32f, faceDown: true, stagger: 0.07f);
            _drawPile.Pulse();
        }

        private IEnumerator PlayMercReturn(ShardsMercenaryReturnedEvent merc)
        {
            Vector2 from = merc.PlayerIndex == MyIndex
                ? _flights.ToLocal(_playedPile.AnchorRect)
                : AnchorOf(merc.PlayerIndex, null);
            yield return _flights.Fly(_queue, merc.DefId, from,
                _flights.ToLocal(_centerDeckPile.AnchorRect), 0.45f, 0.4f, 0.4f);
            _centerDeckPile.Pulse();
        }

        private IEnumerator PlayCleanup(int playerIndex)
        {
            if (playerIndex != MyIndex) yield break;
            var me = Me;
            int count = me != null ? Mathf.Max(1, me.PlayZone.Count) : 2;
            yield return _flights.FlyMany(_queue, null, count,
                _flights.ToLocal(_playedPile.AnchorRect), _flights.ToLocal(_discardPile.AnchorRect),
                0.42f, 0.4f, 0.3f, faceDown: false, stagger: 0.06f);
            _discardPile.Pulse();
        }

        private void PlayStatFloat(int playerIndex, int delta, string label, Color color, RectTransform myTile)
        {
            if (delta == 0) return;
            string text = (delta > 0 ? "+" : "−") + Mathf.Abs(delta) + " <sprite name=\"" + label + "\">";
            var tint = delta >= 0 ? color : UiPalette.WoundedRed;
            if (playerIndex == MyIndex)
                _floats.Spawn(_floats.ToLocal(myTile), text, tint, 26f);
            else if (_opponentPanels.TryGetValue(playerIndex, out var panel))
                _floats.Spawn(_floats.ToLocal(panel), text, tint, 22f);
            RefreshHandLive();               // my numbers stay current mid-batch
            UpdateOpponentLine(playerIndex); // and so do the opponents’
        }

        private IEnumerator PlayChampionHit(int instanceId, int amount)
        {
            if (_boardViews.TryGetValue(instanceId, out var view) && view != null)
            {
                _floats.Spawn(_floats.ToLocal(view.Rect), "−" + amount, UiPalette.WoundedRed, 30f);
                _bursts.Burst(_bursts.ToLocal(view.Rect), UiPalette.WoundedRed, 8, 130f);
                view.SetMarkedDamage(amount);
                yield return Tween.Punch(view.transform, 0.2f, 0.18f);
            }
        }

        private IEnumerator PlayChampionDestroyed(ShardsChampionDestroyedEvent killed)
        {
            Vector2 from = _boardViews.TryGetValue(killed.InstanceId, out var view) && view != null
                ? _flights.ToLocal(view.Rect)
                : AnchorOf(killed.OwnerIndex, _championRow);
            if (view != null) view.gameObject.SetActive(false);
            _bursts.Burst(from, UiPalette.WoundedRed, 14, 200f);
            Vector2 to = killed.OwnerIndex == MyIndex
                ? _flights.ToLocal(_discardPile.AnchorRect)
                : AnchorOf(killed.OwnerIndex, null);
            yield return _flights.Fly(_queue, killed.DefId, from, to, 0.44f, 0.36f, 0.4f);
        }

        private IEnumerator PlayMonsterRevealed(ShardsMonsterRevealedEvent revealed)
        {
            _history.Push(revealed.DefId, -1, UI.Loc.T("appears!"));
            _toast.ShowBanner(UI.Loc.T("An Ingeminex appears: ") + DefName(revealed.DefId));
            yield return _showcase.Play(_queue, revealed.DefId, -1,
                _showcase.ToLocal(_centerDeckPile.AnchorRect), _showcase.ToLocal(_monsterRow));
        }

        private IEnumerator PlayMonsterDefeated(ShardsMonsterDefeatedEvent defeated)
        {
            Vector2 from = _boardViews.TryGetValue(defeated.InstanceId, out var view) && view != null
                ? _flights.ToLocal(view.Rect)
                : _flights.ToLocal(_monsterRow);
            if (view != null) view.gameObject.SetActive(false);
            _bursts.Burst(from, UiPalette.Gold, 16, 240f);
            yield return _flights.Fly(_queue, defeated.DefId, from,
                _flights.ToLocal(_centerDeckPile.AnchorRect), 0.5f, 0.4f, 0.45f);
        }

        private IEnumerator PlayPlayerDamage(ShardsDamageAssignedEvent damage)
        {
            for (int i = 0; i < damage.Targets.Count; i++)
            {
                int target = damage.Targets[i];
                int amount = damage.Amounts[i];
                Vector2 at = target == MyIndex
                    ? _floats.ToLocal(_statHealthRect)
                    : (_opponentPanels.TryGetValue(target, out var panel) ? _floats.ToLocal(panel) : Vector2.zero);
                _floats.Spawn(at, "−" + amount, UiPalette.WoundedRed, 34f);
                _bursts.Burst(at, UiPalette.WoundedRed, 12, 190f);
                RefreshHandLive();
                UpdateOpponentLine(target); // life updates instantly, not post-batch
                if (target != MyIndex && _opponentPanels.TryGetValue(target, out var hitPanel))
                    StartCoroutine(Tween.Punch(hitPanel, 0.12f, 0.2f));
            }
            yield return _queue.Wait(0.35f);
        }

        private IEnumerator PlayShields(ShardsShieldsRevealedEvent shields)
        {
            if (shields.DefIds.Count > 0)
            {
                _history.Push(shields.DefIds[0], shields.PlayerIndex,
                    UI.Loc.T("blocks ") + shields.Prevented, attachable: true);
                for (int i = 1; i < shields.DefIds.Count; i++)
                    _history.AttachAffected(shields.PlayerIndex, shields.DefIds[i]);
            }
            Vector2 at = shields.PlayerIndex == MyIndex
                ? _floats.ToLocal(_statHealthRect)
                : (_opponentPanels.TryGetValue(shields.PlayerIndex, out var panel) ? _floats.ToLocal(panel) : Vector2.zero);
            _floats.Spawn(at, UI.Loc.T("blocked ") + shields.Prevented, new Color(0.7f, 0.72f, 0.78f), 28f);
            if (shields.DefIds.Count > 0)
                yield return _showcase.Play(_queue, shields.DefIds[0], shields.PlayerIndex, at, at);
            if (shields.DefIds.Count > 1)
                _toast.Show(NameOf(shields.PlayerIndex) + UI.Loc.T(" reveals ") + shields.DefIds.Count + UI.Loc.T(" shields — blocks ") + shields.Prevented);
        }

        private IEnumerator PlayExhaust(ShardsCharacterExhaustedEvent exhausted)
        {
            // The tap rotation itself lands with the re-render; this is the click feedback.
            if (exhausted.CardInstanceId < 0)
            {
                if (exhausted.PlayerIndex == MyIndex)
                {
                    _portrait.SetTapped(true);
                    _bursts.Burst(_bursts.ToLocal(_portrait.Rect), UiPalette.Gold, 8, 120f);
                    yield return Tween.Punch(_portrait.transform, 0.16f, 0.15f);
                }
                yield break;
            }
            if (_boardViews.TryGetValue(exhausted.CardInstanceId, out var view) && view != null)
            {
                view.SetTapped(true);
                _bursts.Burst(_bursts.ToLocal(view.Rect), UiPalette.Gold, 8, 120f);
                yield return Tween.Punch(view.transform, 0.16f, 0.15f);
            }
        }

        private IEnumerator PlayBanish(ShardsCardBanishedEvent banished)
        {
            // Banishes are card-caused: attach the victim to its cause; standalone
            // entry only when no cause is on the log.
            if (!_history.AttachAffected(banished.PlayerIndex, banished.DefId))
                _history.Push(banished.DefId, banished.PlayerIndex, UI.Loc.T("banished"));
            Vector2 from = banished.PlayerIndex == MyIndex
                ? _flights.ToLocal(_discardPile.AnchorRect)
                : AnchorOf(banished.PlayerIndex, null);
            yield return _flights.Fly(_queue, banished.DefId, from,
                _flights.ToLocal(_banishPile.AnchorRect), 0.42f, 0.38f, 0.4f);
            _banishPile.Pulse();
        }

        private IEnumerator PlayReturn(ShardsCardReturnedEvent returned)
        {
            _history.AttachAffected(returned.PlayerIndex, returned.DefId);
            if (returned.PlayerIndex != MyIndex) yield break;
            yield return _flights.Fly(_queue, returned.DefId,
                _flights.ToLocal(_discardPile.AnchorRect), _flights.ToLocal(_handRect), 0.42f, 0.6f, 0.38f);
        }

        // ------------------------------------------------------------------ hover preview

        private void OnAnyCardHovered(CardView view, bool entered)
        {
            if (view == _preview || _hand.IsDragging) return;
            if (!entered || string.IsNullOrEmpty(view.DefId))
                _preview.gameObject.SetActive(false);
            else
            {
                _preview.gameObject.SetActive(true);
                _preview.transform.SetAsLastSibling();
                _preview.BindDef(view.DefId);
            }
            BroadcastLocalHover(view, entered);
        }

        /// <summary>Tell everyone which PUBLIC board card we're pointing at — only while
        /// it's our turn (receivers additionally gate on the turn owner). Hand cards are
        /// hidden information and never broadcast.</summary>
        private void BroadcastLocalHover(CardView view, bool entered)
        {
            if (_snap == null || _snap.TurnPlayerIndex != MyIndex) return;
            int hoverId = entered && view.InstanceId > 0 && _boardViews.ContainsKey(view.InstanceId)
                ? view.InstanceId : -1;
            if (hoverId == _lastHoverSent) return;
            _lastHoverSent = hoverId;
            GameNetBridge.Instance?.SendCardHover(MyIndex, hoverId);
        }

        private void OnRemoteHover(int seat, int instanceId)
        {
            if (seat == MyIndex) return; // our own echo — the local hover already shows
            _remoteHoverSeat = seat;
            _remoteHoverId = instanceId;
        }

        /// <summary>Marker follow pass: board views are destroyed/rebuilt on every full
        /// refresh, so resolve the hovered instance to its CURRENT view each frame.</summary>
        private void LateUpdate()
        {
            if (_remoteHoverMark == null) return;
            bool show = _remoteHoverId > 0 && _snap != null &&
                        _remoteHoverSeat == _snap.TurnPlayerIndex &&
                        _boardViews.TryGetValue(_remoteHoverId, out var view) &&
                        view != null && view.gameObject.activeInHierarchy;
            if (!show)
            {
                _remoteHoverMark.gameObject.SetActive(false);
                return;
            }

            var target = _boardViews[_remoteHoverId];
            _remoteHoverMark.gameObject.SetActive(true);
            Vector2 local = UiRootRect.InverseTransformPoint(
                target.Rect.TransformPoint(target.Rect.rect.center));
            _remoteHoverMark.anchoredPosition = local;
            float k = UiRootRect.lossyScale.x > 0f
                ? target.Rect.lossyScale.x / UiRootRect.lossyScale.x : 1f;
            _remoteHoverMark.sizeDelta = new Vector2(CardView.Width * k + 16f, CardView.Height * k + 16f);
            float pulse = 0.28f + 0.18f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 4f));
            _remoteHoverImg.color = UiPalette.WithAlpha(UiPalette.PlayerColor(_remoteHoverSeat), pulse);
        }

        // ------------------------------------------------------------------ data helpers

        private List<CardSnap> HandSnaps(ShardsPlayerSnap me)
        {
            var result = new List<CardSnap>();
            if (me.Hand == null) return result;
            foreach (var card in me.Hand)
                result.Add(Synth(card.DefId, card.InstanceId));
            return result;
        }

        private HashSet<int> PlayableIds(ShardsPlayerSnap me)
        {
            var result = new HashSet<int>();
            if (me.Hand != null && MyPriority)
                foreach (var card in me.Hand)
                    result.Add(card.InstanceId);
            return result;
        }

        /// <summary>Steady faction-color glow for hand cards whose condition is met
        /// (Unify/Dominion/thresholds/If) — fed to HandView.GlowResolver.</summary>
        private Color? HandGlowFor(int instanceId)
        {
            if (_snap == null || !_conditionGlow.Contains(instanceId)) return null;
            var me = Me;
            if (me?.Hand == null) return null;
            foreach (var card in me.Hand)
                if (card.InstanceId == instanceId && ShardsCardDatabase.TryGet(card.DefId, out var def))
                    return SoiCardFaces.FactionColor(def.Faction);
            return null;
        }

        /// <summary>In-play glow for champions/destinies/Ingeminex: red = the viewer can
        /// kill it right now (wins over everything), faction color = its exhaust
        /// condition is met (ready cards only, host-checked).</summary>
        private void ApplyBoardGlow(CardView view, ShardsCardSnap card)
        {
            if (_killable.Contains(card.InstanceId))
                view.SetGlow(true, UiPalette.WoundedRed);
            else if (!card.Exhausted && _conditionGlow.Contains(card.InstanceId) &&
                     ShardsCardDatabase.TryGet(card.DefId, out var def))
                view.SetGlow(true, SoiCardFaces.FactionColor(def.Faction));
            else
                view.SetGlow(false);
        }

        /// <summary>Synthetic CardSnap: DefId + InstanceId only — CardView resolves SoI
        /// ids through the external face resolver, so every shared widget renders them.</summary>
        private static CardSnap Synth(string defId, int instanceId) => new CardSnap
        {
            DefId = defId,
            InstanceId = instanceId,
            EffectiveCost = -1
        };

        private static CardSnap TopSnap(List<ShardsCardSnap> zone) =>
            zone != null && zone.Count > 0 ? Synth(zone[zone.Count - 1].DefId, zone[zone.Count - 1].InstanceId) : null;

        private static List<CardSnap> ZoneSnaps(List<ShardsCardSnap> zone)
        {
            var result = new List<CardSnap>();
            if (zone == null) return result;
            foreach (var card in zone)
                result.Add(Synth(card.DefId, card.InstanceId));
            result.Sort((a, b) => string.CompareOrdinal(DefName(a.DefId), DefName(b.DefId)));
            return result;
        }

        private void ShowPile(string title, List<CardSnap> cards)
        {
            if (cards == null || cards.Count == 0)
            {
                _toast.Show(UI.Loc.T("Nothing there yet."));
                return;
            }
            _cardList.Show(title, cards);
        }

        /// <summary>Banished cards grouped by owning player (market cards last), so
        /// "who banished what" is readable at a glance.</summary>
        private void ShowBanishedByPlayer()
        {
            if (_snap == null || _snap.Banished.Count == 0)
            {
                _toast.Show(UI.Loc.T("Nothing there yet."));
                return;
            }

            var groups = new List<(string, IReadOnlyList<CardSnap>)>();
            foreach (var player in _snap.Players)
            {
                var mine = _snap.Banished.FindAll(c => c.Owner == player.Index);
                if (mine.Count > 0)
                    groups.Add((player.Name, ZoneSnaps(mine)));
            }
            var market = _snap.Banished.FindAll(c => c.Owner < 0);
            if (market.Count > 0)
                groups.Add((UI.Loc.T("center row"), ZoneSnaps(market)));

            _cardList.ShowGroups(UI.Loc.T("Banished (removed from the game)"), groups);
        }

        private string NameOf(int playerIndex) =>
            _snap != null && playerIndex >= 0 && playerIndex < _snap.Players.Count
                ? _snap.Players[playerIndex].Name : "P" + playerIndex;

        private static string DefName(string defId) =>
            ShardsCardDatabase.TryGet(defId, out var def) ? UI.Loc.CardName(defId, def.Name) : defId;
    }
}
