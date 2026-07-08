using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Stack;
using Pascension.Engine.Targeting;

namespace Pascension.Bots
{
    /// <summary>
    /// The algorithmic opponent. Greedy but purposeful: develop the deck early,
    /// kill efficient monsters, race once elite power is online, deny lethal kills
    /// with barriers, counter expensive spells.
    /// </summary>
    public sealed class HeuristicBot : ISyncAgent
    {
        private readonly DeterministicRng _rng;

        /// <summary>Static buy-power scores by card id (higher = better).</summary>
        private static readonly Dictionary<string, int> BuyScore = new()
        {
            // basic
            ["run"] = 3, ["fireball"] = 3, ["clarity"] = 5, ["ban"] = 2,
            ["protective_barrier"] = 2, ["short_sword"] = 4, ["cloth_armor"] = 4, ["stone_totem"] = 4,
            // advanced
            ["counterspell"] = 4, ["random_bullshit_go"] = 3, ["sprint"] = 7, ["meteor"] = 7,
            ["adrenaline_shot"] = 7, ["fireworks"] = 7, ["reflexes"] = 5, ["sabotage"] = 4,
            ["longsword"] = 8, ["tower_shield"] = 8, ["lucky_charm"] = 8, ["merchant_stall"] = 6,
            ["war_banner"] = 7,
            // elite
            ["time_warp"] = 9, ["firestorm"] = 10, ["cataclysm"] = 4, ["divine_shield"] = 5,
            ["mind_steal"] = 5, ["blink"] = 4, ["excalibur"] = 12, ["dragonscale_armor"] = 11,
            ["philosophers_stone"] = 6, ["portal_stone"] = 11, ["throne_of_ambition"] = 8,
            ["travelers_map"] = 12, ["arcane_library"] = 10
        };

        public HeuristicBot(ulong seed) => _rng = new DeterministicRng(seed, 77);

        public PlayerAction ChooseAction(GameEngine engine, PendingInput input)
        {
            var state = engine.State;
            var p = state.Players[input.PlayerIndex];
            var legal = input.LegalActions;
            bool isMyTurn = state.TurnPlayerIndex == p.Index;

            // 1. Lethal on the boss always.
            foreach (var a in legal)
                if (a is AssignDamageAction atk && atk.Target.Kind == TargetKind.Boss)
                    return atk;

            if (!isMyTurn || !state.Stack.IsEmpty)
                return ChooseResponse(engine, p, legal);

            // ---- Own main phase, empty stack: fixed priority order ----

            // 2. Hero damage abilities before spending damage; other actives early.
            foreach (var a in legal)
                if (a is UseHeroAbilityAction hero)
                    return hero;

            // 3. Play hand: draw > resources > permanents. Instants: only proactive draw.
            PlayerAction best = null;
            int bestScore = int.MinValue;
            foreach (var a in legal)
            {
                if (a is not PlayCardAction play) continue;
                var card = state.FindCard(play.CardInstanceId);
                if (card == null) continue;
                var def = card.Def;
                int score = def.Type switch
                {
                    CardType.Instant => def.Id == "reflexes" ? 5 : int.MinValue, // hold reactive instants
                    CardType.Equipment => 20,
                    CardType.Relic => 20,
                    _ => ScoreActionCard(def)
                };
                if (score == int.MinValue) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = play;
                }
            }
            if (best != null) return best;

            // 4. Tap equipment for value.
            foreach (var a in legal)
                if (a is ActivateAbilityAction act)
                    return act;

            // 5. Kill the most rewarding monster we can afford.
            AssignDamageAction bestKill = null;
            int bestReward = -1;
            foreach (var a in legal)
            {
                if (a is not AssignDamageAction atk || atk.Target.Kind != TargetKind.MonsterSlot) continue;
                var monster = state.Market.SlotCard((CardTier)atk.Target.A, atk.Target.B);
                if (monster == null) continue;
                int reward = monster.Def.MonsterHp; // reward roughly scales with HP
                if (reward > bestReward)
                {
                    bestReward = reward;
                    bestKill = atk;
                }
            }
            if (bestKill != null) return bestKill;

