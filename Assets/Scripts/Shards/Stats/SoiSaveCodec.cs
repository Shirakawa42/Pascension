using System;
using System.Security.Cryptography;
using System.Text;

namespace Shards.Stats
{
    /// <summary>File format: base64(HMAC-SHA256(payload)) + "\n" + payloadJson. The MAC
    /// is tamper-evidence for the local file / cloud blob, not secrecy.</summary>
    public static class SoiSaveCodec
    {
        public static string Encode(SoiSaveData data, byte[] secret)
        {
            string payload = SoiStatsJson.Serialize(data);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            using (var hmac = new HMACSHA256(secret))
                return Convert.ToBase64String(hmac.ComputeHash(payloadBytes)) + "\n" + payload;
        }

        /// <summary>expectedProfileKey null skips the profile check (pre-login load).</summary>
        public static bool TryDecode(string text, byte[] secret, string expectedProfileKey,
            out SoiSaveData data)
        {
            data = null;
            if (text == null || secret == null) return false;
            int split = text.IndexOf('\n');
            if (split < 0) return false;

            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(text.Substring(0, split));
            }
            catch (FormatException)
            {
                return false;
            }

            string payload = text.Substring(split + 1);
            byte[] actual;
            using (var hmac = new HMACSHA256(secret))
                actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            if (!FixedTimeEquals(expected, actual)) return false;

            SoiSaveData parsed;
            try
            {
                parsed = SoiStatsJson.Deserialize<SoiSaveData>(payload);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
            if (parsed == null) return false;
            if (expectedProfileKey != null && parsed.ProfileKey != expectedProfileKey) return false;

            data = parsed;
            return true;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            // Fixed-time loop — no early exit on the first mismatching byte.
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
