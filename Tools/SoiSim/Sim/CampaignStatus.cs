using System;
using System.IO;

namespace SoiSim
{
    /// <summary>User-facing campaign monitoring. Two files under Tools/ShardsData:
    ///  - campaign-status.md — the LIVE view (rewritten every few seconds by whatever
    ///    long command is running); open it in the editor, it reloads on change.
    ///  - campaign-log.md — append-only dated history of completed steps (selfplay
    ///    runs, trainings, probes, rank mints). The campaign's paper trail.</summary>
    public static class CampaignStatus
    {
        private static string Dir => Path.Combine(SimConfig.FindRepoRoot(), "Tools", "ShardsData");
        public static string StatusPath => Path.Combine(Dir, "campaign-status.md");
        public static string LogPath => Path.Combine(Dir, "campaign-log.md");

        /// <summary>Rewrites the live status file (cheap; call every few seconds).</summary>
        public static void Update(string phase, string detail)
        {
            try
            {
                File.WriteAllText(StatusPath,
                    "# SoI AI Campaign — live status\n\n" +
                    $"Updated: **{DateTime.Now:HH:mm:ss}**\n\n" +
                    $"**Phase**: {phase}\n\n" +
                    detail.TrimEnd() + "\n\n" +
                    "_History: [campaign-log.md](campaign-log.md)_\n");
            }
            catch (IOException) { /* editor read race — next update wins */ }
        }

        /// <summary>Marks the live status idle and appends a completed step to the log.</summary>
        public static void Complete(string phase, string logLine)
        {
            Log(logLine);
            Update("idle (last: " + phase + ")", logLine);
        }

        public static void Log(string line)
        {
            try
            {
                if (!File.Exists(LogPath))
                    File.WriteAllText(LogPath, "# SoI AI Campaign — history\n\n");
                File.AppendAllText(LogPath, $"- **{DateTime.Now:yyyy-MM-dd HH:mm}** — {line}\n");
            }
            catch (IOException) { }
        }
    }
}
