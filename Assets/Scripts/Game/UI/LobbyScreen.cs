using System.Collections.Generic;
using Pascension.Content;
using Pascension.Engine.Heroes;
using Pascension.Game.View;
using Pascension.Net;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Pascension.Game.UI
{
    /// <summary>
    /// The online lobby, in the house TMP style. Offline it shows the connect panel
    /// (player name + game ID + HOST/JOIN — the game ID is a Unity Relay join code, so
    /// no port forwarding); once connected it renders the replicated slot list from
    /// LobbyNetBehaviour.State, polled every frame (4 slots — trivial cost).
    /// Zero rules logic; zero netcode beyond NetLauncher + LobbyNetBehaviour intents.
    /// </summary>
    public sealed class LobbyScreen : MonoBehaviour
    {
        [Header("Wired by SceneConstruction.PopulateLobbyScene")]
        public UiTheme Theme;
        public RectTransform Root;

        private GameObject _connectPanel;
        private TMP_InputField _nameInput;
        private TMP_InputField _codeInput;
        private Button _hostButton;
        private Button _joinButton;
        private TextMeshProUGUI _connectStatus;
        private bool _busy;

        private GameObject _lobbyPanel;
        private TextMeshProUGUI _codeLabel;
        private TextMeshProUGUI _gameLabel;
        private Button _gameButton;
        private RectTransform _dlcRow;
        private string _dlcRowBuiltFor; // gameId the DLC checkboxes were built for
        private Button _copyButton;
        private Button _readyButton;
        private TextMeshProUGUI _readyLabel;
        private Button _startButton;
        private TextMeshProUGUI _lobbyStatus;
        private readonly SlotRow[] _rows = new SlotRow[LobbyNetBehaviour.MaxSlots];

        private sealed class SlotRow
        {
            public TextMeshProUGUI Label;
            public Button HeroButton;
            public TextMeshProUGUI HeroLabel;
            public Button ActionButton;
            public TextMeshProUGUI ActionLabel;
        }

        private void Awake()
        {
            ContentRegistry.RegisterAll();
            BuildConnectPanel();
            BuildLobbyPanel();
            _lobbyPanel.SetActive(false);
        }

        private void Update()
        {
            var manager = NetworkManager.Singleton;
            bool online = manager != null && manager.IsListening;
            var lobby = LobbyNetBehaviour.Instance;
            bool inLobby = online && lobby != null && lobby.IsSpawned;

            _connectPanel.SetActive(!inLobby);
            _lobbyPanel.SetActive(inLobby);

            if (!inLobby)
            {
                if (!_busy)
                {
                    _hostButton.interactable = !online;
                    _joinButton.interactable = !online;
                    if (online)
                        _connectStatus.text = manager.IsClient && !manager.IsConnectedClient
                            ? Loc.T("Connecting…") : Loc.T("Waiting for the lobby…");
                    else
                    {
                        string reason = manager != null ? manager.DisconnectReason : null;
                        if (!string.IsNullOrEmpty(reason))
                        {
                            // Version-gate reasons arrive as English wire strings
                            // (NetBootstrap.ApproveConnection) — localize by prefix.
                            if (reason.StartsWith("Update required"))
                                _connectStatus.text = Loc.T("Update required — use the UPDATE button in the main menu.");
                            else if (reason.StartsWith("Host update required"))
                                _connectStatus.text = Loc.T("The host runs an older version — they need to update.");
                            else
                                _connectStatus.text = "Disconnected: " + reason;
                        }
                    }
                }
                return;
            }

            RefreshLobby(manager, lobby);
        }

        // ------------------------------------------------------------------ lobby refresh

        private void RefreshLobby(NetworkManager manager, LobbyNetBehaviour lobby)
        {
            _codeLabel.text = Loc.T("GAME ID:  ") + "<color=#E8C15A>" + (NetLauncher.CurrentJoinCode ?? "LAN") + "</color>";
            RefreshGameRow(manager, lobby);

            var state = lobby.State;
            bool isHost = manager.IsHost;
            ulong myId = manager.LocalClientId;
            int mySlot = -1;
            int occupied = 0;

            for (int i = 0; i < state.Slots.Count && i < _rows.Length; i++)
            {
                var slot = state.Slots[i];
                if (slot.Kind != LobbySlotKind.Empty) occupied++;
                if (slot.Kind == LobbySlotKind.Human && slot.ClientId == myId) mySlot = i;
            }

            for (int i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                var slot = i < state.Slots.Count ? state.Slots[i] : null;

                if (slot == null || slot.Kind == LobbySlotKind.Empty)
                {
                    row.Label.text = "<color=#8A8377>" + Loc.T("Open seat") + "</color>";
                    row.HeroButton.gameObject.SetActive(false);
                    row.ActionButton.gameObject.SetActive(isHost);
                    row.ActionLabel.text = Loc.T("ADD BOT");
                    continue;
                }

                bool isHostSlot = slot.Kind == LobbySlotKind.Human && slot.ClientId == state.HostClientId;
                string tags = "";
                if (isHostSlot) tags += "  <size=15><color=#E8C15A>" + Loc.T("HOST") + "</color></size>";
                if (i == mySlot) tags += "  <size=15><color=#8A8377>" + Loc.T("YOU") + "</color></size>";
                if (slot.Kind == LobbySlotKind.Bot) tags += "  <size=15><color=#8A8377>" + Loc.T("BOT") + "</color></size>";
                bool ready = slot.Kind == LobbySlotKind.Bot || isHostSlot || slot.Ready;
                row.Label.text = slot.Name + tags +
                    (ready ? "  <size=15><color=#71B356>" + Loc.T("READY") + "</color></size>"
                           : "  <size=15><color=#C24B3A>" + Loc.T("NOT READY") + "</color></size>");

                row.HeroButton.gameObject.SetActive(true);
                row.HeroLabel.text = HeroDisplayName(slot.HeroId);
                row.HeroButton.interactable = i == mySlot || (isHost && slot.Kind == LobbySlotKind.Bot);

                bool showAction = isHost && !isHostSlot;
                row.ActionButton.gameObject.SetActive(showAction);
                row.ActionLabel.text = slot.Kind == LobbySlotKind.Bot ? Loc.T("REMOVE") : Loc.T("KICK");
            }

            bool canToggleReady = mySlot >= 0 && !isHost;
            _readyButton.gameObject.SetActive(canToggleReady);
            if (canToggleReady)
                _readyLabel.text = state.Slots[mySlot].Ready ? Loc.T("UNREADY") : Loc.T("READY");

            _startButton.gameObject.SetActive(isHost);
            _startButton.interactable = occupied >= 2;
            if (!isHost)
                _lobbyStatus.text = Loc.T("Waiting for the host to start…");
        }

        private string NextHero(string current)
        {
            var lobby = LobbyNetBehaviour.Instance;
            var module = GameCatalog.Get(lobby != null ? lobby.State.GameId : GameCatalog.DefaultGameId);
            var heroes = module.CharactersFor(lobby != null ? lobby.State.DlcFlags : 0);
            if (heroes.Count == 0) return current;
            for (int i = 0; i < heroes.Count; i++)
                if (heroes[i].Id == current)
                    return heroes[(i + 1) % heroes.Count].Id;
            return heroes[0].Id;
        }

        private string HeroDisplayName(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return "—";
            var lobby = LobbyNetBehaviour.Instance;
            var module = GameCatalog.Get(lobby != null ? lobby.State.GameId : GameCatalog.DefaultGameId);
            return module.CharacterDisplayName(heroId);
        }

        // ------------------------------------------------------------------ handlers

        private void SavePlayerName()
        {
            string name = _nameInput.text.Trim();
            if (!string.IsNullOrEmpty(name))
                ClientIdentity.PlayerName = name;
        }

        private async void OnHostClicked()
        {
            if (_busy) return;
            SavePlayerName();
            _busy = true;
            _hostButton.interactable = _joinButton.interactable = false;
            _connectStatus.text = Loc.T("Creating game…");
            try
            {
                string code = await NetLauncher.HostAsync();
                _connectStatus.text = "Game ID: " + code;
            }
            catch (UgsException e)
            {
                _connectStatus.text = e.Message;
            }
            catch (System.Exception e)
            {
                _connectStatus.text = Loc.T("Unexpected error — see the log.");
                Debug.LogException(e);
            }
            finally
            {
                _busy = false;
                _hostButton.interactable = _joinButton.interactable = true;
            }
        }

        private async void OnJoinClicked()
        {
            if (_busy) return;
            SavePlayerName();
            _busy = true;
            _hostButton.interactable = _joinButton.interactable = false;
            _connectStatus.text = Loc.T("Joining…");
            try
            {
                await NetLauncher.JoinAsync(_codeInput.text);
                _connectStatus.text = Loc.T("Connecting to the host…");
            }
            catch (UgsException e)
            {
                _connectStatus.text = e.Message;
            }
            catch (System.Exception e)
            {
                _connectStatus.text = Loc.T("Unexpected error — see the log.");
                Debug.LogException(e);
            }
            finally
            {
                _busy = false;
                _hostButton.interactable = _joinButton.interactable = true;
            }
        }

        private void OnCopyClicked()
        {
            if (!string.IsNullOrEmpty(NetLauncher.CurrentJoinCode))
                GUIUtility.systemCopyBuffer = NetLauncher.CurrentJoinCode;
        }

        private void OnHeroClicked(int slotIndex)
        {
            var lobby = LobbyNetBehaviour.Instance;
            var manager = NetworkManager.Singleton;
            if (lobby == null || !lobby.IsSpawned || manager == null) return;

            var slot = lobby.State.Slots[slotIndex];
            string next = NextHero(slot.HeroId);
            if (slot.Kind == LobbySlotKind.Bot)
                lobby.HostSetBotHero(slotIndex, next);
            else if (slot.Kind == LobbySlotKind.Human && slot.ClientId == manager.LocalClientId)
                lobby.SetHeroRpc(next); // runs locally on the host, over the wire on clients
        }

        private void OnActionClicked(int slotIndex)
        {
            var lobby = LobbyNetBehaviour.Instance;
            var manager = NetworkManager.Singleton;
            if (lobby == null || !lobby.IsSpawned || manager == null || !manager.IsHost) return;

            if (lobby.State.Slots[slotIndex].Kind == LobbySlotKind.Empty)
                lobby.HostAddBot(slotIndex);
            else
                lobby.HostRemoveSlot(slotIndex);
        }

        private void OnReadyClicked()
        {
            var lobby = LobbyNetBehaviour.Instance;
            var manager = NetworkManager.Singleton;
            if (lobby == null || !lobby.IsSpawned || manager == null) return;

            foreach (var slot in lobby.State.Slots)
            {
                if (slot.Kind != LobbySlotKind.Human || slot.ClientId != manager.LocalClientId) continue;
                lobby.SetReadyRpc(!slot.Ready);
                return;
            }
        }

        private void OnStartClicked()
        {
            var lobby = LobbyNetBehaviour.Instance;
            if (lobby == null || !lobby.IsSpawned) return;
            string error = lobby.HostStartGame();
            _lobbyStatus.text = error ?? Loc.T("Starting…");
        }

        private void OnLeaveClicked()
        {
            NetLauncher.Shutdown();
            SceneManager.LoadScene(NetLauncher.LobbySceneName); // back to the offline connect panel
        }

        private void OnGameCycleClicked()
        {
            var manager = NetworkManager.Singleton;
            var lobby = LobbyNetBehaviour.Instance;
            if (manager == null || !manager.IsHost || lobby == null) return;
            var all = GameCatalog.All;
            int index = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i].GameId == lobby.State.GameId)
                    index = i;
            for (int step = 1; step <= all.Count; step++)
            {
                var next = all[(index + step) % all.Count];
                if (!next.Playable) continue;
                lobby.HostSetGame(next.GameId);
                return;
            }
        }

        private void RefreshGameRow(NetworkManager manager, LobbyNetBehaviour lobby)
        {
            var module = GameCatalog.Get(lobby.State.GameId);
            bool isHost = manager.IsHost;
            _gameButton.interactable = isHost && GameCatalog.All.Count > 1;
            _gameLabel.text = module.DisplayName.ToUpperInvariant();

            // Rebuild the DLC checkboxes when the game changes.
            if (_dlcRowBuiltFor != module.GameId)
            {
                _dlcRowBuiltFor = module.GameId;
                for (int i = _dlcRow.childCount - 1; i >= 0; i--)
                    Destroy(_dlcRow.GetChild(i).gameObject);
                float x = 0f;
                foreach (var dlc in module.DlcOptions)
                {
                    var toggle = UiFactory.CreateToggle(Theme, "Dlc_" + dlc.Flag, _dlcRow, dlc.Name);
                    var rt = (RectTransform)toggle.transform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
                    rt.anchoredPosition = new Vector2(x, 0f);
                    rt.sizeDelta = new Vector2(290f, 28f);
                    int flag = dlc.Flag;
                    toggle.onValueChanged.AddListener(on =>
                    {
                        var l = LobbyNetBehaviour.Instance;
                        var m = NetworkManager.Singleton;
                        if (l == null || m == null || !m.IsHost) return;
                        int flags = l.State.DlcFlags;
                        l.HostSetDlc(on ? flags | flag : flags & ~flag);
                    });
                    x += 300f;
                }
            }

            // Sync checkbox states + host-only interactivity.
            int current = lobby.State.DlcFlags;
            for (int i = 0; i < _dlcRow.childCount; i++)
            {
                var toggle = _dlcRow.GetChild(i).GetComponent<UnityEngine.UI.Toggle>();
                if (toggle == null) continue;
                string name = _dlcRow.GetChild(i).name; // "Dlc_<flag>"
                if (int.TryParse(name.Substring(4), out int flag))
                    toggle.SetIsOnWithoutNotify((current & flag) != 0);
                toggle.interactable = isHost;
            }
        }

        // ------------------------------------------------------------------ construction

        private void BuildConnectPanel()
        {
            _connectPanel = UiFactory.CreateRect("ConnectPanel", Root).gameObject;
            UiFactory.Stretch((RectTransform)_connectPanel.transform);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _connectPanel.transform);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(680f, 520f));

            var title = UiFactory.CreateText(Theme, "Title", panel.transform, Loc.T("PLAY ONLINE"), 36f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 4f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(500f, 44f));

            var subtitle = UiFactory.CreateText(Theme, "Subtitle", panel.transform,
                Loc.T("host a game and share its ID — no port forwarding needed"), 16f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -72f), new Vector2(600f, 22f));

            var nameLabel = UiFactory.CreateText(Theme, "NameLabel", panel.transform, Loc.T("YOUR NAME"), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(nameLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-160f, -122f), new Vector2(180f, 22f));
            _nameInput = UiFactory.CreateInputField(Theme, "NameInput", panel.transform, Loc.T("Player name"));
            UiFactory.Place(((RectTransform)_nameInput.transform), new Vector2(0.5f, 1f), new Vector2(0f, -152f), new Vector2(500f, 48f));
            _nameInput.text = ClientIdentity.PlayerName;
            _nameInput.characterLimit = 20;

            var codeLabel = UiFactory.CreateText(Theme, "CodeLabel", panel.transform, Loc.T("GAME ID (TO JOIN A FRIEND)"), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(codeLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-100f, -222f), new Vector2(300f, 22f));
            _codeInput = UiFactory.CreateInputField(Theme, "CodeInput", panel.transform, "e.g. AB3DEF");
            UiFactory.Place(((RectTransform)_codeInput.transform), new Vector2(0.5f, 1f), new Vector2(0f, -252f), new Vector2(500f, 48f));
            _codeInput.characterLimit = 12;
            _codeInput.onValueChanged.AddListener(v =>
            {
                string upper = v.ToUpperInvariant();
                if (upper != v) _codeInput.SetTextWithoutNotify(upper);
            });

            _hostButton = UiFactory.CreateButton(Theme, "HostButton", panel.transform, Loc.T("HOST GAME"), 22f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_hostButton.transform, new Vector2(0.5f, 1f), new Vector2(-130f, -340f), new Vector2(230f, 58f));
            _hostButton.onClick.AddListener(OnHostClicked);

            _joinButton = UiFactory.CreateButton(Theme, "JoinButton", panel.transform, Loc.T("JOIN GAME"), 22f);
            UiFactory.Place((RectTransform)_joinButton.transform, new Vector2(0.5f, 1f), new Vector2(130f, -340f), new Vector2(230f, 58f));
            _joinButton.onClick.AddListener(OnJoinClicked);

            _connectStatus = UiFactory.CreateText(Theme, "Status", panel.transform, "", 17f,
                UiPalette.TextMain, TextAlignmentOptions.Center);
            UiFactory.Place(_connectStatus.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -412f), new Vector2(620f, 44f));

            var back = UiFactory.CreateButton(Theme, "BackButton", panel.transform, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 20f), new Vector2(160f, 44f));
            back.onClick.AddListener(() =>
            {
                NetLauncher.Shutdown();
                SceneFlow.LoadMenu();
            });
        }

        private void BuildLobbyPanel()
        {
            _lobbyPanel = UiFactory.CreateRect("LobbyPanel", Root).gameObject;
            UiFactory.Stretch((RectTransform)_lobbyPanel.transform);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _lobbyPanel.transform);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1040f, 660f));

            var title = UiFactory.CreateText(Theme, "Title", panel.transform, Loc.T("LOBBY"), 34f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 4f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(400f, 42f));

            _codeLabel = UiFactory.CreateText(Theme, "Code", panel.transform, Loc.T("GAME ID:"), 24f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            _codeLabel.characterSpacing = 2f;
            UiFactory.Place(_codeLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-60f, -74f), new Vector2(460f, 34f));

            _copyButton = UiFactory.CreateButton(Theme, "CopyButton", panel.transform, Loc.T("COPY"), 16f);
            UiFactory.Place((RectTransform)_copyButton.transform, new Vector2(0.5f, 1f), new Vector2(210f, -74f), new Vector2(100f, 36f));
            _copyButton.onClick.AddListener(OnCopyClicked);

            var share = UiFactory.CreateText(Theme, "ShareHint", panel.transform,
                Loc.T("friends join from the menu with this ID"), 14f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(share.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -106f), new Vector2(500f, 20f));

            // Game + DLC row: the host cycles the game and toggles DLCs; clients see labels.
            _gameButton = UiFactory.CreateButton(Theme, "GameButton", panel.transform, "", 16f);
            UiFactory.Place((RectTransform)_gameButton.transform, new Vector2(0f, 1f), new Vector2(40f, -74f), new Vector2(240f, 40f));
            _gameButton.onClick.AddListener(OnGameCycleClicked);
            _gameLabel = UiFactory.ButtonLabel(_gameButton);

            // Full-width band of its own — SoI has 3 long-named DLC toggles which must
            // never run under the player slots below.
            _dlcRow = UiFactory.CreateRect("DlcRow", panel.transform);
            UiFactory.Place(_dlcRow, new Vector2(0f, 1f), new Vector2(40f, -130f), new Vector2(960f, 30f));

            for (int i = 0; i < _rows.Length; i++)
                _rows[i] = BuildSlotRow(panel.transform, i);

            _readyButton = UiFactory.CreateButton(Theme, "ReadyButton", panel.transform, Loc.T("READY"), 20f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_readyButton.transform, new Vector2(0.5f, 0f), new Vector2(-300f, 78f), new Vector2(220f, 54f));
            _readyButton.onClick.AddListener(OnReadyClicked);
            _readyLabel = UiFactory.ButtonLabel(_readyButton);

            _startButton = UiFactory.CreateButton(Theme, "StartButton", panel.transform, Loc.T("START GAME"), 20f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_startButton.transform, new Vector2(0.5f, 0f), new Vector2(0f, 78f), new Vector2(240f, 54f));
            _startButton.onClick.AddListener(OnStartClicked);

            var leave = UiFactory.CreateButton(Theme, "LeaveButton", panel.transform, Loc.T("LEAVE"), 20f);
            UiFactory.Place((RectTransform)leave.transform, new Vector2(0.5f, 0f), new Vector2(300f, 78f), new Vector2(220f, 54f));
            leave.onClick.AddListener(OnLeaveClicked);

            _lobbyStatus = UiFactory.CreateText(Theme, "Status", panel.transform, "", 16f,
                UiPalette.TextMain, TextAlignmentOptions.Center);
            UiFactory.Place(_lobbyStatus.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(700f, 36f));
        }

        private SlotRow BuildSlotRow(Transform parent, int index)
        {
            var rowBg = UiFactory.CreatePanel(Theme, $"Slot{index}", parent,
                UiPalette.WithAlpha(UiPalette.PanelLight, 0.55f));
            UiFactory.Place(rowBg.rectTransform, new Vector2(0.5f, 1f),
                new Vector2(0f, -172f - index * 84f), new Vector2(960f, 78f));

            var row = new SlotRow();
            row.Label = UiFactory.CreateText(Theme, "Label", rowBg.transform, "", 20f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(row.Label.rectTransform, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(430f, 60f));

            row.HeroButton = UiFactory.CreateButton(Theme, "HeroButton", rowBg.transform, "", 17f);
            UiFactory.Place((RectTransform)row.HeroButton.transform, new Vector2(1f, 0.5f), new Vector2(-190f, 0f), new Vector2(280f, 52f));
            int captured = index;
            row.HeroButton.onClick.AddListener(() => OnHeroClicked(captured));
            row.HeroLabel = UiFactory.ButtonLabel(row.HeroButton);

            row.ActionButton = UiFactory.CreateButton(Theme, "ActionButton", rowBg.transform, "", 15f);
            UiFactory.Place((RectTransform)row.ActionButton.transform, new Vector2(1f, 0.5f), new Vector2(-20f, 0f), new Vector2(150f, 52f));
            row.ActionButton.onClick.AddListener(() => OnActionClicked(captured));
            row.ActionLabel = UiFactory.ButtonLabel(row.ActionButton);
            return row;
        }
    }
}
