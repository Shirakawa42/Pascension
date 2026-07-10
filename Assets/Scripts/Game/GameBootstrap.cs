using System.Collections.Generic;
using Pascension.Bots;
using Pascension.Content;
using Pascension.Engine.Core;
using Pascension.Engine.Heroes;
using Pascension.Game.UI;
using Pascension.Net;
using UnityEngine;

namespace Pascension.Game
{
    /// <summary>
    /// Boots a solo match: content registration, engine + host construction, one
    /// LocalSession for the human seat and a BotSeat per opponent. NGO is never
    /// started here — solo play is fully in-process.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        public GameScreen Screen;

        [Tooltip("Seconds a bot 'thinks' before submitting.")]
        public float BotThinkDelay = 0.7f;

        /// <summary>The human seat's session — exposed for the UI / debugging.</summary>
        public LocalSession Session { get; private set; }

        public GameHost Host { get; private set; }

        private readonly List<BotSeat> _botSeats = new List<BotSeat>();

        private void Start()
        {
            // Networked play (host or client): HostMatchStarter has already built the
            // session — the UI just binds to it. Solo builds its own host below.
            if (SessionProvider.Current != null)
            {
                ContentRegistry.RegisterAll();
                if (Screen != null)
                {
                    // Host: the lobby-built config's rules. Client: the NetworkSession's
                    // rules object (populated in place by the host's resync).
                    var rules = SessionProvider.Current is NetworkSession ns ? ns.Rules
                        : NetLobbyData.Config != null ? NetLobbyData.Config.Rules
                        : new GameRules();
                    Screen.Bind(SessionProvider.Current, rules);
                }
                else
                    Debug.LogError("GameBootstrap: no GameScreen assigned — run Pascension/Setup/Build All Scenes.");
                return;
            }

            MatchSetup.EnsureDefaults();
            ContentRegistry.RegisterAll();

            var players = new List<PlayerConfig>
            {
                new PlayerConfig
                {
                    Name = MatchSetup.PlayerName,
                    HeroId = MatchSetup.PlayerHeroId,
                    FullControl = PlayerPrefs.GetInt(SceneFlow.PrefFullControl, 0) == 1
                }
            };

            for (int i = 0; i < MatchSetup.Opponents.Count && players.Count < 4; i++)
            {
                var opp = MatchSetup.Opponents[i];
                players.Add(new PlayerConfig
                {
                    Name = BotName(opp.HeroId, i),
                    HeroId = opp.HeroId
                });
            }

            var config = ContentRegistry.StandardConfig(MatchSetup.Seed, players);

            Host = new GameHost(config);
            Session = new LocalSession(Host, 0);
            Host.AttachSeat(Session, isHuman: true);

            for (int i = 1; i < players.Count; i++)
            {
                var opp = MatchSetup.Opponents[i - 1];
                ulong botSeed = MatchSetup.Seed ^ ((ulong)i * 0x9E3779B97F4A7C15UL);
                ISyncAgent agent = opp.Bot == BotKind.Random
                    ? new RandomBot(botSeed)
                    : (ISyncAgent)new HeuristicBot(botSeed);
                var seat = new BotSeat(i, agent, BotThinkDelay);
                seat.Bind(Host);
                Host.AttachSeat(seat, isHuman: false);
                _botSeats.Add(seat);
            }

            if (Screen != null)
                Screen.Bind(Session, config.Rules);
            else
                Debug.LogError("GameBootstrap: no GameScreen assigned — run Pascension/Setup/Build All Scenes.");

            Host.Start();
        }

        private void Update()
        {
            if (Host == null) return;
            Host.Tick(Time.deltaTime);
            for (int i = 0; i < _botSeats.Count; i++)
                _botSeats[i].Tick(Time.deltaTime);
        }

        private static string BotName(string heroId, int index)
        {
            if (HeroDatabase.All.Count > 0)
            {
                foreach (var hero in HeroDatabase.All)
                {
                    if (hero.Id == heroId)
                    {
                        // "Wren the Scout" → "Wren (Bot)"
                        string first = hero.Name;
                        int space = first.IndexOf(' ');
                        if (space > 0) first = first.Substring(0, space);
                        return first + " (Bot)";
                    }
                }
            }
            return "Bot " + (index + 1);
        }
    }
}
