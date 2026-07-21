using System;
using System.Collections.Generic;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Builds a GameRecord from the finished game's omniscient event log plus
    /// two state peeks (initial destiny row — dealt silently in the engine ctor — and
    /// the final state). Requires NO engine changes: every metric below is derivable
    /// from the 27 Shards event types.</summary>
    public static class GameRecorder
    {
        public static GameRecord Extract(
            ShardsEngineAdapter adapter,
            ulong seed,
            IReadOnlyList<string> characters,
            IReadOnlyList<string> destinyRowInitial,
            IReadOnlyList<int> initialHealth,
            IReadOnlyList<int> initialMastery,
            int guardSubmits,
            int rejectedActions,
            double wallMs,
            string termination = null)
        {
            var state = adapter.Inner.State;
            int n = state.Players.Count;

            var record = new GameRecord
            {
                Seed = seed,
                Winner = adapter.WinnerIndex,
                Rounds = state.Round,
                GuardSubmits = guardSubmits,
                RejectedActions = rejectedActions,
                WallMs = Math.Round(wallMs, 2),
                FinalHash = state.ComputeHash(),
                DestinyRowInitial = new List<string>(destinyRowInitial)
            };
            foreach (string c in characters)
                record.Chars.Add(c);
            for (int i = 0; i < n; i++)
                record.Players.Add(new PlayerRecord
                {
                    Character = characters[i],
                    FinalHealth = state.Players[i].Health,
                    FinalMastery = state.Players[i].Mastery
                });

            // Running values for curves + threshold rounds (events carry NewValue, but
            // curves sample at turn starts, so we track continuously).
            var health = new int[n];
            var mastery = new int[n];
            for (int i = 0; i < n; i++)
            {
                health[i] = initialHealth[i];
                mastery[i] = initialMastery[i];
            }
            bool winnerOverwhelmed = false;
            int round = 1, turns = 0;

            var log = adapter.Inner.Log;
            for (int i = 0; i < log.Count; i++)
            {
                switch (log[i])
                {
                    case ShardsTurnStartedEvent e:
                        turns++;
                        round = e.Round;
                        record.Players[e.PlayerIndex].HealthByRound.Add(health[e.PlayerIndex]);
                        record.Players[e.PlayerIndex].MasteryByRound.Add(mastery[e.PlayerIndex]);
                        break;

                    case ShardsCardBoughtEvent e:
                    {
                        var p = record.Players[e.PlayerIndex];
                        // SlotIndex -1 = recruited off the center deck (Shard Defiant),
                        // never offered in the row — kept out of buy-rate math.
                        Bump(e.SlotIndex >= 0 ? p.Buys : p.OffRowRecruits, e.DefId);
                        if (!p.BuyRounds.ContainsKey(e.DefId))
                            p.BuyRounds[e.DefId] = round;
                        if (e.FastPlay)
                            Bump(p.FastPlays, e.DefId);
                        p.GemsSpent += e.CostPaid;
                        break;
                    }

                    case ShardsRowRefilledEvent e:
                        if (e.DefId != null)
                            Bump(record.RowOffers, e.DefId);
                        break;

                    case ShardsFocusedEvent e:
                        record.Players[e.PlayerIndex].FocusCount++;
                        break;

                    case ShardsMasteryChangedEvent e:
                    {
                        var p = record.Players[e.PlayerIndex];
                        mastery[e.PlayerIndex] = e.NewValue;
                        if (p.RoundToM10 < 0 && e.NewValue >= 10) p.RoundToM10 = round;
                        if (p.RoundToM20 < 0 && e.NewValue >= 20) p.RoundToM20 = round;
                        if (p.RoundToM30 < 0 && e.NewValue >= 30) p.RoundToM30 = round;
                        break;
                    }

                    case ShardsHealthChangedEvent e:
                        health[e.PlayerIndex] = e.NewValue;
                        break;

                    case ShardsPowerChangedEvent e:
                        if (e.PlayerIndex == record.Winner && e.NewValue > 1000)
                            winnerOverwhelmed = true;
                        break;

                    case ShardsDamageAssignedEvent e:
                    {
                        var p = record.Players[e.FromPlayerIndex];
                        for (int t = 0; t < e.Amounts.Count; t++)
                        {
                            p.DamageDealt += e.Amounts[t];
                            if (e.Amounts[t] > p.MaxSingleHit)
                                p.MaxSingleHit = e.Amounts[t];
                        }
                        break;
                    }

                    case ShardsShieldsRevealedEvent e:
                        record.Players[e.PlayerIndex].ShieldReveals += e.DefIds.Count;
                        record.Players[e.PlayerIndex].DamagePrevented += e.Prevented;
                        break;

                    case ShardsChampionDeployedEvent e:
                        Bump(record.Players[e.PlayerIndex].ChampionsDeployed, e.DefId);
                        break;

                    case ShardsChampionDestroyedEvent e:
                        record.Players[e.OwnerIndex].ChampionsLost++;
                        if (e.ByPlayerIndex >= 0 && e.ByPlayerIndex != e.OwnerIndex)
                            record.Players[e.ByPlayerIndex].ChampionsKilled++;
                        break;

                    case ShardsRelicRecruitedEvent e:
                        record.Players[e.PlayerIndex].Relics.Add(e.DefId);
                        break;

                    case ShardsDestinyTakenEvent e:
                        record.Players[e.PlayerIndex].Destinies[e.DefId] = round;
                        break;

                    case ShardsMonsterRevealedEvent e:
                        Bump(record.MonstersRevealed, e.DefId);
                        break;

                    case ShardsMonsterAttackedEvent:
                        record.MonsterAttacksLanded++;
                        break;

                    case ShardsMonsterDefeatedEvent e:
                        record.Players[e.PlayerIndex].MonstersDefeated[e.DefId] = round;
                        break;

                    case ShardsCardBanishedEvent e:
                        if (e.PlayerIndex >= 0)
                            record.Players[e.PlayerIndex].CardsBanished++;
                        break;

                    case ShardsCardDrawnEvent e:
                        record.Players[e.PlayerIndex].CardsDrawn++;
                        break;
                }
            }

            record.Turns = turns;
            record.Termination = termination ?? Classify(adapter, winnerOverwhelmed);
            return record;
        }

        private static string Classify(ShardsEngineAdapter adapter, bool winnerOverwhelmed)
        {
            if (!adapter.GameOver) return "guard_cap";
            if (adapter.WinnerIndex < 0) return "tie";
            return winnerOverwhelmed ? "overwhelm" : "kill";
        }

        private static void Bump(Dictionary<string, int> dict, string key) =>
            dict[key] = dict.TryGetValue(key, out int v) ? v + 1 : 1;
    }
}
