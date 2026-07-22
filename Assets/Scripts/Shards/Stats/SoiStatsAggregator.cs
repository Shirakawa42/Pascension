using System;
using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>Pure fold over the record list — no IO, no clock. Semantics pinned by
    /// SoiStatsAggregatorTests:
    /// - filter pass = mode enabled AND (no OpponentKey OR an opponent seat matches it);
    /// - every aggregate runs over the filtered set EXCEPT Opponents (mode filter only)
    ///   and Lifetime* (filtered records + filtered stubs);
    /// - per-card and pair aggregation additionally skip Complete==false records;
    /// - winrate denominators exclude ties.</summary>
    public static class SoiStatsAggregator
    {
        public static SoiStatsAggregates Compute(IReadOnlyList<SoiGameRecord> records,
            IReadOnlyList<SoiGameStub> stubs, SoiStatsFilter f)
        {
            f ??= new SoiStatsFilter();
            var result = new SoiStatsAggregates();

            var filtered = new List<SoiGameRecord>();
            if (records != null)
                foreach (var r in records)
                    if (r != null && ModePasses(f, r.Mode) && OpponentPasses(f, r))
                        filtered.Add(r);
            filtered.Sort(ByEndedAt);

            result.Games = filtered.Count;
            AccumulateTotals(result, filtered);
            AccumulateStreaks(result, filtered);
            AccumulateAverages(result, filtered);
            BuildHeroes(result, filtered);
            PickBestHero(result);
            BuildCards(result, filtered);
            BuildOpponents(result, records, f);
            BuildRecent(result, filtered);
            if (f.OpponentKey != null)
                BuildHeadToHead(result, filtered, f.OpponentKey);
            AccumulateLifetime(result, stubs, f);
            return result;
        }

        public static List<PairAgg> ComputePairs(IReadOnlyList<SoiGameRecord> records,
            SoiStatsFilter f)
        {
            f ??= new SoiStatsFilter();
            var pairs = new Dictionary<string, PairAgg>();
            if (records != null)
                foreach (var r in records)
                {
                    if (r == null || !r.Complete || !ModePasses(f, r.Mode) || !OpponentPasses(f, r))
                        continue;
                    var my = MySeat(r);
                    if (my == null) continue;
                    bool tie = r.WinnerIndex < 0;
                    bool won = !tie && r.WinnerIndex == r.MyIndex;

                    var defs = new List<string>();
                    foreach (var kv in my.Buys)
                        if (kv.Value > 0)
                            defs.Add(kv.Key);
                    defs.Sort(StringComparer.Ordinal);

                    for (int a = 0; a < defs.Count; a++)
                        for (int b = a + 1; b < defs.Count; b++)
                        {
                            string key = defs[a] + "\n" + defs[b];
                            if (!pairs.TryGetValue(key, out var agg))
                                pairs[key] = agg = new PairAgg { DefA = defs[a], DefB = defs[b] };
                            agg.GamesTogether++;
                            if (won) agg.WinsTogether++;
                            if (tie) agg.TiesTogether++;
                        }
                }

            var list = new List<PairAgg>(pairs.Values);
            list.Sort((x, y) =>
            {
                int byA = string.CompareOrdinal(x.DefA, y.DefA);
                return byA != 0 ? byA : string.CompareOrdinal(x.DefB, y.DefB);
            });
            return list;
        }

        // ---------------------------------------------------------------- pieces

        private static void AccumulateTotals(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            foreach (var r in filtered)
            {
                bool tie = r.WinnerIndex < 0;
                bool won = !tie && r.WinnerIndex == r.MyIndex;
                if (tie) result.Ties++;
                else if (won) result.Wins++;
                else result.Losses++;

                var mode = ModeAggFor(result, r.Mode);
                if (mode != null)
                {
                    mode.Games++;
                    if (won) mode.Wins++;
                    if (tie) mode.Ties++;
                }

                if (tie) continue;
                if (won)
                {
                    // A 3+ player win is never "by concede" — someone conceding
                    // doesn't end the game there.
                    bool byConcede = r.Players.Count == 2 && OpponentConceded(r);
                    if (byConcede) result.WinsByConcede++;
                    else if (r.Termination == "overwhelm") result.WinsByOverwhelm++;
                    else result.WinsByKill++;
                }
                else
                {
                    var my = MySeat(r);
                    if (my != null && my.Conceded) result.LossesByConcede++;
                    else if (r.Termination == "overwhelm") result.LossesByOverwhelm++;
                    else result.LossesByKill++;
                }
            }
        }

        private static void AccumulateStreaks(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            int curWin = 0, curLoss = 0, best = 0;
            foreach (var r in filtered)
            {
                if (r.WinnerIndex < 0)
                {
                    // A tie resets both streaks and counts toward neither.
                    curWin = 0;
                    curLoss = 0;
                }
                else if (r.WinnerIndex == r.MyIndex)
                {
                    curWin++;
                    curLoss = 0;
                    if (curWin > best) best = curWin;
                }
                else
                {
                    curLoss++;
                    curWin = 0;
                }
            }
            result.CurrentWinStreak = curWin;
            result.CurrentLossStreak = curLoss;
            result.BestWinStreak = best;
        }

        private static void AccumulateAverages(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            if (filtered.Count == 0) return;
            long rounds = 0, duration = 0, damage = 0;
            int m10Sum = 0, m10Count = 0, m20Sum = 0, m20Count = 0, m30Sum = 0, m30Count = 0;
            foreach (var r in filtered)
            {
                rounds += r.Rounds;
                duration += r.DurationSeconds;
                var my = MySeat(r);
                if (my == null) continue;
                damage += my.DamageDealt;
                if (my.MaxSingleHit > result.MaxSingleHit) result.MaxSingleHit = my.MaxSingleHit;
                if (my.RoundToM10 >= 0) { m10Sum += my.RoundToM10; m10Count++; }
                if (my.RoundToM20 >= 0) { m20Sum += my.RoundToM20; m20Count++; }
                if (my.RoundToM30 >= 0) { m30Sum += my.RoundToM30; m30Count++; }
            }
            result.AvgRounds = (float)rounds / filtered.Count;
            result.AvgDurationSeconds = (float)duration / filtered.Count;
            result.AvgDamageDealt = (float)damage / filtered.Count;
            result.AvgRoundToM10 = m10Count > 0 ? (float)m10Sum / m10Count : -1;
            result.AvgRoundToM20 = m20Count > 0 ? (float)m20Sum / m20Count : -1;
            result.AvgRoundToM30 = m30Count > 0 ? (float)m30Sum / m30Count : -1;
            result.M30ReachRate = (float)m30Count / filtered.Count;
        }

        private static void BuildHeroes(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            var heroes = new Dictionary<string, HeroBuilder>();
            foreach (var r in filtered)
            {
                var my = MySeat(r);
                if (my?.CharacterId == null) continue;
                if (!heroes.TryGetValue(my.CharacterId, out var h))
                    heroes[my.CharacterId] = h = new HeroBuilder { Agg = { CharacterId = my.CharacterId } };
                AccumulateHero(h, r, my, r.WinnerIndex == r.MyIndex, r.WinnerIndex < 0);
            }
            result.Heroes = FinishHeroes(heroes);
        }

        private static void PickBestHero(SoiStatsAggregates result)
        {
            HeroAgg qualified = null;
            float bestRate = -1f;
            foreach (var h in result.Heroes)
            {
                int decisive = h.Games - h.Ties;
                if (decisive < 5) continue;
                float rate = (float)h.Wins / decisive;
                if (qualified == null || rate > bestRate ||
                    (rate == bestRate && h.Games > qualified.Games))
                {
                    qualified = h;
                    bestRate = rate;
                }
            }
            if (qualified != null)
            {
                result.BestHeroCharacterId = qualified.CharacterId;
                result.BestHeroQualified = true;
                return;
            }
            HeroAgg mostPlayed = null;
            foreach (var h in result.Heroes)
                if (mostPlayed == null || h.Games > mostPlayed.Games)
                    mostPlayed = h;
            result.BestHeroCharacterId = mostPlayed?.CharacterId;
            result.BestHeroQualified = false;
        }

        private static void BuildCards(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            var cards = new Dictionary<string, CardAgg>();
            foreach (var r in filtered)
            {
                if (!r.Complete) continue;
                var my = MySeat(r);
                if (my == null) continue;
                AccumulateCards(cards, my, r.WinnerIndex == r.MyIndex, r.WinnerIndex < 0);
            }
            result.Cards = FinishCards(cards);
        }

        private static void BuildOpponents(SoiStatsAggregates result,
            IReadOnlyList<SoiGameRecord> records, SoiStatsFilter f)
        {
            if (records == null) return;
            var opponents = new Dictionary<string, OpponentBuilder>();
            var seenInRecord = new HashSet<string>();
            foreach (var r in records)
            {
                if (r == null || !ModePasses(f, r.Mode)) continue;
                bool tie = r.WinnerIndex < 0;
                bool won = !tie && r.WinnerIndex == r.MyIndex;
                string latestKey = (r.EndedAtUtc ?? "") + "\n" + (r.Guid ?? "");
                seenInRecord.Clear();
                for (int i = 0; i < r.Players.Count; i++)
                {
                    if (i == r.MyIndex) continue;
                    var seat = r.Players[i];
                    if (seat?.Identity == null || !seenInRecord.Add(seat.Identity)) continue;
                    if (!opponents.TryGetValue(seat.Identity, out var b))
                        opponents[seat.Identity] = b = new OpponentBuilder
                        {
                            Agg = { IdentityKey = seat.Identity }
                        };
                    b.Agg.Games++;
                    if (tie) b.Agg.Ties++;
                    else if (won) b.Agg.MyWins++;
                    else b.Agg.MyLosses++;
                    // (EndedAtUtc, Guid) key keeps "latest" deterministic under
                    // input reordering even with equal timestamps.
                    if (b.LatestKey == null || string.CompareOrdinal(latestKey, b.LatestKey) > 0)
                    {
                        b.LatestKey = latestKey;
                        b.Agg.DisplayName = seat.Name;
                        b.Agg.IsBot = seat.IsBot;
                        b.Agg.LastPlayedUtc = r.EndedAtUtc;
                    }
                }
            }
            var keys = new List<string>(opponents.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
                result.Opponents.Add(opponents[key].Agg);
        }

        private static void BuildRecent(SoiStatsAggregates result, List<SoiGameRecord> filtered)
        {
            for (int i = filtered.Count - 1; i >= 0 && result.Recent.Count < 50; i--)
            {
                var r = filtered[i];
                var my = MySeat(r);
                var recent = new RecentGame
                {
                    Id = r.Guid,
                    EndedAtUtc = r.EndedAtUtc,
                    Mode = r.Mode,
                    Termination = r.Termination,
                    Tie = r.WinnerIndex < 0,
                    Won = r.WinnerIndex >= 0 && r.WinnerIndex == r.MyIndex,
                    MyCharacterId = my?.CharacterId,
                    MyMastery = my?.FinalMastery ?? 0,
                    MyHealth = my?.FinalHealth ?? 0,
                    Rounds = r.Rounds,
                    DurationSeconds = r.DurationSeconds
                };
                for (int s = 0; s < r.Players.Count; s++)
                    if (s != r.MyIndex && r.Players[s] != null)
                        recent.OpponentNames.Add(r.Players[s].Name);
                if (my != null)
                    foreach (var kv in my.Buys)
                        recent.MyBuys[kv.Key] = kv.Value;
                result.Recent.Add(recent);
            }
        }

        private static void BuildHeadToHead(SoiStatsAggregates result,
            List<SoiGameRecord> filtered, string opponentKey)
        {
            var h2h = new HeadToHead();
            var heroes = new Dictionary<string, HeroBuilder>();
            var cards = new Dictionary<string, CardAgg>();
            foreach (var r in filtered)
            {
                bool tie = r.WinnerIndex < 0;
                bool myWin = !tie && r.WinnerIndex == r.MyIndex;
                for (int i = 0; i < r.Players.Count; i++)
                {
                    if (i == r.MyIndex) continue;
                    var seat = r.Players[i];
                    if (seat == null || seat.Identity != opponentKey) continue;

                    if (seat.CharacterId != null)
                    {
                        if (!heroes.TryGetValue(seat.CharacterId, out var h))
                            heroes[seat.CharacterId] = h = new HeroBuilder
                            {
                                Agg = { CharacterId = seat.CharacterId }
                            };
                        // Games = games they played that hero; Wins = MY wins in them.
                        AccumulateHero(h, r, seat, myWin, tie);
                    }
                    if (r.Complete)
                        AccumulateCards(cards, seat, myWin, tie);
                }
            }
            h2h.TheirHeroes = FinishHeroes(heroes);
            h2h.TheirCards = FinishCards(cards);
            result.H2H = h2h;
        }

        private static void AccumulateLifetime(SoiStatsAggregates result,
            IReadOnlyList<SoiGameStub> stubs, SoiStatsFilter f)
        {
            result.LifetimeGames = result.Games;
            result.LifetimeWins = result.Wins;
            result.LifetimeTies = result.Ties;
            if (stubs == null) return;
            foreach (var stub in stubs)
            {
                if (stub == null || !ModePasses(f, stub.Mode) || !StubOpponentPasses(f, stub))
                    continue;
                result.LifetimeGames++;
                if (stub.Won) result.LifetimeWins++;
                if (stub.Tie) result.LifetimeTies++;
            }
        }

        // ---------------------------------------------------------------- shared helpers

        private sealed class HeroBuilder
        {
            public readonly HeroAgg Agg = new();
            public long RoundsSum;
            public int M30Sum;
            public int M30Count;
        }

        private sealed class OpponentBuilder
        {
            public readonly OpponentAgg Agg = new();
            public string LatestKey;
        }

        private static void AccumulateHero(HeroBuilder h, SoiGameRecord r, SoiSeatRecord seat,
            bool win, bool tie)
        {
            h.Agg.Games++;
            if (win) h.Agg.Wins++;
            if (tie) h.Agg.Ties++;
            if (seat.MaxSingleHit > h.Agg.MaxSingleHit) h.Agg.MaxSingleHit = seat.MaxSingleHit;
            h.RoundsSum += r.Rounds;
            if (seat.RoundToM30 >= 0)
            {
                h.M30Sum += seat.RoundToM30;
                h.M30Count++;
            }
        }

        private static List<HeroAgg> FinishHeroes(Dictionary<string, HeroBuilder> heroes)
        {
            var keys = new List<string>(heroes.Keys);
            keys.Sort(StringComparer.Ordinal);
            var list = new List<HeroAgg>();
            foreach (var key in keys)
            {
                var h = heroes[key];
                h.Agg.AvgRounds = h.Agg.Games > 0 ? (float)h.RoundsSum / h.Agg.Games : 0;
                h.Agg.AvgRoundToM30 = h.M30Count > 0 ? (float)h.M30Sum / h.M30Count : -1;
                list.Add(h.Agg);
            }
            return list;
        }

        private static void AccumulateCards(Dictionary<string, CardAgg> cards, SoiSeatRecord seat,
            bool win, bool tie)
        {
            foreach (var kv in seat.Buys)
            {
                if (kv.Value <= 0) continue;
                var c = Card(cards, kv.Key);
                c.TimesBought += kv.Value;
                c.GamesBought++;
                if (win) c.WinsWhenBought++;
                if (tie) c.TiesWhenBought++;
            }
            foreach (var kv in seat.Plays)
            {
                if (kv.Value <= 0) continue;
                var c = Card(cards, kv.Key);
                c.TimesPlayed += kv.Value;
                c.GamesPlayed++;
                if (win) c.WinsWhenPlayed++;
                if (tie) c.TiesWhenPlayed++;
            }
            foreach (var kv in seat.ChampionsDeployed)
                Card(cards, kv.Key).ChampionDeploys += kv.Value;
        }

        private static List<CardAgg> FinishCards(Dictionary<string, CardAgg> cards)
        {
            var keys = new List<string>(cards.Keys);
            keys.Sort(StringComparer.Ordinal);
            var list = new List<CardAgg>();
            foreach (var key in keys)
                list.Add(cards[key]);
            return list;
        }

        private static CardAgg Card(Dictionary<string, CardAgg> cards, string defId)
        {
            if (!cards.TryGetValue(defId, out var c))
                cards[defId] = c = new CardAgg { DefId = defId };
            return c;
        }

        private static bool ModePasses(SoiStatsFilter f, string mode)
        {
            switch (mode)
            {
                case "ai": return f.IncludeAi;
                case "mp2": return f.IncludeMp2;
                case "mp3plus": return f.IncludeMp3Plus;
                default: return false;
            }
        }

        private static bool OpponentPasses(SoiStatsFilter f, SoiGameRecord r)
        {
            if (f.OpponentKey == null) return true;
            for (int i = 0; i < r.Players.Count; i++)
                if (i != r.MyIndex && r.Players[i] != null && r.Players[i].Identity == f.OpponentKey)
                    return true;
            return false;
        }

        private static bool StubOpponentPasses(SoiStatsFilter f, SoiGameStub stub)
        {
            if (f.OpponentKey == null) return true;
            if (stub.Opponents == null) return false;
            foreach (var opp in stub.Opponents)
                if (opp != null && opp.Identity == f.OpponentKey)
                    return true;
            return false;
        }

        private static bool OpponentConceded(SoiGameRecord r)
        {
            for (int i = 0; i < r.Players.Count; i++)
                if (i != r.MyIndex && r.Players[i] != null && r.Players[i].Conceded)
                    return true;
            return false;
        }

        private static SoiSeatRecord MySeat(SoiGameRecord r) =>
            r.MyIndex >= 0 && r.MyIndex < r.Players.Count ? r.Players[r.MyIndex] : null;

        private static ModeAgg ModeAggFor(SoiStatsAggregates result, string mode)
        {
            switch (mode)
            {
                case "ai": return result.Ai;
                case "mp2": return result.Mp2;
                case "mp3plus": return result.Mp3Plus;
                default: return null;
            }
        }

        private static int ByEndedAt(SoiGameRecord a, SoiGameRecord b)
        {
            int byTime = string.CompareOrdinal(a.EndedAtUtc ?? "", b.EndedAtUtc ?? "");
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Guid ?? "", b.Guid ?? "");
        }
    }
}
