using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>The tuned value core shared by the greedy bot (argmax policy), the
    /// ISMCTS rollout policy and move ordering. One instance per weight vector;
    /// card values are precomputed per (def, slot, mastery bucket) at construction,
    /// so per-action scoring is dictionary-lookup cheap and thread-safe (read-only).</summary>
    public sealed class ShardsValueModel
    {
        private readonly double[] _w;
        /// <summary>def → [slot 0=play 1=exhaust 2=reward][bucket] → (resources[5], structural).</summary>
        private readonly Dictionary<ShardsCardDef, (double[] Resources, double Structural)[][]> _cache = new();

        public double[] Weights => _w;

        public ShardsValueModel(double[] weights = null)
        {
            _w = weights ?? ShardsEvalWeights.Current;
            foreach (var def in ShardsCardDatabase.All)
            {
                var statics = ShardsCardStatics.Get(def);
                var slots = new (double[], double)[3][];
                slots[0] = CollapseSlot(statics.Play);
                slots[1] = CollapseSlot(statics.Exhaust);
                slots[2] = CollapseSlot(statics.Reward);
                _cache[def] = slots;
            }
        }

        private (double[], double)[] CollapseSlot(EffectAtoms[] perBucket)
        {
            var result = new (double[], double)[CardStatics.Buckets];
            for (int b = 0; b < CardStatics.Buckets; b++)
                result[b] = Collapse(perBucket[b]);
            return result;
        }

        /// <summary>Atoms → (expected resource vector, structural value) under the
        /// current weights: condition classes discounted, PerCount at expected units,
        /// structural capabilities priced.</summary>
        private (double[], double) Collapse(EffectAtoms atoms)
        {
            var res = new double[5];
            double[] classMult =
            {
                1.0, _w[W.Unify], _w[W.Dominion], _w[W.If], _w[W.Faction]
            };
            for (int cls = 0; cls < 5; cls++)
                for (int r = 0; r < 5; r++)
                    res[r] += atoms.Gains[cls, r] * classMult[cls];
            for (int r = 0; r < 5; r++)
                res[r] += atoms.PerUnit[r] * _w[W.PerCountUnits];

            double structural =
                atoms.Warps * atoms.WarpMaxCost * _w[W.WarpPerCost] +
                atoms.RecruitsRow * atoms.RecruitMaxCost * _w[W.RecruitRowPerCost] +
                atoms.DestroysChampions * _w[W.DestroyChampion] +
                atoms.BanishCapacity * _w[W.BanishPerCapacity] +
                (atoms.ReturnsFromDiscard ? _w[W.ReturnFromDiscard] : 0) +
                atoms.CopyEffects * _w[W.CopyEffect] +
                atoms.OppMasteryLoss * _w[W.OppMasteryLoss] +
                atoms.AllLoseHealth * _w[W.AllLoseHealth] +
                atoms.AllLoseMastery * _w[W.AllLoseMastery];
            return (res, structural);
        }

        // ---------------------------------------------------------------- values

        /// <summary>Expected resource gains of one slot at the given mastery.</summary>
        public (double[] Resources, double Structural) Slot(ShardsCardDef def, int slot, int mastery) =>
            _cache[def][slot][CardStatics.BucketOf(mastery)];

        private double ResourceValue(double[] resources) =>
            resources[0] * _w[W.Gems] + resources[1] * _w[W.Power] +
            resources[2] * _w[W.Mastery] + resources[3] * _w[W.Health] +
            resources[4] * _w[W.Draw];

        /// <summary>Weights added after a vector was tuned fall back to a default so
        /// older ShardsEvalWeights versions stay loadable (layout contract).</summary>
        private double WeightAt(int index, double fallback) =>
            index < _w.Length ? _w[index] : fallback;

        /// <summary>Resources in this card's play effect that would NOT fire if played
        /// right now: unlit conditional lines (exact ConditionMet probes) and unlit
        /// self-excluding PerCounts. This is the play-ORDER signal — an enabler in hand
        /// can still light them, so playing this card now wastes that value.</summary>
        private double[] UnlitPotential(ShardsEngine engine, ShardsPlayer player, ShardsCard card)
        {
            var res = new double[5];
            if (card.Def.PlayEffect == null) return res;
            var ctx = new ShardsContext { Engine = engine, ControllerIndex = player.Index, Source = card };
            WalkPotential(card.Def.PlayEffect, ctx, player.Mastery, res, underUnlit: false);
            return res;
        }

        private void WalkPotential(IShardsEffect effect, ShardsContext ctx, int mastery,
            double[] res, bool underUnlit)
        {
            switch (effect)
            {
                case null:
                    return;
                case ShardsComposite composite:
                    foreach (var part in composite.Parts)
                        WalkPotential(part, ctx, mastery, res, underUnlit);
                    return;
                case AtMastery tier:
                    if (mastery >= tier.Threshold)
                        WalkPotential(tier.Inner, ctx, mastery, res, underUnlit);
                    return;
                case BestByMastery best:
                {
                    IShardsEffect chosen = null;
                    int chosenThreshold = int.MinValue;
                    foreach (var (threshold, inner) in best.Tiers)
                        if (mastery >= threshold && threshold >= chosenThreshold)
                        {
                            chosenThreshold = threshold;
                            chosen = inner;
                        }
                    WalkPotential(chosen, ctx, mastery, res, underUnlit);
                    return;
                }
                case Unify unify:
                    WalkPotential(unify.Inner, ctx, mastery, res, underUnlit || !unify.ConditionMet(ctx));
                    return;
                case Dominion dominion:
                    WalkPotential(dominion.Inner, ctx, mastery, res, underUnlit || !dominion.ConditionMet(ctx));
                    return;
                case If conditional:
                    WalkPotential(conditional.Inner, ctx, mastery, res, underUnlit || !conditional.ConditionMet(ctx));
                    return;
                case FactionTrigger trigger:
                    WalkPotential(trigger.Inner, ctx, mastery, res, underUnlit || !trigger.ConditionMet(ctx));
                    return;
                case Gain gain:
                    if (underUnlit)
                    {
                        res[0] += gain.Gems;
                        res[1] += gain.Power;
                        res[2] += gain.Mastery;
                        res[3] += gain.Health;
                        res[4] += gain.Draw;
                    }
                    return;
                case PerCount per:
                    if (underUnlit || !per.ConditionMet(ctx))
                    {
                        var unit = per.PerUnit;
                        double units = _w[W.PerCountUnits];
                        res[0] += unit.gems * units;
                        res[1] += unit.power * units;
                        res[2] += unit.mastery * units;
                        res[3] += unit.health * units;
                        res[4] += unit.draw * units;
                    }
                    return;
                default:
                    return; // structural/custom nodes carry no ordering signal
            }
        }

        /// <summary>Deck-quality value of owning this card (play + recurring exhaust).</summary>
        public double CardValue(ShardsCardDef def, int mastery)
        {
            var play = Slot(def, 0, mastery);
            var exhaust = Slot(def, 1, mastery);
            double value = ResourceValue(play.Resources) + play.Structural;
            double exhaustValue = ResourceValue(exhaust.Resources) + exhaust.Structural;
            value += exhaustValue * (def.IsChampion ? _w[W.ChampionExhaustMult] : 1.0);
            if (def.Shield > 0) value += def.Shield * _w[W.ShieldPerPoint];
            if (def.IsChampion)
            {
                value += def.Defense * _w[W.DefensePerPoint];
                if (def.Taunt) value += _w[W.TauntBonus];
            }
            return value;
        }

        // ---------------------------------------------------------------- policy

        /// <summary>Greedy priority policy: argmax over scored legal actions.
        /// Deterministic given the engine state (ties break on first-seen).</summary>
        public PlayerAction ChooseAction(ShardsEngine engine, int playerIndex)
        {
            var legal = engine.LegalActions(playerIndex);
            var player = engine.State.Players[playerIndex];
            PlayerAction best = null;
            double bestScore = double.MinValue;
            foreach (var action in legal)
            {
                double score = ScoreAction(engine, player, action);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                }
            }
            return best ?? new ShardsEndTurnAction { PlayerIndex = playerIndex };
        }

        public double ScoreAction(ShardsEngine engine, ShardsPlayer player, PlayerAction action)
        {
            switch (action)
            {
                case ShardsPlayCardAction play:
                {
                    var card = engine.State.FindCard(play.CardInstanceId);
                    if (card == null) return double.MinValue;
                    var def = card.Def;
                    var slot = Slot(def, 0, player.Mastery);
                    double score = _w[W.PlayBase] +
                                   slot.Resources[2] * _w[W.PlayMastery] +
                                   slot.Resources[4] * _w[W.PlayDraw] +
                                   slot.Resources[0] + slot.Resources[1] + slot.Structural;
                    if (def.IsChampion) score += _w[W.PlayChampionBonus];
                    if (def.PlayEffect != null && ShardsGlowProbe.ConditionLit(def.PlayEffect,
                            new ShardsContext { Engine = engine, ControllerIndex = player.Index, Source = card }))
                        score += _w[W.PlayConditionLit];
                    // Play-ORDER synergy: while other cards remain in hand, defer plays
                    // whose conditional/PerCount value is currently unlit — an enabler
                    // may still light it (the Carnivorous Vine fix).
                    if (player.Hand.Count > 1)
                    {
                        double potential = ResourceValue(UnlitPotential(engine, player, card));
                        if (potential > 0)
                            score -= potential * WeightAt(W.PlayDeferPotential, 0.6);
                    }
                    return score;
                }
                case ShardsBuyCardAction buy:
                {
                    var card = engine.State.CenterRow[buy.SlotIndex];
                    if (card == null) return double.MinValue;
                    bool late = player.Mastery >= _w[W.FastPlayMasteryGate] * 30.0;
                    if (buy.FastPlay != (late && card.Def.Type == ShardsCardType.Mercenary))
                        return double.MinValue; // recruit early, fast-play late (tuned gate)
                    int cost = engine.EffectiveCost(player, card.Def);
                    double value = CardValue(card.Def, player.Mastery);
                    int deckSize = player.Deck.Count + player.Hand.Count + player.Discard.Count + player.PlayZone.Count;
                    value -= Math.Max(0, deckSize - 10) * _w[W.DeckDilutionPerCard];
                    double perGem = value / Math.Max(1, cost);
                    if (perGem < _w[W.BuyThreshold]) return double.MinValue;
                    return _w[W.BuyBase] + (perGem - _w[W.BuyThreshold]) * 100.0;
                }
                case ShardsExhaustAction exhaust:
                {
                    var card = engine.State.FindCard(exhaust.CardInstanceId);
                    if (card == null) return _w[W.ExhaustBase];
                    var slot = Slot(card.Def, 1, player.Mastery);
                    double net = ResourceValue(slot.Resources) + slot.Structural -
                                 card.Def.ExhaustGemCost * _w[W.Gems];
                    return _w[W.ExhaustBase] + net;
                }
                case ShardsAttackMonsterAction monster:
                {
                    var card = engine.State.FindCard(monster.CardInstanceId);
                    double reward = 0;
                    if (card != null)
                    {
                        var slot = Slot(card.Def, 2, player.Mastery);
                        reward = ResourceValue(slot.Resources) + slot.Structural;
                    }
                    return _w[W.AttackMonsterBase] + reward;
                }
                case ShardsTakeDestinyAction destiny:
                {
                    var card = engine.State.FindCard(destiny.CardInstanceId);
                    double value = card != null ? CardValue(card.Def, player.Mastery) : 0;
                    return _w[W.TakeDestinyBase] + value;
                }
                case ShardsRecruitRelicAction relic:
                {
                    var card = engine.State.FindCard(relic.CardInstanceId);
                    double value = card != null ? CardValue(card.Def, player.Mastery) : 0;
                    return _w[W.RecruitRelicBase] + value;
                }
                case ShardsFocusAction:
                    return _w[W.FocusBase];
                case ShardsEndTurnAction:
                    return _w[W.EndTurnBase];
                default:
                    return double.MinValue; // never concede by policy
            }
        }

        // ---------------------------------------------------------------- decisions

        public DecisionAnswer ChooseAnswer(ShardsEngine engine, DecisionRequest request)
        {
            var answer = new DecisionAnswer { DecisionId = request.Id };
            var player = engine.State.Players[request.PlayerIndex];

            switch (request.Context)
            {
                case "soi.split":
                    FillSplit(engine, request, player, answer);
                    break;

                case "soi.shields":
                    // Revealing is free — shields stay in hand.
                    foreach (var option in request.Options)
                        answer.ChosenOptionIds.Add(option.Id);
                    break;

                case "soi.discard":
                {
                    // Discard lowest kept-value first; shields carry extra keep-weight.
                    var ranked = new List<DecisionOption>(request.Options);
                    ranked.Sort((a, b) => KeepValue(engine, player, a).CompareTo(KeepValue(engine, player, b)));
                    for (int i = 0; i < request.Min && i < ranked.Count; i++)
                        answer.ChosenOptionIds.Add(ranked[i].Id);
                    break;
                }

                case "soi.banish":
                {
                    if (_w[W.BanishStarterValue] > 0)
                        foreach (var option in request.Options)
                        {
                            var card = engine.State.FindCard(option.CardInstanceId);
                            if (card != null && card.Zone == ShardsZone.Discard &&
                                card.Def.Type == ShardsCardType.Starter &&
                                card.DefId != "infinity_shard" &&
                                answer.ChosenOptionIds.Count < request.Max)
                                answer.ChosenOptionIds.Add(option.Id);
                        }
                    break;
                }

                case "soi.reveal":
                case "soi.confirm":
                case "soi.maglev":
                case "soi.keepfast":
                {
                    int take = Math.Max(request.Min, Math.Min(request.Max, request.Options.Count));
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
                    // Best candidate by tuned model value (not raw cost).
                    DecisionOption best = null;
                    double bestValue = double.MinValue;
                    foreach (var option in request.Options)
                    {
                        double value = OptionValue(engine, player, option);
                        if (value > bestValue)
                        {
                            bestValue = value;
                            best = option;
                        }
                    }
                    int want = Math.Max(request.Min, Math.Min(1, request.Max));
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

            // Honor Min even if a branch under-filled (mirrors the heuristic's safety pad).
            for (int i = 0; answer.ChosenOptionIds.Count < request.Min && i < request.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(request.Options[i].Id))
                    answer.ChosenOptionIds.Add(request.Options[i].Id);

            return answer;
        }

        private double KeepValue(ShardsEngine engine, ShardsPlayer player, DecisionOption option)
        {
            var card = engine.State.FindCard(option.CardInstanceId);
            if (card == null) return 0;
            double value = CardValue(card.Def, player.Mastery);
            if (card.Def.Shield > 0)
                value += card.Def.Shield * _w[W.DiscardShieldKeep];
            return value;
        }

        private double OptionValue(ShardsEngine engine, ShardsPlayer player, DecisionOption option)
        {
            ShardsCardDef def = null;
            if (option.DefId != null && ShardsCardDatabase.TryGet(option.DefId, out var byId))
                def = byId;
            else
            {
                var card = engine.State.FindCard(option.CardInstanceId);
                def = card?.Def;
            }
            return def == null ? 0 : CardValue(def, player.Mastery);
        }

        /// <summary>End-turn damage split: kill champions whose tuned kill-value beats
        /// spending the same power on face damage, honor taunts, dump the rest on the
        /// weakest living opponent.</summary>
        private void FillSplit(ShardsEngine engine, DecisionRequest request, ShardsPlayer player, DecisionAnswer answer)
        {
            int budget = request.Max;

            // Taunt first: a Required option MUST swallow its Amount before faces.
            DecisionOption taunt = null;
            foreach (var option in request.Options)
                if (option.Required)
                    taunt = option;

            var protectedOwners = new HashSet<int>();
            if (taunt != null)
                protectedOwners.Add(taunt.OwnerIndex);

            // Face target: weakest living opponent not behind a taunt (else the taunt owner).
            int face = -1, lowest = int.MaxValue;
            foreach (var option in request.Options)
            {
                if (option.Id >= ShardsEngine.ChampionSplitBase) continue;
                if (protectedOwners.Contains(option.Id)) continue;
                var opponent = engine.State.Players[option.Id];
                if (!opponent.Eliminated && opponent.Health < lowest)
                {
                    lowest = opponent.Health;
                    face = option.Id;
                }
            }

            if (taunt != null && taunt.Amount <= budget)
            {
                for (int i = 0; i < taunt.Amount; i++)
                    answer.ChosenOptionIds.Add(taunt.Id);
                budget -= taunt.Amount;
                if (face < 0) face = taunt.OwnerIndex;
            }
            else if (face < 0 && taunt != null)
            {
                // Can't break the taunt: everything goes into it (partial marks persist
                // within the turn only, but the assignment must still be legal).
                for (int i = 0; i < budget; i++)
                    answer.ChosenOptionIds.Add(taunt.Id);
                return;
            }

            // Champion kills that beat face damage, cheapest need first.
            var kills = new List<(int Id, int Need, double Score)>();
            foreach (var option in request.Options)
            {
                if (option.Id < ShardsEngine.ChampionSplitBase || option.Required) continue;
                var champion = engine.State.FindCard(option.CardInstanceId);
                if (champion == null) continue;
                int need = option.Amount > 0 ? option.Amount
                    : Math.Max(1, champion.Def.Defense - champion.DamageThisTurn);
                double killValue = champion.Def.Cost * _w[W.SplitKillPerCost];
                double faceValue = need * _w[W.SplitFaceBias];
                if (killValue > faceValue)
                    kills.Add((option.Id, need, killValue - faceValue));
            }
            kills.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var (id, need, _) in kills)
            {
                if (need > budget) continue;
                for (int i = 0; i < need; i++)
                    answer.ChosenOptionIds.Add(id);
                budget -= need;
            }

            if (face >= 0)
                for (int i = 0; i < budget; i++)
                    answer.ChosenOptionIds.Add(face);
        }
    }
}
