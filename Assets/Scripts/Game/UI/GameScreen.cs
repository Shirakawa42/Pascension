using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;
using Pascension.Game.Presentation;
using Pascension.Game.View;
using Pascension.Net;
using UnityEngine;

namespace Pascension.Game.UI
{
    /// <summary>
    /// The game scene controller: routes session events into the PresentationQueue and
    /// ClientGameView, refreshes every view from the latest snapshot when the queue
    /// drains, converts clicks into PlayerActions picked from the pending legal list,
    /// and drives the decision modal / response window / targeting shortcut.
    /// Contains zero rules logic — legality comes exclusively from PendingSnap.
    /// </summary>
    public sealed class GameScreen : MonoBehaviour
    {
        [Header("Wired by SceneBuilder")]
        public UiTheme Theme;
        public PresentationQueue Queue;
        public HandView Hand;
        public MarketView Market;
        public BoardTrackView Board;
        public PlayerSheetView PlayerSheet;
        public RectTransform OpponentsBar;
        public StackPanelView StackPanel;
        public ResponseWindowView ResponseWindow;
        public DecisionModalView DecisionModal;
        public TargetingArrow Arrow;
        public LogPanel Log;
        public GameOverPanel GameOver;
        public ToastView Toast;
        public CardListModal CardList;
        [Tooltip("Container for the persistent END TURN / PASS button (built at runtime).")]
        public RectTransform ActionBar;
        public PileWidget DrawPile;
        public PileWidget PlayedPile;
        public PileWidget DiscardPile;
        public PileWidget ExilePile;
        public PlayHistoryBar History;
        public OpponentDetailModal OpponentDetail;
        public FlightLayer Flights;
        public GlowBurstLayer Bursts;
        public FloatingNumberLayer Floats;
        public CardShowcase Showcase;

        public ClientGameView View { get; } = new ClientGameView();

        private ISession _session;
        private GameRules _rules;
        private bool _fullControl;
        private readonly List<OpponentSheetView> _opponentSheets = new List<OpponentSheetView>();
        private readonly List<int> _opponentIndices = new List<int>();
        private readonly Dictionary<TargetRef, int> _targetShortcuts = new Dictionary<TargetRef, int>();
        private int _lastAutoPassSeq = -1;
        private bool _gameOverShown;
        private UnityEngine.UI.Button _turnButton;
        private TMPro.TextMeshProUGUI _turnLabel;
        private RectTransform _moveRow;
        private StackArrows _stackArrows;
        private CardView _preview;

        // ------------------------------------------------------------------ binding

