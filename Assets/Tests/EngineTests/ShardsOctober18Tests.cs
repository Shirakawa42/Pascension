using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    public sealed class ShardsOctober18Tests
    {
        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static ShardsEngine Game(int players = 2, ShardsDlc dlc = ShardsDlc.Duel)
        {
            string[] heroes = { "tetra", "decima", "volos", "rez" };
            var specs = Enumerable.Range(0, players).Select(i => new PlayerSpec
                { Name = "P" + i, CharacterId = heroes[i] }).ToList();
            var game = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(181026, specs, dlc)).Inner;
            while (game.PendingInput?.Kind == PendingInputKind.Decision)
            {
                var request = game.PendingInput.Decision;
                var choices = request.DefaultOptionIds.ToList();
                foreach (var option in request.Options)
                    if (choices.Count < request.Min && !option.Disabled && !choices.Contains(option.Id)) choices.Add(option.Id);
                Answer(game, choices.Take(request.Max).ToArray());
            }
            foreach (var player in game.State.Players)
            {
                player.Deck.Clear(); player.Hand.Clear(); player.Discard.Clear();
                player.PlayZone.Clear(); player.Champions.Clear(); player.Destinies.Clear();
                player.Gems = 0; player.Power = 0;
            }
            game.State.ActiveMonsters.Clear();
            game.State.PendingMonsterAttacks.Clear();
            game.State.InvalidateCardIndex();
            return game;
        }

        private static void Submit(ShardsEngine game, PlayerAction action)
        {
            game.State.InvalidateCardIndex();
            var result = game.Submit(action);
            Assert.That(result.Accepted, Is.True, action.Describe() + ": " + result.Error);
        }

        private static void Answer(ShardsEngine game, params int[] choices)
        {
            var request = game.PendingInput.Decision;
            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(choices);
            Submit(game, new SubmitDecisionAction { PlayerIndex = request.PlayerIndex, Answer = answer });
        }

        private static ShardsCard Give(ShardsEngine game, int seat, string id, ShardsZone zone)
        {
            var card = new ShardsCard { InstanceId = game.State.NextInstanceId++, DefId = id, Owner = seat, Zone = zone };
            var player = game.State.Players[seat];
            switch (zone)
            {
                case ShardsZone.Deck: player.Deck.Add(card); break;
                case ShardsZone.Hand: player.Hand.Add(card); break;
                case ShardsZone.Discard: player.Discard.Add(card); break;
                case ShardsZone.PlayZone: player.PlayZone.Add(card); break;
                case ShardsZone.Champions: player.Champions.Add(card); break;
                case ShardsZone.SetAside: player.SetAside.Add(card); break;
                case ShardsZone.Banished: game.State.Banished.Add(card); break;
                default: Assert.Fail("Unsupported fixture zone"); break;
            }
            game.State.InvalidateCardIndex();
            return card;
        }

        private static ShardsCard Row(ShardsEngine game, string id)
        {
            var card = new ShardsCard { InstanceId = game.State.NextInstanceId++, DefId = id, Owner = -1, Zone = ShardsZone.CenterRow };
            game.State.CenterRow[0] = card;
            game.State.InvalidateCardIndex();
            return card;
        }

        private static void Play(ShardsEngine game, ShardsCard card) => Submit(game,
            new ShardsPlayCardAction { PlayerIndex = card.Owner, CardInstanceId = card.InstanceId });

        [Test]
        public void DuelPool_AddsTwoOfEachNewMercenary_LeavesOtherSetsUntouched()
        {
            var duel = Game();
            var counts = duel.InitialCardCounts();
            foreach (string id in new[] { "equilibrium_envoy", "legion_reclaimer" })
            {
                Assert.That(counts[id], Is.EqualTo(2));
                Assert.That(ShardsCardDatabase.Get(id).Type, Is.EqualTo(ShardsCardType.Mercenary));
                Assert.That(ShardsCardDatabase.Get(id).Set, Is.EqualTo("duel"));
            }
            Assert.That(ShardsCardDatabase.Get("equilibrium_envoy").Faction, Is.EqualTo(ShardsFaction.Order));
            Assert.That(ShardsCardDatabase.Get("equilibrium_envoy").Cost, Is.EqualTo(3));
            Assert.That(ShardsCardDatabase.Get("legion_reclaimer").Faction, Is.EqualTo(ShardsFaction.Homodeus));
            Assert.That(ShardsCardDatabase.Get("legion_reclaimer").Cost, Is.EqualTo(7));
            Assert.That(ShardsCardDatabase.Get("legion_reclaimer").Shield, Is.EqualTo(5));
            Assert.That(ShardsCardDatabase.Get("reactor_drone_duel").Cost, Is.EqualTo(2));
            Assert.That(ShardsCardDatabase.Get("reactor_drone").Cost, Is.EqualTo(3));
            var standard = Game(dlc: ShardsDlc.None).InitialCardCounts();
            Assert.That(standard.ContainsKey("equilibrium_envoy"), Is.False);
            Assert.That(standard.ContainsKey("legion_reclaimer"), Is.False);
            Assert.That(standard.ContainsKey("reactor_drone_duel"), Is.False);
        }

        [Test]
        public void Dna_ExactlyThreeGemsActivates_AndTwoCannot()
        {
            var game = Game();
            var player = game.State.Players[0];
            var dna = Give(game, 0, "dna", ShardsZone.SetAside);
            player.SetAside.Remove(dna); player.Destinies.Add(dna);
            player.Gems = 2;
            Assert.That(game.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = dna.InstanceId }).Accepted, Is.False);
            Assert.That(game.LegalActions(0).OfType<ShardsExhaustAction>().Any(a => a.CardInstanceId == dna.InstanceId), Is.False);
            Assert.That(player.PendingRecruitCopies, Is.Zero);
            player.Gems = 3;
            Assert.That(game.LegalActions(0).OfType<ShardsExhaustAction>().Any(a => a.CardInstanceId == dna.InstanceId), Is.True);
            Submit(game, new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = dna.InstanceId });
            Assert.That(player.Gems, Is.Zero);
            Assert.That(player.PendingRecruitCopies, Is.EqualTo(1));
            Assert.That(dna.Exhausted, Is.True);
        }

        [Test]
        public void CenturyForge_WithoutChampions_StacksAndDeploysNextRecruitedChampions()
        {
            var game = Game();
            var player = game.State.Players[0];
            Play(game, Give(game, 0, "century_forge", ShardsZone.Hand));
            Play(game, Give(game, 0, "century_forge", ShardsZone.Hand));
            Assert.That(player.Champions, Is.Empty);
            Assert.That(player.Gems, Is.EqualTo(6));
            Assert.That(player.NextChampionsIntoPlay, Is.EqualTo(2));
            Row(game, "reactor_drone_duel");
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.That(player.NextChampionsIntoPlay, Is.EqualTo(2), "An ally purchase must not consume a champion redirect");
            foreach (string id in new[] { "primus_pilus_duel", "giga_source_adept_duel" })
            {
                var champion = Row(game, id);
                Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
                Assert.That(player.Champions, Does.Contain(champion));
                Assert.That(champion.Exhausted, Is.False);
            }
            Assert.That(player.Gems, Is.Zero);
            Assert.That(player.NextChampionsIntoPlay, Is.Zero);
        }

        [TestCase(0, 0, 0)]
        [TestCase(5, 4, 4)]
        [TestCase(5, 5, 5)]
        [TestCase(5, 6, 5)]
        [TestCase(29, 30, 29)]
        public void Envoy_DrawsAlways_OnlyStrictlyHigherMasteryOpponentLosesOne(int ours, int theirs, int expected)
        {
            var game = Game();
            var player = game.State.Players[0];
            player.Mastery = ours; game.State.Players[1].Mastery = theirs;
            var drawn = Give(game, 0, "crystal", ShardsZone.Deck);
            var envoy = Give(game, 0, "equilibrium_envoy", ShardsZone.Hand);
            var before = ShardsSnapshotBuilder.Build(game, 0);
            Assert.That(before.ConditionGlowIds.Contains(envoy.InstanceId), Is.EqualTo(ours < theirs));
            Play(game, envoy);
            Assert.That(player.Hand, Does.Contain(drawn));
            Assert.That(player.Mastery, Is.EqualTo(ours));
            Assert.That(game.State.Players[1].Mastery, Is.EqualTo(expected));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            var losses = game.Log.FilterFor(-1).OfType<ShardsMasteryChangedEvent>().Where(e => e.Delta < 0).ToList();
            Assert.That(losses.Count, Is.EqualTo(theirs == expected ? 0 : 1));
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).Players[1].Mastery, Is.EqualTo(expected));
        }

        [Test]
        public void Envoy_Multiplayer_DrawsBeforeChoosingOneEligibleOpponent_RejectsOtherTargets()
        {
            var game = Game(4);
            int[] mastery = { 5, 8, 5, 9 };
            for (int i = 0; i < 4; i++) game.State.Players[i].Mastery = mastery[i];
            var drawn = Give(game, 0, "crystal", ShardsZone.Deck);
            Play(game, Give(game, 0, "equilibrium_envoy", ShardsZone.Hand));
            Assert.That(game.State.Players[0].Hand, Does.Contain(drawn));
            var request = game.PendingInput.Decision;
            Assert.That(request.Context, Is.EqualTo("soi.target"));
            Assert.That(request.Options.Select(o => o.Id), Is.EquivalentTo(new[] { 1, 3 }));
            Assert.That(request.Min, Is.EqualTo(1));
            Assert.That(request.Max, Is.EqualTo(1));
            var bad = new DecisionAnswer { DecisionId = request.Id };
            bad.ChosenOptionIds.Add(2);
            Assert.That(game.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = bad }).Accepted, Is.False);
            Answer(game, 3);
            Assert.That(game.State.Players.Select(p => p.Mastery), Is.EqualTo(new[] { 5, 8, 5, 8 }));
            Play(game, Give(game, 0, "equilibrium_envoy", ShardsZone.Hand));
            Answer(game, 1);
            Assert.That(game.State.Players.Select(p => p.Mastery), Is.EqualTo(new[] { 5, 7, 5, 8 }));
        }

        [Test]
        public void Envoy_ExcludesEliminatedOpponents_AutoTargetsSoleEligibleAndSkipsWhenNone()
        {
            var game = Game(4);
            game.State.Players[0].Mastery = 5;
            game.State.Players[1].Mastery = 6;
            game.State.Players[2].Mastery = 4;
            game.State.Players[3].Mastery = 30;
            game.State.Players[3].Eliminated = true;
            Play(game, Give(game, 0, "equilibrium_envoy", ShardsZone.Hand));
            Assert.That(game.State.Players[1].Mastery, Is.EqualTo(5));
            Assert.That(game.State.Players[3].Mastery, Is.EqualTo(30));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            Play(game, Give(game, 0, "equilibrium_envoy", ShardsZone.Hand));
            Assert.That(game.State.Players[1].Mastery, Is.EqualTo(5));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Envoy_RecruitWaitsForPlay_FastPlayDrawsAndDebuffsThenReturnsToCenter(bool fastPlay)
        {
            var game = Game();
            var player = game.State.Players[0];
            player.Gems = 3;
            game.State.Players[1].Mastery = 1;
            var drawn = Give(game, 0, "crystal", ShardsZone.Deck);
            var envoy = Row(game, "equilibrium_envoy");
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = fastPlay });
            Assert.That(player.Gems, Is.Zero);
            Assert.That(player.Hand.Contains(drawn), Is.EqualTo(fastPlay));
            Assert.That(game.State.Players[1].Mastery, Is.EqualTo(fastPlay ? 0 : 1));
            Assert.That(fastPlay ? player.PlayZone : player.Discard, Does.Contain(envoy));
            if (!fastPlay) return;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.State.CenterDeck, Does.Contain(envoy));
            Assert.That(envoy.Owner, Is.EqualTo(-1));
            Assert.That(envoy.FastPlayed, Is.False);
            Assert.That(player.Hand.Concat(player.Discard).Concat(player.PlayZone).Contains(envoy), Is.False);
        }

        [Test]
        public void Reclaimer_DiscountCountsAllOwnedDeckZonesAndRelicChampions_ExcludesLoansAndRemovedCards()
        {
            var game = Game();
            var player = game.State.Players[0];
            var def = ShardsCardDatabase.Get("legion_reclaimer");
            foreach (var zone in new[] { ShardsZone.Deck, ShardsZone.Hand, ShardsZone.Discard, ShardsZone.PlayZone, ShardsZone.Champions })
                Give(game, 0, "testudo_vanguard", zone);
            Give(game, 0, "unknown_god", ShardsZone.Discard);
            Give(game, 0, "testudo_vanguard", ShardsZone.Banished);
            Give(game, 0, "unknown_god", ShardsZone.SetAside);
            Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            Give(game, 0, "testudo_vanguard", ShardsZone.PlayZone).FastPlayed = true;
            Give(game, 0, "testudo_vanguard", ShardsZone.Champions).Owner = 1;
            Give(game, 0, "legion_reclaimer", ShardsZone.Discard);
            Assert.That(game.EffectiveCost(player, def), Is.EqualTo(1));
            player.Deck.Reverse(); player.Hand.Reverse();
            Assert.That(game.EffectiveCost(player, def), Is.EqualTo(1), "Private ordering must have no effect on a public composition discount");
            Give(game, 0, "testudo_vanguard", ShardsZone.Hand);
            Assert.That(game.EffectiveCost(player, def), Is.Zero);
            Give(game, 0, "testudo_vanguard", ShardsZone.Hand);
            Assert.That(game.EffectiveCost(player, def), Is.Zero, "Discount cannot make a negative purchase price");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Reclaimer_LiveDiscountDrivesSnapshotLegalActionsAndActualRecruitOrFastPlayPrice(bool fastPlay)
        {
            var game = Game();
            var player = game.State.Players[0];
            player.Gems = 3;
            for (int i = 0; i < 3; i++) Give(game, 0, "testudo_vanguard", ShardsZone.Deck);
            var reclaimer = Row(game, "legion_reclaimer");
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).RowEffectiveCosts[0], Is.EqualTo(4));
            Assert.That(game.LegalActions(0).OfType<ShardsBuyCardAction>().Any(a => a.SlotIndex == 0), Is.False);
            Assert.That(game.Submit(new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = fastPlay }).Accepted, Is.False);
            var target = Give(game, 0, "unknown_god", ShardsZone.Discard);
            var snapshot = ShardsSnapshotBuilder.Build(game, 0);
            Assert.That(snapshot.RowEffectiveCosts[0], Is.EqualTo(3));
            Assert.That(snapshot.BuyableSlots, Does.Contain(0));
            Assert.That(game.LegalActions(0).OfType<ShardsBuyCardAction>().Count(a => a.SlotIndex == 0), Is.EqualTo(2));
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = fastPlay });
            Assert.That(player.Gems, Is.Zero);
            Assert.That(game.Log.FilterFor(-1).OfType<ShardsCardBoughtEvent>().Last().CostPaid, Is.EqualTo(3));
            Assert.That(reclaimer.FastPlayed, Is.EqualTo(fastPlay));
            Assert.That(fastPlay ? player.PlayZone : player.Discard, Does.Contain(reclaimer));
            Assert.That(fastPlay ? player.Hand : player.Discard, Does.Contain(target));
            Assert.That(game.EffectiveCost(player, reclaimer.Def), Is.EqualTo(3), "Returning an owned champion to hand preserves total ownership");
        }

        [Test]
        public void Reclaimer_DecimaDiscountStacks_AndPrintedCostStillGovernsWarpAndFreeRecruitCaps()
        {
            var game = Game();
            var player = game.State.Players[0];
            player.CharacterId = "decima"; player.Mastery = 5;
            Give(game, 0, "testudo_vanguard", ShardsZone.Hand);
            var card = Row(game, "legion_reclaimer");
            Assert.That(game.EffectiveCost(player, card.Def), Is.EqualTo(4));
            player.FirstBuyUsedThisTurn = true;
            Assert.That(game.EffectiveCost(player, card.Def), Is.EqualTo(6));
            for (int i = 0; i < 6; i++) Give(game, 0, "testudo_vanguard", ShardsZone.Hand);
            Assert.That(game.EffectiveCost(player, card.Def), Is.Zero);
            for (int i = 1; i < game.State.CenterRow.Length; i++) game.State.CenterRow[i] = null;
            var context = new ShardsContext { Engine = game, ControllerIndex = 0 };
            Assert.That(new WarpUpTo(6).Resolve(context).ToList(), Is.Empty);
            Assert.That(new RecruitFromRow(6).Resolve(context).ToList(), Is.Empty);
            Assert.That(game.State.CenterRow[0], Is.SameAs(card));
        }

        [Test]
        public void Reclaimer_ChampionDeathKeepsDiscount_BanishImmediatelyRaisesVisiblePrice()
        {
            var game = Game();
            var player = game.State.Players[0];
            var champion = Give(game, 0, "testudo_vanguard", ShardsZone.Champions);
            Row(game, "legion_reclaimer");
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).RowEffectiveCosts[0], Is.EqualTo(6));
            game.DestroyChampion(player, champion, 1);
            Assert.That(player.Discard, Does.Contain(champion));
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).RowEffectiveCosts[0], Is.EqualTo(6));
            game.Banish(champion, player.Discard);
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).RowEffectiveCosts[0], Is.EqualTo(7));
        }

        [Test]
        public void Reclaimer_ReturnChoiceIncludesRelicChampions_ExcludesAlliesAndOtherPlayersCards()
        {
            var game = Game();
            var normal = Give(game, 0, "testudo_vanguard", ShardsZone.Discard);
            var relic = Give(game, 0, "unknown_god", ShardsZone.Discard);
            var ally = Give(game, 0, "reactor_drone_duel", ShardsZone.Discard);
            Give(game, 1, "testudo_vanguard", ShardsZone.Discard);
            Play(game, Give(game, 0, "legion_reclaimer", ShardsZone.Hand));
            var request = game.PendingInput.Decision;
            Assert.That(request.Context, Is.EqualTo("soi.return"));
            Assert.That(request.Options.Select(o => o.CardInstanceId), Is.EquivalentTo(new[] { normal.InstanceId, relic.InstanceId }));
            Assert.That(request.Min, Is.EqualTo(1));
            Answer(game, relic.InstanceId);
            Assert.That(game.State.Players[0].Hand, Does.Contain(relic));
            Assert.That(game.State.Players[0].Discard, Does.Contain(normal));
            Assert.That(game.State.Players[0].Discard, Does.Contain(ally));
            Assert.That(game.Log.FilterFor(-1).OfType<ShardsCardReturnedEvent>().Last().InstanceId, Is.EqualTo(relic.InstanceId));
        }

        [Test]
        public void Reclaimer_NoChampionInDiscardCompletesWithoutDecision()
        {
            var game = Game();
            Give(game, 0, "reactor_drone_duel", ShardsZone.Discard);
            Play(game, Give(game, 0, "legion_reclaimer", ShardsZone.Hand));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            Assert.That(game.State.Players[0].Hand, Is.Empty);
        }

        [Test]
        public void Reclaimer_HandShieldPreventsFiveDamage_StaysInHandAndIsPubliclyRevealed()
        {
            var game = Game();
            game.State.Players[0].Power = 6;
            var shield = Give(game, 1, "legion_reclaimer", ShardsZone.Hand);
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.shields"));
            Assert.That(game.PendingInput.Decision.PlayerIndex, Is.EqualTo(1));
            Assert.That(game.PendingInput.Decision.Options.Select(o => o.CardInstanceId), Does.Contain(shield.InstanceId));
            Answer(game, shield.InstanceId);
            Assert.That(game.State.Players[1].Health, Is.EqualTo(49));
            Assert.That(game.State.Players[1].Hand, Does.Contain(shield));
            var revealed = game.Log.FilterFor(-1).OfType<ShardsShieldsRevealedEvent>().Single();
            Assert.That(revealed.Prevented, Is.EqualTo(5));
            Assert.That(revealed.HandInstanceIds, Does.Contain(shield.InstanceId));
        }
    }
}
