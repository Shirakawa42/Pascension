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

        public static void LoadMenu() => SceneManager.LoadScene(MenuScene);
    }
}
