using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;

namespace Pascension.Bots
{
    /// <summary>
    /// A synchronous decision-maker for one player seat. Used directly by headless
    /// simulations/tests; the host wraps these for real-time play (with think-delays)
    /// and the Ollama bot implements the same surface asynchronously via AgentRunner.
    /// </summary>
    public interface ISyncAgent
    {
        /// <summary>Pick one of the legal actions while holding priority.</summary>
        PlayerAction ChooseAction(GameEngine engine, PendingInput input);

        /// <summary>Answer a pending decision (targets, modes, inn choices, orderings…).</summary>
        DecisionAnswer ChooseDecision(GameEngine engine, DecisionRequest request);
    }

    /// <summary>Drives a GameEngine with a set of agents until the game ends (or a safety cap).</summary>
    public static class GameDriver
    {
        public static void Run(GameEngine engine, ISyncAgent[] agents, int maxRounds = 200)
        {
            int guard = 0;
            while (!engine.State.GameOver && engine.State.Round <= maxRounds)
            {
                if (++guard > 500000)
                    throw new System.InvalidOperationException("Game driver did not terminate");

                var pending = engine.PendingInput;
                if (pending == null)
                    break;

                var agent = agents[pending.PlayerIndex];
                PlayerAction action;
                if (pending.Kind == PendingInputKind.Decision)
                {
                    action = new SubmitDecisionAction
                    {
                        PlayerIndex = pending.PlayerIndex,
                        Answer = agent.ChooseDecision(engine, pending.Decision)
                    };
                }
                else
                {
                    action = agent.ChooseAction(engine, pending);
                }

                var result = engine.Submit(action);
                if (!result.Accepted)
                    throw new System.InvalidOperationException(
                        $"Agent submitted illegal action '{action.Describe()}': {result.Error}");
            }
        }
    }
}
