using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shards.Engine;

namespace SoiSim
{
    public static class SimConfig
    {
        public const int SchemaVersion = 1;

        public const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        /// <summary>SHA-256 over the canonical config JSON — the merge guard for JSONL files.</summary>
        public static string ConfigHash(int dlc, ShardsRules rules, string bots, int budget, string botVersion)
        {
            string canonical = SimJson.Line(new
            {
                schema = SchemaVersion,
                dlc,
                rules = new
                {
                    startingHealth = rules.StartingHealth,
                    maxHealth = rules.MaxHealth,
                    handSize = rules.HandSize,
                    masteryCap = rules.MasteryCap,
                    centerRowSize = rules.CenterRowSize
                },
                bots,
                budget,
                botVersion
            });
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var sb = new StringBuilder("sha256:");
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Repo root = nearest ancestor containing CLAUDE.md (same idiom as the
        /// ShardsCardExportTests exports).</summary>
        public static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repo root (CLAUDE.md) not found above " + AppContext.BaseDirectory);
        }
    }
}