        public void Bind(ISession session, GameRules rules)
        {
            _session = session;
            _rules = rules;
            _fullControl = PlayerPrefs.GetInt(SceneFlow.PrefFullControl, 0) == 1;

            Market.Init(Theme, rules);
            Board.Init(Theme, rules);
            PlayerSheet.Init(Theme, rules);
            StackPanel.Init(Theme);
            ResponseWindow.Init(Theme);
            DecisionModal.Init(Theme);
            Arrow.Init(Theme);
            Log.Init(Theme);
            GameOver.Init(Theme);
            Toast.Init(Theme);
            CardList.Init(Theme);
            if (History != null) History.Init(Theme);
            if (Flights != null) Flights.Init(Theme);
            if (Bursts != null) Bursts.Init(Theme);
            if (Floats != null) Floats.Init(Theme);
            if (Showcase != null) Showcase.Init(Theme, Bursts);
            if (DrawPile != null) DrawPile.Init(Theme, "Draw", faceDown: true);
            if (PlayedPile != null) PlayedPile.Init(Theme, "Played", faceDown: false);
            if (DiscardPile != null) DiscardPile.Init(Theme, "Discard", faceDown: false);
            if (ExilePile != null) ExilePile.Init(Theme, "Exile", faceDown: false);
            if (OpponentDetail != null)
            {
                OpponentDetail.Init(Theme);
                OpponentDetail.BrowseRequested += (title, cards) => CardList.Show(title, SortedByName(cards));
            }
            if (DrawPile != null) DrawPile.Clicked += () => ShowPile("Draw pile (alphabetical, order hidden)", Me()?.Deck);
            if (PlayedPile != null) PlayedPile.Clicked += () => ShowPile("Played this turn", Me()?.PlayedThisTurn);
            if (DiscardPile != null) DiscardPile.Clicked += () => ShowPile("Discard pile", Me()?.Discard);
            if (ExilePile != null) ExilePile.Clicked += () => ShowPile("Exile", Me()?.Exile);

            session.SnapshotReceived += OnSnapshot;
            session.EventsReceived += OnEvents;
            session.InputRequested += OnInputRequested;
            session.ActionRejected += OnActionRejected;

            Queue.EventPlayer = PlayEvent;
            Queue.Drained += RefreshAll;

            // Stack-target arrows: above the table views, below the response window.
            // (ResponseWindow.Container is a nested rect — use the canvas-level root's index.)
            var responseRoot = transform.Find("ResponseWindow");
            _stackArrows = StackArrows.Create(transform, Theme,
                responseRoot != null ? responseRoot.GetSiblingIndex() : transform.childCount - 1);

            // Large hover preview so small card text is always readable (created last = on top).
            _preview = CardViewFactory.Create(transform, Theme, 1.3f);
            _preview.Rect.anchorMin = _preview.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            _preview.Rect.pivot = new Vector2(0.5f, 0.5f);
            _preview.SetRaycastable(false);
            _preview.Group.blocksRaycasts = false;
            _preview.Group.interactable = false;
            _preview.gameObject.SetActive(false);
            CardView.AnyHovered += OnAnyCardHovered;

            Hand.CardClicked += OnHandCardClicked;
            Market.SlotClicked += OnMarketSlotClicked;
            Board.NodeClicked += OnNodeClicked;
            Board.BossClicked += OnBossClicked;
            StackPanel.ItemClicked += OnStackItemClicked;
            PlayerSheet.EquipmentClicked += OnEquipmentClicked;
            PlayerSheet.HeroAbilityClicked += OnHeroAbilityClicked;
            PlayerSheet.ZoneClicked += OnZoneClicked;
            ResponseWindow.PassClicked += OnPassClicked;

            if (ActionBar != null)
            {
                _turnButton = UiFactory.CreateButton(Theme, "TurnButton", ActionBar, "END TURN", 21f,
                    UiPalette.Gold, UiPalette.Background);
                UiFactory.Stretch((RectTransform)_turnButton.transform);
                _turnButton.onClick.AddListener(OnPassClicked);
                _turnLabel = UiFactory.ButtonLabel(_turnButton);
                _turnButton.interactable = false;

                // Quick-move buttons (the track's bottom nodes can sit behind the hand fan).
                _moveRow = UiFactory.CreateRect("MoveRow", ActionBar);
                _moveRow.anchorMin = new Vector2(0f, 1f);
                _moveRow.anchorMax = new Vector2(1f, 1f);
                _moveRow.pivot = new Vector2(0.5f, 0f);
                _moveRow.anchoredPosition = new Vector2(0f, 8f);
                _moveRow.sizeDelta = new Vector2(0f, 40f);
                var moveLayout = _moveRow.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                moveLayout.spacing = 4f;
                moveLayout.childAlignment = TextAnchor.MiddleRight;
                moveLayout.childControlWidth = false;
                moveLayout.childControlHeight = false;
                moveLayout.childForceExpandWidth = false;
                moveLayout.childForceExpandHeight = false;
            }
        }

        private void OnDestroy() => CardView.AnyHovered -= OnAnyCardHovered;

        private PlayerSnap Me()
        {
            var s = View.Snapshot;
            return s != null ? s.Players[s.ViewerIndex] : null;
        }

        private void RenderPiles(PlayerSnap me)
        {
            if (me == null) return;
            DrawPile?.Render(me.DeckCount, null);
            PlayedPile?.Render(me.PlayedThisTurn.Count, LastOf(me.PlayedThisTurn));
            DiscardPile?.Render(me.Discard.Count, LastOf(me.Discard));
            ExilePile?.Render(me.Exile.Count, LastOf(me.Exile));
        }

        private static CardSnap LastOf(List<CardSnap> list) =>
            list != null && list.Count > 0 ? list[list.Count - 1] : null;

        private void ShowPile(string title, List<CardSnap> cards)
        {
            if (cards == null) return;
            CardList.Show($"{title} — {cards.Count}", SortedByName(cards));
        }

        /// <summary>Alphabetical browse order for every pile (hides draw order by design).</summary>
        private static List<CardSnap> SortedByName(IEnumerable<CardSnap> cards)
        {
            var sorted = new List<CardSnap>(cards);
            sorted.Sort((a, b) =>
            {
                string an = Engine.Cards.CardDatabase.TryGet(a.DefId ?? "", out var ad) ? ad.Name : a.DefId ?? "";
                string bn = Engine.Cards.CardDatabase.TryGet(b.DefId ?? "", out var bd) ? bd.Name : b.DefId ?? "";
                int byName = string.CompareOrdinal(an, bn);
                return byName != 0 ? byName : a.InstanceId.CompareTo(b.InstanceId);
            });
            return sorted;
        }

        private string _previewSourceId;

