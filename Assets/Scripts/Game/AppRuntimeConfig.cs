using UnityEngine;
using UnityEngine.Rendering;

namespace Pascension.Game
{
    /// <summary>
    /// App-wide runtime configuration applied once at startup.
    ///
    /// Governs the frame rate so the game never renders UNCAPPED and never burns the GPU
    /// on a window nobody is looking at. Both games are static 2D scenes that need no more
    /// than 60 FPS, but nothing else bounds the rate: the active quality level can have
    /// vSync off (the editor default here is "Very Low", vSyncCount 0, and a build user can
    /// pick any vSync-off level) and Application.targetFrameRate defaults to -1 ("as fast
    /// as possible"). Without a cap the renderer spins out thousands of FPS and pins the
    /// GPU at 100%.
    ///
    /// A single foreground cap is not enough: runInBackground is on (so an online host /
    /// solo bot turns keep resolving while the window is unfocused), and with vSync off a
    /// backgrounded window still rendering at 60 FPS keeps the GPU boosted at full power —
    /// there are no vSync sync points to let it idle. So a <see cref="FrameRateGovernor"/>
    /// re-applies the cap on every focus change and throttles HARD in the background (a
    /// handful of frames per second — ample for the host to keep ticking), then restores
    /// 60 the instant focus returns.
    ///
    /// Skipped in batch mode so headless sims and the -runTests harness run flat out.
    /// </summary>
    public static class AppRuntimeConfig
    {
        /// <summary>Foreground cap — smooth for a card game, trivial GPU load.</summary>
        public const int ForegroundFrameRate = 60;

        /// <summary>Background cap — the window is unfocused, so render a few frames per
        /// second only (keeps the host loop alive without spinning the GPU).</summary>
        public const int BackgroundFrameRate = 8;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (Application.isBatchMode) return;
            Application.targetFrameRate = ForegroundFrameRate;

            var go = new GameObject("~FrameRateGovernor") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<FrameRateGovernor>();
        }

        private sealed class FrameRateGovernor : MonoBehaviour
        {
            private void Start() => ApplyForFocus(Application.isFocused);
            private void OnApplicationFocus(bool focused) => ApplyForFocus(focused);

            private static void ApplyForFocus(bool focused)
            {
                Application.targetFrameRate = focused ? ForegroundFrameRate : BackgroundFrameRate;
                // Second, independent lever: render only every Nth loop frame while
                // backgrounded, so even if a platform ignores targetFrameRate on focus
                // loss the GPU still does almost no rendering work.
                OnDemandRendering.renderFrameInterval = focused ? 1 : 3;
            }
        }
    }
}
