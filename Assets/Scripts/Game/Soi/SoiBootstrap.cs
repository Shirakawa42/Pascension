using Pascension.Game.UI;
using Pascension.Net;
using UnityEngine;

namespace Pascension.Game.Soi
{
    /// <summary>Binds the game screen to the session created by the multiplayer lobby.</summary>
    public sealed class SoiBootstrap : MonoBehaviour
    {
        public SoiGameScreen Screen;

        private void Start()
        {
            if (SessionProvider.Current == null)
            {
                SceneFlow.LoadMenu();
                return;
            }
            if (Screen == null)
            {
                Debug.LogError("SoiBootstrap: no SoiGameScreen assigned — run Pascension/Setup/Build All Scenes.");
                return;
            }
            var module = GameCatalog.Get("shards");
            object rules = SoiSoloMatch.Current != null ? SoiSoloMatch.Current.Config.Rules
                : SessionProvider.Current is NetworkSession ns ? ns.Rules
                : NetLobbyData.Config != null ? module.RulesOf(NetLobbyData.Config) : null;
            Screen.Bind(SessionProvider.Current, rules ?? module.Codec.CreateRules());
        }
    }
}
