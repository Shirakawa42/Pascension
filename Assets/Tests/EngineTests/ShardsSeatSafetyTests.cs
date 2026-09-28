using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Snapshot privacy and safe concurrent card lookup.</summary>
    public sealed class ShardsSeatSafetyTests
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
        public void FindCard_SurvivesConcurrentLookupsAndInvalidation()
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "A", CharacterId = "decima" },
                new() { Name = "B", CharacterId = "volos" }
            };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(92, specs, AllDlc));
            var state = adapter.Inner.State;
            int probeId = state.Players[0].Hand[0].InstanceId;

            Exception failure = null;
            var threads = new List<Thread>();
            for (int t = 0; t < 8; t++)
            {
                bool invalidator = t == 0;
                threads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 20000; i++)
                        {
                            if (invalidator && i % 50 == 0)
                                state.InvalidateCardIndex();
                            var card = state.FindCard(probeId);
                            if (card == null || card.InstanceId != probeId)
                                throw new InvalidOperationException("index returned a wrong card");
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }));
            }
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads)
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)),
                    "a thread hung — the card index corrupted under concurrency");
            Assert.IsNull(failure, failure?.ToString());
        }

        [TestCase(0, false)]
        [TestCase(1, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        public void Snapshot_ConditionGlowsDoNotRevealOpponentHiddenZones(int viewerIndex, bool destiny)
        {
            var engine = SnapshotEngine();
            string defId = "aegis_archivist";
            if (destiny)
            {
                // The same private condition must be safe on either public zone,
                // including future destinies that inspect their owner's hand.
                defId = "test_private_condition_destiny";
                ShardsCardDatabase.Register(new ShardsCardDef
                {
                    Id = defId,
                    Type = ShardsCardType.Destiny,
                    ExhaustEffect = new Dominion(E.Gems(1))
                });
            }
            var opponent = engine.State.Players[1 - viewerIndex];
            var permanent = AddCard(engine, opponent, defId,
                destiny ? ShardsZone.DestinyRow : ShardsZone.Champions);
            for (int i = 0; i < 3; i++) AddCard(engine, opponent, "crystal", ShardsZone.Hand);
            foreach (string id in new[] { "reactor_drone", "spore_cleric", "nil_assassin" })
                AddCard(engine, opponent, id, ShardsZone.Deck);

            var before = ShardsSnapshotBuilder.Build(engine, viewerIndex);
            Assert.IsFalse(ShardsSnapshotBuilder.Build(engine, opponent.Index)
                .ConditionGlowIds.Contains(permanent.InstanceId), "owner's condition starts unmet");

            (opponent.Hand, opponent.Deck) = (opponent.Deck, opponent.Hand);
            foreach (var card in opponent.Hand) card.Zone = ShardsZone.Hand;
            foreach (var card in opponent.Deck) card.Zone = ShardsZone.Deck;

            Assert.Contains(permanent.InstanceId,
                ShardsSnapshotBuilder.Build(engine, opponent.Index).ConditionGlowIds,
                "the hidden swap must change the owner's condition to expose the original leak");
            var after = ShardsSnapshotBuilder.Build(engine, viewerIndex);
            Assert.IsNull(after.Players[opponent.Index].Hand);
            Assert.IsNull(after.Players[opponent.Index].FullDeck);
            Assert.AreEqual(ShardsJson.Wire.Serialize(before), ShardsJson.Wire.Serialize(after),
                "changing only an opponent's private hand/deck allocation must not change the viewer's snapshot");
        }

        [TestCase(0)]
        [TestCase(1)]
        public void Snapshot_ConditionGlowsPreserveViewerHintsOnly(int viewerIndex)
        {
            var engine = SnapshotEngine();
            var viewer = engine.State.Players[viewerIndex];
            var opponent = engine.State.Players[1 - viewerIndex];
            foreach (string id in new[] { "reactor_drone", "spore_cleric", "nil_assassin" })
            {
                AddCard(engine, viewer, id, ShardsZone.Hand);
                AddCard(engine, opponent, id, ShardsZone.Hand);
            }
            var ownChampion = AddCard(engine, viewer, "aegis_archivist", ShardsZone.Champions);
            var ownDestiny = AddCard(engine, viewer, "power_struggle", ShardsZone.DestinyRow);
            var ownHand = AddCard(engine, viewer, "bulwark_chanter", ShardsZone.Hand);
            AddCard(engine, opponent, "aegis_archivist", ShardsZone.Champions);
            AddCard(engine, opponent, "power_struggle", ShardsZone.DestinyRow);
            var row = new ShardsCard
            {
                InstanceId = engine.State.NextInstanceId++, DefId = "bulwark_chanter",
                Owner = -1, Zone = ShardsZone.CenterRow
            };
            engine.State.CenterRow[0] = row;

            CollectionAssert.AreEquivalent(new[]
                { ownChampion.InstanceId, ownDestiny.InstanceId, ownHand.InstanceId, row.InstanceId },
                ShardsSnapshotBuilder.Build(engine, viewerIndex).ConditionGlowIds,
                "own hand, market, champion and destiny hints remain available; opponent hints stay private");

            ownChampion.Exhausted = true;
            ownDestiny.Exhausted = true;
            CollectionAssert.AreEquivalent(new[] { ownHand.InstanceId, row.InstanceId },
                ShardsSnapshotBuilder.Build(engine, viewerIndex).ConditionGlowIds,
                "exhausted permanents must not show ready-condition hints");
        }

        private static ShardsEngine SnapshotEngine()
        {
            var engine = new ShardsEngine(ShardsContentRegistry.StandardConfig(42,
                new List<PlayerSpec>
                {
                    new() { Name = "A", CharacterId = "decima" },
                    new() { Name = "B", CharacterId = "tetra" }
                }, AllDlc | ShardsDlc.Duel));
            while (engine.PendingInput.Kind == PendingInputKind.Decision)
            {
                var pending = engine.PendingInput;
                var action = DefaultActions.For(new PendingSnap
                    { Kind = pending.Kind, PlayerIndex = pending.PlayerIndex, Decision = pending.Decision });
                Assert.IsTrue(engine.Submit(action).Accepted);
            }
            Array.Clear(engine.State.CenterRow, 0, engine.State.CenterRow.Length);
            foreach (var player in engine.State.Players)
            {
                player.Hand.Clear();
                player.Deck.Clear();
                player.Discard.Clear();
                player.PlayZone.Clear();
                player.Champions.Clear();
                player.Destinies.Clear();
                player.PlayedThisTurn.Clear();
            }
            return engine;
        }

        private static ShardsCard AddCard(ShardsEngine engine, ShardsPlayer player, string id, ShardsZone zone)
        {
            var card = new ShardsCard
                { InstanceId = engine.State.NextInstanceId++, DefId = id, Owner = player.Index, Zone = zone };
            if (zone == ShardsZone.Hand) player.Hand.Add(card);
            else if (zone == ShardsZone.Deck) player.Deck.Add(card);
            else if (zone == ShardsZone.Champions) player.Champions.Add(card);
            else player.Destinies.Add(card);
            return card;
        }

    }
}
