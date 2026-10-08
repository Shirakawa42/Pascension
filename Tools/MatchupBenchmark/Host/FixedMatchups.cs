using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class FixedMatchups
    {
        internal const ulong SeedBase = 12682136550676000000UL;
        private static readonly string[] Heroes = { "decima", "tetra", "volos", "kosynwu", "rez" };
        internal static (string Learner, string Opponent) Pair(ulong seed)
        {
            if (seed < SeedBase || seed >= SeedBase + 2400) throw new ArgumentException("Seed outside this fixed benchmark");
            int index = (int)((seed - SeedBase) % 16), learner = index / 4, opponent = index % 4;
            if (opponent >= learner) opponent++;
            return (Heroes[learner], Heroes[opponent]);
        }

        internal static void Apply(Adapter game, ulong seed, int learnerSeat)
        {
            if (learnerSeat < 0 || learnerSeat > 1 || game.Decision?.Context != "soi.herodraft")
                throw new InvalidOperationException("Fixed matchup requires untouched two-player draft");
            var pair = Pair(seed); int count = 0;
            while (game.Decision?.Context == "soi.herodraft")
            {
                if (++count > 2) throw new InvalidOperationException("Unexpected draft length");
                var request = game.Decision;
                string hero = request.PlayerIndex == learnerSeat ? pair.Learner : pair.Opponent;
                var option = request.Options.Find(o => o.DefId == hero && !o.Disabled);
                if (option == null) throw new InvalidOperationException("Requested hero is not legal");
                game.ApplyExternal(new SubmitDecisionAction { PlayerIndex = request.PlayerIndex,
                    Answer = new DecisionAnswer { DecisionId = request.Id, ChosenOptionIds = new List<int> { option.Id } } });
            }
            if (count != 2 || game.Engine.State.Players[learnerSeat].CharacterId != pair.Learner
                || game.Engine.State.Players[1-learnerSeat].CharacterId != pair.Opponent)
                throw new InvalidOperationException("Actual heroes differ from declared matchup");
        }

        internal static object SelfTest()
        {
            var counts = new Dictionary<string, int>(); int drafts = 0;
            for (int index = 0; index < 2400; index++)
            {
                var pair = Pair(SeedBase + (ulong)index);
                if (pair.Learner == "rez" || pair.Learner == pair.Opponent) throw new Exception("Forbidden learner matchup");
                string key = pair.Learner + "/" + pair.Opponent;
                counts[key] = counts.TryGetValue(key, out int n) ? n + 2 : 2;
                if (index >= 16) continue;
                for (int seat = 0; seat < 2; seat++)
                {
                    var game = new Adapter(new ShardsEngine(Program.Config(SeedBase + (ulong)index)), automaticSingletons:false);
                    Apply(game, SeedBase + (ulong)index, seat); drafts++;
                }
            }
            if (counts.Count != 16) throw new Exception("Missing matchup");
            foreach (int count in counts.Values) if (count != 300) throw new Exception("Unbalanced benchmark");
            return new { passed=true, actualDrafts=drafts, matchups=counts, games=4800, gamesPerSeatPerMatchup=150 };
        }
    }
}