        private void OnAnyCardHovered(CardView card, bool entered)
        {
            if (_preview == null || card == _preview) return;
            string key = card.GetInstanceID().ToString();
            if (entered)
            {
                if (string.IsNullOrEmpty(card.DefId)) return; // hidden card / card back
                _previewSourceId = key;
                _preview.BindDef(card.DefId);
                // Show on the opposite side of the hovered card so it never covers it.
                float sideX = transform.InverseTransformPoint(card.transform.position).x;
                _preview.Rect.anchoredPosition = new Vector2(sideX > 0f ? -430f : 430f, 30f);
                _preview.gameObject.SetActive(true);
                _preview.Rect.SetAsLastSibling();
            }
            else if (_previewSourceId == key)
            {
                _previewSourceId = null;
                _preview.gameObject.SetActive(false);
            }
        }

        /// <summary>Arrows from each stack item's controller to its targets — everyone sees
        /// exactly what is being attacked/targeted while responses are possible.</summary>
        private void RenderStackArrows(ClientSnapshot s)
        {
            if (_stackArrows == null) return;
            var entries = new List<StackArrows.Entry>();
            foreach (var item in s.Stack)
            {
                if (item.Targets == null || item.Targets.Count == 0) continue;
                var from = SheetAnchor(item.ControllerIndex, s.ViewerIndex);
                if (from == null) continue;
                foreach (var t in item.Targets)
                {
                    RectTransform to = null;
                    switch (t.Kind)
                    {
                        case TargetKind.MonsterSlot: to = Market.SlotRect(t.A - 1, t.B); break;
                        case TargetKind.Boss: to = Board.BossRect; break;
                        case TargetKind.Player: to = SheetAnchor(t.A, s.ViewerIndex); break;
                    }
                    if (to == null) continue;
                    var color = item.Kind == "DamageAssignment" ? UiPalette.Danger : UiPalette.TargetBlue;
                    entries.Add(new StackArrows.Entry(ArrowLocal(from), ArrowLocal(to), color));
                }
            }
            _stackArrows.Render(entries);
        }

        private RectTransform SheetAnchor(int playerIndex, int viewerIndex)
        {
            if (playerIndex == viewerIndex) return PlayerSheet.Container;
            for (int i = 0; i < _opponentIndices.Count; i++)
                if (_opponentIndices[i] == playerIndex)
                    return (RectTransform)_opponentSheets[i].transform;
            return null;
        }

        private Vector2 ArrowLocal(RectTransform rt)
        {
            var world = rt.TransformPoint(rt.rect.center);
            return _stackArrows.Container.InverseTransformPoint(world);
        }

        private void UpdateMoveButtons(List<int> legalSteps)
        {
            if (_moveRow == null) return;
            for (int i = _moveRow.childCount - 1; i >= 0; i--)
                Destroy(_moveRow.GetChild(i).gameObject);
            if (legalSteps.Count == 0) return;

            legalSteps.Sort();
            var shown = new List<int>();
            if (legalSteps.Count <= 5)
                shown.AddRange(legalSteps);
            else
            {
                for (int i = 0; i < 4; i++) shown.Add(legalSteps[i]);
                shown.Add(legalSteps[legalSteps.Count - 1]);
            }

            foreach (int steps in shown)
            {
                int s = steps;
                var button = UiFactory.CreateButton(Theme, $"Move{s}", _moveRow, "+" + s, 15f);
                ((RectTransform)button.transform).sizeDelta = new Vector2(38f, 38f);
                button.onClick.AddListener(() => TrySubmit<MoveStepsAction>(a => a.Steps == s));
            }
        }

        // ------------------------------------------------------------------ session events

        private void OnSnapshot(ClientSnapshot snapshot)
        {
            View.Apply(snapshot);
            if (Queue == null || Queue.IsIdle)
                RefreshAll();
        }

        private void OnEvents(List<GameEvent> batch) => Queue.Enqueue(batch);

        private void OnInputRequested(PendingSnap pending)
        {
            // Snapshot carries the same data; nothing to do beyond a refresh opportunity.
        }

        private void OnActionRejected(string error)
        {
            Toast.Show(error);
            Log.Append("<i>Rejected: " + error + "</i>");
        }

        // ------------------------------------------------------------------ full refresh

