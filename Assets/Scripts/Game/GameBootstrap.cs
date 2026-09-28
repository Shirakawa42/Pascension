using Pascension.Content;
using Pascension.Engine.Core;
using Pascension.Game.UI;
using Pascension.Net;
using UnityEngine;

namespace Pascension.Game
{
    /// <summary>Binds the game screen to the session created by the multiplayer lobby.</summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        public GameScreen Screen;

        private void Start()
        {
            if (SessionProvider.Current == null)
            {
                SceneFlow.LoadMenu();
                return;
            }
            if (Screen == null)
            {
                Debug.LogError("GameBootstrap: no GameScreen assigned — run Pascension/Setup/Build All Scenes.");
                return;
            }
            ContentRegistry.RegisterAll();
            var rules = SessionProvider.Current is NetworkSession ns ? ns.Rules as GameRules
                : NetLobbyData.Config is GameConfig cfg ? cfg.Rules : null;
            Screen.Bind(SessionProvider.Current, rules ?? new GameRules());
        }
    }
}