            // 6. Buy or race. Racing takes over once elite power is unlocked or the board is short.
            bool racing = p.Level >= 8 || p.Position >= 40;
            var buy = BestBuy(engine, p, legal, racing);
            if (buy != null) return buy;

            // 7. Move with whatever AP is left.
            MoveStepsAction bestMove = null;
            foreach (var a in legal)
                if (a is MoveStepsAction move && (bestMove == null || move.Steps > bestMove.Steps))
                    bestMove = move;
            if (bestMove != null) return bestMove;

            return legal.Find(a => a is PassPriorityAction);
        }

        private static int ScoreActionCard(CardDefinition def)
        {
            // Draw first (more options), then AP, then damage.
            if (def.Id == "clarity") return 15;
            if (def.Id == "adrenaline_shot" || def.Id == "fireworks") return 14;
            if (def.Id == "time_warp") return 18;
            if (def.Id == "ban") return 1;
            return 10;
        }

        private BuyCardAction BestBuy(GameEngine engine, PlayerState p, List<PlayerAction> legal, bool racing)
        {
            BuyCardAction best = null;
            double bestValue = racing ? 9.5 : 0.0; // when racing, only exceptional cards beat moving
            foreach (var a in legal)
            {
                if (a is not BuyCardAction buy) continue;
                var card = engine.State.Market.Rows[buy.TierIndex][buy.SlotIndex];
                if (card == null) continue;
                int score = BuyScore.TryGetValue(card.DefId, out int s) ? s : 3;
                // Prefer higher tiers slightly; avoid flooding on cheap cards late.
                double value = score + buy.TierIndex * 0.5 + _rng.Next(2) * 0.25;
                if (value > bestValue)
                {
                    bestValue = value;
                    best = buy;
                }
            }
            return best;
        }

        private PlayerAction ChooseResponse(GameEngine engine, PlayerState p, List<PlayerAction> legal)
        {
            var state = engine.State;
            var top = state.Stack.Top;

            if (top != null && top.ControllerIndex != p.Index)
            {
                // Deny an opponent's kill with Protective Barrier / Divine Shield.
                if (top.Kind == StackItemKind.DamageAssignment && top.Targets.Count > 0 &&
                    top.Targets[0].Kind == TargetKind.MonsterSlot)
                {
                    var monster = state.Market.SlotCard((CardTier)top.Targets[0].A, top.Targets[0].B);
                    if (monster != null && monster.Def.MonsterHp >= 4)
                    {
                        foreach (var a in legal)
                            if (a is PlayCardAction play)
                            {
                                var def = state.FindCard(play.CardInstanceId)?.Def;
                                if (def != null && (def.Id == "protective_barrier" || def.Id == "divine_shield"))
                                    return play;
                            }
                    }
                }

                // Counter expensive opposing spells.
                if (top.Kind == StackItemKind.Spell && top.SpellCard != null && top.SpellCard.Def.Cost >= 5)
                {
                    foreach (var a in legal)
                        if (a is PlayCardAction play)
                        {
                            var def = state.FindCard(play.CardInstanceId)?.Def;
                            if (def != null && (def.Id == "counterspell" || def.Id == "mind_steal"))
                                return play;
                        }
                }
            }

            return legal.Find(a => a is PassPriorityAction);
        }

