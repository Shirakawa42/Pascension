using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Pins the neural feature schema. Any layout change must bump
    /// ShardsStateEncoder.SchemaVersion — selfplay data and weight blobs carry it.</summary>
    public sealed class ShardsEncoderTests
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static ShardsEngineAdapter NewGame(ulong seed) => new(
            ShardsContentRegistry.StandardConfig(seed, new List<PlayerSpec>
            {
                new() { Name = "P0", CharacterId = "decima" },
                new() { Name = "P1", CharacterId = "volos" }
            }, AllDlc));

        [Test]
        public void Schema_IsPinned()
        {
            Assert.AreEqual(1, ShardsStateEncoder.SchemaVersion);
            Assert.AreEqual(768, ShardsStateEncoder.FeatureCount);
            Assert.AreEqual(52, ShardsStateEncoder.CardVecSize);
        }

        [Test]
        public void Encode_KnownIndicesForAKnownState()
        {
            var adapter = NewGame(101);
            var state = adapter.Inner.State;
            var dst = new float[ShardsStateEncoder.FeatureCount];
            ShardsStateEncoder.Encode(state, 0, dst);

            // Own hand pool (offset 0): 5 starters — crystals contribute 1 gem each in
            // the unconditional-gems slot (index 0). P0's opening hand always holds
            // crystals (7 of 10 starters are crystals).
            Assert.Greater(dst[0], 0, "own-hand unconditional gems must reflect crystals");
            // Health scalar: full 50 → 1.0 for both players.
            int ps = 14 * 52 + 7;
            Assert.AreEqual(1f, dst[ps + 0], 1e-6, "own health scalar");
            Assert.AreEqual(1f, dst[ps + 12], 1e-6, "opp health scalar");
            // P1 (volos seat) starts at mastery 1 → opp mastery scalar = 1/30.
            Assert.AreEqual(1f / 30f, dst[ps + 12 + 1], 1e-6);
            // Global: viewer 0 is the turn player and seat 0; all three DLC bits set.
            int g = ps + 24;
            Assert.AreEqual(1f, dst[g + 1], 1e-6);
            Assert.AreEqual(1f, dst[g + 2], 1e-6);
            Assert.AreEqual(1f, dst[g + 3], 1e-6);
            Assert.AreEqual(1f, dst[g + 4], 1e-6);
            Assert.AreEqual(1f, dst[g + 5], 1e-6);
        }

        [Test]
        public void Encode_IsInvariantUnderDeterminization()
        {
            // THE drift-killer property: the encoder sees only the information set,
            // so resampling hidden zones (own deck order, opponent hand/deck split)
            // must not change a single feature.
            var adapter = NewGame(103);
            var bot = new ShardsHeuristicBot(10300, adapter.Inner);
            for (int i = 0; i < 50 && !adapter.GameOver; i++)
            {
                var pending = adapter.PendingInput;
                var action = bot.Choose(pending, null) ?? adapter.DefaultActionFor(pending);
                if (!adapter.Submit(action).Accepted)
                    adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
            }
            if (adapter.GameOver || adapter.PendingInput?.Kind != Pascension.Engine.Core.PendingInputKind.Priority)
                Assert.Inconclusive("seed 103 did not stop on a live priority point");

            var before = new float[ShardsStateEncoder.FeatureCount];
            ShardsStateEncoder.Encode(adapter.Inner.State, 0, before);

            var fork = adapter.Inner.Fork(rngReseed: 777);
            ShardsDeterminizer.Sample(fork.State, 0, fork.State.Rng);
            var after = new float[ShardsStateEncoder.FeatureCount];
            ShardsStateEncoder.Encode(fork.State, 0, after);

            for (int i = 0; i < before.Length; i++)
                Assert.AreEqual(before[i], after[i], 1e-6f,
                    $"feature {i} changed under determinization — information leak in the encoder");
        }
    }
}
