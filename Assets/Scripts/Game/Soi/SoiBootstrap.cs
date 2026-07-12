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

            var config = module.BuildConfig(MatchSetup.Seed, specs, MatchSetup.DlcFlags);
            var adapter = module.CreateEngine(config);

            Host = new GameHost(adapter, specs.Count, module.ResponseTimeoutOf(config));
            Session = new LocalSession(Host, 0);
            Host.AttachSeat(Session, isHuman: true);

            for (int i = 1; i < specs.Count; i++)
            {
                ulong botSeed = MatchSetup.Seed ^ ((ulong)i * 0x9E3779B97F4A7C15UL);
                var seat = new BotSeat(i, module.CreateBot("heuristic", botSeed, adapter), BotThinkDelay);
                seat.Bind(Host);
                Host.AttachSeat(seat, isHuman: false);
                _botSeats.Add(seat);
            }

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
