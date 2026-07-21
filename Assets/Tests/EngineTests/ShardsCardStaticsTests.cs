using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Guards the tuned value model's card-statics layer: every Custom/Do in
    /// the content must carry an annotation (ShardsCustomAnnotations) — this is what a
    /// balance patch or new DLC trips over when it adds bespoke logic.</summary>
    public sealed class ShardsCardStaticsTests
    {
        private const ShardsDlc AllDlc =
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;

        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        [Test]
        public void EveryCustomOrDoEffect_HasAnAnnotation()
        {
            var offenders = new List<string>();
            foreach (var def in ShardsCardDatabase.All)
            {
                var statics = ShardsCardStatics.Get(def);
                for (int b = 0; b < CardStatics.Buckets; b++)
                {
                    if (statics.Play[b].Opaque) offenders.Add(def.Id + ":play@" + b * 5);
                    if (statics.Exhaust[b].Opaque) offenders.Add(def.Id + ":exhaust@" + b * 5);
                    if (statics.Reward[b].Opaque) offenders.Add(def.Id + ":reward@" + b * 5);
                }
            }
            Assert.IsEmpty(offenders,
                "unannotated Custom/Do effects — add entries to ShardsCustomAnnotations:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void Statics_CaptureKnownCardShapes()
        {
            // Crystal: plain 1 gem.
            var crystal = ShardsCardStatics.Get(ShardsCardDatabase.Get("crystal"));
            Assert.AreEqual(1, crystal.Play[0].Gains[EffectAtoms.Unconditional, EffectAtoms.Gems], 1e-9);

            // Infinity Shard: power scales across mastery buckets (2 → 3 → 5 → lethal).
            var shard = ShardsCardStatics.Get(ShardsCardDatabase.Get("infinity_shard"));
            double p0 = shard.Play[0].Gains[EffectAtoms.Unconditional, EffectAtoms.Power];
            double p30 = shard.Play[6].Gains[EffectAtoms.Unconditional, EffectAtoms.Power];
            Assert.AreEqual(2, p0, 1e-9);
            Assert.Greater(p30, 100, "M30 Infinity Shard is the lethal line");

            // Kiln Drone: Inspire line lands in the If class.
            var kiln = ShardsCardStatics.Get(ShardsCardDatabase.Get("kiln_drone"));
            Assert.AreEqual(2, kiln.Play[0].Gains[EffectAtoms.Unconditional, EffectAtoms.Gems], 1e-9);
            Assert.AreEqual(2, kiln.Play[0].Gains[EffectAtoms.IfClass, EffectAtoms.Gems], 1e-9);
        }

        [Test]
        public void GreedyEvalBot_FullGames_TerminateWithAWinner()
        {
            for (ulong seed = 31; seed <= 33; seed++)
            {
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "G0", CharacterId = "decima" },
                    new() { Name = "G1", CharacterId = "kosynwu" }
                };
                var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, AllDlc));
                var model = new ShardsValueModel();
                var bots = new IBotAgent[]
                {
                    new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                    new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
                };
                int guard = 0;
                while (!adapter.GameOver && guard++ < 30000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending, $"seed {seed}: stall at {guard}");
                    var action = bots[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted)
                        Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted,
                            $"seed {seed}: greedy AND default rejected");
                }
                Assert.IsTrue(adapter.GameOver, $"seed {seed}: no result in 30000 submits");
            }
        }
    }
}
