namespace Pascension.Core
{
    /// <summary>
    /// Dotted-numeric version comparison for update checks and the multiplayer version
    /// gate. Segment-wise and numeric ("1.0.9" &lt; "1.0.10"), missing segments count as 0
    /// ("1.0" == "1.0.0" &lt; "1.0.1"), and null/empty/garbage segments count as 0 so a
    /// malformed manifest can never look "newer" than a real build.
    /// </summary>
    public static class VersionCompare
    {
        public static int Compare(string a, string b)
        {
            string[] partsA = (a ?? "").Split('.');
            string[] partsB = (b ?? "").Split('.');
            int length = partsA.Length > partsB.Length ? partsA.Length : partsB.Length;
            for (int i = 0; i < length; i++)
            {
                long segA = Segment(partsA, i);
                long segB = Segment(partsB, i);
                if (segA != segB) return segA < segB ? -1 : 1;
            }
            return 0;
        }

        public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

        /// <summary>Leading digits of the segment ("10", "10-beta" → 10); absent or
        /// non-numeric → 0.</summary>
        private static long Segment(string[] parts, int i)
        {
            if (i >= parts.Length) return 0;
            string s = parts[i];
            long value = 0;
            bool any = false;
            for (int c = 0; c < s.Length; c++)
            {
                if (s[c] < '0' || s[c] > '9') break;
                value = value * 10 + (s[c] - '0');
                any = true;
                if (value > int.MaxValue) return int.MaxValue; // absurd segment; clamp
            }
            return any ? value : 0;
        }
    }
}