        private void RefreshAll()
        {
            var s = View.Snapshot;
            if (s == null) return;

            bool queueIdle = Queue == null || Queue.IsIdle;
            var pending = s.Pending;
            var me = s.Players[s.ViewerIndex];

            bool localPriority = queueIdle && pending != null &&
                                 pending.Kind == PendingInputKind.Priority &&
                                 pending.PlayerIndex == s.ViewerIndex &&
                                 pending.LegalActions != null;
            var decision = queueIdle && pending != null &&
                           pending.Kind == PendingInputKind.Decision &&
                           pending.PlayerIndex == s.ViewerIndex
                ? pending.Decision
                : null;

            // ---- affordances from the legal action list ----
            var playable = new HashSet<int>();
            var buyable = new HashSet<(int, int)>();
            var attackable = new HashSet<(int, int)>();
            var reachable = new HashSet<int>();
            var legalMoveSteps = new List<int>();
            var equipActivatable = new HashSet<int>();
            bool bossAttackable = false, heroActive = false, heroUltimate = false, onlyPass = true;

            if (localPriority)
            {
                foreach (var action in pending.LegalActions)
                {
                    if (!(action is PassPriorityAction)) onlyPass = false;
                    switch (action)
                    {
                        case PlayCardAction play:
                            playable.Add(play.CardInstanceId);
                            break;
                        case BuyCardAction buy:
                            buyable.Add((buy.TierIndex, buy.SlotIndex));
                            break;
                        case AssignDamageAction attack when attack.Target.Kind == TargetKind.MonsterSlot:
                            attackable.Add((attack.Target.A - 1, attack.Target.B));
                            break;
                        case AssignDamageAction attack when attack.Target.Kind == TargetKind.Boss:
                            bossAttackable = true;
                            break;
                        case MoveStepsAction move:
                            reachable.Add(me.Position + move.Steps);
                            legalMoveSteps.Add(move.Steps);
                            break;
                        case ActivateAbilityAction act:
                            equipActivatable.Add(act.SourceInstanceId);
                            break;
                        case UseHeroAbilityAction hero:
                            if (hero.Ultimate) heroUltimate = true;
                            else heroActive = true;
                            break;
                    }
                }
            }

            // ---- targeting-shortcut map for single-pick ChooseTargets decisions ----
            _targetShortcuts.Clear();
            var targetableSlots = new HashSet<(int, int)>();
            var targetableStack = new HashSet<int>();
            bool bossTargetable = false;
            bool targetingShortcut = decision != null &&
                                     decision.Kind == DecisionKind.ChooseTargets &&
                                     decision.Min == 1 && decision.Max == 1 && !decision.Ordered;
            if (targetingShortcut)
            {
                foreach (var option in decision.Options)
                {
                    if (!option.Target.HasValue) continue;
                    var t = option.Target.Value;
                    _targetShortcuts[t] = option.Id;
                    switch (t.Kind)
                    {
                        case TargetKind.MonsterSlot:
                            targetableSlots.Add((t.A - 1, t.B));
                            break;
                        case TargetKind.Boss:
                            bossTargetable = true;
                            break;
                        case TargetKind.StackItem:
                            targetableStack.Add(t.A);
                            break;
                    }
                }
                Arrow.Show(new Vector2(0f, -420f));
            }
            else
            {
                Arrow.Hide();
            }

            // ---- render everything from the snapshot ----
            Hand.Render(me.Hand, playable);
            Market.Render(s, me.Level, buyable, attackable, targetableSlots);
            Board.Render(s, reachable);
            Board.SetBossGlow(bossTargetable || bossAttackable,
                bossTargetable ? UiPalette.TargetBlue : UiPalette.Danger);
            PlayerSheet.Render(me, equipActivatable, heroActive, heroUltimate);
            StackPanel.Render(s, targetableStack);
            RenderOpponents(s);
            RenderStackArrows(s);
            RenderPiles(me);
            UpdateMoveButtons(legalMoveSteps);

            // ---- response window (no timer — players take as long as they need) ----
            if (localPriority && s.Stack.Count > 0)
            {
                if (!ResponseWindow.IsShown)
                    ResponseWindow.Show();
                Hand.SetPulse(playable);
            }
            else
            {
                ResponseWindow.Hide();
                Hand.SetPulse(null);
            }

            // ---- persistent END TURN / PASS button ----
            if (_turnButton != null)
            {
                bool myMainPhase = localPriority && s.TurnPlayerIndex == s.ViewerIndex &&
                                   s.Phase == Phase.Main && s.Stack.Count == 0;
                _turnButton.interactable = localPriority;
                _turnLabel.text = !localPriority ? "WAITING..."
                    : myMainPhase ? "END TURN"
                    : "PASS";
            }

            // ---- decision modal ----
            if (decision != null)
            {
                var request = decision;
                DecisionModal.Show(request, View,
                    picks => SubmitDecision(request, picks),
                    blocking: !targetingShortcut);
            }
            else
            {
                DecisionModal.Hide();
            }

            // ---- auto-pass safety net (host fast-pass covers non-full-control seats) ----
            if (localPriority && onlyPass && !_fullControl && _lastAutoPassSeq != s.EventSeq)
            {
                _lastAutoPassSeq = s.EventSeq;
                TrySubmit<PassPriorityAction>(_ => true);
            }

            // ---- game over ----
            if (s.GameOver && queueIdle && !_gameOverShown)
            {
                _gameOverShown = true;
                string winnerName = null;
                string winnerHero = null;
                if (s.WinnerIndex >= 0 && s.WinnerIndex < s.Players.Count)
                {
                    winnerName = s.Players[s.WinnerIndex].Name;
                    winnerHero = s.Players[s.WinnerIndex].HeroId;
                }
                GameOver.Show(winnerName, winnerHero, s.WinnerIndex == s.ViewerIndex);
            }
        }

