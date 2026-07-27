using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>`coverage` — what does this policy NEVER do?
    ///
    /// This exists because of a specific, expensive failure. Until 2026-07-25 the row reroll
    /// was priced strictly below passing, so an argmax policy could never choose it: the
    /// action appeared in ZERO rollouts and ZERO training positions, and every net was fit to
    /// a game in which rerolling did not exist. Nothing caught it for six days, because every
    /// instrument in the repo measured WIN RATE — and a blind spot shared by both seats is
    /// invisible to win rate by construction.
    ///
    /// The balance report cannot catch it either: its card test requires ≥100 acquisitions
    /// before a card is even considered, so a card the bot never buys is structurally absent
    /// from the output. Absence of evidence is rendered as no evidence at all.
    ///
    /// So this command measures presence, not performance. A zero in any column is a claim
    /// about the policy's blind spots, and each zero is either a bug, a scoring hole, or a
    /// deliberate strategic choice that should be stated rather than assumed.</summary>
    public static class CoverageCommand
    {
        private sealed class Tally
        {
            public readonly ConcurrentCounter Actions = new();
            public readonly ConcurrentCounter Contexts = new();
            public readonly ConcurrentCounter Bought = new();
            public readonly ConcurrentCounter Played = new();
            public readonly ConcurrentCounter OfferedInGames = new();
            public readonly ConcurrentCounter OwnedAtEnd = new();
            public readonly ConcurrentCounter WinType = new();
            public int Games, Finished;
        }

        /// <summary>Lock-free-enough counter: per-thread dictionaries merged at the end.
        /// A shared ConcurrentDictionary was measurably slower than the games themselves.</summary>
        private sealed class ConcurrentCounter
        {
            private readonly ThreadLocal<Dictionary<string, long>> _local =
                new(() => new Dictionary<string, long>(), trackAllValues: true);

            public void Add(string key, long n = 1)
            {
                var d = _local.Value;
                d[key] = d.TryGetValue(key, out long v) ? v + n : n;
            }

            public Dictionary<string, long> Merge()
            {
                var merged = new Dictionary<string, long>();
                foreach (var d in _local.Values)
                    foreach (var kv in d)
                        merged[kv.Key] = merged.TryGetValue(kv.Key, out long v) ? v + kv.Value : kv.Value;
                return merged;
            }
        }

        public static int Run(Cli cli)
        {
            string kind = cli.GetStr("--bots", "bench:greedy-v5");
            int games = cli.GetInt("--games", 4000);
            ulong seedBase = cli.GetULong("--seed-base", 880000);
            int threads = cli.GetInt("--threads", Math.Max(1, Environment.ProcessorCount - 1));
            string outPath = cli.GetStr("--out", null);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var factory = new BotFactory(kind, 0);
            var tally = new Tally { Games = games };

            Console.WriteLine($"coverage: {games} games, bots={kind}, dlc mask {(int)SimConfig.AllDlc}");
            var sw = Stopwatch.StartNew();
            int finished = 0;

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                ulong seed = seedBase + (ulong)g;
                var rng = new DeterministicRng(seed * 31 + 7, 91);
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[a] },
                    new() { Name = "S1", CharacterId = chars[b] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
                var seats = new IBotAgent[2];
                for (int i = 0; i < 2; i++) seats[i] = factory.Create(seed, i, adapter.Inner);

                var offered = new HashSet<string>();
                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;

                    if (pending.Kind == PendingInputKind.Decision)
                        tally.Contexts.Add(pending.Decision.Context ?? "(null)");
                    else
                        foreach (var card in adapter.Inner.State.CenterRow)
                            if (card != null) offered.Add(card.DefId);

                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);

                    tally.Actions.Add(action.GetType().Name);
                    switch (action)
                    {
                        case ShardsBuyCardAction buy:
                        {
                            var slot = adapter.Inner.State.CenterRow[buy.SlotIndex];
                            if (slot != null)
                            {
                                tally.Bought.Add(slot.DefId);
                                if (buy.FastPlay) tally.Actions.Add("  ↳ fast-play");
                            }
                            break;
                        }
                        case ShardsPlayCardAction play:
                        {
                            var card = adapter.Inner.State.FindCard(play.CardInstanceId);
                            if (card != null) tally.Played.Add(card.DefId);
                            break;
                        }
                    }

                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }

                foreach (string def in offered) tally.OfferedInGames.Add(def);
                if (adapter.GameOver)
                {
                    Interlocked.Increment(ref finished);
                    // "Owned at end" catches every acquisition route — warps, relic recruits,
                    // destiny takes, return-from-discard — not just the direct buy action.
                    foreach (var p in adapter.Inner.State.Players)
                        foreach (var card in AllOwned(p))
                            tally.OwnedAtEnd.Add(card.DefId);
                    int w = adapter.WinnerIndex;
                    tally.WinType.Add(w < 0 ? "tie"
                        : adapter.Inner.State.Players[w].Mastery >= 30 ? "reached M30"
                        : "below M30");
                }
            });
            tally.Finished = finished;

            string report = Render(tally, kind, sw.Elapsed);
            Console.Write(report);
            if (outPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllText(outPath, report);
                Console.WriteLine($"  -> {outPath}");
            }
            return 0;
        }

        private static IEnumerable<ShardsCard> AllOwned(ShardsPlayer p)
        {
            foreach (var c in p.Deck) yield return c;
            foreach (var c in p.Hand) yield return c;
            foreach (var c in p.Discard) yield return c;
            foreach (var c in p.PlayZone) yield return c;
            foreach (var c in p.Champions) yield return c;
            foreach (var c in p.Destinies) yield return c;
        }

        /// <summary>Every priority action the engine can advertise. Listed explicitly so a
        /// type that NEVER appears still shows up as a zero row — a histogram of what
        /// happened can only ever show what happened.</summary>
        private static readonly string[] ExpectedActions =
        {
            nameof(ShardsPlayCardAction), nameof(ShardsBuyCardAction), nameof(ShardsRerollRowAction),
            nameof(ShardsFocusAction), nameof(ShardsHeroAbilityAction), nameof(ShardsExhaustAction),
            nameof(ShardsAttackMonsterAction), nameof(ShardsTakeDestinyAction),
            nameof(ShardsRecruitRelicAction), nameof(ShardsEndTurnAction),
            nameof(SubmitDecisionAction)
        };

        /// <summary>Decision contexts that a 2-player game can actually reach. A zero in one
        /// of these is a real blind spot.</summary>
        private static readonly string[] ExpectedContexts =
        {
            "soi.split", "soi.shields", "soi.keepfast", "soi.herodraft", "soi.maglev",
            "soi.reveal", "soi.banish", "soi.return", "soi.destroy", "soi.warp",
            "soi.recruit", "soi.copy", "soi.discard", "soi.scry", "soi.reorder", "soi.handpick",
            "soi.mode", "soi.removeshop", "soi.tutor", "soi.reset", "soi.relic", "soi.destiny",
            "soi.defiant", "soi.confirm"
        };

        /// <summary>Contexts that are UNREACHABLE in a duel by design, and must not be
        /// reported as blind spots — a detector that cries wolf gets ignored, which would
        /// cost far more than the thing it detects.
        ///
        /// `soi.target` is the "which opponent?" prompt. All three sites — OpponentLosesMastery
        /// (ShardsEffects.cs:540), OpponentDrawsThenDiscards (ShardsDuelEffects.cs:205) and
        /// Reactor Drone's DestroyOpponent (ShardsDuelSet.cs:949) — auto-resolve when there is
        /// exactly one living opponent. In a 2-player game there always is.</summary>
        private static readonly Dictionary<string, string> UnreachableInDuel = new()
        {
            ["soi.target"] = "auto-resolves with one living opponent (3 sites, all guarded on Count > 1)"
        };

        private static string Render(Tally t, string kind, TimeSpan wall)
        {
            var actions = t.Actions.Merge();
            var contexts = t.Contexts.Merge();
            var bought = t.Bought.Merge();
            var played = t.Played.Merge();
            var offered = t.OfferedInGames.Merge();
            var owned = t.OwnedAtEnd.Merge();
            var winType = t.WinType.Merge();

            var sb = new StringBuilder();
            sb.AppendLine("# SoI Action-Space Coverage");
            sb.AppendLine();
            sb.AppendLine($"- Bots: **{kind}** · games {t.Games} ({t.Finished} finished) · " +
                          $"DLC mask {(int)SimConfig.AllDlc} · {wall.TotalSeconds:F1}s");
            sb.AppendLine("- A **zero** below is a blind spot: a bug, a scoring hole, or a strategic");
            sb.AppendLine("  choice that should be stated rather than assumed. Win rate cannot see any of them.");
            sb.AppendLine();

            sb.AppendLine("## 1. Priority actions");
            sb.AppendLine();
            sb.AppendLine("| Action | Times chosen | Per game |");
            sb.AppendLine("|---|---:|---:|");
            var missingActions = new List<string>();
            foreach (string name in ExpectedActions)
            {
                long n = actions.TryGetValue(name, out long v) ? v : 0;
                if (n == 0) missingActions.Add(name);
                sb.AppendLine($"| {(n == 0 ? "🚨 " : "")}{name} | {n:N0} | {(double)n / Math.Max(1, t.Games):F2} |");
            }
            long fast = actions.TryGetValue("  ↳ fast-play", out long fp) ? fp : 0;
            sb.AppendLine($"| {(fast == 0 ? "🚨 " : "")}↳ of which mercenary fast-play | {fast:N0} | " +
                          $"{(double)fast / Math.Max(1, t.Games):F2} |");
            sb.AppendLine();

            sb.AppendLine("## 2. Decision contexts");
            sb.AppendLine();
            sb.AppendLine("| Context | Times reached | Per game |");
            sb.AppendLine("|---|---:|---:|");
            var missingContexts = new List<string>();
            foreach (string ctx in ExpectedContexts.OrderBy(c => c))
            {
                long n = contexts.TryGetValue(ctx, out long v) ? v : 0;
                if (n == 0) missingContexts.Add(ctx);
                sb.AppendLine($"| {(n == 0 ? "🚨 " : "")}{ctx} | {n:N0} | {(double)n / Math.Max(1, t.Games):F3} |");
            }
            foreach (var kv in contexts.Where(k => !ExpectedContexts.Contains(k.Key) &&
                                                   !UnreachableInDuel.ContainsKey(k.Key)))
                sb.AppendLine($"| ⚠ UNLISTED {kv.Key} | {kv.Value:N0} | — |");
            sb.AppendLine();
            foreach (var kv in UnreachableInDuel)
            {
                long n = contexts.TryGetValue(kv.Key, out long v) ? v : 0;
                sb.AppendLine(n == 0
                    ? $"`{kv.Key}` is 0 as expected — {kv.Value}."
                    : $"🚨 `{kv.Key}` fired {n:N0} times but is supposed to be unreachable in a duel " +
                      $"({kv.Value}) — the guard has regressed.");
            }
            sb.AppendLine();

            sb.AppendLine("## 3. Cards never acquired");
            sb.AppendLine();
            var neverBought = new List<(string Def, long Offered)>();
            var neverOffered = new List<string>();
            foreach (var def in ShardsCardDatabase.All.OrderBy(d => d.Id))
            {
                if (def.Type == ShardsCardType.Starter || def.Type == ShardsCardType.Monster) continue;
                long off = offered.TryGetValue(def.Id, out long o) ? o : 0;
                long own = owned.TryGetValue(def.Id, out long w) ? w : 0;
                if (own > 0) continue;
                if (off == 0) neverOffered.Add(def.Id);
                else neverBought.Add((def.Id, off));
            }

            if (neverBought.Count == 0 && neverOffered.Count == 0)
                sb.AppendLine("Every non-starter card was acquired at least once. ✅");
            else
            {
                sb.AppendLine($"**{neverBought.Count} cards were OFFERED but never once ended up owned.** These are");
                sb.AppendLine("the policy's rejected cards — the highest-signal list in this report, because the");
                sb.AppendLine("balance report's ≥100-acquisition floor hides them completely.");
                sb.AppendLine();
                sb.AppendLine("| Card | Cost | Type | Games offered in |");
                sb.AppendLine("|---|---:|---|---:|");
                foreach (var (id, off) in neverBought.OrderByDescending(x => x.Offered).Take(40))
                {
                    var def = ShardsCardDatabase.Get(id);
                    sb.AppendLine($"| {id} | {def.Cost} | {def.Type} | {off:N0} |");
                }
                if (neverOffered.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**{neverOffered.Count} never appeared in the row at all** (relics/destinies " +
                                  "reach play by other routes, so some of these are expected):");
                    sb.AppendLine();
                    sb.AppendLine("`" + string.Join("`, `", neverOffered.Take(40)) + "`");
                }
            }
            sb.AppendLine();

            sb.AppendLine("## 4. Cards never PLAYED though owned");
            sb.AppendLine();
            // Destinies are TAKEN into their own zone and exhausted, never played, so listing
            // them here is noise that buries the real cases.
            var ownedNeverPlayed = ShardsCardDatabase.All
                .Where(d => d.Type != ShardsCardType.Destiny)
                .Where(d => owned.ContainsKey(d.Id) && !played.ContainsKey(d.Id))
                .Select(d => d.Id).OrderBy(x => x).ToList();
            sb.AppendLine(ownedNeverPlayed.Count == 0
                ? "Every owned non-destiny card was played at least once. ✅ " +
                  "(Destinies are exhausted from their own zone, never played, so they are excluded.)"
                : "Owned but never played — a card that only ever sat in a deck: `" +
                  string.Join("`, `", ownedNeverPlayed.Take(40)) + "`");
            sb.AppendLine();

            sb.AppendLine("## 5. Winner's final mastery");
            sb.AppendLine();
            sb.AppendLine("⚠ This is *how the winner finished*, not *how they won* — a player can reach");
            sb.AppendLine("M30 and still win by damage. For the actual win-type split use the balance");
            sb.AppendLine("report's `Win type` line, which reads the terminating event.");
            sb.AppendLine();
            long totalWins = winType.Values.Sum();
            foreach (var kv in winType.OrderByDescending(k => k.Value))
                sb.AppendLine($"- winner {kv.Key}: **{100.0 * kv.Value / Math.Max(1, totalWins):F1}%** ({kv.Value:N0})");
            sb.AppendLine();

            sb.AppendLine("## 6. Verdict");
            sb.AppendLine();
            if (missingActions.Count > 0)
                sb.AppendLine($"- 🚨 **{missingActions.Count} action type(s) NEVER chosen**: " +
                              string.Join(", ", missingActions) +
                              " — this is the reroll bug's exact signature.");
            if (missingContexts.Count > 0)
                sb.AppendLine($"- ⚠ {missingContexts.Count} decision context(s) never reached: " +
                              string.Join(", ", missingContexts) +
                              " — either unreachable content, or a card that is never bought.");
            if (neverBought.Count > 0)
                sb.AppendLine($"- ⚠ {neverBought.Count} card(s) offered but never acquired.");
            if (missingActions.Count == 0 && missingContexts.Count == 0 && neverBought.Count == 0)
                sb.AppendLine("- ✅ Full coverage: every action, every context, every card.");
            return sb.ToString();
        }
    }
}
