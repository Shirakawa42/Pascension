using UnityEngine;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Verbose presentation-layer logging, greppable by area tag. Enabled by default so
    /// automated play-tests can verify animation/interaction sequencing from the console;
    /// flip <see cref="Enabled"/> off for release builds.
    /// </summary>
    public static class UiLog
    {
        public static bool Enabled = true;

        public static void Log(string area, string message)
        {
            if (Enabled)
                Debug.Log($"[UI:{area}] {message}");
        }
    }
}