        private void RenderOpponents(ClientSnapshot s)
        {
            // Lazily create one sheet per non-local player, in seat order.
            if (_opponentSheets.Count == 0)
            {
                for (int i = 0; i < s.Players.Count; i++)
                {
                    if (i == s.ViewerIndex) continue;
                    var sheet = OpponentSheetView.Create(OpponentsBar, Theme, i);
                    int playerIndex = i;
                    sheet.Clicked += () =>
                    {
                        var snap = View.Snapshot;
                        if (snap != null && OpponentDetail != null)
                            OpponentDetail.Show(snap.Players[playerIndex], _rules);
                    };
                    _opponentSheets.Add(sheet);
                    _opponentIndices.Add(i);
                }
            }
            for (int k = 0; k < _opponentSheets.Count; k++)
            {
                int playerIndex = _opponentIndices[k];
                _opponentSheets[k].Render(s.Players[playerIndex], s.TurnPlayerIndex == playerIndex);
            }
        }

        // ------------------------------------------------------------------ click handlers

        private void OnHandCardClicked(int instanceId)
        {
            if (View.Snapshot == null) return;
            if (!TrySubmit<PlayCardAction>(a => a.CardInstanceId == instanceId))
                Toast.Show("You can't play that right now.");
        }

        private void OnMarketSlotClicked(int tierIndex, int slotIndex)
        {
            var target = TargetRef.Monster(tierIndex + 1, slotIndex);
            if (_targetShortcuts.TryGetValue(target, out int optionId))
            {
                SubmitShortcutTarget(optionId);
                return;
            }
            if (TrySubmit<BuyCardAction>(a => a.TierIndex == tierIndex && a.SlotIndex == slotIndex))
                return;
            if (TrySubmit<AssignDamageAction>(a =>
                    a.Target.Kind == TargetKind.MonsterSlot &&
                    a.Target.A == tierIndex + 1 && a.Target.B == slotIndex))
                return;
            // Not actionable — quietly ignore (hover states already communicate legality).
        }

        private void OnNodeClicked(int step)
        {
            var me = View.LocalPlayer;
            if (me == null) return;
            int steps = step - me.Position;
            if (steps > 0)
                TrySubmit<MoveStepsAction>(a => a.Steps == steps);
        }

        private void OnBossClicked()
        {
            if (_targetShortcuts.TryGetValue(TargetRef.TheBoss(), out int optionId))
            {
                SubmitShortcutTarget(optionId);
                return;
            }
            if (TrySubmit<AssignDamageAction>(a => a.Target.Kind == TargetKind.Boss))
                return;
            // The final step has no track node of its own — clicking the boss moves there.
            var me = View.LocalPlayer;
            if (me != null && _rules != null)
                TrySubmit<MoveStepsAction>(a => a.Steps == _rules.BoardSteps - me.Position);
        }

        private void OnStackItemClicked(int stackItemId)
        {
            if (_targetShortcuts.TryGetValue(TargetRef.Spell(stackItemId), out int optionId))
                SubmitShortcutTarget(optionId);
        }

        private void OnEquipmentClicked(int instanceId)
        {
            if (_targetShortcuts.TryGetValue(TargetRef.CardById(instanceId), out int optionId))
            {
                SubmitShortcutTarget(optionId);
                return;
            }
            if (!TrySubmit<ActivateAbilityAction>(a => a.SourceInstanceId == instanceId))
                Toast.Show("That ability isn't available right now.");
        }

        private void OnHeroAbilityClicked(bool ultimate)
        {
            if (!TrySubmit<UseHeroAbilityAction>(a => a.Ultimate == ultimate))
                Toast.Show("Hero ability not available right now.");
        }

        private void OnZoneClicked(string zone)
        {
            var me = View.LocalPlayer;
            if (me == null) return;
            switch (zone)
            {
                case "Deck":
                    CardList.Show($"Deck — {me.DeckCount} cards (alphabetical, order hidden)", me.Deck);
                    break;
                case "Discard":
                    CardList.Show("Discard", me.Discard);
                    break;
                case "Exile":
                    CardList.Show("Exile", me.Exile);
                    break;
                case "Played":
                    CardList.Show("Played this turn", me.PlayedThisTurn);
                    break;
                case "Relics":
                    CardList.Show("Relics", me.Relics);
                    break;
            }
        }

        private void OnPassClicked() => TrySubmit<PassPriorityAction>(_ => true);

        // ------------------------------------------------------------------ submission

