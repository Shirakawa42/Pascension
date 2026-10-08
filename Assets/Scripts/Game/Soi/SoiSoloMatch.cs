using System;
using Pascension.Core;
using Pascension.Game.UI;
using Pascension.Net;
using Shards.AI;
using Shards.Content;
using Shards.Engine;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pascension.Game.Soi
{
    /// <summary>Local human-versus-frozen-policy match; no networking or external process.</summary>
    public sealed class SoiSoloMatch : MonoBehaviour
    {
        private GameHost _host;
        private IPlayablePolicyEngine _engine;
        private LocalSession _session;
        private bool _started, _failed;
        private float _nextDecision;
        private const float ActionDelaySeconds = 1f;
        public static SoiSoloMatch Current { get; private set; }
        public ShardsConfig Config { get; private set; }
        public int HumanSeat { get; private set; }
        public SoiAiProfile Profile { get; private set; }

        public static void Launch() => Launch(SoiAiProfile.Auld);

        public static void Launch(SoiAiProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (Current != null) Destroy(Current.gameObject);
            NetLauncher.Shutdown();
            var asset = Resources.Load<TextAsset>(profile.PolicyResource);
            if (asset == null) throw new InvalidOperationException("The Shards policy asset is missing");
            // Keep the deployed search profile explicit and independent of
            // experimental defaults used by the headless benchmarking tools.
            var settingsAsset = Resources.Load<TextAsset>(profile.SearchResource);
            if (settingsAsset == null) throw new InvalidOperationException("The Shards search settings asset is missing");
            var searchSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<PolicySearchSettings>(settingsAsset.text)
                ?? throw new InvalidOperationException("The Shards search settings asset is invalid");
            ulong seed = (ulong)DateTime.UtcNow.Ticks;
            var go = new GameObject("Shards solo match");
            var match = go.AddComponent<SoiSoloMatch>();
            Current = match; DontDestroyOnLoad(go);
            match.Profile = profile;
            match.HumanSeat = (int)(seed & 1);
            var players = new System.Collections.Generic.List<PlayerSpec>();
            for (int seat = 0; seat < 2; seat++) players.Add(new PlayerSpec
            {
                Name = seat == match.HumanSeat ? Loc.T("You") : profile.Name,
                CharacterId = seat == 0 ? "decima" : "tetra",
                FullControl = seat == match.HumanSeat && PlayerPrefs.GetInt(SceneFlow.PrefFullControl, 0) == 1
            });
            match.Config = ShardsContentRegistry.StandardConfig(seed, players, ShardsDlc.Duel);
            match._engine = profile.FullInformation
                ? (IPlayablePolicyEngine)new Shards.Nyou.PolicyEngine(match.Config, new Shards.Nyou.FrozenPolicy(asset.bytes), unchecked((int)(seed >> 1)), searchSettings)
                : new PolicyEngine(match.Config, new FrozenPolicy(asset.bytes), unchecked((int)(seed >> 1)), tacticalSearch: true, searchSettings: searchSettings);
            match._host = new GameHost(match._engine, 2, 0);
            match._engine.BindSubmit(action => match._host.Submit(action.PlayerIndex, action));
            match._session = new LocalSession(match._host, match.HumanSeat);
            match._host.AttachSeat(match._session, true);
            SessionProvider.Current = match._session;
            SceneFlow.LoadGame("GameShards");
        }

        private void Update()
        {
            if (SceneManager.GetActiveScene().name != "GameShards")
            {
                if (_started) Destroy(gameObject);
                return;
            }
            if (_failed) return;
            if (!_started) { _started = true; _host.Start(); _nextDecision = Time.unscaledTime + ActionDelaySeconds; return; }
            if (_engine.GameOver || _engine.PendingInput == null || _engine.PendingInput.PlayerIndex == HumanSeat) return;
            try
            {
                _engine.PreparePolicy();
                if (Time.unscaledTime < _nextDecision) return;
                if (_engine.TryStepPolicy())
                    _nextDecision = Time.unscaledTime + ActionDelaySeconds;
            }
            catch (Exception error) { _failed = true; Debug.LogException(error); }
        }

        private void OnDestroy()
        {
            if (SessionProvider.Current == _session) SessionProvider.Clear();
            if (Current == this) Current = null;
        }
    }
}