        public DecisionAnswer ChooseDecision(GameEngine engine, DecisionRequest request)
        {
            var state = engine.State;
            var p = state.Players[request.PlayerIndex];
            var answer = new DecisionAnswer { DecisionId = request.Id };

            switch (request.Kind)
            {
                case DecisionKind.InnChoice:
                {
                    // Early: XP. Later: thin the deck of Moves, else draw.
                    int moveCount = 0;
                    foreach (var c in p.Discard)
                        if (c.DefId == "move")
                            moveCount++;
                    int first = p.Level < 4 ? 0 : moveCount >= 2 ? 2 : 1;
                    answer.ChosenOptionIds.Add(first);
                    if (request.Max > 1)
                    {
                        int second = first == 0 ? 1 : 0;
                        answer.ChosenOptionIds.Add(second);
                    }
                    return answer;
                }

                case DecisionKind.ChooseTargets:
                {
                    // Counter the priciest spell; barrier the monster under attack; hex the weakest.
                    int bestId = request.Options[0].Id;
                    int bestScore = int.MinValue;
                    foreach (var option in request.Options)
                    {
                        int score = 0;
                        if (option.Target is { } t)
                        {
                            if (t.Kind == TargetKind.StackItem)
                            {
                                var item = state.Stack.Find(t.A);
                                score = item?.SpellCard?.Def.Cost ?? 0;
                                if (item != null && item.ControllerIndex == p.Index) score -= 100;
                            }
                            else if (t.Kind == TargetKind.MonsterSlot)
                            {
                                var top = state.Stack.Top;
                                bool underAttack = top is { Kind: StackItemKind.DamageAssignment } &&
                                                   top.Targets.Count > 0 && top.Targets[0].Equals(t);
                                score = underAttack ? 50 : 0;
                                var monster = state.Market.SlotCard((CardTier)t.A, t.B);
                                if (monster != null)
                                    score += 20 - engine.Api.EffectiveMonsterHp(monster);
                            }
                            else if (t.Kind == TargetKind.Player)
                            {
                                // Hit the race leader.
                                score = state.Players[t.A].Position;
                            }
                            else if (t.Kind == TargetKind.Card)
                            {
                                var card = state.FindCard(t.A);
                                score = card != null && card.Owner != p.Index ? card.Def.Cost : -10;
                            }
                        }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestId = option.Id;
                        }
                    }
                    answer.ChosenOptionIds.Add(bestId);
                    return answer;
                }

                case DecisionKind.ChooseCards:
                {
                    // Exiling / discarding: dump "move" and cheap defaults first; keeping: keep expensive.
                    bool keeping = request.Title.StartsWith("Keep");
                    var scored = new List<(int id, int score)>();
                    foreach (var option in request.Options)
                    {
                        var card = state.FindCard(option.CardInstanceId);
                        int cost = card?.Def.Cost ?? 0;
                        bool isDefaultJunk = card != null && (card.DefId == "move" || card.DefId == "fire_bolt");
                        int score = keeping ? cost : (isDefaultJunk ? 100 : 10 - cost);
                        scored.Add((option.Id, score));
                    }
                    scored.Sort((a, b) => b.score.CompareTo(a.score));
                    int take = keeping ? request.Max : (request.Min > 0 ? request.Min : System.Math.Min(request.Max, CountJunk(scored)));
                    for (int i = 0; i < take && i < scored.Count; i++)
                        answer.ChosenOptionIds.Add(scored[i].id);
                    if (answer.ChosenOptionIds.Count < request.Min)
                        for (int i = 0; answer.ChosenOptionIds.Count < request.Min && i < scored.Count; i++)
                            if (!answer.ChosenOptionIds.Contains(scored[i].id))
                                answer.ChosenOptionIds.Add(scored[i].id);
                    return answer;
                }

                case DecisionKind.YesNo:
                    answer.ChosenOptionIds.Add(0); // free value: always yes
                    return answer;

                default:
                {
                    // OrderCards / ChooseMode: defaults are fine.
                    int count = request.Min;
                    foreach (var option in request.Options)
                    {
                        if (answer.ChosenOptionIds.Count >= count) break;
                        answer.ChosenOptionIds.Add(option.Id);
                    }
                    return answer;
                }
            }
        }

        private static int CountJunk(List<(int id, int score)> scored)
        {
            int n = 0;
            foreach (var (_, score) in scored)
                if (score >= 100)
                    n++;
            return n;
        }
    }
}
