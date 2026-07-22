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
            public byte[] Data;
            public string Note = "";
        }

        public static int RunEmit(Cli cli)
        {
            string binPath = cli.GetStr("--bin", null) ?? throw new CliError("--bin required");
            string jsonPath = cli.GetStr("--json", null) ?? throw new CliError("--json required");
            string outPath = cli.GetStr("--out", Path.Combine(SimConfig.FindRepoRoot(),
                "Assets", "Scripts", "Shards", "Bots", "Neural", "ShardsNetWeights.g.cs"));
            // Comma-separated generations to DROP from the registry (rejected
            // checkpoints). NEVER retire a generation a minted rank pins — blobs are
            // unrecoverable once dropped (retrain to get them back).
            string retire = cli.GetStr("--retire", "");
            cli.RejectUnknown();

            var header = JObject.Parse(File.ReadAllText(jsonPath));
            byte[] blob = File.ReadAllBytes(binPath);
            var incoming = new GenEntry
            {
                Generation = header.Value<int>("generation"),
                SchemaVersion = header.Value<int>("schemaVersion"),
                Layers = header["layers"].ToObject<int[]>(),
                Sha256 = header.Value<string>("sha256"),
                Data = blob,
                Note = $"valAcc {header.Value<double?>("valAcc")} · {header.Value<long?>("positions"):N0} positions · {DateTime.Now:yyyy-MM-dd}"
            };

            // Sanity: the blob must load before we embed it.
            _ = new ShardsNeuralEval(blob, incoming.Layers, incoming.SchemaVersion, incoming.Sha256);

            var entries = ReadExistingGenerations();
            entries.RemoveAll(e => e.Generation == incoming.Generation);
            entries.Add(incoming);
            entries.Sort((a, b) => a.Generation.CompareTo(b.Generation));
            if (!string.IsNullOrEmpty(retire))
                foreach (string tok in retire.Split(','))
                {
                    int gen = int.Parse(tok.Trim());
                    if (entries.RemoveAll(e => e.Generation == gen) > 0)
                        Console.WriteLine($"emit-net: RETIRED generation {gen}");
                }

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
                    // Blob storage migrated base64-string → byte[] RVA (the string
                    // shape overflowed the 16MB PE user-string heap at 6 gens); read
                    // whichever shape the compiled registry carries.
                    byte[] data = t.GetField("Data") != null
                        ? (byte[])t.GetField("Data").GetValue(item)
                        : Convert.FromBase64String((string)t.GetField("Blob").GetValue(item));
                    entries.Add(new GenEntry
                    {
                        Generation = (int)t.GetField("Generation").GetValue(item),
                        SchemaVersion = (int)t.GetField("SchemaVersion").GetValue(item),
                        Layers = (int[])t.GetField("Layers").GetValue(item),
                        Sha256 = (string)t.GetField("Sha256").GetValue(item),
                        Data = data,
                        Note = (string)(t.GetField("Note")?.GetValue(item) ?? "")
                    });
                }
            }
            return entries;
        }

        private static string Render(List<GenEntry> entries)
        {
            var newest = entries[entries.Count - 1];
            int total = 0;
            foreach (var e in entries) total += e.Data.Length;
            var sb = new StringBuilder(total * 4 + (64 << 10));
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// SoI value-net registry: one FROZEN blob per embedded generation (minted");
            sb.AppendLine("// ranks pin generations; Current = newest). f16 bytes as RVA byte[] data —");
            sb.AppendLine("// string literals hit the 16MB PE user-string heap limit; byte arrays don't.");
            sb.AppendLine("// Regenerate with:  soisim emit-net --bin <weights.bin> --json <weights.json>");
            sb.AppendLine("//                   [--retire g1,g2]  (drop rejected checkpoints — NEVER a minted gen)");
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
            sb.AppendLine("            public byte[] Data;");
            sb.AppendLine("            public string Note;");
            sb.AppendLine("        }");
            sb.AppendLine();
            foreach (var e in entries)
            {
                sb.AppendLine($"        private static readonly byte[] Gen{e.Generation}Data =");
                sb.AppendLine("        {");
                const int perLine = 512;
                for (int i = 0; i < e.Data.Length; i++)
                {
                    if (i % perLine == 0) sb.Append("            ");
                    sb.Append(e.Data[i]);
                    sb.Append(',');
                    if (i % perLine == perLine - 1 || i == e.Data.Length - 1) sb.AppendLine();
                }
                sb.AppendLine("        };");
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
                sb.AppendLine($"                Data = Gen{e.Generation}Data,");
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
            sb.AppendLine($"        public static byte[] CurrentData => Gen{newest.Generation}Data;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static int RunFixture(Cli cli)
        {
            string outPath = cli.GetStr("--out", Path.Combine(SimConfig.FindRepoRoot(),
                "Tools", "ShardsData", "neural", "fixtures.soip"));
            int count = cli.GetInt("--count", 64);
            // Match the schema of the net whose parity these fixtures pin. Pass
            // --schema 1 when the CURRENT net deploys the frozen v1 pooled encoding.
            int schema = cli.GetInt("--schema", ShardsStateEncoder.SchemaVersion);
            if (schema != 1 && schema != ShardsStateEncoder.SchemaVersion)
                throw new CliError($"--schema must be 1 or {ShardsStateEncoder.SchemaVersion}");
            int featureCount = schema == 1
                ? ShardsStateEncoder.V1FeatureCount
                : ShardsStateEncoder.FeatureCount;
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var model = new ShardsValueModel();
            var features = new float[featureCount];

            using var writer = new PositionWriter(outPath, schema, featureCount);
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
                if (schema == 1) ShardsStateEncoder.EncodeV1(adapter.Inner.State, viewer, features);
                else ShardsStateEncoder.Encode(adapter.Inner.State, viewer, features);
                writer.Write(features, z: 0, q: -1, seed, (ushort)guard, (byte)viewer, flags: 2);
            }
            Console.WriteLine($"netfixture: {count} encoded states -> {outPath}");
            return 0;
        }
    }
}
