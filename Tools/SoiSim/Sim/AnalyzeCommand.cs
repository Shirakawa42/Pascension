using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SoiSim
{
    public static class AnalyzeCommand
    {
        public static int Run(Cli cli)
        {
            string inPattern = cli.GetStr("--in", null);
            bool allowMixed = cli.Has("--allow-mixed");
            string root = SimConfig.FindRepoRoot();
            string dataDir = Path.Combine(root, "Tools", "ShardsData");
            string mdPath = cli.GetStr("--report", Path.Combine(dataDir, "balance-report.md"));
            string jsonPath = cli.GetStr("--json", Path.Combine(dataDir, "balance-report.json"));
            string csvPath = cli.GetStr("--csv", Path.Combine(dataDir, "sim-summary.csv"));
            cli.RejectUnknown();

            var files = ResolveInputs(inPattern, Path.Combine(dataDir, "sim"));
            if (files.Count == 0)
                throw new CliError("no input .jsonl files found (use --in <path-or-glob>)");
            Console.WriteLine($"analyze: {files.Count} file(s)");
            foreach (string f in files)
                Console.WriteLine($"  {f}");

            var result = Analyzer.Analyze(files, allowMixed);
            ReportWriter.WriteAll(result, mdPath, jsonPath, csvPath);
            Console.WriteLine($"  {result.Games} games ({result.Decisive} decisive, {result.Ties} ties, {result.Failures} failures)");
            Console.WriteLine($"  -> {mdPath}");
            Console.WriteLine($"  -> {jsonPath}");
            Console.WriteLine($"  -> {csvPath}");
            return result.Failures == 0 ? 0 : 1;
        }

        private static List<string> ResolveInputs(string pattern, string defaultDir)
        {
            if (pattern == null)
            {
                return Directory.Exists(defaultDir)
                    ? Directory.GetFiles(defaultDir, "run-*.jsonl")
                        .Where(f => !f.EndsWith(".errors.jsonl")).OrderBy(f => f).ToList()
                    : new List<string>();
            }
            if (File.Exists(pattern))
                return new List<string> { pattern };
            string dir = Path.GetDirectoryName(pattern);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string glob = Path.GetFileName(pattern);
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, glob).Where(f => !f.EndsWith(".errors.jsonl")).OrderBy(f => f).ToList()
                : new List<string>();
        }
    }
}
