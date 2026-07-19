using System;
using System.Threading.Tasks;
using Pascension.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Pascension.Game.Update
{
    /// <summary>
    /// Silent update check against the GitHub Releases manifest. One check per app
    /// run (static cache) — returning to the menu from a game reuses the result.
    /// ANY failure (no internet, 404 before the first release, malformed JSON) is
    /// silent: no button, no error, one Debug.Log line.
    /// </summary>
    public static class UpdateChecker
    {
        public const string DefaultManifestUrl =
            "https://github.com/Shirakawa42/Pascension/releases/latest/download/latest.json";
        public const string ReleasesPageUrl =
            "https://github.com/Shirakawa42/Pascension/releases/latest";

        /// <summary>Test override: -updateurl <url> on the command line (AutoClientDriver
        /// arg pattern), or set directly from editor/MCP code.</summary>
        public static string OverrideUrl;

        public static string ManifestUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideUrl)) return OverrideUrl;
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], "-updateurl", StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return DefaultManifestUrl;
            }
        }

        public enum State { Unchecked, Checking, UpToDate, UpdateAvailable, Failed }

        public static State Current { get; private set; } = State.Unchecked;
        public static UpdateManifest Manifest { get; private set; }

        private static Task _inFlight;

        /// <summary>Fetch + parse + compare with Application.version. One check per app
        /// run: concurrent/later callers await the SAME in-flight task (a menu reload
        /// mid-check must still learn the result). Skipped in batchmode, and in the
        /// editor unless an override URL is set (the editor is permanently "1.0" and
        /// would always see the button).</summary>
        public static Task CheckAsync() => _inFlight ??= CheckInternalAsync();

        private static async Task CheckInternalAsync()
        {
            if (Application.isBatchMode ||
                (Application.isEditor && ManifestUrl == DefaultManifestUrl))
            {
                Current = State.Failed;
                Debug.Log("[Update] check skipped (editor/batchmode)");
                return;
            }

            Current = State.Checking;
            try
            {
                var manifest = await FetchManifestAsync(10);
                Manifest = manifest;
                Current = VersionCompare.IsNewer(manifest.Version, Application.version)
                    ? State.UpdateAvailable
                    : State.UpToDate;
                Debug.Log("[Update] check: installed v" + Application.version +
                          ", latest v" + manifest.Version + " → " + Current);
            }
            catch (Exception e)
            {
                Current = State.Failed;
                Debug.Log("[Update] check failed (silent): " + e.Message);
            }
        }

        /// <summary>Fetch and validate the manifest. Throws UpdateFailedException with a
        /// friendly message on any failure — the installer also calls this right before
        /// downloading so a stale cached hash never mismatches a newer release.</summary>
        public static async Task<UpdateManifest> FetchManifestAsync(int timeoutSeconds)
        {
            using var request = UnityWebRequest.Get(ManifestUrl);
            request.timeout = timeoutSeconds;
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
                throw new UpdateFailedException("Download failed — check your connection.",
                    new Exception(request.error + " (" + request.responseCode + ") " + ManifestUrl));

            if (!UpdateManifest.TryParse(request.downloadHandler.text, out var manifest, out string error))
                throw new UpdateFailedException("Download failed — check your connection.",
                    new Exception(error));
            return manifest;
        }

        /// <summary>The package for the running platform (editor maps to its host OS);
        /// null when the manifest doesn't carry this platform → manual-download fallback.</summary>
        public static UpdatePackage PackageForThisPlatform(UpdateManifest manifest)
        {
            if (manifest?.Platforms == null) return null;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return manifest.Platforms.Windows;
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return manifest.Platforms.Macos;
                default:
                    return null;
            }
        }
    }
}
