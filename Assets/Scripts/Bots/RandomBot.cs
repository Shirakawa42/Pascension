using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;

namespace Pascension.Bots
{
    /// <summary>
    /// Chooses uniformly among legal actions. Exists to fuzz the engine in soak tests —
    /// it plays terribly but must never be able to break the rules.
    /// </summary>
    public sealed class RandomBot : ISyncAgent
    {
        private readonly DeterministicRng _rng;

        public RandomBot(ulong seed) => _rng = new DeterministicRng(seed, 99);

        public PlayerAction ChooseAction(GameEngine engine, PendingInput input)
        {
            var legal = input.LegalActions;
            // Bias toward passing a little so turns don't sprawl.
            if (_rng.Next(4) == 0)
                return legal.Find(a => a is PassPriorityAction);
            return legal[_rng.Next(legal.Count)];
        }

        public DecisionAnswer ChooseDecision(GameEngine engine, DecisionRequest request)
        {
            var answer = new DecisionAnswer { DecisionId = request.Id };
            int count = request.Min + (request.Max > request.Min ? _rng.Next(request.Max - request.Min + 1) : 0);
            var pool = new List<int>();
            foreach (var option in request.Options)
                pool.Add(option.Id);
            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                int pick = _rng.Next(pool.Count);
                answer.ChosenOptionIds.Add(pool[pick]);
                pool.RemoveAt(pick);
            }
            return answer;
        }
    }
}
