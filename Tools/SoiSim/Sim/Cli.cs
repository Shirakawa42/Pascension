using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoiSim
{
    public sealed class CliError : Exception
    {
        public CliError(string message) : base(message) { }
    }

    /// <summary>Tiny `--flag value` / `--flag` parser. args[0] is the command name.</summary>
    public sealed class Cli
    {
        private readonly Dictionary<string, string> _options = new();
        private readonly HashSet<string> _consumed = new();

        public string Command { get; private set; }

        public static Cli Parse(string[] args)
        {
            var cli = new Cli { Command = args.Length > 0 ? args[0] : "" };
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--"))
                    throw new CliError($"expected an --option, got '{a}'");
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                cli._options[a] = hasValue ? args[++i] : "";
            }
            return cli;
        }

        public bool Has(string name)
        {
            _consumed.Add(name);
            return _options.ContainsKey(name);
        }

        public string GetStr(string name, string fallback)
        {
            _consumed.Add(name);
            return _options.TryGetValue(name, out string v) && v.Length > 0 ? v : fallback;
        }

        public int GetInt(string name, int fallback)
        {
            string v = GetStr(name, null);
            if (v == null) return fallback;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                throw new CliError($"{name} expects an integer, got '{v}'");
            return n;
        }

        public ulong GetULong(string name, ulong fallback)
        {
            string v = GetStr(name, null);
            if (v == null) return fallback;
            if (!ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong n))
                throw new CliError($"{name} expects a non-negative integer, got '{v}'");
            return n;
        }

        /// <summary>Error on any option that no command consumed — catches typos.</summary>
        public void RejectUnknown()
        {
            foreach (var key in _options.Keys)
                if (!_consumed.Contains(key))
                    throw new CliError($"unknown option '{key}' for command '{Command}'");
        }
    }
}
