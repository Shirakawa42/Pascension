using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    public sealed class AnalysisResult
    {
        public List<RunHeader> Headers = new();
        public int Games, Decisive, Ties, Failures;
        public int KillWins, OverwhelmWins;
        public double RoundsP10, RoundsP50, RoundsP90;
        public double AvgSubmits;
        public int ComebackWins;

        public int P0Wins;                              // of decisive games
        public int MirrorGames, MirrorP0Wins, MirrorDecisive;

        public sealed class CharacterStat
        {
            public string Id;
            public int PlayerGames, Wins;               // ties count 0.5 in WinScore
            public double WinScore;
        }
        public Dictionary<string, CharacterStat> Characters = new();
        /// <summary>matchup key "a:b" (sorted) → (aWinScore, games). Mirror rows track seat-0 score.</summary>
        public Dictionary<string, (double AScore, int Games)> Matchups = new();

        public sealed class CardStat
        {
            public string DefId;
            public string Name;
            public string Faction;
            public string Type;
            public int Cost;
            public int Offers;
            public int RowBuys;
            public int OffRowRecruits;
            public int FastPlays;
            public int AcquiringPlayerGames;
            public double AcquiringWinScore;
            public double AvgFirstBuyRound;
            public double ImpactDelta;                  // stratified WR(acquired) − WR(offered, not acquired)
            public double ImpactP = 1;
            public bool ImpactSignificant;              // BH FDR 10% + effect floor
            public List<string> CoAcquired = new();     // top lift, flagged cards only
        }
        public Dictionary<string, CardStat> Cards = new();

        public sealed class FeatureStat
        {
            public string Name;
            /// <summary>Win rate per quartile of the feature (Q1..Q4) with Ns.</summary>
            public double[] QuartileWr = new double[4];
            public int[] QuartileN = new int[4];
            public double LogisticOddsRatio = 1;        // per +1 SD
            public double LogisticP = 1;
        }
        public List<FeatureStat> Features = new();
        public Dictionary<string, (double WinScore, int N)> DominantFactionWr = new();

        public sealed class SimpleStat
        {
            public string DefId;
            public string Name;
            public int Opportunities;                   // games offered / games by character / reveals
            public int Taken;
            public double TakenWinScore;
            public double NotTakenWinScore;
            public int NotTakenN;
            public double AvgRound;
        }
        public Dictionary<string, SimpleStat> Relics = new();
        public Dictionary<string, SimpleStat> Destinies = new();
        public Dictionary<string, SimpleStat> Monsters = new();
        public int MonsterAttacksLanded;
        public long DamagePrevented, DamageDealt;
    }

    /// <summary>Aggregates JSONL game records into every question the balance report
    /// answers. Pure reads; deterministic given the same input files.</summary>
    public static class Analyzer
    {
        public static AnalysisResult Analyze(IReadOnlyList<string> files, bool allowMixed)
        {
            var records = new List<GameRecord>();
            var result = new AnalysisResult();

            foreach (string file in files)
            {
                using var reader = new StreamReader(file);
                string line = reader.ReadLine();
                if (line == null) continue;
                var header = JsonConvert.DeserializeObject<RunHeader>(line, SimJson.Settings);
                if (header?.Type != "header")
                    throw new CliError($"{file}: first line is not a run header");
                if (result.Headers.Count > 0 && header.ConfigHash != result.Headers[0].ConfigHash && !allowMixed)
                    throw new CliError($"{file}: config hash differs from the first file — pass --allow-mixed to override");
                result.Headers.Add(header);
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var record = JsonConvert.DeserializeObject<GameRecord>(line, SimJson.Settings);
                    if (record?.Type == "game")
                        records.Add(record);
                }
            }
            if (records.Count == 0)
                throw new CliError("no game records found");

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();

            Overall(records, result);
            SeatAdvantage(records, result);
            CharactersAndMatchups(records, result);
            Cards(records, result);
            Playstyles(records, result);
            SetPieces(records, result);
            return result;
        }

        private static double WinScore(GameRecord r, int seat) =>
            r.Winner < 0 ? 0.5 : r.Winner == seat ? 1 : 0;

        private static ShardsCardDef Def(string id) =>
            ShardsCardDatabase.TryGet(id, out var def) ? def : null;

        private static bool Decisive(GameRecord r) => r.Termination is "kill" or "overwhelm";

        private static void Overall(List<GameRecord> records, AnalysisResult res)
        {
            var rounds = new List<int>();
            long submits = 0;
            foreach (var r in records)
            {
                res.Games++;
                switch (r.Termination)
                {
                    case "kill": res.Decisive++; res.KillWins++; break;
                    case "overwhelm": res.Decisive++; res.OverwhelmWins++; break;
                    case "tie": res.Ties++; break;
                    default: res.Failures++; continue;
                }
                rounds.Add(r.Rounds);
                submits += r.GuardSubmits;

                if (r.Winner >= 0 && ComebackWin(r))
                    res.ComebackWins++;
            }
            rounds.Sort();
            res.RoundsP10 = Stats.Percentile(rounds, 0.10);
            res.RoundsP50 = Stats.Percentile(rounds, 0.50);
            res.RoundsP90 = Stats.Percentile(rounds, 0.90);
            res.AvgSubmits = records.Count > 0 ? submits / (double)Math.Max(1, res.Games - res.Failures) : 0;

            foreach (var r in records)
            {
                res.MonsterAttacksLanded += r.MonsterAttacksLanded;
                foreach (var p in r.Players)
                {
                    res.DamagePrevented += p.DamagePrevented;
                    res.DamageDealt += p.DamageDealt;
                }
            }
        }

        private static bool ComebackWin(GameRecord r)
        {
            var w = r.Players[r.Winner];
            var l = r.Players[1 - r.Winner];
            int mid = Math.Min(Math.Min(w.HealthByRound.Count, l.HealthByRound.Count), r.Rounds / 2);
            if (mid <= 0) return false;
            return w.HealthByRound[mid - 1] < l.HealthByRound[mid - 1];
        }

        private static void SeatAdvantage(List<GameRecord> records, AnalysisResult res)
        {
            foreach (var r in records)
            {
                if (!Decisive(r) && r.Termination != "tie") continue;
                bool mirror = r.Chars[0] == r.Chars[1];
                if (mirror)
                {
                    res.MirrorGames++;
                    if (r.Winner >= 0)
                    {
                        res.MirrorDecisive++;
                        if (r.Winner == 0) res.MirrorP0Wins++;
                    }
                }
                if (r.Winner == 0) res.P0Wins++;
            }
        }

        private static void CharactersAndMatchups(List<GameRecord> records, AnalysisResult res)
        {
            foreach (var r in records)
            {
                if (r.Termination is not ("kill" or "overwhelm" or "tie")) continue;
                string a = r.Chars[0], b = r.Chars[1];
                var key = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
                string matchupKey = key.Item1 + ":" + key.Item2;

                // For the matchup entry, score is from the SORTED-first character's view;
                // in a mirror it is seat 0's view (seat advantage inside the mirror).
                int firstSeat = r.Chars[0] == key.Item1 ? 0 : 1;
                double firstScore = WinScore(r, firstSeat);
                var cur = res.Matchups.TryGetValue(matchupKey, out var m) ? m : (0.0, 0);
                res.Matchups[matchupKey] = (cur.Item1 + firstScore, cur.Item2 + 1);

                for (int seat = 0; seat < 2; seat++)
                {
                    if (!res.Characters.TryGetValue(r.Chars[seat], out var c))
                        res.Characters[r.Chars[seat]] = c = new AnalysisResult.CharacterStat { Id = r.Chars[seat] };
                    c.PlayerGames++;
                    double s = WinScore(r, seat);
                    c.WinScore += s;
                    if (s > 0.75) c.Wins++;
                }
            }
        }

        private static void Cards(List<GameRecord> records, AnalysisResult res)
        {
            // Universe: defs that were ever offered or acquired.
            foreach (var r in records)
            {
                if (r.Termination is not ("kill" or "overwhelm" or "tie")) continue;
                foreach (var kv in r.RowOffers) Card(res, kv.Key).Offers += kv.Value;
                foreach (var p in r.Players)
                {
                    foreach (var kv in p.Buys) Card(res, kv.Key).RowBuys += kv.Value;
                    foreach (var kv in p.OffRowRecruits) Card(res, kv.Key).OffRowRecruits += kv.Value;
                    foreach (var kv in p.FastPlays) Card(res, kv.Key).FastPlays += kv.Value;
                }
            }

            // Impact: stratify player-games by (matchup, seat); within a stratum compare
            // WR of acquirers vs non-acquirers among games where the card was OFFERED.
            var strata = new Dictionary<string, List<(GameRecord R, int Seat)>>();
            foreach (var r in records)
            {
                if (r.Winner is not (-1 or 0 or 1) || r.Termination is not ("kill" or "overwhelm" or "tie")) continue;
                for (int seat = 0; seat < 2; seat++)
                {
                    string sKey = r.Chars[0] + ">" + r.Chars[1] + "#" + seat;
                    if (!strata.TryGetValue(sKey, out var list))
                        strata[sKey] = list = new List<(GameRecord, int)>();
                    list.Add((r, seat));
                }
            }

            var defIds = res.Cards.Keys.ToList();
            var pValues = new List<double>();
            var firstBuyRounds = new Dictionary<string, (long Sum, int N)>();
            foreach (string defId in defIds)
            {
                var perStratum = new List<(int, int, int, int)>();
                foreach (var list in strata.Values)
                {
                    int accS = 0, accN = 0, nonS = 0, nonN = 0;
                    foreach (var (r, seat) in list)
                    {
                        if (!r.RowOffers.ContainsKey(defId)) continue;
                        var p = r.Players[seat];
                        bool acquired = p.Buys.ContainsKey(defId) || p.OffRowRecruits.ContainsKey(defId);
                        double score = WinScore(r, seat);
                        if (acquired)
                        {
                            accN++;
                            if (score > 0.75) accS++;
                        }
                        else
                        {
                            nonN++;
                            if (score > 0.75) nonS++;
                        }
                    }
                    if (accN > 0 && nonN > 0)
                        perStratum.Add((accS, accN, nonS, nonN));
                }

                var card = res.Cards[defId];
                var (delta, _, _, p2, _) = Stats.PooledDelta(perStratum);
                card.ImpactDelta = delta;
                card.ImpactP = p2;
                pValues.Add(p2);

                foreach (var r in records)
                {
                    if (r.Termination is not ("kill" or "overwhelm" or "tie")) continue;
                    for (int seat = 0; seat < 2; seat++)
                    {
                        var pl = r.Players[seat];
                        bool acquired = pl.Buys.ContainsKey(defId) || pl.OffRowRecruits.ContainsKey(defId);
                        if (!acquired) continue;
                        card.AcquiringPlayerGames++;
                        card.AcquiringWinScore += WinScore(r, seat);
                        if (pl.BuyRounds.TryGetValue(defId, out int round))
                        {
                            var cur = firstBuyRounds.TryGetValue(defId, out var f) ? f : (0L, 0);
                            firstBuyRounds[defId] = (cur.Item1 + round, cur.Item2 + 1);
                        }
                    }
                }
            }
            foreach (var kv in firstBuyRounds)
                res.Cards[kv.Key].AvgFirstBuyRound = (double)kv.Value.Sum / kv.Value.N;

            // BH at FDR 10% + effect floor: |Δ| ≥ 5 points and ≥ 100 acquisitions.
            var significant = Stats.BenjaminiHochberg(pValues, 0.10);
            for (int i = 0; i < defIds.Count; i++)
            {
                var card = res.Cards[defIds[i]];
                card.ImpactSignificant = significant[i] &&
                                         Math.Abs(card.ImpactDelta) >= 0.05 &&
                                         card.RowBuys + card.OffRowRecruits >= 100;
            }

            // Co-acquisition lift for flagged cards (top 5).
            var flagged = defIds.Where(d => res.Cards[d].ImpactSignificant).ToList();
            if (flagged.Count > 0)
            {
                var acquisitionSets = new List<HashSet<string>>();
                foreach (var r in records)
                    foreach (var p in r.Players)
                    {
                        var set = new HashSet<string>(p.Buys.Keys);
                        set.UnionWith(p.OffRowRecruits.Keys);
                        acquisitionSets.Add(set);
                    }
                int total = acquisitionSets.Count;
                var baseRate = new Dictionary<string, double>();
                foreach (string d in defIds)
                    baseRate[d] = acquisitionSets.Count(s => s.Contains(d)) / (double)total;

                foreach (string d in flagged)
                {
                    var with = acquisitionSets.Where(s => s.Contains(d)).ToList();
                    if (with.Count == 0) continue;
                    res.Cards[d].CoAcquired = defIds
                        .Where(o => o != d && baseRate[o] > 0.01)
                        .Select(o => (Def: o, Lift: with.Count(s => s.Contains(o)) / (double)with.Count / baseRate[o]))
                        .OrderByDescending(t => t.Lift)
                        .Take(5)
                        .Select(t => $"{t.Def} ×{t.Lift:F1}")
                        .ToList();
                }
            }
        }

        private static AnalysisResult.CardStat Card(AnalysisResult res, string defId)
        {
            if (!res.Cards.TryGetValue(defId, out var card))
            {
                var def = Def(defId);
                res.Cards[defId] = card = new AnalysisResult.CardStat
                {
                    DefId = defId,
                    Name = def?.Name ?? defId,
                    Faction = def?.Faction.ToString() ?? "?",
                    Type = def?.Type.ToString() ?? "?",
                    Cost = def?.Cost ?? 0
                };
            }
            return card;
        }

        private static void Playstyles(List<GameRecord> records, AnalysisResult res)
        {
            var names = new[]
            {
                "factionConcentration", "avgBuyCost", "championShare",
                "focusCount", "masteryAtRound8", "totalAcquisitions", "earlyAggression"
            };
            var rows = new List<double[]>();
            var wins = new List<int>();

            foreach (var r in records)
            {
                if (r.Winner is not (0 or 1)) continue;
                for (int seat = 0; seat < 2; seat++)
                {
                    var p = r.Players[seat];
                    var acquisitions = new Dictionary<string, int>(p.Buys);
                    foreach (var kv in p.OffRowRecruits)
                        acquisitions[kv.Key] = acquisitions.TryGetValue(kv.Key, out int v) ? v + kv.Value : kv.Value;

                    int total = acquisitions.Values.Sum();
                    var byFaction = new Dictionary<string, int>();
                    double costSum = 0;
                    int champions = 0;
                    foreach (var kv in acquisitions)
                    {
                        var def = Def(kv.Key);
                        if (def == null) continue;
                        string f = def.Faction.ToString();
                        byFaction[f] = (byFaction.TryGetValue(f, out int v) ? v : 0) + kv.Value;
                        costSum += def.Cost * kv.Value;
                        if (def.IsChampion) champions += kv.Value;
                    }
                    double herfindahl = total == 0 ? 0 :
                        byFaction.Values.Sum(v => (double)v * v) / ((double)total * total);
                    string dominant = byFaction.Count == 0 ? "none" :
                        byFaction.OrderByDescending(kv => kv.Value).First().Key;

                    // Early aggression = opponent health missing at their 6th turn.
                    // (NOT total damage dealt — that trivially separates winners and
                    // tells us nothing about style.)
                    var opp = r.Players[1 - seat];
                    double oppHealthAt6 = opp.HealthByRound.Count >= 6
                        ? opp.HealthByRound[5]
                        : opp.HealthByRound.Count > 0
                            ? opp.HealthByRound[opp.HealthByRound.Count - 1]
                            : opp.FinalHealth;

                    rows.Add(new[]
                    {
                        herfindahl,
                        total == 0 ? 0 : costSum / total,
                        total == 0 ? 0 : champions / (double)total,
                        p.FocusCount,
                        p.MasteryByRound.Count >= 8 ? p.MasteryByRound[7] : p.FinalMastery,
                        total,
                        Math.Max(0, 50 - oppHealthAt6)
                    });
                    wins.Add(r.Winner == seat ? 1 : 0);

                    var cur = res.DominantFactionWr.TryGetValue(dominant, out var d) ? d : (0.0, 0);
                    res.DominantFactionWr[dominant] = (cur.Item1 + (r.Winner == seat ? 1 : 0), cur.Item2 + 1);
                }
            }
            if (rows.Count < 50) return;

            // Quartile tables per feature.
            for (int f = 0; f < names.Length; f++)
            {
                var stat = new AnalysisResult.FeatureStat { Name = names[f] };
                var ordered = Enumerable.Range(0, rows.Count).OrderBy(i => rows[i][f]).ToList();
                for (int q = 0; q < 4; q++)
                {
                    int from = q * ordered.Count / 4, to = (q + 1) * ordered.Count / 4;
                    int n = 0, w = 0;
                    for (int i = from; i < to; i++)
                    {
                        n++;
                        w += wins[ordered[i]];
                    }
                    stat.QuartileN[q] = n;
                    stat.QuartileWr[q] = n == 0 ? 0 : (double)w / n;
                }
                res.Features.Add(stat);
            }

            // One logistic regression: win ~ seat is implicit in the data (both seats
            // present per game), features standardized so odds ratios are per +1 SD.
            int k = names.Length;
            var mean = new double[k];
            var sd = new double[k];
            foreach (var row in rows)
                for (int f = 0; f < k; f++)
                    mean[f] += row[f];
            for (int f = 0; f < k; f++) mean[f] /= rows.Count;
            foreach (var row in rows)
                for (int f = 0; f < k; f++)
                    sd[f] += (row[f] - mean[f]) * (row[f] - mean[f]);
            for (int f = 0; f < k; f++) sd[f] = Math.Sqrt(sd[f] / rows.Count);

            var x = rows.Select(row =>
            {
                var std = new double[k];
                for (int f = 0; f < k; f++)
                    std[f] = sd[f] < 1e-9 ? 0 : (row[f] - mean[f]) / sd[f];
                return std;
            }).ToArray();

            var fit = Stats.Logistic(x, wins.ToArray());
            if (fit != null)
            {
                var (beta, se) = fit.Value;
                for (int f = 0; f < k; f++)
                {
                    res.Features[f].LogisticOddsRatio = Math.Exp(beta[f + 1]);
                    double z = se[f + 1] < 1e-12 ? 0 : beta[f + 1] / se[f + 1];
                    res.Features[f].LogisticP = Stats.TwoSidedP(z);
                }
            }
        }

        private static void SetPieces(List<GameRecord> records, AnalysisResult res)
        {
            foreach (var r in records)
            {
                if (r.Termination is not ("kill" or "overwhelm" or "tie")) continue;
                for (int seat = 0; seat < 2; seat++)
                {
                    var p = r.Players[seat];
                    double score = WinScore(r, seat);

                    // Relics: opportunity (recruit-rate denominator) is filled in by the
                    // report from the owning character's player-game count.
                    foreach (string relic in p.Relics)
                    {
                        var s = Simple(res.Relics, relic);
                        s.Taken++;
                        s.TakenWinScore += score;
                    }

                    // Destinies: opportunity = the destiny sat in the initial row.
                    foreach (string d in r.DestinyRowInitial)
                    {
                        var s = Simple(res.Destinies, d);
                        s.Opportunities++;
                        if (p.Destinies.TryGetValue(d, out int round))
                        {
                            s.Taken++;
                            s.TakenWinScore += score;
                            s.AvgRound += round;
                        }
                        else
                        {
                            s.NotTakenN++;
                            s.NotTakenWinScore += score;
                        }
                    }

                    foreach (var kv in p.MonstersDefeated)
                    {
                        var s = Simple(res.Monsters, kv.Key);
                        s.Taken++;
                        s.TakenWinScore += score;
                        s.AvgRound += kv.Value;
                    }
                }

                foreach (var kv in r.MonstersRevealed)
                    Simple(res.Monsters, kv.Key).Opportunities += kv.Value;
            }

            foreach (var s in res.Destinies.Values)
                if (s.Taken > 0) s.AvgRound /= s.Taken;
            foreach (var s in res.Monsters.Values)
                if (s.Taken > 0) s.AvgRound /= s.Taken;
        }

        private static AnalysisResult.SimpleStat Simple(Dictionary<string, AnalysisResult.SimpleStat> dict, string defId)
        {
            if (!dict.TryGetValue(defId, out var s))
            {
                var def = Def(defId);
                dict[defId] = s = new AnalysisResult.SimpleStat { DefId = defId, Name = def?.Name ?? defId };
            }
            return s;
        }
    }
}
