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
                // Global --dlc flag (consumed here so per-command RejectUnknown ignores it).
                // Default is ALL dlc incl. Duel — the shipped configuration. `--dlc base`
                // drops Duel for legacy comparisons against pre-2026-07-25 artifacts.
                string dlc = cli.GetStr("--dlc", "all");
                if (dlc == "base")
                    SimConfig.AllDlc &= ~Shards.Engine.ShardsDlc.Duel;
                else if (dlc != "all" && dlc != "duel")
                    throw new CliError($"--dlc expects 'all' or 'base', got '{dlc}'");
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
                    case "probe":
                        return ProbeCommand.Run(cli);
                    case "converge":
                        return ConvergeCommand.Run(cli);
                    case "ablation":
                        return AblationCommand.Run(cli);
                    case "coverage":
                        return CoverageCommand.Run(cli);
                    case "fit":
                        return FitCommand.Run(cli);
                    case "rank":
                        return RankCommand.Run(cli);
                    case "dump-positions":
                        return DumpPositionsCommand.Run(cli);
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
            Console.WriteLine("  soisim probe [--a strong] [--budget 200] [--b greedy] [--games 100] [--threads N-1]");
            Console.WriteLine("               [--sprt] [--elo0 0] [--elo1 15] [--result path.json] [--allow-small]");
            Console.WriteLine("  soisim converge [--games 40] [--budgets 16,32,...] [--sample-stride 3]");
            Console.WriteLine("  soisim ablation [--games 2000] [--seed-base 770000] [--threads N-1]");
            Console.WriteLine("               (buy-vs-play Elo attribution — which axis carries the strength)");
            Console.WriteLine("  soisim coverage [--bots kind] [--games 4000] [--out path.md]");
            Console.WriteLine("               (what does this policy NEVER do — actions, contexts, cards)");
            Console.WriteLine("  soisim fit   [--games 4000] [--bots greedy|basket-96|...] [--epochs 4000]");
            Console.WriteLine("               [--min-round 4] [--seed-base 990000] [--no-emit]");
            Console.WriteLine("  soisim rank  [--games 100] [--rollouts 64] [--points-per-game 6] [--stride 2]");
            Console.WriteLine("               [--topk 0] [--tail frozen|greedy] [--siblings actions|baskets]");
            Console.WriteLine("               [--min-round 4] [--seed-base 880000]");
            Console.WriteLine("               (do the evaluators rank SIBLING candidate turns correctly — the");
            Console.WriteLine("                planner precondition; headroom column decides if reranking can win)");
            Console.WriteLine("  soisim dump-positions [--games 12] [--sample 2] [--min-round 3]");
            Console.WriteLine("  soisim smoke");
            Console.WriteLine();
            Console.WriteLine("bot kinds: random | heuristic | greedy | strong | strong-fast | rank:iron..diamond");
            Console.WriteLine("FROZEN benchmarks (never change these — every candidate is measured against them):");
            Console.WriteLine("  bench:heuristic | bench:greedy-v5 | bench:rollout-1200 | bench:rollout-4800");
        }
    }
}
