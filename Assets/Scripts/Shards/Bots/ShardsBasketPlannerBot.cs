using System;
using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>One candidate spend-set for a turn: which center-row defs to buy (each at
    /// most once), whether to Focus, whether to fire the hero ability. A null
    /// <see cref="Defs"/> is the NATURAL basket — the unconstrained tuned-greedy turn,
    /// which is always candidate 0 and is what the search has to beat to act at all.</summary>
    public sealed class ShardsBasketPlan
    {
        public List<string> Defs;
        public bool Focus;
        public bool Hero;
        /// <summary>Reroll the DEADEST row slot (lowest value-per-gem) before buying, and
        /// allow buying whatever the refill reveals. The one spend the def-list cannot
        /// express, since the refill's identity does not exist at plan time.</summary>
        public bool Reroll;
    }

    /// <summary>Executes one basket through a turn, one action at a time: free actions by
    /// the tuned model first, then the prescribed spends, then end turn. State-driven —
    /// every step re-reads LegalActions, so a prescription the turn cannot afford simply
    /// underfills instead of stalling. Shared verbatim between the live bot and the
    /// `soisim rank` harness leaf construction (the measured thing is the shipped thing).</summary>
    public sealed class ShardsBasketCursor
    {
        private readonly List<string> _remaining;
        private bool _focus, _hero, _reroll;
        private int _rerolledSlot = -1;

        public ShardsBasketCursor(ShardsBasketPlan plan)
        {
            _remaining = plan.Defs == null ? null : new List<string>(plan.Defs);
            _focus = plan.Focus;
            _hero = plan.Hero;
            _reroll = plan.Reroll;
        }

        /// <summary>Natural basket: defer to the tuned greedy policy wholesale.</summary>
        public bool IsNatural => _remaining == null;

        public PlayerAction Next(ShardsEngine engine, int playerIndex, ShardsValueModel model)
        {
            if (IsNatural)
                return model.ChooseAction(engine, playerIndex);

            // A prescribed reroll refilled a slot last step: the reveal joins the
            // allowed-buy set IF it clears the tuned buy bar — "reroll, then buy the
            // refill if it is actually worth it". Force-buying an unknown reveal would
            // make every reroll leaf carry a coin-flip junk purchase.
            if (_rerolledSlot >= 0)
            {
                var revealed = engine.State.CenterRow[_rerolledSlot];
                if (revealed != null && !_remaining.Contains(revealed.DefId))
                {
                    var owner = engine.State.Players[playerIndex];
                    double perGem = model.CardValue(revealed.Def, owner.Mastery) /
                                    Math.Max(1, engine.EffectiveCost(owner, revealed.Def));
                    if (perGem >= model.Weights[W.BuyThreshold])
                        _remaining.Add(revealed.DefId);
                }
                _rerolledSlot = -1;
            }

            var action = ShardsPlannerBot.BestFreeAction(engine, playerIndex, model);
            if (action != null) return action;

            var legal = engine.LegalActions(playerIndex);
            var player = engine.State.Players[playerIndex];

            // Reroll BEFORE buying, so the refill is still purchasable this turn. Target
            // the deadest slot — lowest value-per-gem, the same rule the tuned model's
            // own reroll scoring uses — and never a slot we intend to buy from.
            if (_reroll)
            {
                ShardsRerollRowAction bestReroll = null;
                double deadest = double.MaxValue;
                foreach (var la in legal)
                {
                    if (la is not ShardsRerollRowAction reroll) continue;
                    var card = engine.State.CenterRow[reroll.SlotIndex];
                    if (card == null || _remaining.Contains(card.DefId)) continue;
                    double perGem = model.CardValue(card.Def, player.Mastery) /
                                    Math.Max(1, engine.EffectiveCost(player, card.Def));
                    if (perGem < deadest)
                    {
                        deadest = perGem;
                        bestReroll = reroll;
                    }
                }
                if (bestReroll != null)
                {
                    _reroll = false;
                    _rerolledSlot = bestReroll.SlotIndex;
                    return bestReroll;
                }
            }

            bool late = player.Mastery >= model.Weights[W.FastPlayMasteryGate] * 30.0;
            ShardsBuyCardAction bestBuy = null;
            double bestValue = double.MinValue;
            bool bestMatches = false;
            foreach (var la in legal)
            {
                if (la is not ShardsBuyCardAction buy) continue;
                var card = engine.State.CenterRow[buy.SlotIndex];
                if (card == null || !_remaining.Contains(card.DefId)) continue;
                // Prefer the recruit/fast-play variant the tuned gate would use, but take
                // the other over skipping the basket item entirely.
                bool matches = buy.FastPlay ==
                    (late && card.Def.Type == ShardsCardType.Mercenary);
                double value = model.CardValue(card.Def, player.Mastery);
                if (bestBuy == null || (matches && !bestMatches) ||
                    (matches == bestMatches && value > bestValue))
                {
                    bestBuy = buy;
                    bestValue = value;
                    bestMatches = matches;
                }
            }
            if (bestBuy != null)
            {
                _remaining.Remove(engine.State.CenterRow[bestBuy.SlotIndex].DefId);
                return bestBuy;
            }
            if (_focus)
                foreach (var la in legal)
                    if (la is ShardsFocusAction focus)
                    {
                        _focus = false;
                        return focus;
                    }
            if (_hero)
                foreach (var la in legal)
                    if (la is ShardsHeroAbilityAction hero)
                    {
                        _hero = false;
                        return hero;
                    }
            return new ShardsEndTurnAction { PlayerIndex = playerIndex };
        }
    }

    /// <summary>Search budget for <see cref="ShardsBasketPlannerBot"/>. Cost per turn is
    /// roughly (Baskets × Stage1 + Finalists × RolloutsPerWorld) × Worlds × ~0.5 ms —
    /// the defaults land near 200 ms, inside the shipping think-budget, and
    /// RolloutsPerWorld is the "think longer = stronger" knob.</summary>
    public sealed class ShardsBasketPlannerConfig
    {
        /// <summary>Determinized worlds per basket; leaf values average across them.</summary>
        public int Worlds = 2;
        /// <summary>Stage-1 screening rollouts per (basket, world) — just enough to sort
        /// the field before the deciding sample is spent on the finalists.</summary>
        public int Stage1RolloutsPerWorld = 8;
        /// <summary>Non-natural finalists advanced to stage 2 (natural always advances).
        /// Sized to the ~21-basket v2 field. The v3 experiment showed this knob cannot
        /// rescue a bigger field by itself (5 slots over 30 candidates still screened
        /// below v2) — growing the space needs a sublinear funnel, not more slots.</summary>
        public int Finalists = 3;
        /// <summary>Stage-2 deciding rollouts per (finalist, world). FRESH seeds — stage 1
        /// selected on its own sample, so reusing it would let selection noise masquerade
        /// as a real lead (the same cross-validation split `soisim rank` uses).</summary>
        public int RolloutsPerWorld = 24;
        /// <summary>Do not search before this round. Was 4 while rounds 1–3 were an
        /// unmeasured domain (v1's −76 Elo was partly noise-deviations there); then
        /// `soisim rank --min-round 1 --max-round 3` measured the OPENING as the richest
        /// domain of all — rollout headroom +0.027/turn vs +0.015 mid-game, top-2 truth
        /// gap 0.052 — because opening buys compound through the whole game. With the
        /// two-stage/margin rails carrying the anti-noise burden, the gate opens at 1.</summary>
        public int MinRound = 1;
        /// <summary>A challenger must beat the NATURAL turn's stage-2 score by this much
        /// to be executed. The search argmaxes over ~15 noisy estimates, so without a
        /// margin it deviates on noise at nearly every turn; with margin → ∞ the bot is
        /// exactly the tuned greedy (bounded downside, by construction).</summary>
        public double DeviationMargin = 0.05;
        /// <summary>Safety cap on submits while simulating one turn.</summary>
        public int MaxTurnSubmits = 400;
        /// <summary>Safety cap on submits per rollout (a full game is ~300).</summary>
        public int RolloutGuard = 20000;
    }

    /// <summary>Plans a turn as a PURCHASE BASKET scored by terminal rollouts.
    ///
    /// Every element here was measured before it was built, on `soisim rank`:
    ///  · Candidate FIRST ACTIONS are the wrong unit: their end-of-turn leaves differ by
    ///    0.013 true win-prob at the decision margin, and cross-validated headroom over the
    ///    tuned policy is +0.002/decision — nothing, for any selector. (That is why
    ///    ShardsPlannerBot sits at 12.7% and stays retired.)
    ///  · Static evaluators are the wrong signal: steering basket choice by the fitted
    ///    linear eval measured NEGATIVE headroom (−0.015/turn). Only rollout scoring
    ///    measured positive: +0.012/turn with a 48-rollout selector.
    ///  · Baskets are where the stakes live: sibling turns differ by 0.060 mean |Δtruth|
    ///    (vs 0.033 for actions), matching the ablation's 84-92% buy-axis share.
    ///
    /// So: at each of MY turn starts, enumerate ~15 spend-sets (the natural greedy turn,
    /// nothing, singletons, top pairs, focus/hero combos), play each to its end-of-turn
    /// leaf on determinized forks (common rng across baskets), score each leaf with
    /// terminal rollouts under the tuned policy (common random numbers, per-world leaf
    /// dedup by state hash so identical turns are priced once), and execute the argmax
    /// through a state-driven cursor. Decisions inside the turn stay with the tuned model.
    ///
    /// Fairness: forks are determinized with <see cref="ShardsDeterminizer"/> before any
    /// simulation — the bot never reads hidden order or the opponent's hand.</summary>
    public sealed class ShardsBasketPlannerBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _model;
        private readonly ShardsBasketPlannerConfig _config;
        private readonly DeterministicRng _rng;
        private readonly ShardsCloneArena _leafArena = new();
        private readonly ShardsCloneArena _rolloutArena = new();

        private (int Round, int Player) _planKey = (-1, -1);
        private ShardsBasketCursor _cursor;

        public string Descriptor =>
            $"basket-w{_config.Worlds}x{_config.Stage1RolloutsPerWorld}+{_config.RolloutsPerWorld}" +
            $"-m{_config.DeviationMargin:0.00}-r{_config.MinRound}";

        public ShardsBasketPlannerBot(ulong seed, ShardsEngine engine,
            ShardsValueModel model = null, ShardsBasketPlannerConfig config = null)
        {
            _engine = engine;
            _model = model ?? new ShardsValueModel();
            _config = config ?? new ShardsBasketPlannerConfig();
            _rng = new DeterministicRng(seed, 41);
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            if (pending.Kind == PendingInputKind.Decision)
                return new SubmitDecisionAction
                {
                    PlayerIndex = pending.PlayerIndex,
                    Answer = _model.ChooseAnswer(_engine, pending.Decision)
                };

            int me = pending.PlayerIndex;
            if (_engine.State.TurnPlayerIndex != me)
                return _model.ChooseAction(_engine, me); // defensive: never true in a duel

            var key = (_engine.State.Round, me);
            if (_planKey != key)
            {
                _cursor = Plan(me);
                _planKey = key;
            }
            return _cursor.Next(_engine, me, _model);
        }

        private ShardsBasketCursor Plan(int me)
        {
            var natural = new ShardsBasketPlan(); // Defs = null
            if (_engine.State.Round < _config.MinRound)
                return new ShardsBasketCursor(natural);

            var baskets = EnumerateBaskets(_engine, me, _model);
            int worlds = Math.Max(1, _config.Worlds);
            var worldSeeds = new ulong[worlds];
            for (int w = 0; w < worlds; w++)
                worldSeeds[w] = (((ulong)_rng.NextUInt() << 32) | _rng.NextUInt()) | 1UL;
            var stage1Seeds = DrawSeeds(worlds, Math.Max(1, _config.Stage1RolloutsPerWorld));
            var stage2Seeds = DrawSeeds(worlds, Math.Max(1, _config.RolloutsPerWorld));

            // Stage 1 — screen the whole field on a cheap CRN sample.
            var screened = ScoreBaskets(baskets, me, worldSeeds, stage1Seeds);

            // Stage 2 — natural plus the best challengers, on FRESH CRN seeds. Selecting
            // on stage 1 and deciding on stage 2 is what stops the argmax from crowning
            // its own noise (the first gate probe, 39.3%, is what happens otherwise).
            var finalists = new List<ShardsBasketPlan> { baskets[0] };
            foreach (int i in Enumerable_RangeByScoreDesc(screened))
            {
                if (finalists.Count > _config.Finalists) break;
                if (i != 0) finalists.Add(baskets[i]);
            }
            var decided = ScoreBaskets(finalists, me, worldSeeds, stage2Seeds);

            int best = 0; // index into finalists; 0 = natural
            for (int i = 1; i < finalists.Count; i++)
                if (decided[i] > decided[best])
                    best = i;
            // Execute a deviation only when it clearly beats the incumbent.
            if (best != 0 && decided[best] - decided[0] < _config.DeviationMargin)
                best = 0;
            return new ShardsBasketCursor(finalists[best]);
        }

        /// <summary>Indices of <paramref name="scores"/> in descending score order —
        /// explicit insertion sort so ties keep basket-enumeration order (Array.Sort is
        /// unstable, and a nondeterministic finalist set would break replay).</summary>
        private static int[] Enumerable_RangeByScoreDesc(double[] scores)
        {
            var order = new int[scores.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 1; i < order.Length; i++)
            {
                int key = order[i];
                int k = i - 1;
                while (k >= 0 && scores[order[k]] < scores[key])
                {
                    order[k + 1] = order[k];
                    k--;
                }
                order[k + 1] = key;
            }
            return order;
        }

        /// <summary>Scores each basket as the mean over worlds of its end-of-turn leaf's
        /// rollout value. CRN throughout: same world seeds and same rollout seeds for
        /// every basket. Identical leaves within a world are priced once (an unaffordable
        /// basket collapses into a smaller one, and re-rolling the same leaf would only
        /// add noise between two scores that should be equal).</summary>
        private double[] ScoreBaskets(List<ShardsBasketPlan> baskets, int me,
            ulong[] worldSeeds, ulong[,] rolloutSeeds)
        {
            var leafValues = new Dictionary<ulong, double>[worldSeeds.Length];
            for (int w = 0; w < worldSeeds.Length; w++)
                leafValues[w] = new Dictionary<ulong, double>();
            var scores = new double[baskets.Count];
            for (int b = 0; b < baskets.Count; b++)
            {
                double total = 0;
                for (int w = 0; w < worldSeeds.Length; w++)
                {
                    var sim = _engine.Fork(rngReseed: worldSeeds[w], quiet: true, arena: _leafArena);
                    ShardsDeterminizer.Sample(sim.State, me, sim.State.Rng);
                    RunToTurnEnd(sim, me, _model, baskets[b], _config.MaxTurnSubmits);
                    ulong hash = sim.State.ComputeFullHash();
                    if (!leafValues[w].TryGetValue(hash, out double value))
                    {
                        value = ScoreLeaf(sim, me, w, rolloutSeeds);
                        leafValues[w][hash] = value;
                    }
                    total += value;
                }
                scores[b] = total / worldSeeds.Length;
            }
            return scores;
        }

        private ulong[,] DrawSeeds(int worlds, int perWorld)
        {
            var seeds = new ulong[worlds, perWorld];
            for (int w = 0; w < worlds; w++)
                for (int j = 0; j < perWorld; j++)
                    seeds[w, j] = (((ulong)_rng.NextUInt() << 32) | _rng.NextUInt()) | 1UL;
            return seeds;
        }

        private double ScoreLeaf(ShardsEngine leaf, int me, int world, ulong[,] rolloutSeeds)
        {
            if (leaf.State.GameOver)
                return leaf.State.WinnerIndex < 0 ? 0.5 : leaf.State.WinnerIndex == me ? 1 : 0;
            if (leaf.PendingInput?.Kind != PendingInputKind.Priority)
                return 0.5; // parked mid-decision; cannot fork — price as unknown
            int n = rolloutSeeds.GetLength(1);
            double sum = 0;
            for (int j = 0; j < n; j++)
            {
                var rf = leaf.Fork(rngReseed: rolloutSeeds[world, j], quiet: true, arena: _rolloutArena);
                ShardsDeterminizer.Sample(rf.State, me, rf.State.Rng);
                sum += RolloutToTerminal(rf, me, _model, _config.RolloutGuard);
            }
            return sum / n;
        }

        // ------------------------------------------------------------ shared statics

        /// <summary>The candidate spend-sets for this turn. Grown in measured tiers, each
        /// gated by `soisim rank` before any bot probe:
        ///  · v1: natural (always index 0 — the incumbent), nothing, focus/hero alone,
        ///    singletons, top-6 pairs by value, top-3 def+focus, top triple;
        ///  · v2 (+18→+30 Elo): the combo tier — pairs+focus, triple+focus, focus+hero,
        ///    def+hero. V5's real turns buy AND focus; a space without the combos was
        ///    handicapped against the incumbent. ~21 candidates. THE SHIPPED SPACE.
        ///
        /// ⚠ v3 (feasible pairs + late quad + reroll-then-buy, ~30 candidates) was built,
        /// measured, and REVERTED 2026-07-27: the harness scored the space itself higher
        /// (+0.0208 vs +0.0149 ideal-selector headroom, same seeds) but the BOT screened
        /// 49.5–51.5% vs greedy against v2's 52.7–53.2 — the stage-1 funnel tax over 30
        /// candidates (expected max of 30 noise draws at 16 rollouts ≈ 0.26) exceeds the
        /// space gain, and widening to 5 finalists did not close it. Growing the space
        /// again requires a funnel that scales sublinearly (successive halving, or a
        /// stage-0 prior filter) — the cursor's Reroll support stays for that day.</summary>
        public static List<ShardsBasketPlan> EnumerateBaskets(ShardsEngine engine, int me,
            ShardsValueModel model)
        {
            var player = engine.State.Players[me];
            var defs = new List<(string Id, double Value)>();
            var seen = new HashSet<string>();
            foreach (var card in engine.State.CenterRow)
                if (card != null && seen.Add(card.DefId))
                    defs.Add((card.DefId, model.CardValue(card.Def, player.Mastery)));
            // Deterministic order: value desc, id as the tie-break.
            defs.Sort((x, y) => x.Value != y.Value
                ? y.Value.CompareTo(x.Value)
                : string.CompareOrdinal(x.Id, y.Id));

            var baskets = new List<ShardsBasketPlan>
            {
                new() { Defs = null },                       // 0: natural (incumbent)
                new() { Defs = new List<string>() },         // spend nothing
                new() { Defs = new List<string>(), Focus = true },
                new() { Defs = new List<string>(), Hero = true }
            };
            foreach (var (id, _) in defs)
                baskets.Add(new ShardsBasketPlan { Defs = new List<string> { id } });
            var pairs = new List<(List<string> Defs, double Value)>();
            for (int i = 0; i < defs.Count; i++)
                for (int k = i + 1; k < defs.Count; k++)
                    pairs.Add((new List<string> { defs[i].Id, defs[k].Id },
                        defs[i].Value + defs[k].Value));
            pairs.Sort((x, y) => x.Value != y.Value
                ? y.Value.CompareTo(x.Value)
                : string.CompareOrdinal(x.Defs[0] + x.Defs[1], y.Defs[0] + y.Defs[1]));
            for (int i = 0; i < pairs.Count && i < 6; i++)
                baskets.Add(new ShardsBasketPlan { Defs = pairs[i].Defs });
            for (int i = 0; i < defs.Count && i < 3; i++)
                baskets.Add(new ShardsBasketPlan
                {
                    Defs = new List<string> { defs[i].Id },
                    Focus = true
                });
            if (defs.Count >= 3)
                baskets.Add(new ShardsBasketPlan
                {
                    Defs = new List<string> { defs[0].Id, defs[1].Id, defs[2].Id }
                });
            // Combo tier: buy AND focus/hero in the same turn.
            for (int i = 0; i < pairs.Count && i < 3; i++)
                baskets.Add(new ShardsBasketPlan
                {
                    Defs = new List<string>(pairs[i].Defs),
                    Focus = true
                });
            if (defs.Count >= 3)
                baskets.Add(new ShardsBasketPlan
                {
                    Defs = new List<string> { defs[0].Id, defs[1].Id, defs[2].Id },
                    Focus = true
                });
            baskets.Add(new ShardsBasketPlan
            {
                Defs = new List<string>(),
                Focus = true,
                Hero = true
            });
            if (defs.Count >= 1)
                baskets.Add(new ShardsBasketPlan
                {
                    Defs = new List<string> { defs[0].Id },
                    Hero = true
                });
            return baskets;
        }

        /// <summary>Simulates one whole turn under a basket (natural → the full greedy
        /// tail; prescribed → the cursor policy) until the turn has passed.</summary>
        public static void RunToTurnEnd(ShardsEngine fork, int me, ShardsValueModel model,
            ShardsBasketPlan basket, int maxSubmits)
        {
            if (basket.Defs == null)
            {
                ShardsPlannerBot.CompleteTurn(fork, me, model, freeOnly: false, maxSubmits);
                return;
            }
            var cursor = new ShardsBasketCursor(basket);
            int submits = 0;
            while (!fork.State.GameOver && fork.State.TurnPlayerIndex == me &&
                   submits++ < maxSubmits)
            {
                var next = fork.PendingInput;
                if (next == null) break;
                PlayerAction action = next.Kind == PendingInputKind.Decision
                    ? new SubmitDecisionAction
                    {
                        PlayerIndex = next.PlayerIndex,
                        Answer = model.ChooseAnswer(fork, next.Decision)
                    }
                    : cursor.Next(fork, next.PlayerIndex, model);
                if (!fork.Submit(action).Accepted)
                {
                    if (next.Kind == PendingInputKind.Decision ||
                        !fork.Submit(new ShardsEndTurnAction { PlayerIndex = next.PlayerIndex }).Accepted)
                        break;
                }
            }
        }

        /// <summary>Plays a fork to terminal under the tuned model (both seats) and scores
        /// it for <paramref name="me"/>: win 1, tie 0.5, loss 0.</summary>
        public static double RolloutToTerminal(ShardsEngine rf, int me, ShardsValueModel model,
            int guardLimit)
        {
            int guard = 0;
            while (!rf.State.GameOver && guard++ < guardLimit)
            {
                var next = rf.PendingInput;
                if (next == null) break;
                PlayerAction action = next.Kind == PendingInputKind.Decision
                    ? new SubmitDecisionAction
                    {
                        PlayerIndex = next.PlayerIndex,
                        Answer = model.ChooseAnswer(rf, next.Decision)
                    }
                    : model.ChooseAction(rf, next.PlayerIndex);
                if (!rf.Submit(action).Accepted)
                    break;
            }
            if (!rf.State.GameOver) return 0.5; // guard hit — indistinguishable from noise
            return rf.State.WinnerIndex < 0 ? 0.5 : rf.State.WinnerIndex == me ? 1 : 0;
        }
    }
}
