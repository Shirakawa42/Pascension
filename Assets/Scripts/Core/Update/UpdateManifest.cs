using Newtonsoft.Json;

namespace Pascension.Core
{
    /// <summary>One downloadable build in the update manifest.</summary>
    public sealed class UpdatePackage
    {
        public string Url;
        public string Sha256;     // 64 hex chars, compared case-insensitively
        public long SizeBytes;
    }

    /// <summary>Per-platform packages; a missing platform is legal (the updater falls
    /// back to opening the releases page).</summary>
    public sealed class UpdatePlatforms
    {
        public UpdatePackage Windows;
        public UpdatePackage Macos;
    }

    /// <summary>
    /// The latest.json release manifest — the CI/updater contract. Published by the
    /// release workflow as an asset on every GitHub Release; fetched by the game via
    /// the stable /releases/latest/download/latest.json redirect. Schema:
    /// { "version":"1.0.123", "tag":"v1.0.123", "publishedAt":"...",
    ///   "platforms": { "windows": {url,sha256,sizeBytes}, "macos": {...} } }
    /// </summary>
    public sealed class UpdateManifest
    {
        public string Version;
        public string Tag;
        public string PublishedAt;
        public UpdatePlatforms Platforms;

        /// <summary>Parse + validate. Error strings are English and log-only (never
        /// shown raw in UI). Returns false on malformed JSON, missing version, or a
        /// present package missing its URL/hash.</summary>
        public static bool TryParse(string json, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            try
            {
                manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
            }
            catch (JsonException e)
            {
                error = "Malformed manifest JSON: " + e.Message;
                return false;
            }
            if (manifest == null)
            {
                error = "Manifest is empty";
                return false;
            }
            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                error = "Manifest has no version";
                return false;
            }
            if (!ValidatePackage(manifest.Platforms?.Windows, "windows", out error)) return false;
            if (!ValidatePackage(manifest.Platforms?.Macos, "macos", out error)) return false;
            error = null;
            return true;
        }

        private static bool ValidatePackage(UpdatePackage package, string name, out string error)
        {
            error = null;
            if (package == null) return true; // absent platform is legal
            if (string.IsNullOrWhiteSpace(package.Url))
            {
                error = "Manifest package '" + name + "' has no url";
                return false;
            }
            if (!IsSha256Hex(package.Sha256))
            {
                error = "Manifest package '" + name + "' has an invalid sha256";
                return false;
            }
            return true;
        }

        private static bool IsSha256Hex(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
