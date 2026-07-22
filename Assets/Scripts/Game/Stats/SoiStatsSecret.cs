using System.Security.Cryptography;
using System.Text;

namespace Pascension.Game.Stats
{
    /// <summary>
    /// HMAC key for the local stats files. TAMPER DETERRENCE ONLY, not security —
    /// anyone with the binary can reassemble it; it just keeps a text editor from
    /// silently editing win counts. Assembled from split fragments so the key never
    /// appears whole as a literal. MUST stay deterministic across platforms, installs
    /// and app versions (never derive from anything machine- or version-specific):
    /// it has to decode saves written by any other copy of the game.
    /// </summary>
    internal static class SoiStatsSecret
    {
        private static byte[] _key;

        internal static byte[] Key => _key ??= Build();

        private static byte[] Build()
        {
            // Split obfuscated fragments — reordered and hex-folded before hashing.
            const string a = "cension.s";
            const string b = "tats.v1";
            const string c = "pas";
            byte[] d = { 0x5A, 0x11, 0xC7, 0x2E, 0x83, 0x4B, 0xD9, 0x60, 0x1F, 0xA4 };

            var sb = new StringBuilder(64);
            sb.Append(c).Append(a).Append(b).Append('/');
            for (int i = 0; i < d.Length; i++)
                sb.Append(d[d.Length - 1 - i].ToString("x2"));
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }
}
