using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>emit-net: embeds a trained weights.bin/.json into the generated
    /// ShardsNetWeights.g.cs. PRESERVES every previously embedded generation (read via
    /// reflection from the compiled registry) — minted ranks pin FROZEN generations
    /// (GOLD = gen 0) and must never drift when Current moves. netfixture: writes the
    /// committed parity fixture states.</summary>
    public static class EmitNetCommand
    {
        private sealed class GenEntry
        {
            public int Generation;
            public int SchemaVersion;
            public int[] Layers;
            public string Sha256;
            public string Blob;
            public string Note = "";
        }

        public static int RunEmit(Cli cli)
        {
            string binPath = cli.GetStr("--bin", null) ?? throw new CliError("--bin required");
            string jsonPath = cli.GetStr("--json", null) ?? throw new CliError("--json required");
            string outPath = cli.GetStr("--out", Path.Combine(SimConfig.FindRepoRoot(),
                "Assets", "Scripts", "Shards", "Bots", "Neural", "ShardsNetWeights.g.cs"));
            cli.RejectUnknown();

            var header = JObject.Parse(File.ReadAllText(jsonPath));
            byte[] blob = File.ReadAllBytes(binPath);
            var incoming = new GenEntry
            {
                Generation = header.Value<int>("generation"),
                SchemaVersion = header.Value<int>("schemaVersion"),
                Layers = header["layers"].ToObject<int[]>(),
                Sha256 = header.Value<string>("sha256"),
                Blob = Convert.ToBase64String(blob),
                Note = $"valAcc {header.Value<double?>("valAcc")} · {header.Value<long?>("positions"):N0} positions · {DateTime.Now:yyyy-MM-dd}"
            };

            // Sanity: the blob must load before we embed it.
            _ = new ShardsNeuralEval(blob, incoming.Layers, incoming.SchemaVersion, incoming.Sha256);

            var entries = ReadExistingGenerations();
            entries.RemoveAll(e => e.Generation == incoming.Generation);
            entries.Add(incoming);
            entries.Sort((a, b) => a.Generation.CompareTo(b.Generation));

            File.WriteAllText(outPath, Render(entries), new UTF8Encoding(false));
            CampaignStatus.Log($"net generation {incoming.Generation} embedded ({incoming.Note})");
            Console.WriteLine($"emit-net: generation {incoming.Generation} embedded " +
                              $"({blob.Length:N0} bytes); file now holds generations " +
                              string.Join(", ", entries.ConvertAll(e => e.Generation.ToString())) +
                              $" -> {outPath}");
            return 0;
        }

        /// <summary>Older generations from the COMPILED registry (reflection so this
        /// works before and after the multi-gen shape exists).</summary>
        private static List<GenEntry> ReadExistingGenerations()
        {
            var entries = new List<GenEntry>();
            var type = typeof(ShardsNetWeights);
            var allField = type.GetField("All", BindingFlags.Public | BindingFlags.Static);
            if (allField?.GetValue(null) is Array all)
            {
                foreach (var item in all)
                {
                    var t = item.GetType();
                    entries.Add(new GenEntry
                    {
                        Generation = (int)t.GetField("Generation").GetValue(item),
                        SchemaVersion = (int)t.GetField("SchemaVersion").GetValue(item),
                        Layers = (int[])t.GetField("Layers").GetValue(item),
                        Sha256 = (string)t.GetField("Sha256").GetValue(item),
                        Blob = (string)t.GetField("Blob").GetValue(item),
                        Note = (string)(t.GetField("Note")?.GetValue(item) ?? "")
                    });
                }
            }
            else if ((bool)(type.GetProperty("Available")?.GetValue(null) ?? false))
            {
                // Legacy single-blob shape (pre-multi-gen): preserve its one net.
                entries.Add(new GenEntry
                {
                    Generation = (int)type.GetProperty("Generation").GetValue(null),
                    SchemaVersion = (int)type.GetProperty("SchemaVersion").GetValue(null),
                    Layers = (int[])type.GetProperty("Layers").GetValue(null),
                    Sha256 = (string)type.GetProperty("Sha256").GetValue(null),
                    Blob = (string)type.GetProperty("CurrentBlob").GetValue(null),
                    Note = "migrated from single-blob format"
                });
            }
            return entries;
        }

        private static string Render(List<GenEntry> entries)
        {
            var newest = entries[entries.Count - 1];
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// SoI value-net registry: one FROZEN blob per embedded generation (minted");
            sb.AppendLine("// ranks pin generations; Current = newest). Base64 f16, sha256-verified.");
            sb.AppendLine("// Regenerate with:  soisim emit-net --bin <weights.bin> --json <weights.json>");
            foreach (var e in entries)
                sb.AppendLine($"// gen {e.Generation}: layers [{string.Join(",", e.Layers)}] · {e.Note}");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("namespace Shards.Bots");
            sb.AppendLine("{");
            sb.AppendLine("    public static class ShardsNetWeights");
            sb.AppendLine("    {");
            sb.AppendLine("        public sealed class NetSpec");
            sb.AppendLine("        {");
            sb.AppendLine("            public int Generation;");
            sb.AppendLine("            public int SchemaVersion;");
            sb.AppendLine("            public int[] Layers;");
            sb.AppendLine("            public string Sha256;");
            sb.AppendLine("            public string Blob;");
            sb.AppendLine("            public string Note;");
            sb.AppendLine("        }");
            sb.AppendLine();
            foreach (var e in entries)
            {
                sb.AppendLine($"        private static string Gen{e.Generation}Blob => string.Concat(");
                const int chunk = 60000;
                for (int i = 0; i < e.Blob.Length; i += chunk)
                {
                    string piece = e.Blob.Substring(i, Math.Min(chunk, e.Blob.Length - i));
                    sb.AppendLine($"            \"{piece}\"{(i + chunk < e.Blob.Length ? "," : ");")}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("        public static readonly NetSpec[] All =");
            sb.AppendLine("        {");
            foreach (var e in entries)
            {
                sb.AppendLine("            new NetSpec");
                sb.AppendLine("            {");
                sb.AppendLine($"                Generation = {e.Generation},");
                sb.AppendLine($"                SchemaVersion = {e.SchemaVersion},");
                sb.AppendLine($"                Layers = new[] {{ {string.Join(", ", e.Layers)} }},");
                sb.AppendLine($"                Sha256 = \"{e.Sha256}\",");
                sb.AppendLine($"                Blob = Gen{e.Generation}Blob,");
                sb.AppendLine($"                Note = \"{e.Note?.Replace("\"", "'")}\"");
                sb.AppendLine("            },");
            }
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public static bool Available => true;");
            sb.AppendLine($"        public static int Generation => {newest.Generation};");
            sb.AppendLine($"        public static int SchemaVersion => {newest.SchemaVersion};");
            sb.AppendLine($"        public static int FeatureCount => {ShardsStateEncoder.FeatureCount};");
            sb.AppendLine($"        public static int[] Layers => new[] {{ {string.Join(", ", newest.Layers)} }};");
            sb.AppendLine($"        public static string Sha256 => \"{newest.Sha256}\";");
            sb.AppendLine($"        public static string CurrentBlob => Gen{newest.Generation}Blob;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static int RunFixture(Cli cli)
        {
            string outPath = cli.GetStr("--out", Path.Combine(SimConfig.FindRepoRoot(),
                "Tools", "ShardsData", "neural", "fixtures.soip"));
            int count = cli.GetInt("--count", 64);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var model = new ShardsValueModel();
            var features = new float[ShardsStateEncoder.FeatureCount];

            using var writer = new PositionWriter(outPath);
            ulong seed = 5000;
            while (writer.Written < count)
            {
                seed++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "F0", CharacterId = chars[(int)(seed % (ulong)chars.Count)] },
                    new() { Name = "F1", CharacterId = chars[(int)((seed + 2) % (ulong)chars.Count)] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
                var bots = new IBotAgent[]
                {
                    new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                    new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
                };
                // Sample the position after 40 accepted submits (mid-game variety).
                int guard = 0;
                while (!adapter.GameOver && guard++ < 40)
                {
                    var pending = adapter.PendingInput;
                    var action = bots[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted)
                        adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
                }
                if (adapter.GameOver || adapter.PendingInput?.Kind != Pascension.Engine.Core.PendingInputKind.Priority)
                    continue;
                int viewer = adapter.PendingInput.PlayerIndex;
                ShardsStateEncoder.Encode(adapter.Inner.State, viewer, features);
                writer.Write(features, z: 0, q: -1, seed, (ushort)guard, (byte)viewer, flags: 2);
            }
            Console.WriteLine($"netfixture: {count} encoded states -> {outPath}");
            return 0;
        }
    }
}
