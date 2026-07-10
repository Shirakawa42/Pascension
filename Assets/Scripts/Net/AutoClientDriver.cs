using System;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using UnityEngine;

namespace Pascension.Net
{
    /// <summary>
    /// Headless-ish verification client: launch a build with
    ///   pascension.exe -joincode ABC123 [-playername Name]
    /// and it joins that game over Relay, auto-readies in the lobby, and auto-plays
    /// (pass priority / default decisions) so a real remote seat exists end-to-end.
    /// Inert without the -joincode argument — normal players never see this.
    /// </summary>
    public static class AutoClientDriver
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var args = Environment.GetCommandLineArgs();
            string code = null, name = "AutoClient";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-joincode", StringComparison.OrdinalIgnoreCase)) code = args[i + 1];
                if (string.Equals(args[i], "-playername", StringComparison.OrdinalIgnoreCase)) name = args[i + 1];
            }
            if (string.IsNullOrEmpty(code)) return;

            var go = new GameObject("AutoClientDriver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AutoClientBehaviour>().Configure(code, name);
        }
    }

    /// <summary>Joins, auto-readies, auto-plays. Logs with the [AutoClient] prefix so the
    /// battery can assert against Player.log.</summary>
    public sealed class AutoClientBehaviour : MonoBehaviour
    {
        private string _code;
        private ISession _boundSession;
        private float _readyPoll;
        private bool _joining;

        public void Configure(string code, string name)
        {
            _code = code;
            ClientIdentity.PlayerName = name;
            Debug.Log("[AutoClient] configured: join " + code + " as " + name +
                      " guid " + ClientIdentity.Guid.Substring(0, 8));
            Join();
        }

        private async void Join()
        {
            if (_joining) return;
            _joining = true;
            try
            {
                await NetLauncher.JoinAsync(_code);
                Debug.Log("[AutoClient] JoinAsync ok — waiting for lobby/game sync");
            }
            catch (Exception e)
            {
                Debug.Log("[AutoClient] JOIN FAILED: " + e.Message);
            }
            finally
            {
                _joining = false;
            }
        }

        private void Update()
        {
            // Lobby: auto-ready once we occupy a slot.
            _readyPoll += Time.deltaTime;
            if (_readyPoll > 1f)
            {
                _readyPoll = 0f;
                var manager = Unity.Netcode.NetworkManager.Singleton;
                var lobby = LobbyNetBehaviour.Instance;
                if (manager != null && manager.IsConnectedClient && lobby != null && lobby.IsSpawned)
                {
                    foreach (var slot in lobby.State.Slots)
                        if (slot.Kind == LobbySlotKind.Human && slot.ClientId == manager.LocalClientId && !slot.Ready)
                        {
                            Debug.Log("[AutoClient] auto-ready");
                            lobby.SetReadyRpc(true);
                        }
                }
            }

            // Game: bind to whatever session appears and auto-answer inputs.
            var session = SessionProvider.Current;
            if (!ReferenceEquals(session, _boundSession))
            {
                if (_boundSession != null)
                    _boundSession.InputRequested -= OnInputRequested;
                _boundSession = session;
                if (_boundSession != null)
                {
                    _boundSession.InputRequested += OnInputRequested;
                    Debug.Log("[AutoClient] session bound (seat " + _boundSession.LocalPlayerIndex + ")");
                }
            }
        }

        private void OnInputRequested(PendingSnap pending)
        {
            if (_boundSession == null || pending == null) return;
            PlayerAction action;
            if (pending.Kind == PendingInputKind.Decision && pending.Decision != null)
            {
                var req = pending.Decision;
                var answer = new Engine.Decisions.DecisionAnswer { DecisionId = req.Id };
                answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
                for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                    if (!answer.ChosenOptionIds.Contains(req.Options[i].Id))
                        answer.ChosenOptionIds.Add(req.Options[i].Id);
                while (answer.ChosenOptionIds.Count > req.Max)
                    answer.ChosenOptionIds.RemoveAt(answer.ChosenOptionIds.Count - 1);
                action = new SubmitDecisionAction { Answer = answer };
            }
            else
            {
                action = null;
                if (pending.LegalActions != null)
                    foreach (var legal in pending.LegalActions)
                        if (legal is PassPriorityAction)
                        {
                            action = legal;
                            break;
                        }
                if (action == null && pending.LegalActions != null && pending.LegalActions.Count > 0)
                    action = pending.LegalActions[0];
                if (action == null) return;
            }
            Debug.Log("[AutoClient] auto-answer: " + action.Describe());
            _boundSession.SubmitAction(action);
        }
    }
}
