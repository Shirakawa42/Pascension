using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Utility export (run explicitly, mirrors ExportArtManifest): dumps the
    /// registered Shards card database as a markdown table for the shards-cards skill
    /// registry, and the art manifest skeleton for the M7 import window.</summary>
    public sealed class ShardsCardExportTests
    {
        [Test]
        public void ExportShardsCardTable()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            string root = FindRepoRoot();
            var sb = new StringBuilder();
            sb.AppendLine("| Id | Name | Set | Faction | Type | Cost | Qty | Def | Shield | Rules (functional paraphrase) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

            var defs = new List<ShardsCardDef>(ShardsCardDatabase.All);
            defs.Sort((a, b) =>
            {
                int set = SetOrder(a.Set).CompareTo(SetOrder(b.Set));
                if (set != 0) return set;
                int type = a.Type.CompareTo(b.Type);
                if (type != 0) return type;
                int faction = a.Faction.CompareTo(b.Faction);
                if (faction != 0) return faction;
                return string.CompareOrdinal(a.Id, b.Id);
            });
            foreach (var def in defs)
                sb.AppendLine($"| {def.Id} | {def.Name} | {def.Set} | {def.Faction} | {def.Type} | {def.Cost} | {def.Quantity} | " +
                              $"{(def.Defense > 0 ? def.Defense.ToString() : "–")} | {(def.Shield > 0 ? def.Shield.ToString() : "–")} | {def.RulesText.Replace("|", "/")} |");
            File.WriteAllText(Path.Combine(root, "Tools", "ShardsData", "cards-table.md"), sb.ToString());

            // Art-source manifest skeleton (M7): user fills url-or-path per id; personal use only.
            var manifest = new StringBuilder();
            manifest.AppendLine("[");
            for (int i = 0; i < defs.Count; i++)
                manifest.AppendLine($"  {{ \"id\": \"{defs[i].Id}\", \"source\": \"\" }}{(i < defs.Count - 1 ? "," : "")}");
            manifest.AppendLine("]");
            string manifestPath = Path.Combine(root, "Tools", "soi_art_sources.json");
            if (!File.Exists(manifestPath))
                File.WriteAllText(manifestPath, manifest.ToString());

            Assert.Pass($"exported {defs.Count} defs");
        }

        private static int SetOrder(string set) => set switch
        {
            "base" => 0,
            "relics_of_the_future" => 1,
            "shadow_of_salvation" => 2,
            "into_the_horizon" => 3,
            _ => 9
        };

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found");
            return dir.FullName;
        }
    }
}
