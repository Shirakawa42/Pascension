using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SoiSim
{
    /// <summary>Renders an AnalysisResult as balance-report.md (human),
    /// balance-report.json (machine — the goal-3 diffing interface) and
    /// sim-summary.csv (per-card table).</summary>
    public static class ReportWriter
    {
        public static void WriteAll(AnalysisResult res, string mdPath, string jsonPath, string csvPath)
        {
            File.WriteAllText(mdPath, Markdown(res), Encoding.UTF8);
            File.WriteAllText(jsonPath, MachineJson(res), Encoding.UTF8);
            File.WriteAllText(csvPath, Csv(res), Encoding.UTF8);
        }

        private static string Pct(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

        private static string PctCi(int successes, int n)
        {
            if (n == 0) return "–";
            var (lo, hi) = Stats.Wilson(successes, n);
            return $"{Pct((double)successes / n)} [{Pct(lo)}–{Pct(hi)}]";
        }

        private static string Markdown(AnalysisResult res)
        {
            var sb = new StringBuilder();
            var h = res.Headers[0];
            sb.AppendLine("# SoI Balance Report");
            sb.AppendLine();
            sb.AppendLine("## 1. Reproducibility");
            sb.AppendLine();
            sb.AppendLine($"- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · git `{h.GitRev}` · schema {h.Schema}");
            sb.AppendLine($"- Bots: **{h.BotVersion}**" + (h.Budget > 0 ? $" @ budget {h.Budget}" : "") +
                          $" · DLC mask {h.Dlc} · seed base {h.SeedBase} · tag `{h.Tag}`");
            sb.AppendLine($"- Config hash: `{h.ConfigHash}`" + (res.Headers.Count > 1 ? $" · {res.Headers.Count} merged files" : ""));
            sb.AppendLine($"- Games: **{res.Games}** ({res.Decisive} decisive, {res.Ties} ties, {res.Failures} failures)");
            sb.AppendLine();

            sb.AppendLine("## 2. Game health");
            sb.AppendLine();
            sb.AppendLine($"- Rounds p10/p50/p90: **{res.RoundsP10:F0} / {res.RoundsP50:F0} / {res.RoundsP90:F0}** · avg submits/game {res.AvgSubmits:F0}");
            sb.AppendLine($"- Tie rate: {PctCi(res.Ties, res.Games)} · failures (guard/stall/error): **{res.Failures}**" +
                          (res.Failures > 0 ? " ⚠ investigate the .errors.jsonl sidecars" : ""));
            int wins = res.KillWins + res.OverwhelmWins;
            sb.AppendLine($"- Win type: {res.KillWins} kill / {res.OverwhelmWins} Infinity-Shard overwhelm ({PctCi(res.OverwhelmWins, Math.Max(1, wins))} of wins — mastery-race viability)");
            sb.AppendLine($"- Comeback wins (winner behind on health at midpoint): {PctCi(res.ComebackWins, Math.Max(1, wins))}");
            sb.AppendLine($"- Shields prevented {res.DamagePrevented} of {res.DamagePrevented + res.DamageDealt} incoming damage ({Pct(res.DamagePrevented / Math.Max(1.0, res.DamagePrevented + res.DamageDealt))})");
            sb.AppendLine();

            sb.AppendLine("## 3. Seat advantage (staggered start: P0 M0, P1 M1)");
            sb.AppendLine();
            int decisive = res.Decisive;
            sb.AppendLine($"- P0 win rate, all decisive games: **{PctCi(res.P0Wins, decisive)}** (n={decisive})");
            sb.AppendLine($"- P0 win rate, mirror matches only (no character confound): **{PctCi(res.MirrorP0Wins, res.MirrorDecisive)}** (n={res.MirrorDecisive})");
            sb.AppendLine();

            sb.AppendLine("## 4. Characters");
            sb.AppendLine();
            sb.AppendLine("| Character | Player-games | Win score |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in res.Characters.Values.OrderByDescending(c => c.WinScore / Math.Max(1, c.PlayerGames)))
                sb.AppendLine($"| {c.Id} | {c.PlayerGames} | {PctCi((int)Math.Round(c.WinScore), c.PlayerGames)} |");
            sb.AppendLine();
            sb.AppendLine("Matchups (win score of the alphabetically-first character; mirrors show seat-0 score):");
            sb.AppendLine();
            sb.AppendLine("| Matchup | Games | First's score |");
            sb.AppendLine("|---|---|---|");
            foreach (var kv in res.Matchups.OrderBy(k => k.Key))
                sb.AppendLine($"| {kv.Key} | {kv.Value.Games} | {Pct(kv.Value.AScore / Math.Max(1, kv.Value.Games))} |");
            sb.AppendLine();

            sb.AppendLine("## 5. Playstyles (observational!)");
            sb.AppendLine();
            sb.AppendLine("Win rate by feature quartile (Q1 lowest → Q4 highest), plus logistic odds ratio per +1 SD:");
            sb.AppendLine();
            sb.AppendLine("| Feature | Q1 | Q2 | Q3 | Q4 | OR/SD | p |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in res.Features)
            {
                string or = f.LogisticOddsRatio > 100 ? ">100 (near-separation)"
                    : f.LogisticOddsRatio < 0.01 ? "<0.01 (near-separation)"
                    : f.LogisticOddsRatio.ToString("F2", CultureInfo.InvariantCulture);
                sb.AppendLine($"| {f.Name} | {Pct(f.QuartileWr[0])} | {Pct(f.QuartileWr[1])} | {Pct(f.QuartileWr[2])} | {Pct(f.QuartileWr[3])} | {or} | {f.LogisticP:F3} |");
            }
            sb.AppendLine();
            sb.AppendLine("Win rate by dominant purchase faction:");
            sb.AppendLine();
            sb.AppendLine("| Faction | Player-games | Win rate |");
            sb.AppendLine("|---|---|---|");
            foreach (var kv in res.DominantFactionWr.OrderByDescending(k => k.Value.WinScore / Math.Max(1, k.Value.N)))
                sb.AppendLine($"| {kv.Key} | {kv.Value.N} | {PctCi((int)Math.Round(kv.Value.WinScore), kv.Value.N)} |");
            sb.AppendLine();

            sb.AppendLine("## 6. Cards");
            sb.AppendLine();
            var flagged = res.Cards.Values.Where(c => c.ImpactSignificant)
                .OrderByDescending(c => c.ImpactDelta).ToList();
            if (flagged.Count > 0)
            {
                sb.AppendLine("### Flagged (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions)");
                sb.AppendLine();
                sb.AppendLine("| Card | Cost | Impact Δ | p | Buy rate | Co-acquired (lift) |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var c in flagged)
                    sb.AppendLine($"| {c.Name} ({c.Faction}) | {c.Cost} | **{c.ImpactDelta * 100:+0.0;-0.0} pts** | {c.ImpactP:F4} | {Pct(c.Offers == 0 ? 0 : (double)c.RowBuys / c.Offers)} | {string.Join(", ", c.CoAcquired)} |");
                sb.AppendLine();
                sb.AppendLine("Positive Δ + healthy buy rate ⇒ nerf candidate; negative Δ or rock-bottom buy rate ⇒ buff candidate. " +
                              "Cross-check the co-acquisition column before blaming a single card.");
            }
            else
            {
                sb.AppendLine("No card cleared the significance bar (BH FDR 10%, |Δ| ≥ 5 pts, ≥ 100 acquisitions).");
            }
            sb.AppendLine();
            sb.AppendLine("### Buy-rate outliers by cost band (full table in sim-summary.csv)");
            sb.AppendLine();
            foreach (var band in new[] { (1, 3, "1–3"), (4, 6, "4–6"), (7, 99, "7+") })
            {
                var cards = res.Cards.Values
                    .Where(c => c.Cost >= band.Item1 && c.Cost <= band.Item2 && c.Offers >= 50 && c.Type != "Monster")
                    .OrderBy(c => c.Offers == 0 ? 0 : (double)c.RowBuys / c.Offers).ToList();
                if (cards.Count == 0) continue;
                var least = cards.Take(3).Select(c => $"{c.Name} {Pct((double)c.RowBuys / c.Offers)}");
                var most = cards.AsEnumerable().Reverse().Take(3).Select(c => $"{c.Name} {Pct((double)c.RowBuys / c.Offers)}");
                sb.AppendLine($"- Cost {band.Item3}: least bought — {string.Join(", ", least)} · most bought — {string.Join(", ", most)}");
            }
            sb.AppendLine();

            sb.AppendLine("## 7. Relics, destinies, monsters");
            sb.AppendLine();
            if (res.Relics.Count > 0)
            {
                sb.AppendLine("| Relic | Recruits | WR when recruited |");
                sb.AppendLine("|---|---|---|");
                foreach (var s in res.Relics.Values.OrderBy(s => s.DefId))
                    sb.AppendLine($"| {s.Name} | {s.Taken} | {PctCi((int)Math.Round(s.TakenWinScore), s.Taken)} |");
                sb.AppendLine();
            }
            if (res.Destinies.Count > 0)
            {
                sb.AppendLine("| Destiny | In initial row | Taken | Avg round | WR taken | WR not taken |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var s in res.Destinies.Values.OrderByDescending(s => s.Taken / Math.Max(1.0, s.Opportunities)))
                    sb.AppendLine($"| {s.Name} | {s.Opportunities} | {s.Taken} ({Pct(s.Taken / Math.Max(1.0, s.Opportunities))}) | {s.AvgRound:F1} | {PctCi((int)Math.Round(s.TakenWinScore), s.Taken)} | {PctCi((int)Math.Round(s.NotTakenWinScore), s.NotTakenN)} |");
                sb.AppendLine();
            }
            if (res.Monsters.Count > 0)
            {
                sb.AppendLine($"| Monster | Revealed | Defeated | Avg defeat round | Defeater WR |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var s in res.Monsters.Values.OrderBy(s => s.DefId))
                    sb.AppendLine($"| {s.Name} | {s.Opportunities} | {s.Taken} ({Pct(s.Taken / Math.Max(1.0, s.Opportunities))}) | {s.AvgRound:F1} | {PctCi((int)Math.Round(s.TakenWinScore), s.Taken)} |");
                sb.AppendLine($"\nMonster attacks landed: {res.MonsterAttacksLanded}");
                sb.AppendLine();
            }

            sb.AppendLine("## 8. Methodology & caveats");
            sb.AppendLine();
            sb.AppendLine("- Every proportion carries a Wilson 95% interval; per-card deltas are stratified by matchup×seat, " +
                          "inverse-variance pooled, and Benjamini-Hochberg corrected (FDR 10%) with an effect floor.");
            sb.AppendLine("- **These are correlations between THESE bots' policies, not causal card effects.** A card bought " +
                          "when already ahead will look like a winner. Treat findings as directional input; re-test surprising " +
                          "ones with a targeted A/B (forced-strategy bot variant) before patching.");
            sb.AppendLine("- Seat counts are balanced exactly 50/50 per matchup by construction; seeds are sequential and " +
                          "reproducible (`soisim run --seed-base " + h.SeedBase + "`).");
            return sb.ToString();
        }

        private static string MachineJson(AnalysisResult res)
        {
            var h = res.Headers[0];
            return SimJson.Line(new
            {
                schema = h.Schema,
                configHash = h.ConfigHash,
                botVersion = h.BotVersion,
                budget = h.Budget,
                gitRev = h.GitRev,
                date = DateTime.UtcNow.ToString("o"),
                games = res.Games,
                decisive = res.Decisive,
                ties = res.Ties,
                failures = res.Failures,
                roundsP50 = res.RoundsP50,
                overwhelmShareOfWins = (double)res.OverwhelmWins / Math.Max(1, res.KillWins + res.OverwhelmWins),
                seat = new
                {
                    p0Wins = res.P0Wins,
                    decisiveGames = res.Decisive,
                    mirrorP0Wins = res.MirrorP0Wins,
                    mirrorDecisive = res.MirrorDecisive
                },
                characters = res.Characters.Values.ToDictionary(c => c.Id, c => new
                {
                    playerGames = c.PlayerGames,
                    winScore = c.WinScore
                }),
                matchups = res.Matchups.ToDictionary(kv => kv.Key, kv => new
                {
                    games = kv.Value.Games,
                    firstScore = kv.Value.AScore
                }),
                cards = res.Cards.Values.ToDictionary(c => c.DefId, c => new
                {
                    c.Name,
                    c.Faction,
                    c.Type,
                    c.Cost,
                    c.Offers,
                    c.RowBuys,
                    c.OffRowRecruits,
                    c.FastPlays,
                    buyRate = c.Offers == 0 ? 0 : (double)c.RowBuys / c.Offers,
                    c.AcquiringPlayerGames,
                    acquiringWinRate = c.AcquiringPlayerGames == 0 ? 0 : c.AcquiringWinScore / c.AcquiringPlayerGames,
                    c.AvgFirstBuyRound,
                    impactDelta = c.ImpactDelta,
                    impactP = c.ImpactP,
                    flagged = c.ImpactSignificant
                }),
                destinies = res.Destinies.Values.ToDictionary(s => s.DefId, s => new
                {
                    s.Name,
                    inInitialRow = s.Opportunities,
                    taken = s.Taken,
                    avgRound = s.AvgRound,
                    takenWinScore = s.TakenWinScore,
                    notTakenN = s.NotTakenN,
                    notTakenWinScore = s.NotTakenWinScore
                }),
                relics = res.Relics.Values.ToDictionary(s => s.DefId, s => new
                {
                    s.Name,
                    recruited = s.Taken,
                    winScore = s.TakenWinScore
                }),
                monsters = res.Monsters.Values.ToDictionary(s => s.DefId, s => new
                {
                    s.Name,
                    revealed = s.Opportunities,
                    defeated = s.Taken,
                    avgDefeatRound = s.AvgRound,
                    defeaterWinScore = s.TakenWinScore
                })
            });
        }

        private static string Csv(AnalysisResult res)
        {
            var sb = new StringBuilder();
            sb.AppendLine("defId,name,faction,type,cost,offers,rowBuys,offRowRecruits,fastPlays,buyRate,acquiringPlayerGames,acquiringWinRate,avgFirstBuyRound,impactDelta,impactP,flagged");
            foreach (var c in res.Cards.Values.OrderBy(c => c.DefId))
            {
                double buyRate = c.Offers == 0 ? 0 : (double)c.RowBuys / c.Offers;
                double awr = c.AcquiringPlayerGames == 0 ? 0 : c.AcquiringWinScore / c.AcquiringPlayerGames;
                sb.AppendLine(string.Join(",",
                    c.DefId, Quote(c.Name), c.Faction, c.Type, c.Cost, c.Offers, c.RowBuys, c.OffRowRecruits,
                    c.FastPlays, buyRate.ToString("F4", CultureInfo.InvariantCulture),
                    c.AcquiringPlayerGames, awr.ToString("F4", CultureInfo.InvariantCulture),
                    c.AvgFirstBuyRound.ToString("F2", CultureInfo.InvariantCulture),
                    c.ImpactDelta.ToString("F4", CultureInfo.InvariantCulture),
                    c.ImpactP.ToString("F5", CultureInfo.InvariantCulture),
                    c.ImpactSignificant ? "1" : "0"));
            }
            return sb.ToString();
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
