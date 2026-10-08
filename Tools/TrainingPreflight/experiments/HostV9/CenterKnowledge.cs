using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    // Only authorized decision options and public events enter this ledger.
    // Never read CenterDeck entries, opponent hand/deck, or private draw events.
    internal sealed class CenterKnowledge
    {
        internal const int Capacity = 4;
        private readonly List<string>[] _top = { new(), new() };
        internal IReadOnlyList<string> For(int seat) => _top[seat];
        internal void Clear() { _top[0].Clear(); _top[1].Clear(); }
        internal static int FloodMask(ShardsEngine engine) =>
            (engine.State.Players[0].DoomGateFloodUsed ? 1 : 0) |
            (engine.State.Players[1].DoomGateFloodUsed ? 2 : 0);

        internal void ObserveDecision(int actor, DecisionRequest request)
        {
            if (request.Context != "soi.scry" && request.Context != "soi.reorder") return;
            // Scry/reorder are private information, scoped to the deciding seat.
            var known = _top[actor];
            int n = request.Options.Count;
            var tail = known.Skip(n).ToArray();
            known.Clear();
            foreach (var option in request.Options.Take(Capacity)) known.Add(option.DefId);
            foreach (var id in tail) if (known.Count < Capacity) known.Add(id);
        }

        internal void BeforeSubmit(int actor, DecisionRequest request, PlayerAction action)
        {
            if (action is not SubmitDecisionAction answer || request == null ||
                (request.Context != "soi.scry" && request.Context != "soi.reorder")) return;
            var known = _top[actor];
            var tail = known.Skip(request.Options.Count).ToArray();
            known.Clear();
            var ids = answer.Answer.ChosenOptionIds;
            if (request.Context == "soi.scry")
            {
                foreach (var option in request.Options)
                    if (!ids.Contains(option.Id) && known.Count < Capacity) known.Add(option.DefId);
            }
            else
                foreach (int id in ids)
                {
                    var option = request.Options.Find(o => o.Id == id);
                    if (option == null) throw new InvalidOperationException("Unknown revealed reorder option");
                    if (known.Count < Capacity) known.Add(option.DefId);
                }
            foreach (var id in tail) if (known.Count < Capacity) known.Add(id);
            // Do not infer an opponent's private selection/order from engine state.
            _top[1 - actor].Clear();
        }

        internal void AfterSubmit(ShardsEngine engine, int logStart, int beforeCount, int beforeFlood)
        {
            if (FloodMask(engine) != beforeFlood) { Clear(); return; }
            int publicPops = 0, publicBottomReturns = 0;
            for (int i = logStart; i < engine.Log.Count; i++)
            {
                string popped = engine.Log[i] switch
                {
                    ShardsRowRefilledEvent row => row.DefId,
                    ShardsMonsterRevealedEvent monster => monster.DefId,
                    _ => null
                };
                if (popped != null)
                {
                    publicPops++;
                    foreach (var known in _top)
                    {
                        if (known.Count == 0) continue;
                        if (known[0] != popped) known.Clear();
                        else known.RemoveAt(0);
                    }
                }
                // Includes Longshot's pop-and-return sequence, whose net deck
                // count can be unchanged. Other reveals conservatively forget.
                if (engine.Log[i] is ShardsCardsRevealedEvent) Clear();
                if (engine.Log[i] is ShardsRowRerolledEvent || engine.Log[i] is ShardsMercenaryReturnedEvent ||
                    engine.Log[i] is ShardsMonsterDefeatedEvent) publicBottomReturns++;
            }
            // Unmodeled takes, bottom returns, etc. may invalidate knowledge;
            // forget rather than inspect hidden deck order to repair it.
            if (engine.State.CenterDeck.Count != beforeCount - publicPops + publicBottomReturns) Clear();
        }
    }
}
