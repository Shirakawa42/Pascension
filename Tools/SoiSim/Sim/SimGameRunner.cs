using System;
using System.Collections.Generic;
using System.Diagnostics;
using Pascension.Core;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    public sealed class GameSpec
    {
        public ulong Seed;
        /// <summary>Character ids in seat order.</summary>
        public string[] Characters;

        public string MatchupKey
        {
            get
            {
                var sorted = (string[])Characters.Clone();
                Array.Sort(sorted, StringComparer.Ordinal);
                return string.Join(":", sorted);
            }
        }
    }

    /// <summary>Runs one headless game to completion and extracts its GameRecord.
    /// The loop is the ShardsRulingsTests bot-sim idiom minus the asserts: failures
    /// become records (termination stall/guard_cap/error), never thrown asserts.</summary>
    public static class SimGameRunner
    {
        public const int GuardLimit = 30000;

        public static GameRecord RunOne(GameSpec spec, BotFactory bots)
        {
            var sw = Stopwatch.StartNew();
            var specs = new List<PlayerSpec>();
            for (int i = 0; i < spec.Characters.Length; i++)
                specs.Add(new PlayerSpec { Name = "Bot" + i, CharacterId = spec.Characters[i] });

            ShardsEngineAdapter adapter = null;
            var destinyRowInitial = new List<string>();
            var initialHealth = new List<int>();
            var initialMastery = new List<int>();
            try
            {
                adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(spec.Seed, specs, SimConfig.AllDlc));

                // State peeks BEFORE play: the destiny row is dealt silently in the
                // engine ctor (no event), and curves need the staggered start values.
                foreach (var card in adapter.Inner.State.DestinyRow)
                    destinyRowInitial.Add(card.DefId);
                foreach (var p in adapter.Inner.State.Players)
                {
                    initialHealth.Add(p.Health);
                    initialMastery.Add(p.Mastery);
                }

                var seats = new IBotAgent[spec.Characters.Length];
                for (int i = 0; i < seats.Length; i++)
                    seats[i] = bots.Create(spec.Seed, i, adapter.Inner);

                int guard = 0, rejected = 0;
                string termination = null;
                while (!adapter.GameOver && guard++ < GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null)
                    {
                        termination = "stall";
                        break;
                    }
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted)
                    {
                        rejected++;
                        if (!adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        {
                            termination = "stall";
                            break;
                        }
                    }
                }
                sw.Stop();

                return GameRecorder.Extract(adapter, spec.Seed, spec.Characters, destinyRowInitial,
                    initialHealth, initialMastery, guard, rejected, sw.Elapsed.TotalMilliseconds,
                    termination);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var record = adapter != null
                    ? SafeExtract(adapter, spec, destinyRowInitial, initialHealth, initialMastery,
                        sw.Elapsed.TotalMilliseconds)
                    : new GameRecord { Seed = spec.Seed, Chars = new List<string>(spec.Characters) };
                record.Termination = "error";
                record.Error = ex.GetType().Name + ": " + ex.Message;
                return record;
            }
        }

        private static GameRecord SafeExtract(ShardsEngineAdapter adapter, GameSpec spec,
            List<string> destinyRowInitial, List<int> initialHealth, List<int> initialMastery,
            double wallMs)
        {
            try
            {
                return GameRecorder.Extract(adapter, spec.Seed, spec.Characters, destinyRowInitial,
                    initialHealth, initialMastery, 0, 0, wallMs, "error");
            }
            catch
            {
                return new GameRecord { Seed = spec.Seed, Chars = new List<string>(spec.Characters) };
            }
        }
    }
}
