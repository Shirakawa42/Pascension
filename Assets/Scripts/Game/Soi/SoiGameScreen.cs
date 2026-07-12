using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Game.View;
using Pascension.Net;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// The Shards of Infinity table. Builds ALL of its UI at runtime in Bind (the
    /// LobbyScreen pattern — nothing to serialize, view Init at runtime only) and
    /// re-renders every zone from each snapshot: SoI has no response windows or
    /// speculative staging, so a straight rebuild is correct and simple. Zero rules
    /// logic here — it renders ShardsSnapshot and submits PlayerActions.
    /// </summary>
    public sealed class SoiGameScreen : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform UiRootRect;

        private ISession _session;
        private ShardsSnapshot _snap;
        private PendingSnap _pending;
        private bool _gameOverShown;

        // Static chrome (built once).
        private TextMeshProUGUI _hudTurn;
        private TextMeshProUGUI _hudCounts;
        private TextMeshProUGUI _statHealth, _statMastery, _statGems, _statPower;
        private Button _endTurn, _focus, _relics;
        private TextMeshProUGUI _statusLine;
        private RectTransform _centerRow, _destinyRow, _monsterRow;
        private RectTransform _handRow, _championRow, _playZoneRow, _destinyOwnRow;
        private RectTransform _opponentStrip;
        private SoiDecisionModal _modal;
        private ToastView _toast;
        private PauseOverlayView _pauseOverlay;
        private SoiCardWidget _preview;
        private RectTransform _gameOverPanel;
        private TextMeshProUGUI _gameOverText;

        private readonly List<SoiCardWidget> _widgets = new List<SoiCardWidget>();

        // ------------------------------------------------------------------ bind

        public void Bind(ISession session, object rules)
        {
            _session = session;

            BuildLayout();

            session.SnapshotReceived += OnSnapshot;
            session.EventsReceived += OnEvents;
            session.InputRequested += OnInputRequested;
            session.ActionRejected += OnActionRejected;

            _pauseOverlay = PauseOverlayView.Create(UiRootRect, Theme);
            session.PauseChanged += OnPauseChanged;
            NetEvents.LocalClientDisconnected += OnLocalClientDisconnected;
            if (session is NetworkSession netSession && netSession.CurrentPause != null &&
                netSession.CurrentPause.Paused)
                OnPauseChanged(netSession.CurrentPause);

            SoiCardWidget.AnyHovered += OnCardHovered;
        }

        private void OnDestroy()
        {
            SoiCardWidget.AnyHovered -= OnCardHovered;
            NetEvents.LocalClientDisconnected -= OnLocalClientDisconnected;
            if (_session != null)
                _session.PauseChanged -= OnPauseChanged;
        }

        // ------------------------------------------------------------------ layout (built once)

        private void BuildLayout()
        {
            var root = UiRootRect;

            // HUD (top-right).
            _hudTurn = UiFactory.CreateText(Theme, "HudTurn", root, "", 20f, UiPalette.Gold,
                TextAlignmentOptions.Right, FontStyles.Bold);
            UiFactory.Place(_hudTurn.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -26f), new Vector2(500f, 30f));
            _hudCounts = UiFactory.CreateText(Theme, "HudCounts", root, "", 14f, UiPalette.TextDim,
                TextAlignmentOptions.Right);
            UiFactory.Place(_hudCounts.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -54f), new Vector2(500f, 22f));

            _statusLine = UiFactory.CreateText(Theme, "Status", root, "", 17f, UiPalette.GoldDim,
                TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(_statusLine.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(900f, 26f));

            // Opponents (top-left).
            _opponentStrip = UiFactory.CreateRect("Opponents", root);
            UiFactory.Place(_opponentStrip, new Vector2(0f, 1f), new Vector2(14f, -12f), new Vector2(1200f, 160f));
            _opponentStrip.pivot = new Vector2(0f, 1f);

            // Center row (market).
            var rowLabel = UiFactory.CreateText(Theme, "RowLabel", root, "CENTER ROW", 13f, UiPalette.TextDim,
                TextAlignmentOptions.Left, FontStyles.Bold);
            UiFactory.Place(rowLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(-560f, 190f), new Vector2(200f, 20f));
            _centerRow = UiFactory.CreateRect("CenterRow", root);
            UiFactory.Place(_centerRow, new Vector2(0.5f, 0.5f), new Vector2(0f, 66f), new Vector2(1100f, 214f));

            // Destiny row + Ingeminex (only populated with ItH).
            _destinyRow = UiFactory.CreateRect("DestinyRow", root);
            UiFactory.Place(_destinyRow, new Vector2(0.5f, 0.5f), new Vector2(-220f, -78f), new Vector2(900f, 54f));
            _monsterRow = UiFactory.CreateRect("MonsterRow", root);
            UiFactory.Place(_monsterRow, new Vector2(0.5f, 0.5f), new Vector2(430f, -78f), new Vector2(420f, 60f));

            // Player stats block (bottom-left).
            _statHealth = Stat(root, "HEALTH", new Vector2(20f, -150f), UiPalette.HealthyGreen, out _);
            _statMastery = Stat(root, "MASTERY", new Vector2(180f, -150f), UiPalette.Gold, out _);
            _statGems = Stat(root, "GEMS", new Vector2(340f, -150f), new Color(0.45f, 0.68f, 0.95f), out _);
            _statPower = Stat(root, "POWER", new Vector2(500f, -150f), UiPalette.WoundedRed, out _);

            _focus = UiFactory.CreateButton(Theme, "Focus", root, "FOCUS\n(1 gem → 1 mastery)", 14f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_focus.transform, new Vector2(0f, 0.5f), new Vector2(670f, -150f), new Vector2(200f, 78f));
            ((RectTransform)_focus.transform).pivot = new Vector2(0f, 0.5f);
            _focus.onClick.AddListener(() => Submit(new ShardsFocusAction { PlayerIndex = MyIndex }));

            _relics = UiFactory.CreateButton(Theme, "Relics", root, "RELIC", 14f);
            UiFactory.Place((RectTransform)_relics.transform, new Vector2(0f, 0.5f), new Vector2(890f, -150f), new Vector2(150f, 78f));
            ((RectTransform)_relics.transform).pivot = new Vector2(0f, 0.5f);
            _relics.onClick.AddListener(OnRelicsClicked);
            _relics.gameObject.SetActive(false);

            _endTurn = UiFactory.CreateButton(Theme, "EndTurn", root, "END TURN", 21f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_endTurn.transform, new Vector2(1f, 0.5f), new Vector2(-110f, -150f), new Vector2(190f, 78f));
            _endTurn.onClick.AddListener(() => Submit(new ShardsEndTurnAction { PlayerIndex = MyIndex }));

            // My board rows.
            _championRow = LabeledRow(root, "MY CHAMPIONS (click to exhaust)", new Vector2(-690f, 120f), new Vector2(500f, 96f));
            _playZoneRow = LabeledRow(root, "PLAYED THIS TURN", new Vector2(430f, -390f), new Vector2(560f, 70f));
            _destinyOwnRow = LabeledRow(root, "MY DESTINIES", new Vector2(430f, -470f), new Vector2(560f, 60f));

            _handRow = UiFactory.CreateRect("Hand", root);
            UiFactory.Place(_handRow, new Vector2(0.5f, 0f), new Vector2(-260f, 100f), new Vector2(1300f, 220f));

            // Shared services.
            _modal = SoiDecisionModal.Create(root, Theme);

            var toastRect = UiFactory.CreateRect("ToastHost", root);
            UiFactory.Place(toastRect, new Vector2(0.5f, 0f), new Vector2(0f, 360f), new Vector2(600f, 60f));
            _toast = toastRect.gameObject.AddComponent<ToastView>();
            _toast.Container = toastRect;
            _toast.Init(Theme);

            // Fixed hover preview (top-left, below the opponent strip).
            _preview = SoiCardWidget.Create(root, Theme, new Vector2(280f, 392f));
            _preview.Rect.anchorMin = _preview.Rect.anchorMax = new Vector2(0f, 1f);
            _preview.Rect.pivot = new Vector2(0f, 1f);
            _preview.Rect.anchoredPosition = new Vector2(18f, -186f);
            var previewGroup = _preview.GetComponent<CanvasGroup>();
            previewGroup.blocksRaycasts = false;
            previewGroup.interactable = false;
            _preview.gameObject.SetActive(false);

            // Game-over panel (simple, self-contained).
            _gameOverPanel = UiFactory.CreateRect("GameOver", root);
            UiFactory.Stretch(_gameOverPanel);
            UiFactory.CreateDimmer("Dimmer", _gameOverPanel);
            var goPanel = UiFactory.CreatePanel(Theme, "Panel", _gameOverPanel, UiPalette.Panel);
            UiFactory.Place((RectTransform)goPanel.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 300f));
            _gameOverText = UiFactory.CreateText(Theme, "Text", goPanel.transform, "", 30f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_gameOverText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(520f, 90f));
            var menuButton = UiFactory.CreateButton(Theme, "Menu", goPanel.transform, "BACK TO MENU", 18f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)menuButton.transform, new Vector2(0.5f, 0f), new Vector2(0f, 46f), new Vector2(260f, 56f));
            menuButton.onClick.AddListener(() =>
            {
                SessionProvider.Current = null;
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
            });
            _gameOverPanel.gameObject.SetActive(false);
        }

        private TextMeshProUGUI Stat(RectTransform root, string label, Vector2 pos, Color color,
            out RectTransform rect)
        {
            rect = UiFactory.CreateRect("Stat_" + label, root);
            UiFactory.Place(rect, new Vector2(0f, 0.5f), pos, new Vector2(150f, 78f));
            var bg = UiFactory.CreatePanel(Theme, "Bg", rect, UiPalette.WithAlpha(UiPalette.Panel, 0.9f));
            UiFactory.Stretch((RectTransform)bg.transform);
            var caption = UiFactory.CreateText(Theme, "Caption", rect, label, 12f, UiPalette.TextDim,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(caption.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(150f, 18f));
            var value = UiFactory.CreateText(Theme, "Value", rect, "0", 30f, color,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(value.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(146f, 44f));
            value.enableAutoSizing = true;
            value.fontSizeMin = 14f;
            value.fontSizeMax = 30f;
            return value;
        }

        private RectTransform LabeledRow(RectTransform root, string label, Vector2 pos, Vector2 size)
        {
            var caption = UiFactory.CreateText(Theme, "Label_" + label, root, label, 11f, UiPalette.TextDim,
                TextAlignmentOptions.Left, FontStyles.Bold);
            UiFactory.Place(caption.rectTransform, new Vector2(0.5f, 0.5f),
                pos + new Vector2(0f, size.y / 2f + 12f), new Vector2(size.x, 16f));
            caption.alignment = TextAlignmentOptions.Center;
            var row = UiFactory.CreateRect("Row_" + label, root);
            UiFactory.Place(row, new Vector2(0.5f, 0.5f), pos, size);
            return row;
        }

        // ------------------------------------------------------------------ session events

        private int MyIndex => _session.LocalPlayerIndex;
        private ShardsPlayerSnap Me => _snap != null ? _snap.Players[MyIndex] : null;
        private bool MyTurn => _snap != null && _snap.TurnPlayerIndex == MyIndex;
        private bool MyPriority => _pending != null && _pending.PlayerIndex == MyIndex &&
                                   _pending.Kind == PendingInputKind.Priority;

        private void OnSnapshot(SnapshotBase snapshotBase)
        {
            _snap = snapshotBase as ShardsSnapshot;
            if (_snap == null) return;
            RenderAll();
        }

        private void OnEvents(List<GameEvent> batch)
        {
            foreach (var e in batch)
            {
                switch (e)
                {
                    case ShardsPlayerEliminatedEvent elim when _snap != null:
                        _toast.ShowBanner(NameOf(elim.PlayerIndex) + " has been eliminated!");
                        break;
                    case ShardsMonsterRevealedEvent monster:
                        _toast.ShowBanner("An Ingeminex appears: " + DefName(monster.DefId));
                        break;
                    case ShardsShieldsRevealedEvent shields:
                        _toast.Show(NameOf(shields.PlayerIndex) + " reveals shields — prevents " + shields.Prevented);
                        break;
                    case ShardsDestinyTakenEvent destiny:
                        _toast.Show(NameOf(destiny.PlayerIndex) + " takes a destiny: " + DefName(destiny.DefId));
                        break;
                    case ShardsRelicRecruitedEvent relic:
                        _toast.Show(NameOf(relic.PlayerIndex) + " recruits " + DefName(relic.DefId));
                        break;
                }
            }
        }

        private void OnInputRequested(PendingSnap pending)
        {
            _pending = pending;
            if (pending != null && pending.Kind == PendingInputKind.Decision &&
                pending.PlayerIndex == MyIndex && pending.Decision != null)
            {
                ShowDecision(pending.Decision);
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

        // ------------------------------------------------------------------ decisions

        private void ShowDecision(DecisionRequest request)
        {
            _modal.Show(request, id => OptionLabel(request, id), chosen =>
            {
                var answer = new DecisionAnswer { DecisionId = request.Id };
                answer.ChosenOptionIds.AddRange(chosen);
                Submit(new SubmitDecisionAction { PlayerIndex = MyIndex, Answer = answer });
            });
        }

        private string OptionLabel(DecisionRequest request, int id)
        {
            foreach (var option in request.Options)
                if (option.Id == id)
                    return option.Label;
            return id.ToString();
        }

        private void OnRelicsClicked()
        {
            var me = Me;
            if (me?.SetAside == null || me.SetAside.Count == 0) return;
            var request = new DecisionRequest
            {
                Id = -1,
                PlayerIndex = MyIndex,
                Title = "Recruit a relic (free, once per game)",
                Context = "local.relic",
                Min = 0,
                Max = 1
            };
            foreach (var relic in me.SetAside)
                request.Options.Add(new DecisionOption(relic.InstanceId, DefName(relic.DefId)));
            _modal.Show(request, id => OptionLabel(request, id), chosen =>
            {
                if (chosen.Count > 0)
                    Submit(new ShardsRecruitRelicAction { PlayerIndex = MyIndex, CardInstanceId = chosen[0] });
            });
        }

        // ------------------------------------------------------------------ rendering

        private void RenderAll()
        {
            if (_snap == null) return;
            ClearWidgets();

            RenderHud();
            RenderOpponents();
            RenderCenterRow();
            RenderDestinyAndMonsters();
            RenderMyBoard();
            RenderHand();
            RefreshInteractivity();
            RenderGameOver();
        }

        private void ClearWidgets()
        {
            _widgets.Clear();
            foreach (var row in new[] { _centerRow, _destinyRow, _monsterRow, _handRow, _championRow, _playZoneRow, _destinyOwnRow, _opponentStrip })
                foreach (Transform child in row)
                    Destroy(child.gameObject);
        }

        private void RenderHud()
        {
            _hudTurn.text = $"ROUND {_snap.Round}  ·  {(MyTurn ? "YOUR TURN" : NameOf(_snap.TurnPlayerIndex) + "'S TURN")}";
            _hudCounts.text = $"center deck {_snap.CenterDeckCount}  ·  banished {_snap.Banished.Count}";
        }

        private void RenderOpponents()
        {
            float x = 0f;
            foreach (var player in _snap.Players)
            {
                if (player.Index == MyIndex) continue;
                var panel = UiFactory.CreatePanel(Theme, "Opp_" + player.Index, _opponentStrip,
                    UiPalette.WithAlpha(UiPalette.Panel, player.Eliminated ? 0.4f : 0.92f));
                var rect = (RectTransform)panel.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, 0f);
                rect.sizeDelta = new Vector2(370f, 156f);
                x += 382f;

                bool theirTurn = _snap.TurnPlayerIndex == player.Index;
                var name = UiFactory.CreateText(Theme, "Name", rect, player.Name +
                        (player.Eliminated ? "  (eliminated)" : theirTurn ? "  ← turn" : ""),
                    16f, theirTurn ? UiPalette.Gold : UiPalette.TextMain, TextAlignmentOptions.Left, FontStyles.Bold);
                UiFactory.Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -16f), new Vector2(340f, 22f));

                var stats = UiFactory.CreateText(Theme, "Stats", rect,
                    $"HP {player.Health}   M {player.Mastery}   hand {player.HandCount}   deck {player.DeckCount}   discard {player.Discard.Count}",
                    13f, UiPalette.TextDim, TextAlignmentOptions.Left);
                UiFactory.Place(stats.rectTransform, new Vector2(0f, 1f), new Vector2(14f, -40f), new Vector2(350f, 20f));

                // Champions — mini chips, click to attack (engine validates power/taunt).
                float cx = 12f;
                foreach (var champion in player.Champions)
                {
                    var chip = SoiCardWidget.Create(rect, Theme, new Vector2(82f, 92f));
                    chip.Rect.anchorMin = chip.Rect.anchorMax = new Vector2(0f, 0f);
                    chip.Rect.pivot = new Vector2(0f, 0f);
                    chip.Rect.anchoredPosition = new Vector2(cx, 8f);
                    cx += 88f;
                    chip.Show(champion.DefId, champion.InstanceId, champion.Exhausted, champion.DamageThisTurn);
                    int target = player.Index;
                    chip.Clicked += w => Submit(new ShardsAttackChampionAction
                    {
                        PlayerIndex = MyIndex,
                        TargetPlayerIndex = target,
                        CardInstanceId = w.InstanceId
                    });
                    _widgets.Add(chip);
                    if (cx > 350f) break;
                }
            }
        }

        private void RenderCenterRow()
        {
            for (int slot = 0; slot < _snap.CenterRow.Count; slot++)
            {
                var card = _snap.CenterRow[slot];
                var widget = SoiCardWidget.Create(_centerRow, Theme, new Vector2(150f, 210f));
                widget.Rect.anchorMin = widget.Rect.anchorMax = new Vector2(0f, 0.5f);
                widget.Rect.pivot = new Vector2(0f, 0.5f);
                widget.Rect.anchoredPosition = new Vector2(slot * 158f, 0f);
                if (card == null)
                {
                    widget.Show(null);
                    continue;
                }
                widget.Show(card.DefId, card.InstanceId);
                int slotIndex = slot;
                string defId = card.DefId;
                widget.Clicked += _ => OnRowSlotClicked(slotIndex, defId);
                _widgets.Add(widget);
            }
        }

        private void OnRowSlotClicked(int slot, string defId)
        {
            if (!MyPriority) return;
            if (!ShardsCardDatabase.TryGet(defId, out var def)) return;

            if (def.Type == ShardsCardType.Mercenary)
            {
                var request = new DecisionRequest
                {
                    Id = -1,
                    PlayerIndex = MyIndex,
                    Title = def.Name + ": recruit it, or fast-play it now?",
                    Context = "local.buy",
                    Min = 0,
                    Max = 1
                };
                request.Options.Add(new DecisionOption(0, "Recruit (to your discard pile)"));
                request.Options.Add(new DecisionOption(1, "Fast-play (effect now, returns to the center deck)"));
                _modal.Show(request, id => OptionLabel(request, id), chosen =>
                {
                    if (chosen.Count == 0) return;
                    Submit(new ShardsBuyCardAction { PlayerIndex = MyIndex, SlotIndex = slot, FastPlay = chosen[0] == 1 });
                });
                return;
            }
            Submit(new ShardsBuyCardAction { PlayerIndex = MyIndex, SlotIndex = slot });
        }

        private void RenderDestinyAndMonsters()
        {
            bool ith = _snap.DestinyRow.Count > 0 || _snap.ActiveMonsters.Count > 0 || Me?.Destinies.Count > 0;
            _destinyRow.gameObject.SetActive(ith);
            _monsterRow.gameObject.SetActive(_snap.ActiveMonsters.Count > 0);

            float x = 0f;
            bool eligible = MyPriority && Me != null && !Me.DestinyTaken && Me.Mastery >= 5;
            foreach (var destiny in _snap.DestinyRow)
            {
                var button = UiFactory.CreateButton(Theme, "Destiny_" + destiny.InstanceId, _destinyRow,
                    DefName(destiny.DefId), 11f, eligible ? UiPalette.Gold : UiPalette.PanelLight, UiPalette.Background);
                var rect = (RectTransform)button.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(x, 0f);
                rect.sizeDelta = new Vector2(142f, 46f);
                x += 148f;
                int id = destiny.InstanceId;
                string defId = destiny.DefId;
                button.onClick.AddListener(() =>
                {
                    if (eligible)
                        Submit(new ShardsTakeDestinyAction { PlayerIndex = MyIndex, CardInstanceId = id });
                    else
                        _toast.Show(DefName(defId) + ": " + RulesOf(defId));
                });
            }

            x = 0f;
            foreach (var monster in _snap.ActiveMonsters)
            {
                var chip = SoiCardWidget.Create(_monsterRow, Theme, new Vector2(100f, 60f));
                chip.Rect.anchorMin = chip.Rect.anchorMax = new Vector2(0f, 0.5f);
                chip.Rect.pivot = new Vector2(0f, 0.5f);
                chip.Rect.anchoredPosition = new Vector2(x, 0f);
                x += 106f;
                chip.Show(monster.DefId, monster.InstanceId, damage: monster.DamageThisTurn);
                chip.Clicked += w => Submit(new ShardsAttackMonsterAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                _widgets.Add(chip);
            }
        }

        private void RenderMyBoard()
        {
            var me = Me;
            if (me == null) return;

            _statHealth.text = me.Health.ToString();
            _statMastery.text = me.Mastery + " / 30";
            _statGems.text = me.Gems.ToString();
            _statPower.text = me.Power.ToString();

            _relics.gameObject.SetActive(me.SetAside != null && me.SetAside.Count > 0 && !me.RelicRecruited);

            float x = 0f;
            foreach (var champion in me.Champions)
            {
                var chip = SoiCardWidget.Create(_championRow, Theme, new Vector2(96f, 96f));
                chip.Rect.anchorMin = chip.Rect.anchorMax = new Vector2(0f, 0.5f);
                chip.Rect.pivot = new Vector2(0f, 0.5f);
                chip.Rect.anchoredPosition = new Vector2(x, 0f);
                x += 102f;
                chip.Show(champion.DefId, champion.InstanceId, champion.Exhausted, champion.DamageThisTurn);
                chip.Clicked += w => Submit(new ShardsExhaustAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                _widgets.Add(chip);
            }

            x = 0f;
            foreach (var played in me.PlayZone)
            {
                var chip = UiFactory.CreateText(Theme, "Played", _playZoneRow, "· " + DefName(played.DefId), 13f,
                    UiPalette.TextDim, TextAlignmentOptions.Left);
                UiFactory.Place(chip.rectTransform, new Vector2(0f, 1f), new Vector2(6f, -(x)), new Vector2(540f, 18f));
                x += 18f;
                if (x > 66f) { chip.text += "  (+" + (me.PlayZone.Count - 4) + " more)"; break; }
            }

            x = 0f;
            foreach (var destiny in me.Destinies)
            {
                var chip = SoiCardWidget.Create(_destinyOwnRow, Theme, new Vector2(96f, 56f));
                chip.Rect.anchorMin = chip.Rect.anchorMax = new Vector2(0f, 0.5f);
                chip.Rect.pivot = new Vector2(0f, 0.5f);
                chip.Rect.anchoredPosition = new Vector2(x, 0f);
                x += 102f;
                chip.Show(destiny.DefId, destiny.InstanceId, destiny.Exhausted);
                chip.Clicked += w => Submit(new ShardsExhaustAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                _widgets.Add(chip);
            }
        }

        private void RenderHand()
        {
            var me = Me;
            if (me?.Hand == null) return;
            int count = me.Hand.Count;
            float step = Mathf.Min(158f, count > 1 ? 1140f / (count - 1) : 158f);
            float start = -(count - 1) * step / 2f;
            for (int i = 0; i < count; i++)
            {
                var card = me.Hand[i];
                var widget = SoiCardWidget.Create(_handRow, Theme, new Vector2(150f, 210f));
                widget.Rect.anchoredPosition = new Vector2(start + i * step, 0f);
                widget.Show(card.DefId, card.InstanceId);
                widget.Clicked += w => Submit(new ShardsPlayCardAction { PlayerIndex = MyIndex, CardInstanceId = w.InstanceId });
                _widgets.Add(widget);
            }
        }

        private void RefreshInteractivity()
        {
            bool canAct = MyPriority && !_gameOverShown;
            var me = Me;
            _endTurn.interactable = canAct;
            _focus.interactable = canAct && me != null && me.Gems >= 1 && !me.FocusedThisTurn && !me.CharacterExhausted;
            _relics.interactable = canAct && me != null && me.Mastery >= 10;
            _statusLine.text = _gameOverShown ? "" :
                _pending == null ? "" :
                _pending.PlayerIndex == MyIndex
                    ? (_pending.Kind == PendingInputKind.Decision ? "" : "Your move — play cards, buy, then END TURN.")
                    : "Waiting for " + NameOf(_pending.PlayerIndex) + "…";
        }

        private void RenderGameOver()
        {
            if (!_snap.GameOver || _gameOverShown) return;
            _gameOverShown = true;
            _gameOverPanel.gameObject.SetActive(true);
            _gameOverPanel.SetAsLastSibling();
            _gameOverText.text = _snap.WinnerIndex < 0 ? "IT'S A TIE"
                : _snap.WinnerIndex == MyIndex ? "VICTORY!"
                : NameOf(_snap.WinnerIndex) + " WINS";
        }

        // ------------------------------------------------------------------ hover preview

        private void OnCardHovered(SoiCardWidget widget, bool entered)
        {
            if (widget == _preview) return;
            if (!entered || widget.DefId == null)
            {
                _preview.gameObject.SetActive(false);
                return;
            }
            _preview.gameObject.SetActive(true);
            _preview.transform.SetAsLastSibling();
            _preview.Show(widget.DefId);
        }

        // ------------------------------------------------------------------ helpers

        private string NameOf(int playerIndex) =>
            _snap != null && playerIndex >= 0 && playerIndex < _snap.Players.Count
                ? _snap.Players[playerIndex].Name : "P" + playerIndex;

        private static string DefName(string defId) =>
            ShardsCardDatabase.TryGet(defId, out var def) ? def.Name : defId;

        private static string RulesOf(string defId) =>
            ShardsCardDatabase.TryGet(defId, out var def) ? def.RulesText : "";
    }
}
