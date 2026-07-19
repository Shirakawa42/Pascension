using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>
    /// Host-side heuristic player for Shards of Infinity (holds the in-process engine,
    /// like Pascension's SyncAgentBot pattern). Strategy: play everything with mastery
    /// sources first, exhaust for free value, kill Ingeminex, buy by value-per-gem,
    /// focus with spare gems, concentrate end-turn damage on the weakest opponent,
    /// reveal every shield (reveals are free — shields stay in hand).
    /// </summary>
    public sealed class ShardsHeuristicBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly DeterministicRng _rng;
        private readonly bool _random;

        public ShardsHeuristicBot(ulong seed, ShardsEngine engine, bool random = false)
        {
            _engine = engine;
            _rng = new DeterministicRng(seed);
            _random = random;
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            if (pending.Kind == PendingInputKind.Decision)
                return Decide(pending.Decision);
            return _random ? PickRandom(pending) : PickAction(pending.PlayerIndex);
        }

        // ------------------------------------------------------------- priority

        private PlayerAction PickRandom(PendingSnap pending)
        {
            var legal = pending.LegalActions;
            if (legal == null || legal.Count == 0) return null;
            var usable = legal.FindAll(a => a is not ConcedeAction);
            if (usable.Count == 0) return legal[0];
            return usable[_rng.Next(usable.Count)];
        }

        private PlayerAction PickAction(int playerIndex)
        {
            var player = _engine.State.Players[playerIndex];
            var legal = _engine.LegalActions(playerIndex);

            // 1. Play the whole hand, best card first (mastery early: thresholds check
            //    at play time, so mastery sources should resolve before threshold cards).
            ShardsPlayCardAction bestPlay = null;
            double bestPlayScore = double.MinValue;
            foreach (var action in legal)
            {
                if (action is not ShardsPlayCardAction play) continue;
                var card = _engine.State.FindCard(play.CardInstanceId);
                if (card == null) continue;
                double score = PlayOrderScore(card.Def, player);
                if (score > bestPlayScore)
                {
                    bestPlayScore = score;
                    bestPlay = play;
                }
            }
            if (bestPlay != null) return bestPlay;

            // 2. Free value: exhaust champions/destinies.
            foreach (var action in legal)
                if (action is ShardsExhaustAction exhaust)
                    return exhaust;

            // 3. Ingeminex: always worth killing (reward + cancels its attack).
            foreach (var action in legal)
                if (action is ShardsAttackMonsterAction monster)
                    return monster;

            // 4. Free pickups.
            foreach (var action in legal)
                if (action is ShardsTakeDestinyAction destiny)
                    return destiny;
            foreach (var action in legal)
                if (action is ShardsRecruitRelicAction relic)
                    return relic;

            // (Champion kills happen in the end-of-turn damage split, not mid-turn.)

            // 5. Buy the best value per gem (recruit; fast-play late when the deck is set).
            ShardsBuyCardAction bestBuy = null;
            double bestBuyScore = 0.6; // don't buy junk just because it's affordable
            foreach (var action in legal)
            {
                if (action is not ShardsBuyCardAction buy) continue;
                var card = _engine.State.CenterRow[buy.SlotIndex];
                if (card == null) continue;
                bool late = player.Mastery >= 20;
                if (buy.FastPlay != (late && card.Def.Type == ShardsCardType.Mercenary))
                    continue;
                int cost = _engine.EffectiveCost(player, card.Def);
                double score = CardValue(card.Def, player) / System.Math.Max(1, cost);
                if (score > bestBuyScore)
                {
                    bestBuyScore = score;
                    bestBuy = buy;
                }
            }
            if (bestBuy != null) return bestBuy;

            // 6. Focus with a spare gem (mastery is the long game).
            foreach (var action in legal)
                if (action is ShardsFocusAction focus)
                    return focus;

            return new ShardsEndTurnAction { PlayerIndex = playerIndex };
        }

        private static double PlayOrderScore(ShardsCardDef def, ShardsPlayer player)
        {
            var value = EstimateGains(def.PlayEffect, player);
            double score = value.mastery * 100 + value.draw * 10 + value.gems + value.power;
            if (def.IsChampion) score += 50; // board presence + Inspire enablement
            return score;
        }

        private static double CardValue(ShardsCardDef def, ShardsPlayer player)
        {
            var play = EstimateGains(def.PlayEffect, player);
            var exhaust = EstimateGains(def.ExhaustEffect, player);
            double value = play.mastery * 3.0 + play.draw * 1.6 + play.gems * 1.0 +
                           play.power * 1.0 + play.health * 0.3;
            // Champions pay off every turn.
            value += (exhaust.mastery * 3.0 + exhaust.draw * 1.6 + exhaust.gems + exhaust.power) *
                     (def.IsChampion ? 2.0 : 1.0);
            if (def.Shield > 0) value += def.Shield * 0.3;
            if (def.IsChampion) value += def.Defense * 0.2;
            return value;
        }

        /// <summary>Walk the composed effect tree and estimate expected gains. Conditional
        /// branches count at half weight; unmet mastery tiers are ignored.</summary>
        private static (double gems, double power, double mastery, double health, double draw)
            EstimateGains(IShardsEffect effect, ShardsPlayer player, double weight = 1.0)
        {
            if (effect == null || weight < 0.05) return default;
            switch (effect)
            {
                case Gain gain:
                    return (gain.Gems * weight, System.Math.Min(gain.Power, 20) * weight,
                            gain.Mastery * weight, gain.Health * weight, gain.Draw * weight);
                case ShardsComposite composite:
                {
                    (double g, double p, double m, double h, double d) total = default;
                    foreach (var part in composite.Parts)
                    {
                        var v = EstimateGains(part, player, weight);
                        total = (total.g + v.gems, total.p + v.power, total.m + v.mastery,
                                 total.h + v.health, total.d + v.draw);
                    }
                    return total;
                }
                case AtMastery tier:
                    return player.Mastery >= tier.Threshold
                        ? EstimateGains(tier.Inner, player, weight)
                        : default;
                case BestByMastery best:
                {
                    IShardsEffect chosen = null;
                    int chosenThreshold = int.MinValue;
                    foreach (var (threshold, inner) in best.Tiers)
                        if (player.Mastery >= threshold && threshold >= chosenThreshold)
                        {
                            chosenThreshold = threshold;
                            chosen = inner;
                        }
                    return EstimateGains(chosen, player, weight);
                }
                case Unify unify:
                    return EstimateGains(unify.Inner, player, weight * 0.6);
                case Dominion dominion:
                    return EstimateGains(dominion.Inner, player, weight * 0.3);
                case If conditional:
                    return EstimateGains(conditional.Inner, player, weight * 0.5);
                case PerCount per:
                {
                    var unit = per.PerUnit;
                    const double units = 2.0;
                    return (unit.gems * units * weight, unit.power * units * weight,
                            unit.mastery * units * weight, unit.health * units * weight,
                            unit.draw * units * weight);
                }
                default:
                    // Opaque bespoke logic (Custom/choices) — assume modest value.
                    return (0, weight, 0, 0, 0);
            }
        }

        // ------------------------------------------------------------- decisions

        private PlayerAction Decide(DecisionRequest request)
        {
            var answer = new DecisionAnswer { DecisionId = request.Id };
            var player = _engine.State.Players[request.PlayerIndex];

            switch (request.Context)
            {
                case "soi.split":
                {
                    // Concentrate everything on the weakest living opponent WITHOUT a
                    // taunt champion; if every opponent hides behind a taunt (Zetta),
                    // kill the cheapest killable taunt and dump the rest on its owner.
                    var protectedOwners = new HashSet<int>();
                    foreach (var option in request.Options)
                        if (option.Required)
                            protectedOwners.Add(option.OwnerIndex);

                    int target = -1, lowest = int.MaxValue;
                    foreach (var option in request.Options)
                    {
                        if (option.Id >= ShardsEngine.ChampionSplitBase) continue; // champions: mid-turn business
                        if (protectedOwners.Contains(option.Id)) continue;
                        var opponent = _engine.State.Players[option.Id];
                        if (!opponent.Eliminated && opponent.Health < lowest)
                        {
                            lowest = opponent.Health;
                            target = option.Id;
                        }
                    }
                    if (target >= 0)
                    {
                        for (int i = 0; i < request.Max; i++)
                            answer.ChosenOptionIds.Add(target);
                    }
                    else
                    {
                        foreach (var option in request.Options)
                            if (option.Required && option.Amount <= request.Max)
                            {
                                for (int i = 0; i < option.Amount; i++)
                                    answer.ChosenOptionIds.Add(option.Id);
                                for (int i = option.Amount; i < request.Max; i++)
                                    answer.ChosenOptionIds.Add(option.OwnerIndex);
                                break;
                            }
                    }
                    break;
                }
                case "soi.shields":
                    // Revealing is free (shields stay in hand) — reveal everything.
                    foreach (var option in request.Options)
                        answer.ChosenOptionIds.Add(option.Id);
                    break;
                case "soi.discard":
                {
                    // Discard the cheapest cards.
                    var ranked = new List<DecisionOption>(request.Options);
                    ranked.Sort((a, b) => CostOf(a).CompareTo(CostOf(b)));
                    for (int i = 0; i < request.Min && i < ranked.Count; i++)
                        answer.ChosenOptionIds.Add(ranked[i].Id);
                    break;
                }
                case "soi.banish":
                {
                    // Deck-thinning: banish a starter from the DISCARD pile if available.
                    foreach (var option in request.Options)
                    {
                        var card = _engine.State.FindCard(option.CardInstanceId);
                        if (card != null && card.Zone == ShardsZone.Discard &&
                            card.Def.Type == ShardsCardType.Starter &&
                            card.DefId != "infinity_shard" &&
                            answer.ChosenOptionIds.Count < request.Max)
                            answer.ChosenOptionIds.Add(option.Id);
                    }
                    break;
                }
                case "soi.reveal":   // Unify/Dominion reveals are free value
                case "soi.confirm":
                case "soi.maglev":
                case "soi.keepfast":
                {
                    int take = System.Math.Max(request.Min, System.Math.Min(request.Max, request.Options.Count));
                    for (int i = 0; i < take; i++)
                        answer.ChosenOptionIds.Add(request.Options[i].Id);
                    break;
                }
                case "soi.warp":
                case "soi.recruit":
                case "soi.copy":
                case "soi.destroy":
                case "soi.return":
                case "soi.destiny":
                case "soi.relic":
                {
                    // Pick the most expensive candidate (better card ≈ higher cost).
                    DecisionOption best = null;
                    int bestCost = -1;
                    foreach (var option in request.Options)
                    {
                        int cost = CostOf(option);
                        if (cost > bestCost)
                        {
                            bestCost = cost;
                            best = option;
                        }
                    }
                    int want = System.Math.Max(request.Min, System.Math.Min(1, request.Max));
                    if (best != null && want > 0)
                        answer.ChosenOptionIds.Add(best.Id);
                    break;
                }
                default:
                {
                    for (int i = 0; i < request.Min && i < request.Options.Count; i++)
                        answer.ChosenOptionIds.Add(request.Options[i].Id);
                    break;
                }
            }

            // Safety: honor Min even if a branch under-filled.
            for (int i = 0; answer.ChosenOptionIds.Count < request.Min && i < request.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(request.Options[i].Id))
                    answer.ChosenOptionIds.Add(request.Options[i].Id);

            return new SubmitDecisionAction { PlayerIndex = request.PlayerIndex, Answer = answer };
        }

        private int CostOf(DecisionOption option)
        {
            var card = _engine.State.FindCard(option.CardInstanceId);
            return card?.Def.Cost ?? 0;
        }
    }
}
