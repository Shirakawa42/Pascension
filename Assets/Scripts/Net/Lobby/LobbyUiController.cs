using System.Collections.Generic;
using Pascension.Content;
using Pascension.Engine.Heroes;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Pascension.Net
{
    /// <summary>
    /// Fully code-built lobby UI (legacy uGUI Text — no TMP dependency, so it works even
    /// before TMP essentials are imported). Offline it shows host/join controls (these
    /// double as the MainMenu integration surface — a menu can equally call NetLauncher
    /// directly); once connected it renders the replicated slot list from
    /// LobbyNetBehaviour.State, polled every frame (4 slots — trivial cost).
    /// The scene's EventSystem + InputSystemUIInputModule are authored by NetSceneBuilder.
    /// </summary>
    public sealed class LobbyUiController : MonoBehaviour
    {
        private static readonly Color PanelColor = new(0.13f, 0.15f, 0.22f, 0.95f);
        private static readonly Color RowColor = new(0.18f, 0.20f, 0.30f, 0.9f);
        private static readonly Color ButtonColor = new(0.25f, 0.32f, 0.50f);
        private static readonly Color InputColor = new(0.09f, 0.10f, 0.15f);

        private Font _font;

        private GameObject _connectPanel;
        private InputField _addressInput;
        private InputField _portInput;
        private Button _hostButton;
        private Button _joinButton;
        private Text _connectStatus;

        private GameObject _lobbyPanel;
        private Button _readyButton;
        private Text _readyLabel;
        private Button _startButton;
        private Text _lobbyStatus;
        private readonly SlotRow[] _rows = new SlotRow[LobbyNetBehaviour.MaxSlots];

        private sealed class SlotRow
        {
            public Text Label;
            public Button HeroButton;
            public Text HeroLabel;
            public Button ActionButton;
            public Text ActionLabel;
        }

        private void Awake()
        {
            ContentRegistry.RegisterAll();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUi();
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
                _hostButton.interactable = !online;
                _joinButton.interactable = !online;
                if (online)
                {
                    _connectStatus.text = manager.IsClient && !manager.IsConnectedClient
                        ? "Connecting…"
                        : "Waiting for the lobby…";
                }
                else
                {
                    string reason = manager != null ? manager.DisconnectReason : null;
                    if (!string.IsNullOrEmpty(reason))
                        _connectStatus.text = "Disconnected: " + reason;
                }
                return;
            }

            RefreshLobby(manager, lobby);
        }

        // ---------------- lobby refresh ----------------

        private void RefreshLobby(NetworkManager manager, LobbyNetBehaviour lobby)
        {
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
                    row.Label.text = "Slot " + (i + 1) + " — empty";
                    row.HeroButton.gameObject.SetActive(false);
                    row.ActionButton.gameObject.SetActive(isHost);
                    row.ActionLabel.text = "Add Bot";
                    continue;
                }

                bool isHostSlot = slot.Kind == LobbySlotKind.Human && slot.ClientId == state.HostClientId;
                string tags = "";
                if (isHostSlot) tags += " (host)";
                if (i == mySlot) tags += " (you)";
                bool ready = slot.Kind == LobbySlotKind.Bot || isHostSlot || slot.Ready;
                row.Label.text = slot.Name + tags + " — " + (ready ? "ready" : "not ready");

                row.HeroButton.gameObject.SetActive(true);
                row.HeroLabel.text = HeroDisplayName(slot.HeroId);
                row.HeroButton.interactable = i == mySlot || (isHost && slot.Kind == LobbySlotKind.Bot);

                bool showAction = isHost && !isHostSlot;
                row.ActionButton.gameObject.SetActive(showAction);
                row.ActionLabel.text = slot.Kind == LobbySlotKind.Bot ? "Remove" : "Kick";
            }

            bool canToggleReady = mySlot >= 0 && !isHost;
            _readyButton.gameObject.SetActive(canToggleReady);
            if (canToggleReady)
                _readyLabel.text = state.Slots[mySlot].Ready ? "Unready" : "Ready";

            _startButton.gameObject.SetActive(isHost);
            _startButton.interactable = occupied >= 2;
            if (!isHost)
                _lobbyStatus.text = "Waiting for the host to start…";
        }

        private static string NextHero(string current)
        {
            var heroes = HeroDatabase.All;
            if (heroes.Count == 0) return current;
            for (int i = 0; i < heroes.Count; i++)
                if (heroes[i].Id == current)
                    return heroes[(i + 1) % heroes.Count].Id;
            return heroes[0].Id;
        }

        private static string HeroDisplayName(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return "—";
            try
            {
                return HeroDatabase.Get(heroId).Name;
            }
            catch (KeyNotFoundException)
            {
                return heroId;
            }
        }

        // ---------------- button handlers ----------------

        private void OnHostClicked()
        {
            if (!TryParsePort(out ushort port)) return;
            _connectStatus.text = "Starting host on port " + port + "…";
            if (!NetLauncher.StartHost(port))
                _connectStatus.text = "Could not start host (see console)";
        }

        private void OnJoinClicked()
        {
            if (!TryParsePort(out ushort port)) return;
            _connectStatus.text = "Connecting to " + _addressInput.text + ":" + port + "…";
            if (!NetLauncher.StartClient(_addressInput.text, port))
                _connectStatus.text = "Could not start client (see console)";
        }

        private bool TryParsePort(out ushort port)
        {
            if (ushort.TryParse(_portInput.text, out port) && port != 0) return true;
            _connectStatus.text = "Invalid port";
            return false;
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
            _lobbyStatus.text = error ?? "Starting…";
        }

        private void OnLeaveClicked()
        {
            NetLauncher.Shutdown();
            SceneManager.LoadScene(NetLauncher.LobbySceneName); // reset to the offline lobby
        }

        // ---------------- UI construction ----------------

        private void BuildUi()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);

            _connectPanel = BuildPanel(canvasGo.transform, "ConnectPanel", 560, 420);
            BuildConnectPanel(_connectPanel.transform);

            _lobbyPanel = BuildPanel(canvasGo.transform, "LobbyPanel", 940, 640);
            BuildLobbyPanel(_lobbyPanel.transform);

            _lobbyPanel.SetActive(false);
        }

        private void BuildConnectPanel(Transform parent)
        {
            AddText(parent, "Pascension — Online", 34, TextAnchor.MiddleCenter, 50);
            AddText(parent, "Host a lobby, or join one by IP", 18, TextAnchor.MiddleCenter, 28);
            _addressInput = AddInputRow(parent, "Address", "127.0.0.1", InputField.ContentType.Standard);
            _portInput = AddInputRow(parent, "Port", NetLauncher.DefaultPort.ToString(),
                InputField.ContentType.IntegerNumber);

            var row = AddRow(parent, 56);
            _hostButton = AddButton(row.transform, "Host Game", OnHostClicked, out _);
            _joinButton = AddButton(row.transform, "Join Game", OnJoinClicked, out _);
            _connectStatus = AddText(parent, "", 16, TextAnchor.MiddleCenter, 44);
        }

        private void BuildLobbyPanel(Transform parent)
        {
            AddText(parent, "Lobby", 30, TextAnchor.MiddleCenter, 44);
            for (int i = 0; i < _rows.Length; i++)
            {
                int index = i; // capture per-iteration for the button closures
                _rows[i] = BuildSlotRow(parent, index);
            }

            var controls = AddRow(parent, 56);
            _readyButton = AddButton(controls.transform, "Ready", OnReadyClicked, out _readyLabel);
            _startButton = AddButton(controls.transform, "Start Game", OnStartClicked, out _);
            AddButton(controls.transform, "Leave", OnLeaveClicked, out _);
            _lobbyStatus = AddText(parent, "", 16, TextAnchor.MiddleCenter, 40);
        }

        private SlotRow BuildSlotRow(Transform parent, int index)
        {
            var rowGo = AddRow(parent, 68);
            rowGo.AddComponent<Image>().color = RowColor;
            var layout = rowGo.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 8, 8);

            var row = new SlotRow();
            row.Label = AddText(rowGo.transform, "", 19, TextAnchor.MiddleLeft, 52);
            row.Label.GetComponent<LayoutElement>().flexibleWidth = 1;

            row.HeroButton = AddButton(rowGo.transform, "", () => OnHeroClicked(index), out row.HeroLabel);
            var heroLe = row.HeroButton.GetComponent<LayoutElement>();
            heroLe.preferredWidth = 280;
            heroLe.flexibleWidth = 0;

            row.ActionButton = AddButton(rowGo.transform, "", () => OnActionClicked(index), out row.ActionLabel);
            var actionLe = row.ActionButton.GetComponent<LayoutElement>();
            actionLe.preferredWidth = 130;
            actionLe.flexibleWidth = 0;
            return row;
        }

        private GameObject BuildPanel(Transform parent, string name, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(width, height); // anchors default to center
            go.GetComponent<Image>().color = PanelColor;

            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 20, 20);
            layout.spacing = 10;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.UpperCenter;
            return go;
        }

        private GameObject AddRow(Transform parent, float height)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 12;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.MiddleCenter;

            var element = go.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            return go;
        }

        private Text AddText(Transform parent, string content, int size, TextAnchor anchor, float height)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.text = content;

            var element = go.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            return text;
        }

        private Button AddButton(Transform parent, string label, UnityAction onClick, out Text labelText)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button),
                typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = ButtonColor;

            var element = go.GetComponent<LayoutElement>();
            element.preferredHeight = 44;
            element.minHeight = 38;

            var button = go.GetComponent<Button>();
            button.onClick.AddListener(onClick);

            var textGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var rect = (RectTransform)textGo.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            labelText = textGo.GetComponent<Text>();
            labelText.font = _font;
            labelText.fontSize = 18;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = Color.white;
            labelText.text = label;
            return button;
        }

        private InputField AddInputRow(Transform parent, string label, string initialValue,
            InputField.ContentType contentType)
        {
            var row = AddRow(parent, 46);

            var labelText = AddText(row.transform, label, 18, TextAnchor.MiddleLeft, 40);
            var labelElement = labelText.GetComponent<LayoutElement>();
            labelElement.preferredWidth = 110;
            labelElement.flexibleWidth = 0;

            var go = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField),
                typeof(LayoutElement));
            go.transform.SetParent(row.transform, false);
            go.GetComponent<Image>().color = InputColor;
            var element = go.GetComponent<LayoutElement>();
            element.preferredHeight = 40;
            element.flexibleWidth = 1;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var rect = (RectTransform)textGo.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10, 6);
            rect.offsetMax = new Vector2(-10, -6);

            var text = textGo.GetComponent<Text>();
            text.font = _font;
            text.fontSize = 18;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.supportRichText = false;

            var input = go.GetComponent<InputField>();
            input.textComponent = text;
            input.contentType = contentType;
            input.text = initialValue;
            return input;
        }
    }
}
