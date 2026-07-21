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
        }
    }
}
