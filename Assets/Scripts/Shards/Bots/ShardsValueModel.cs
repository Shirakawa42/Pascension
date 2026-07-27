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

        /// <summary>characterId → net value of activating that hero's Duel ability
        /// (effect value minus its gem cost; the health cost is state-dependent and
        /// priced at scoring time). Precomputed: the abilities are fixed effects, so
        /// this is 5 entries, not a per-call effect walk.</summary>
        private readonly Dictionary<string, double> _heroAbilityValue = new();

        /// <summary>Reverts the four decisions added 2026-07-27 (removeshop / reset /
        /// defiant / mode) to the old `default` fall-through, purely so the fix can be
        /// A/B'd against what shipped before it. Never set in a shipped bot.</summary>
        private readonly bool _legacyDecisions;

        public ShardsValueModel(double[] weights = null, bool legacyDecisions = false)
        {
            _legacyDecisions = legacyDecisions;
            // Pad so a champion tuned before the current layout still reads every weight
            // (W.Pad is a no-op once the tuner has caught up).
            _w = W.Pad(weights ?? ShardsEvalWeights.Current);
            foreach (var def in ShardsCardDatabase.All)
            {
                var statics = ShardsCardStatics.Get(def);
                var slots = new (double[], double)[3][];
                slots[0] = CollapseSlot(statics.Play);
                slots[1] = CollapseSlot(statics.Exhaust);
                slots[2] = CollapseSlot(statics.Reward);
                _cache[def] = slots;
            }
            foreach (var characterId in ShardsEngine.DraftableCharacters)
            {
                var effect = ShardsEngine.HeroAbilityEffect(characterId);
                if (effect == null) continue; // Decima's ability is a passive discount
                var (res, structural) = Collapse(ShardsCardStatics.StandaloneAtoms(effect, 0));
                var spec = ShardsEngine.HeroAbilityInfo(characterId);
                _heroAbilityValue[characterId] =
                    ResourceValue(res) + structural - spec.Gems * _w[W.Gems];
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
                atoms.AllLoseMastery * _w[W.AllLoseMastery] +
                atoms.ScryDepth * _w[W.ScryPerCard] +
                atoms.ReorderDepth * _w[W.ReorderPerCard] +
                atoms.OppHandStrips * _w[W.OppHandStrip];
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
                            score -= potential * _w[W.PlayDeferPotential];
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
                case ShardsHeroAbilityAction:
                {
                    // Duel: spare-gem utility (draw / heal / banish / scry), priced from
                    // the ability's own effect atoms rather than a constant.
                    if (player.CharacterId == null ||
                        !_heroAbilityValue.TryGetValue(player.CharacterId, out double net))
                        return double.MinValue; // passive or unknown hero: nothing to activate
                    var spec = ShardsEngine.HeroAbilityInfo(player.CharacterId);
                    // Life is cheap at full health and near-suicidal when low, so scale the
                    // health component of the cost by scarcity (Ko Syn Wu pays 3).
                    if (spec.Health > 0)
                    {
                        double scarcity = engine.State.Rules.StartingHealth /
                                          (double)Math.Max(1, player.Health);
                        net -= spec.Health * _w[W.Health] * scarcity;
                    }
                    // Multiplicative, so a net-negative ability scores BELOW end turn and
                    // is simply not used. See W.HeroAbilityValueScale.
                    return net * _w[W.HeroAbilityValueScale];
                }
                case ShardsRerollRowAction reroll:
                {
                    // Duel: pay a CLIMBING price (1, 2, 3… per turn) to bottom a row card
                    // and refill. Worth it exactly when the slot is dead — i.e. sits below
                    // the buy threshold, so we would never spend gems on it anyway.
                    var card = engine.State.CenterRow[reroll.SlotIndex];
                    if (card == null) return double.MinValue;
                    int cost = ShardsEngine.RerollCost(player);
                    if (player.Gems < cost) return double.MinValue;
                    int slotCost = Math.Max(1, engine.EffectiveCost(player, card.Def));
                    double perGem = CardValue(card.Def, player.Mastery) / slotCost;
                    double deadness = _w[W.BuyThreshold] - perGem; // > 0 ⇒ we'd never buy it
                    return _w[W.RerollBase] + deadness * _w[W.RerollRowQualityDelta] -
                           cost * _w[W.Gems];
                }
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
                case "soi.tutor":     // Grim Tutor: fetch the highest-value deck card
                case "soi.handpick":  // Whisper Extractor: strip the victim's best card
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

                // ---- 2026-07-27: four decisions that fell through to `default` ----
                //
                // `soisim coverage` found each of these had exactly ONE reachable branch
                // across 4000 games, because the default adds Options[0..Min): a Min=0
                // decision was declined forever, and a forced one always took the first
                // option. Neither branch appeared in any game or any training position —
                // the row-reroll bug's shape, one level below the action histogram.
                //
                // These deliberately reuse the EXISTING tuned quantities (CardValue, the
                // BuyThreshold/DeckDilutionPerCard buy bar, W.Gems) rather than adding new
                // weight indices, so they are sensible immediately and tunable later
                // without a layout change.

                case "soi.removeshop" when !_legacyDecisions:
                {
                    // Reactor Drone / Remove-from-shop: bottom a row card and refill, FREE.
                    // Measured 0 taken / 7432 declined. Churn the DEADEST slot — the one we
                    // would not buy — since a fresh card is strictly better than a slot the
                    // buy bar already rejects. If the whole row is live, decline: removing a
                    // card we might still want is a real cost.
                    DecisionOption deadest = null;
                    double worst = double.MaxValue;
                    foreach (var option in request.Options)
                    {
                        var card = engine.State.FindCard(option.CardInstanceId);
                        if (card == null) continue;
                        double perGem = CardValue(card.Def, player.Mastery) /
                                        Math.Max(1, engine.EffectiveCost(player, card.Def));
                        if (perGem < worst) { worst = perGem; deadest = option; }
                    }
                    if (deadest != null && worst < _w[W.BuyThreshold])
                        answer.ChosenOptionIds.Add(deadest.Id);
                    break;
                }

                case "soi.reset" when !_legacyDecisions:
                {
                    // Un-exhaust one of your champions — FREE, and a second activation of
                    // the best exhaust on the board. Measured 0 taken / 1716 declined.
                    // Take it whenever any ready-again exhaust is worth more than its gem
                    // cost; there is nothing to trade away.
                    DecisionOption best = null;
                    double bestNet = 0;
                    foreach (var option in request.Options)
                    {
                        var card = engine.State.FindCard(option.CardInstanceId);
                        if (card == null) continue;
                        var slot = Slot(card.Def, 1, player.Mastery);
                        double net = ResourceValue(slot.Resources) + slot.Structural -
                                     card.Def.ExhaustGemCost * _w[W.Gems];
                        if (net > bestNet) { bestNet = net; best = option; }
                    }
                    if (best != null) answer.ChosenOptionIds.Add(best.Id);
                    break;
                }

                case "soi.defiant" when !_legacyDecisions:
                {
                    // Shard Defiant reveals a center-deck card: recruit it or banish it.
                    // Measured 5847 Keep / 0 Banish. It is FREE, so the buy bar runs at
                    // cost 0 — but dilution still applies, and a card below the bar makes a
                    // lean deck worse (eval-rules R7). Option id 1 = Keep, 2 = Banish.
                    var revealed = request.Options.Count > 0
                        ? ShardsCardDatabase.TryGet(request.Options[0].DefId, out var def) ? def : null
                        : null;
                    int size = player.Deck.Count + player.Hand.Count +
                               player.Discard.Count + player.PlayZone.Count;
                    double worth = revealed == null
                        ? 0
                        : CardValue(revealed, player.Mastery) -
                          Math.Max(0, size - 10) * _w[W.DeckDilutionPerCard];
                    answer.ChosenOptionIds.Add(worth > _w[W.BuyThreshold] ? 1 : 2);
                    break;
                }

                case "soi.mode" when !_legacyDecisions:
                {
                    // Reactor Drone: 1 = gain 2 gems, 2 = gain 3 gems then banish this card.
                    // Measured 5664 mode-1 / 0 mode-2. Mode 2 is +1 gem AND self-thinning,
                    // so it is better exactly when the card's remaining per-cycle value is
                    // below what the extra gem plus the dilution it stops are worth.
                    // Per-cycle rather than face value because a card in a deck of N is only
                    // drawn about 5/N times a turn (eval-rules R7).
                    //
                    // ⚠ ReactorChoice builds bare mode options — no CardInstanceId, no DefId
                    // (ShardsDuelSet.cs:531-532) — so the source must be inferred. The engine
                    // only sets BanishAtCleanup when the resolver really is a drone still in
                    // the play zone; resolved from a COPY (Ojas / Duplication Fabricator /
                    // Warpquartz) mode 2 banishes nothing and is +3 gems for free, so it
                    // strictly dominates. Reading DefId off the option would have made
                    // `source` null and flipped this to ALWAYS mode 2 — the same bug mirrored.
                    var drone = player.PlayZone.Find(c => c.DefId == "reactor_drone_duel" ||
                                                          c.DefId == "reactor_drone");
                    int deckSize = player.Deck.Count + player.Hand.Count +
                                   player.Discard.Count + player.PlayZone.Count;
                    double throughput = 5.0 / Math.Max(5, deckSize);
                    double keep = drone == null
                        ? 0 // a copy: nothing of ours gets banished, so keeping costs nothing
                        : CardValue(drone.Def, player.Mastery) * throughput;
                    double thin = _w[W.Gems] + Math.Max(0, deckSize - 10) * _w[W.DeckDilutionPerCard];
                    answer.ChosenOptionIds.Add(thin > keep ? 2 : 1);
                    break;
                }

                case "soi.target":
                {
                    // Target the opponent closest to death — the same rule the damage
                    // split uses. Option ids are seat indices (see DestroyOpponent).
                    DecisionOption closest = null;
                    int lowestHealth = int.MaxValue;
                    foreach (var option in request.Options)
                    {
                        if (option.Id < 0 || option.Id >= engine.State.Players.Count) continue;
                        var opponent = engine.State.Players[option.Id];
                        if (opponent.Eliminated || opponent.Health >= lowestHealth) continue;
                        lowestHealth = opponent.Health;
                        closest = option;
                    }
                    if (closest != null)
                        answer.ChosenOptionIds.Add(closest.Id);
                    break;
                }

                case "soi.herodraft":
                {
                    // Draft the lobby-configured hero while it is still available; the
                    // Min-pad below falls back to the first free hero otherwise.
                    if (request.DefaultOptionIds.Count > 0)
                        foreach (var option in request.Options)
                            if (option.Id == request.DefaultOptionIds[0])
                            {
                                answer.ChosenOptionIds.Add(option.Id);
                                break;
                            }
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