        /// <summary>Submit the first pending legal action of type T matching the predicate.</summary>
        private bool TrySubmit<T>(Func<T, bool> match) where T : PlayerAction
        {
            var pending = View.Snapshot?.Pending;
            if (pending == null || pending.LegalActions == null ||
                pending.PlayerIndex != View.LocalPlayerIndex)
                return false;
            foreach (var action in pending.LegalActions)
            {
                if (action is T typed && match(typed))
                {
                    _session.SubmitAction(typed);
                    return true;
                }
            }
            return false;
        }

        private void SubmitDecision(DecisionRequest request, List<int> optionIds)
        {
            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(optionIds);
            _session.SubmitAction(new SubmitDecisionAction { Answer = answer });
        }

        private void SubmitShortcutTarget(int optionId)
        {
            var pending = View.Snapshot?.Pending;
            if (pending?.Decision == null) return;
            DecisionModal.Hide();
            Arrow.Hide();
            SubmitDecision(pending.Decision, new List<int> { optionId });
        }

        // ------------------------------------------------------------------ event playback

        // ------------------------------------------------------------------ event presentation

        /// <summary>Destination of the most recent buy (Express Delivery sends it to hand).</summary>
        private ZoneType _lastBuyDestination = ZoneType.Discard;

        private bool IsViewer(int playerIndex) => playerIndex == View.LocalPlayerIndex;

        /// <summary>Flight anchor for a zone. All opponent-owned zones route to their sheet.</summary>
        private RectTransform AnchorFor(ZoneType zone, int owner)
        {
            if (owner >= 0 && !IsViewer(owner))
                return SheetAnchor(owner, View.LocalPlayerIndex) ?? PlayerSheet.Container;
            switch (zone)
            {
                case ZoneType.Deck: return DrawPile != null ? DrawPile.AnchorRect : PlayerSheet.Container;
                case ZoneType.Hand: return Hand.Container;
                case ZoneType.Discard: return DiscardPile != null ? DiscardPile.AnchorRect : PlayerSheet.Container;
                case ZoneType.Exile: return ExilePile != null ? ExilePile.AnchorRect : PlayerSheet.Container;
                case ZoneType.PlayedThisTurn: return PlayedPile != null ? PlayedPile.AnchorRect : PlayerSheet.Container;
                default: return PlayerSheet.Container;
            }
        }

        /// <summary>Floating number over an anchor; silently skipped before the anchor exists.</summary>
        private void SpawnFloat(RectTransform anchor, string text, Color color, float size)
        {
            if (anchor == null || Floats == null) return;
            Floats.Spawn(Floats.ToLocal(anchor), text, color, size);
        }

        private PileWidget PileFor(ZoneType zone) => zone switch
        {
            ZoneType.Deck => DrawPile,
            ZoneType.Discard => DiscardPile,
            ZoneType.Exile => ExilePile,
            ZoneType.PlayedThisTurn => PlayedPile,
            _ => null
        };

