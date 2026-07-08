using Pascension.Engine.Events;

namespace Pascension.Engine.Core
{
    /// <summary>
    /// Checks run whenever the game would grant priority or continue processing:
    /// monster deaths (reward → exile → refill), boss death (win), level-ups.
    /// Loops until nothing changes.
    /// </summary>
    public static class StateBasedActions
    {
        /// <summary>Returns true if anything happened.</summary>
        public static bool Run(GameApi api)
        {
            var state = api.State;
            bool any = false;
            bool changed = true;
            while (changed && !state.GameOver)
            {
                changed = false;

                // Monster deaths.
                foreach (var (tier, slot, monster) in state.Market.Monsters())
                {
                    if (monster.MarkedDamage >= api.EffectiveMonsterHp(monster))
                    {
                        int killer = monster.LastDamagedBy >= 0 ? monster.LastDamagedBy : state.TurnPlayerIndex;
                        api.Emit(new MonsterDiedEvent
                        {
                            InstanceId = monster.InstanceId,
                            DefId = monster.DefId,
                            Tier = tier,
                            SlotIndex = slot,
                            KillerIndex = killer
                        });
                        var reward = monster.Def.MonsterReward;
                        api.ExileFromMarket(monster);
                        if (reward != null)
                            api.QueueInternal(reward, killer, null, $"{monster.Def.Name} reward");
                        api.RefillSlot(tier, slot);
                        changed = true;
                        any = true;
                        break; // re-enumerate; the row changed
                    }
                }
                if (changed) continue;

                // Boss death → victory.
                var boss = state.Boss;
                if (boss != null && boss.MarkedDamage >= state.Rules.BossHp)
                {
                    int winner = boss.LastDamagedBy >= 0 ? boss.LastDamagedBy : state.TurnPlayerIndex;
                    state.GameOver = true;
                    state.WinnerIndex = winner;
                    api.Emit(new GameEndedEvent { WinnerIndex = winner, Reason = "Boss defeated" });
                    return true;
                }

                // Level-ups.
                foreach (var p in state.Players)
                {
                    while (p.Level < state.Rules.MaxLevel && p.Xp >= state.Rules.XpFromLevel(p.Level))
                    {
                        p.Xp -= state.Rules.XpFromLevel(p.Level);
                        p.Level++;
                        api.Emit(new LeveledUpEvent { PlayerIndex = p.Index, NewLevel = p.Level });
                        changed = true;
                        any = true;
                    }
                }
            }
            return any;
        }
    }
}
