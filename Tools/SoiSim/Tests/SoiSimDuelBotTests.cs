using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Duel of Doom adds exactly two new priority actions — the hero ability and
    /// the row reroll — and until 2026-07-25 the bots could not meaningfully take either:
    /// ShardsHeuristicBot's ladder had no case for them at all, and ShardsValueModel scored
    /// them with constants that made the hero ability fire unconditionally and the reroll
    /// strictly worse than passing, so an argmax policy could never pick it. Every net and
    /// every tuned weight vector was then fit to games where rerolling did not exist.
    ///
    /// These tests assert the actions are genuinely part of both policies. They are
    /// deliberately behavioural rather than structural: a `case` in a switch proves
    /// nothing if the score keeps it below END TURN forever.</summary>
    [TestFixture]
    public sealed class SoiSimDuelBotTests
    {
        // 60 → 400 (2026-08-02): the whisper_extractor removal perturbed the center-deck
        // pool enough that greedy's rerolls (already rare, ~0.44/game for V5) landed on
        // exactly 0 across the old 60-game sample — a detection-floor miss, not a lost
        // capability (at 400 games both actions show up again). The assertions are
        // unchanged; only the sample grew. Still ~1s.
        private const int Games = 400;

        private sealed class Usage
        {
            public int HeroAbility, Reroll, EndTurn, Games, Finished;
            public override string ToString() =>
                $"{Finished}/{Games} finished · hero {HeroAbility} · reroll {Reroll} · endturn {EndTurn}";
        }

        private static Usage Play(string kind, ShardsDlc dlc)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(dlc);
            var factory = new BotFactory(kind, 0);
            var rng = new DeterministicRng(31337, 55);
            var usage = new Usage { Games = Games };

            for (int g = 0; g < Games; g++)
            {
                ulong seed = 31337 + (ulong)g;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "S0", CharacterId = chars[a] },
                    new() { Name = "S1", CharacterId = chars[b] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, dlc));
                var seats = new IBotAgent[2];
                for (int i = 0; i < 2; i++) seats[i] = factory.Create(seed, i, adapter.Inner);

                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    switch (action)
                    {
                        case ShardsHeroAbilityAction: usage.HeroAbility++; break;
                        case ShardsRerollRowAction: usage.Reroll++; break;
                        case ShardsEndTurnAction: usage.EndTurn++; break;
                    }
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
                if (adapter.GameOver) usage.Finished++;
            }
            return usage;
        }

        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        [TestCase("heuristic")]
        [TestCase("greedy")]
        public void BothPolicies_UseTheHeroAbility_AndTheRowReroll(string kind)
        {
            var usage = Play(kind, AllWithDuel);
            Assert.GreaterOrEqual(usage.Finished, Games - 2, $"games stalled: {usage}");
            Assert.Greater(usage.HeroAbility, 0,
                $"{kind} never activated a hero ability across {Games} Duel games ({usage})");
            Assert.Greater(usage.Reroll, 0,
                $"{kind} never rerolled a row slot across {Games} Duel games ({usage}) — " +
                "this is how the reroll stayed out of every training position before 2026-07-25");
        }

        [TestCase("heuristic")]
        [TestCase("greedy")]
        public void RerollIsSelective_NotSpammed(string kind)
        {
            // The opposite failure: a reroll that always scores above END TURN burns the
            // whole gem pool digging every turn. Rerolls should be far rarer than turns.
            var usage = Play(kind, AllWithDuel);
            Assert.Less(usage.Reroll, usage.EndTurn,
                $"{kind} rerolled more often than it ended turns ({usage}) — the price " +
                "ladder or the deadness term is not being respected");
        }

        [Test]
        public void NoMintedRankCheats()
        {
            // ShardsSearchConfig.PerfectInformation skips determinization, so the search
            // plans against the opponent's real hand. It exists only to bound what hidden
            // information costs. A rank shipping with it set would be undetectable in
            // normal play and would silently break the fairness guarantee every rank
            // advertises — so assert the default is off and that nothing turns it on.
            Assert.IsFalse(new ShardsSearchConfig().PerfectInformation,
                "PerfectInformation must default to OFF");
            Assert.IsFalse(ShardsSearchConfig.ForSims(200).PerfectInformation);
            Assert.IsFalse(ShardsSearchConfig.ForRealGames(1.0).PerfectInformation);

            foreach (var rank in ShardsBotRanks.Minted)
            {
                var bot = ShardsBotRanks.Create(rank.KindString, 1, null);
                Assert.IsNotNull(bot, rank.Id);
                if (bot is not ShardsSearchBot search) continue;
                var field = typeof(ShardsSearchBot).GetField("_config",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.IsNotNull(field, "ShardsSearchBot._config was renamed — update this guard");
                var config = (ShardsSearchConfig)field.GetValue(search);
                Assert.IsFalse(config.PerfectInformation, $"rank {rank.Id} CHEATS");
            }
        }

        [Test]
        public void WeightLayout_DefaultsCoverEveryIndex()
        {
            // W.Defaults is indexed by weight id in two places (W.Pad and every model
            // built from a short vector). A length drift silently mis-prices everything
            // after the gap, or throws deep inside a rollout.
            Assert.AreEqual(W.Count, W.Defaults.Length,
                "W.Defaults must have exactly one entry per weight — append the default " +
                "in the same change as the index");
            var padded = W.Pad(new[] { 1.0, 2.0 });
            Assert.AreEqual(W.Count, padded.Length);
            Assert.AreEqual(1.0, padded[0], 1e-12, "Pad must preserve the supplied prefix");
            Assert.AreEqual(W.Defaults[W.Count - 1], padded[W.Count - 1], 1e-12);
            var already = new double[W.Count];
            Assert.AreSame(already, W.Pad(already), "Pad must not copy an already-current vector");
        }

        [Test]
        public void NoDuelActions_LeakIntoNonDuelGames()
        {
            // Both actions are gated on the Duel flag in LegalActions; if a bot ever
            // fabricates one, the engine rejects it and the seat silently falls back to a
            // default action — a strength bug that no other test would surface.
            var usage = Play("greedy",
                ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon);
            Assert.AreEqual(0, usage.HeroAbility, $"hero ability chosen without Duel ({usage})");
            Assert.AreEqual(0, usage.Reroll, $"reroll chosen without Duel ({usage})");
        }
    }
}