        private IEnumerator PlayEvent(GameEvent e)
        {
            string line = EventText.Describe(e, View.Snapshot, View.LocalPlayerIndex);
            if (line != null)
                Log.Append(line);

            switch (e)
            {
                case TurnStartedEvent ts:
                    Toast.ShowBanner(EventText.Name(View.Snapshot, View.LocalPlayerIndex, ts.PlayerIndex) +
                                     (ts.PlayerIndex == View.LocalPlayerIndex ? "r turn" : "'s turn"));
                    return Queue.Wait(0.5f);

                case CoalescedDrawEvent cd:
                    return PlayDraw(cd.PlayerIndex, cd.Count);

                case CardDrawnEvent drawn:
                    return PlayDraw(drawn.PlayerIndex, 1);

                case CardPlayedEvent played: // mana ability — off-stack play
                    return PlayShowcase(played.DefId, played.PlayerIndex, played.InstanceId,
                        AnchorFor(ZoneType.PlayedThisTurn, played.PlayerIndex));

                case StackPushedEvent sp when sp.Kind == "Spell" && sp.DefId != null:
                    return PlayShowcase(sp.DefId, sp.ControllerIndex, sp.SourceInstanceId,
                        StackPanel.Container);

                case StackPushedEvent:
                    return Queue.Wait(0.3f);

                case CardMovedEvent cm:
                    return PlayCardMoved(cm);

                case DeckShuffledEvent ds:
                    return PlayReshuffle(ds.PlayerIndex);

                case CardBoughtEvent buy:
                    return PlayBuy(buy);

                case MarketRefilledEvent mr when mr.InstanceId >= 0:
                    Market.SetSlotHidden((int)mr.Tier - 1, mr.SlotIndex, true);
                    return PlayRefill(mr);

                case PlayerMovedEvent moved:
                    return PlayMove(moved);

                case InnReachedEvent inn:
                    Bursts.Burst(Bursts.ToLocal(Board.NodeRect(inn.InnStep)), UiPalette.Gold, 14, 220f);
                    return Queue.Wait(0.3f);

                case DamageMarkedEvent dm:
                    return PlayImpact(dm);

                case MonsterDiedEvent md:
                {
                    Market.FlashSlot((int)md.Tier - 1, md.SlotIndex, UiPalette.Gold);
                    var slot = Market.SlotRect((int)md.Tier - 1, md.SlotIndex);
                    Bursts.Burst(Bursts.ToLocal(slot), UiPalette.Gold, 18, 300f);
                    return Queue.Wait(0.45f);
                }

                case ApChangedEvent ap when ap.Delta > 0:
                    SpawnFloat(IsViewer(ap.PlayerIndex)
                            ? PlayerSheet.ApCrystalRect : SheetAnchor(ap.PlayerIndex, View.LocalPlayerIndex),
                        $"+{ap.Delta} AP", UiPalette.Gold, 26f);
                    return null;

                case DamagePoolChangedEvent dp when dp.Delta > 0:
                    SpawnFloat(IsViewer(dp.PlayerIndex)
                            ? PlayerSheet.DamageCrystalRect : SheetAnchor(dp.PlayerIndex, View.LocalPlayerIndex),
                        $"+{dp.Delta} DMG", UiPalette.WoundedRed, 26f);
                    return null;

                case XpGainedEvent xp:
                    SpawnFloat(IsViewer(xp.PlayerIndex)
                            ? PlayerSheet.XpBarRect : SheetAnchor(xp.PlayerIndex, View.LocalPlayerIndex),
                        $"+{xp.Amount} XP", UiPalette.HealthyGreen, 26f);
                    return Queue.Wait(0.1f);

                case LeveledUpEvent lu:
                {
                    var anchor = IsViewer(lu.PlayerIndex)
                        ? PlayerSheet.Container : SheetAnchor(lu.PlayerIndex, View.LocalPlayerIndex);
                    Bursts.Burst(Bursts.ToLocal(anchor), UiPalette.Gold, 22, 340f);
                    Toast.ShowBanner($"{EventText.Name(View.Snapshot, View.LocalPlayerIndex, lu.PlayerIndex)} — Level {lu.NewLevel}!");
                    return Queue.Wait(0.4f);
                }

                case SpellCounteredEvent:
                    Bursts.Burst(Bursts.ToLocal(StackPanel.Container), UiPalette.TargetBlue, 16, 300f);
                    return Queue.Wait(0.4f);

                case StackFizzledEvent:
                    return Queue.Wait(0.25f);

                case ExtraTurnEvent:
                case PlayerConcededEvent:
                case GameEndedEvent:
                    return Queue.Wait(0.45f);

                default:
                    return null;
            }
        }

        private IEnumerator PlayMove(PlayerMovedEvent moved)
        {
            yield return Board.AnimatePawn(moved.PlayerIndex, moved.FromStep, moved.ToStep, Queue);
            yield return Queue.Wait(0.1f);
        }

        private IEnumerator PlayDraw(int playerIndex, int count)
        {
            if (IsViewer(playerIndex))
            {
                var from = Flights.ToLocal(AnchorFor(ZoneType.Deck, playerIndex));
                var to = Flights.ToLocal(Hand.Container) + new Vector2(0f, 60f);
                DrawPile?.Pulse();
                yield return Flights.FlyMany(Queue, null, count, from, to, 0.5f, 0.8f, 0.28f, faceDown: true, stagger: 0.07f);
            }
            else
            {
                // A quick card-back pop over the opponent's sheet.
                var sheet = SheetAnchor(playerIndex, View.LocalPlayerIndex);
                var at = Flights.ToLocal(sheet);
                yield return Flights.Fly(Queue, null, at, at + new Vector2(0f, 46f), 0.28f, 0.36f, 0.22f, faceDown: true);
            }
        }

        private IEnumerator PlayShowcase(string defId, int playerIndex, int sourceInstanceId, RectTransform dest)
        {
            Vector2 from;
            if (IsViewer(playerIndex))
            {
                var handRect = Hand.HideCard(sourceInstanceId);
                from = Showcase.ToLocal(handRect != null ? handRect : Hand.Container);
            }
            else
            {
                from = Showcase.ToLocal(SheetAnchor(playerIndex, View.LocalPlayerIndex));
            }
            History?.Push(defId, playerIndex);
            yield return Showcase.Play(Queue, defId, playerIndex, from, Showcase.ToLocal(dest));
        }

