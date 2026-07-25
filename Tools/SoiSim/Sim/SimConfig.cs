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

        /// <summary>The DLC mask every command simulates under — ALL DLC including Duel
        /// of Doom, which is the configuration the game actually ships and is played in.
        ///
        /// This defaulted to the three content DLCs until 2026-07-25, so every net, every
        /// CMA-ES weight vector and every benchmark before that date was fit to a game
        /// without hero drafts, hero abilities or row rerolls. Mirror matches hid it
        /// perfectly (both bots shared the blind spot) while a human who used those
        /// mechanics extracted the whole edge. Train on the game you ship.
        ///
        /// `--dlc base` (parsed in Program.Main) drops Duel for legacy/comparison runs.
        /// ConfigHash includes the mask, so duel and base runs self-segregate into
        /// different JSONL files.</summary>
        public static ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

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
