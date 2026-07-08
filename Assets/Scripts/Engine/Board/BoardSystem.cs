using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;
using Pascension.Engine.Turns;

namespace Pascension.Engine.Board
{
    /// <summary>Movement along the 50-step track, inn detection, and checkpoint clamping.</summary>
    public static class BoardSystem
    {
        /// <summary>Total steps granted for a paid move, after hero/static bonuses.</summary>
        public static int TotalSteps(GameApi api, PlayerState p, int paidSteps)
        {
            int bonus = 0;
            foreach (var s in api.ActiveStatics(p))
                if (s is IMoveBonusModifier m)
                    bonus += m.BonusSteps(api.State, p, paidSteps);
            return paidSteps + bonus;
        }

        /// <summary>Move forward (AP already paid by the caller). Queues inn rewards for newly reached inns.
        /// countsAsMove=false for free moves from abilities/relics (they don't consume Pathfinder's "first move").</summary>
        public static void MoveForward(GameApi api, PlayerState p, int totalSteps, bool countsAsMove = true)
        {
            int from = p.Position;
            int to = from + totalSteps;
            if (to > api.State.Rules.BoardSteps) to = api.State.Rules.BoardSteps;
            if (to == from) return;

            p.Position = to;
            if (countsAsMove) p.MovesThisTurn++;
            api.Emit(new PlayerMovedEvent { PlayerIndex = p.Index, FromStep = from, ToStep = to });

            foreach (int inn in api.State.Rules.InnSteps)
            {
                if (inn > from && inn <= to && !p.ClaimedInns.Contains(inn))
                {
                    p.ClaimedInns.Add(inn);
                    p.ClaimedInns.Sort();
                    if (inn > p.LastInnCheckpoint) p.LastInnCheckpoint = inn;
                    api.Emit(new InnReachedEvent { PlayerIndex = p.Index, InnStep = inn });
                    api.QueueInternal(new InnRewardEffect(), p.Index, null, $"Inn at step {inn}");
                }
            }
        }

        /// <summary>Move a player backwards (from future PvP effects). Clamped at their last inn checkpoint.</summary>
        public static void MoveBack(GameApi api, PlayerState p, int steps)
        {
            int from = p.Position;
            int to = from - steps;
            if (to < p.LastInnCheckpoint) to = p.LastInnCheckpoint;
            if (to == from) return;
            p.Position = to;
            api.Emit(new PlayerMovedEvent { PlayerIndex = p.Index, FromStep = from, ToStep = to });
        }
    }
}