        private IEnumerator PlayCardMoved(CardMovedEvent cm)
        {
            bool viewerOwned = IsViewer(cm.OwnerIndex);

            switch (cm.From, cm.To)
            {
                case (ZoneType.Hand, ZoneType.Stack):
                    if (viewerOwned) Hand.HideCard(cm.InstanceId);
                    return null; // the StackPushed showcase carries the visual

                case (ZoneType.Hand, ZoneType.PlayedThisTurn):
                    return null; // CardPlayedEvent (mana ability) owns this

                case (ZoneType.MarketRow, _):
                    _lastBuyDestination = cm.To; // CardBought (with slot coords) owns the flight
                    return null;

                case (_, ZoneType.Pile):
                    return Fly(cm.DefId, AnchorFor(cm.From, cm.OwnerIndex), Market.PileLabelRect(1), 0.5f, 0.35f, 0.22f);
            }

            // Same-anchor flights (opponent zone → opponent zone) have nothing to show.
            var fromRect = cm.From == ZoneType.Stack ? StackPanel.Container : AnchorFor(cm.From, cm.OwnerIndex);
            var toRect = AnchorFor(cm.To, cm.OwnerIndex);
            if (fromRect == toRect)
                return null;

            float duration = cm.From == ZoneType.Hand && cm.To == ZoneType.Discard ? 0.16f : 0.24f;
            var tint = cm.To == ZoneType.Exile ? (Color?)UiPalette.Gold : null;
            PileFor(cm.To)?.Pulse();
            if (viewerOwned && cm.From == ZoneType.Hand)
                Hand.HideCard(cm.InstanceId);
            return Fly(cm.DefId, fromRect, toRect, 0.6f, 0.4f, duration, tint);
        }

        private IEnumerator Fly(string defId, RectTransform from, RectTransform to,
            float fromScale, float toScale, float duration, Color? tint = null)
        {
            yield return Flights.Fly(Queue, defId, Flights.ToLocal(from), Flights.ToLocal(to),
                fromScale, toScale, duration, faceDown: defId == null, tint: tint);
        }

        private IEnumerator PlayReshuffle(int playerIndex)
        {
            if (!IsViewer(playerIndex))
                yield break;
            var from = Flights.ToLocal(DiscardPile != null ? DiscardPile.AnchorRect : PlayerSheet.Container);
            var to = Flights.ToLocal(DrawPile != null ? DrawPile.AnchorRect : PlayerSheet.Container);
            DiscardPile?.Pulse();
            yield return Flights.FlyMany(Queue, null, 4, from, to, 0.5f, 0.5f, 0.3f, faceDown: true, stagger: 0.06f);
            DrawPile?.Pulse();
        }

        private IEnumerator PlayBuy(CardBoughtEvent buy)
        {
            int tierIndex = (int)buy.Tier - 1;
            Market.PunchSlot(tierIndex, buy.SlotIndex);
            Market.SetSlotHidden(tierIndex, buy.SlotIndex, true);
            var slotRect = Market.SlotRect(tierIndex, buy.SlotIndex);
            if (buy.CostPaid > 0)
                Floats.Spawn(Floats.ToLocal(slotRect), $"-{buy.CostPaid} AP", UiPalette.Gold, 26f);

            var destZone = IsViewer(buy.PlayerIndex) && _lastBuyDestination == ZoneType.Hand
                ? ZoneType.Hand : ZoneType.Discard;
            _lastBuyDestination = ZoneType.Discard;
            var dest = AnchorFor(destZone, buy.PlayerIndex);
            PileFor(destZone)?.Pulse();
            yield return Fly(buy.DefId, slotRect, dest, 0.5f, 0.4f, 0.3f);
        }

        private IEnumerator PlayRefill(MarketRefilledEvent mr)
        {
            int tierIndex = (int)mr.Tier - 1;
            yield return Fly(null, Market.PileLabelRect(tierIndex), Market.SlotRect(tierIndex, mr.SlotIndex),
                0.35f, 0.5f, 0.2f);
            Market.SetSlotHidden(tierIndex, mr.SlotIndex, false);
        }

        private IEnumerator PlayImpact(DamageMarkedEvent dm)
        {
            RectTransform target = null;
            if (dm.Target.Kind == TargetKind.MonsterSlot)
            {
                Market.FlashSlot(dm.Target.A - 1, dm.Target.B, UiPalette.Danger);
                target = Market.SlotRect(dm.Target.A - 1, dm.Target.B);
            }
            else if (dm.Target.Kind == TargetKind.Boss)
            {
                Board.FlashBoss();
                target = Board.BossRect;
            }

            if (target != null)
            {
                var local = Bursts.ToLocal(target);
                var attacker = SheetAnchor(dm.ByPlayerIndex, View.LocalPlayerIndex) ?? PlayerSheet.Container;
                var dir = (local - Bursts.ToLocal(attacker)).normalized;
                Bursts.Burst(local, UiPalette.WoundedRed, 14, 320f, dir);
                Floats.Spawn(Floats.ToLocal(target), $"-{dm.Amount}", UiPalette.WoundedRed, 34f);
            }
            return Queue.Wait(0.45f);
        }
    }
}
