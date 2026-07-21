using System.Collections.Generic;
using Pascension.Core;
using Pascension.Game.UI;
using Pascension.Net;
using UnityEngine;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Boots a Shards of Infinity match. Networked play binds to the session
    /// HostMatchStarter built; solo builds an in-process host through the ShardsModule
    /// (mirrors GameBootstrap, but everything routes through the game module so this
    /// file never touches Shards types directly except the rules cast).
    /// </summary>
    public sealed class SoiBootstrap : MonoBehaviour
    {
        public SoiGameScreen Screen;

        [Tooltip("Seconds a bot 'thinks' before submitting.")]
        public float BotThinkDelay = 0.6f;

        public LocalSession Session { get; private set; }
        public GameHost Host { get; private set; }

        private readonly List<BotSeat> _botSeats = new List<BotSeat>();
        private readonly List<SearchBotSeat> _searchSeats = new List<SearchBotSeat>();

        private void Start()
        {
            var module = GameCatalog.Get("shards");

            if (SessionProvider.Current != null)
            {
                if (Screen != null)
                {
                    object rules = SessionProvider.Current is NetworkSession ns ? ns.Rules
                        : NetLobbyData.Config != null ? module.RulesOf(NetLobbyData.Config)
                        : null;
                    Screen.Bind(SessionProvider.Current, rules ?? module.Codec.CreateRules());
                }
                else
                    Debug.LogError("SoiBootstrap: no SoiGameScreen assigned — run Pascension/Setup/Build All Scenes.");
                return;
            }

            MatchSetup.EnsureDefaults();

            var specs = new List<PlayerSpec>
            {
                new PlayerSpec
                {
                    Name = MatchSetup.PlayerName,
                    CharacterId = ValidCharacter(module, MatchSetup.PlayerHeroId, 0),
                    FullControl = false
                }
            };
            for (int i = 0; i < MatchSetup.Opponents.Count && specs.Count < module.MaxPlayers; i++)
            {
                string character = ValidCharacter(module, MatchSetup.Opponents[i].HeroId, specs.Count);
                specs.Add(new PlayerSpec
                {
                    Name = module.CharacterDisplayName(character) + " (Bot)",
                    CharacterId = character,
                    IsBot = true
                });
            }

            // Random first player: seat 0 always starts, so shuffle who sits where —
            // the Mastery stagger is keyed by seat index and follows automatically.
            var order = new List<int>();
            for (int i = 0; i < specs.Count; i++) order.Add(i);
            new Pascension.Engine.Core.DeterministicRng(MatchSetup.Seed, sequence: 131UL).Shuffle(order);
            var seated = new List<PlayerSpec>();
            foreach (int o in order) seated.Add(specs[o]);
            int humanSeat = order.IndexOf(0);

            var config = module.BuildConfig(MatchSetup.Seed, seated, MatchSetup.DlcFlags);
            var adapter = module.CreateEngine(config);

            Host = new GameHost(adapter, seated.Count, module.ResponseTimeoutOf(config));
            Session = new LocalSession(Host, humanSeat);
            Host.AttachSeat(Session, isHuman: true);

            // One rank for every bot seat (the SoI difficulty ladder).
            string botKind = string.IsNullOrEmpty(MatchSetup.SoiBotKind) ? "rank:bronze" : MatchSetup.SoiBotKind;
            bool searchKind = Shards.Bots.ShardsBotRanks.IsSearchKind(botKind);
            for (int i = 0; i < seated.Count; i++)
            {
                if (i == humanSeat) continue;
                // (i + 1): a bot in seat 0 must never share the engine's own seed.
                ulong botSeed = MatchSetup.Seed ^ (((ulong)i + 1) * 0x9E3779B97F4A7C15UL);
                var agent = module.CreateBot(botKind, botSeed, adapter);
                if (searchKind)
                {
                    // Search ranks think up to seconds per decision — off the main thread.
                    var seat = new SearchBotSeat(i, agent, Host);
                    seat.SeatFaulted += (player, error) =>
                        Debug.LogError($"SoI search bot seat {player} faulted (safe default submitted): {error}");
                    seat.SearchCompleted += (player, iterations, ms) =>
                        Debug.Log($"SoI search seat {player}: {iterations} iterations in {ms} ms");
                    Host.AttachSeat(seat, isHuman: false);
                    _searchSeats.Add(seat);
                }
                else
                {
                    var seat = new BotSeat(i, agent, BotThinkDelay);
                    seat.Bind(Host);
                    Host.AttachSeat(seat, isHuman: false);
                    _botSeats.Add(seat);
                }
            }

            if (Screen != null && _searchSeats.Count > 0)
                Screen.IsBotThinking = player =>
                {
                    for (int s = 0; s < _searchSeats.Count; s++)
                        if (_searchSeats[s].PlayerIndex == player)
                            return _searchSeats[s].IsThinking;
                    return false;
                };

            if (Screen != null)
                Screen.Bind(Session, module.RulesOf(config));
            else
                Debug.LogError("SoiBootstrap: no SoiGameScreen assigned — run Pascension/Setup/Build All Scenes.");

            Host.Start();
        }

        private static string ValidCharacter(IGameModule module, string requested, int slot)
        {
            var characters = module.CharactersFor(MatchSetup.DlcFlags);
            foreach (var c in characters)
                if (c.Id == requested)
                    return requested;
            return module.DefaultCharacterFor(slot, MatchSetup.DlcFlags);
        }

        private void Update()
        {
            if (Host == null) return;
            Host.Tick(Time.deltaTime);
            for (int i = 0; i < _botSeats.Count; i++)
                _botSeats[i].Tick(Time.deltaTime);
        }
    }
}
