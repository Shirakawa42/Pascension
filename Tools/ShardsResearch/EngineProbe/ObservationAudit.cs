using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Shards.Content;
using Shards.Engine;

namespace Shards.Research
{
    internal static class ObservationAudit
    {
        public static void Run()
        {
            ShardsContentRegistry.EnsureRegistered();
            var e = new ShardsEngine(ShardsContentRegistry.StandardConfig(42,
                new List<PlayerSpec> { new() { Name = "P0", CharacterId = "decima" },
                    new() { Name = "P1", CharacterId = "tetra" } }, ShardsDlc.Duel));
            while (e.PendingInput.Kind == PendingInputKind.Decision)
            {
                var p = e.PendingInput;
                var action = DefaultActions.For(new PendingSnap { Kind = p.Kind,
                    PlayerIndex = p.PlayerIndex, Decision = p.Decision });
                if (!e.Submit(action).Accepted) throw new InvalidOperationException("Draft failed");
            }
            var opponent = e.State.Players[1];
            opponent.Hand.Clear();
            opponent.Deck.Clear();
            opponent.Discard.Clear();
            opponent.PlayZone.Clear();
            opponent.Champions.Clear();
            opponent.PlayedThisTurn.Clear();
            var archivist = Add(e, opponent, "aegis_archivist", ShardsZone.Champions);
            for (int i = 0; i < 3; i++) Add(e, opponent, "crystal", ShardsZone.Hand);
            foreach (var faction in new[] { ShardsFaction.Homodeus, ShardsFaction.Order, ShardsFaction.Undergrowth })
            {
                var def = ShardsCardDatabase.All.First(d => d.Faction == faction && d.Type == ShardsCardType.Ally);
                Add(e, opponent, def.Id, ShardsZone.Deck);
            }
            var before = ShardsSnapshotBuilder.Build(e, 0);
            bool glowBefore = before.ConditionGlowIds.Contains(archivist.InstanceId);
            var temp = opponent.Hand;
            opponent.Hand = opponent.Deck;
            opponent.Deck = temp;
            foreach (var card in opponent.Hand) card.Zone = ShardsZone.Hand;
            foreach (var card in opponent.Deck) card.Zone = ShardsZone.Deck;
            var after = ShardsSnapshotBuilder.Build(e, 0);
            bool glowAfter = after.ConditionGlowIds.Contains(archivist.InstanceId);
            // All fields, including hints, must be invariant under the hidden swap.
            var json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            bool equalPublicSnapshots = JsonSerializer.Serialize(before, json) == JsonSerializer.Serialize(after, json);
            bool ownerSeesGlow = ShardsSnapshotBuilder.Build(e, 1).ConditionGlowIds.Contains(archivist.InstanceId);

            var own = e.State.Players[0];
            own.Deck.Clear(); own.Hand.Clear(); own.Discard.Clear(); own.PlayZone.Clear(); own.Champions.Clear();
            var fast = Add(e, own, "nil_assassin", ShardsZone.PlayZone);
            fast.FastPlayed = true;
            int allegianceCount = AllegianceEffect.OwnedCount(own, ShardsFaction.Wraethe);
            bool fullDeckIncludesLoan = ShardsSnapshotBuilder.Build(e, 0).Players[0].FullDeck.Any(c => c.InstanceId == fast.InstanceId);
            if (glowBefore || glowAfter || !equalPublicSnapshots || !ownerSeesGlow || allegianceCount != 1 || fullDeckIncludesLoan)
                throw new InvalidOperationException("Audit assumptions changed; inspect current engine");
            own.Gems = 2;
            var defiant = new ShardsCard { InstanceId = e.State.NextInstanceId++, DefId = "shard_defiant",
                Owner = 0, Zone = ShardsZone.DestinyRow };
            own.Destinies.Add(defiant);
            e.State.CenterDeck.Clear();
            e.State.CenterDeck.Add(new ShardsCard { InstanceId = e.State.NextInstanceId++, DefId = "comet",
                Owner = -1, Zone = ShardsZone.CenterDeck });
            if (!e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = defiant.InstanceId }).Accepted)
                throw new InvalidOperationException("Defiant activation failed");
            if (e.PendingInput.Decision.Context != "soi.defiant") throw new InvalidOperationException("Unexpected decision");
            var request = e.PendingInput.Decision;
            bool keepDisabled = request.Options.Find(o => o.Id == 1).Disabled;
            bool keepRejected = !e.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = new DecisionAnswer
                { DecisionId = request.Id, ChosenOptionIds = new List<int> { 1 } } }).Accepted;
            if (!keepDisabled || !keepRejected) throw new InvalidOperationException("Comet could be freely recruited");
            var fallback = (SubmitDecisionAction)DefaultActions.For(new PendingSnap { Kind = e.PendingInput.Kind,
                PlayerIndex = 0, Decision = request });
            if (!fallback.Answer.ChosenOptionIds.SequenceEqual(new[] { 2 }) || !e.Submit(fallback).Accepted)
                throw new InvalidOperationException("Defiant timeout failed to banish Comet");
            bool cometRecruited = own.Discard.Any(c => c.DefId == "comet");
            bool cometBanished = e.State.Banished.Any(c => c.DefId == "comet");
            if (cometRecruited || !cometBanished || own.Gems != 0) throw new InvalidOperationException("Comet audit assumptions changed");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                fixture = "synthetic-observation-boundaries-v2-fixed",
                note = "Direct state fixtures for boundary isolation, not a natural-play frequency estimate",
                opponentHandMasked = after.Players[1].Hand == null,
                identicalPublicSnapshots = equalPublicSnapshots,
                ownerStillSeesConditionGlow = ownerSeesGlow,
                opponentChampionGlowWithCrystalHand = glowBefore,
                opponentChampionGlowWithThreeFactionHand = glowAfter,
                fastPlayedWraetheAllegianceCount = allegianceCount,
                fastPlayedLoanInOwnFullDeck = fullDeckIncludesLoan,
                cometKeepDisabled = keepDisabled,
                cometKeepRejected = keepRejected,
                cometRecruitedByShardDefiant = cometRecruited,
                cometBanishedByDefault = cometBanished,
                shardDefiantActivationGemsSpent = 2,
                cometPrintedCost = ShardsCardDatabase.Get("comet").Cost
            }, json));
        }

        private static ShardsCard Add(ShardsEngine e, ShardsPlayer player, string id, ShardsZone zone)
        {
            var card = new ShardsCard { InstanceId = e.State.NextInstanceId++, DefId = id,
                Owner = player.Index, Zone = zone };
            if (zone == ShardsZone.Hand) player.Hand.Add(card);
            else if (zone == ShardsZone.Deck) player.Deck.Add(card);
            else if (zone == ShardsZone.Champions) player.Champions.Add(card);
            else player.PlayZone.Add(card);
            return card;
        }
    }
}
