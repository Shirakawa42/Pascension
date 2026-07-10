using UnityEngine.SceneManagement;

namespace Pascension.Game.UI
{
    /// <summary>Scene names + navigation helpers and shared PlayerPrefs keys.</summary>
    public static class SceneFlow
    {
        public const string MenuScene = "MainMenu";
        public const string GameScene = "Game";

        public const string PrefFullControl = "pascension.fullControl";
        public const string PrefMasterVolume = "pascension.volume.master";
        public const string PrefMusicVolume = "pascension.volume.music";

        public static void LoadGame() => SceneManager.LoadScene(GameScene);

        /// <summary>Load a specific game's table scene (two-game split).</summary>
        public static void LoadGame(string sceneName) => SceneManager.LoadScene(sceneName);

        public static void LoadMenu() => SceneManager.LoadScene(MenuScene);
    }
}
