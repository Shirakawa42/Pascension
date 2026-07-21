using System;

namespace SoiSim
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            try
            {
                var cli = Cli.Parse(args);
                switch (args[0])
                {
                    case "bench":
                        return BenchCommand.Run(cli);
                    case "run":
                        return RunCommand.Run(cli);
                    case "analyze":
                        return AnalyzeCommand.Run(cli);
                    case "tune":
                        return TuneCommand.Run(cli);
                    case "evaluate":
                        return EvaluateCommand.Run(cli);
                    case "smoke":
                        return SmokeCommand.Run(cli);
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                        PrintUsage();
                        return 2;
                }
            }
            catch (CliError e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("SoiSim — Shards of Infinity headless simulation & balance tooling");
            Console.WriteLine();
            Console.WriteLine("usage:");
            Console.WriteLine("  soisim bench [--games 200] [--players 2] [--seed-base 1] [--threads N-1]");
            Console.WriteLine("  soisim run   [--bots heuristic|random] [--budget 0] [--games-per-matchup 400]");
            Console.WriteLine("               [--matchups all|decima:tetra,...] [--seed-base 1] [--threads N-1]");
            Console.WriteLine("               [--out path.jsonl] [--append] [--tag baseline]");
            Console.WriteLine("  soisim analyze [--in path-or-glob] [--allow-mixed] [--report md] [--json path] [--csv path]");
            Console.WriteLine("  soisim tune  [--generations 300] [--games-per-candidate 240] [--lambda 16]");
            Console.WriteLine("               [--sigma 0.15] [--seed 1] [--threads N-1] [--no-emit] [--out path]");
            Console.WriteLine("  soisim evaluate [--games 1000] [--seed-base 424242] [--threads N-1]");
            Console.WriteLine("  soisim smoke");
        }
    }
}
