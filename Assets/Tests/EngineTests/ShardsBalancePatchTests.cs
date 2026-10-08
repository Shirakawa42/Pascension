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
    /// <summary>Behavioral coverage for the October Duel balance mechanics.</summary>
    public sealed class ShardsBalancePatchTests
    {
        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static ShardsEngine Game(ulong seed = 42, int players = 2)
        {
            var specs = new List<PlayerSpec>();
            string[] heroes = { "decima", "tetra", "volos", "rez" };
            for (int i = 0; i < players; i++)
                specs.Add(new PlayerSpec { Name = "P" + i, CharacterId = heroes[i] });
            var engine = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, ShardsDlc.Duel)).Inner;
            Drain(engine);
            return engine;
        }

        private static void Submit(ShardsEngine engine, PlayerAction action)
        {
            var result = engine.Submit(action);
            Assert.That(result.Accepted, Is.True, action.Describe() + ": " + result.Error);
        }

        private static void Drain(ShardsEngine engine)
        {
            int guard = 0;
            while (engine.PendingInput?.Kind == PendingInputKind.Decision && guard++ < 100)
            {
                var request = engine.PendingInput.Decision;
                var choices = request.DefaultOptionIds.ToList();
                foreach (var option in request.Options)
                    if (choices.Count < request.Min && !option.Disabled && !choices.Contains(option.Id))
                        choices.Add(option.Id);
                var answer = new DecisionAnswer { DecisionId = request.Id };
                answer.ChosenOptionIds.AddRange(choices.Take(request.Max));
                Submit(engine, new SubmitDecisionAction { PlayerIndex = request.PlayerIndex, Answer = answer });
            }
            Assert.That(guard, Is.LessThan(100), "Decision pump must terminate");
        }

        private static ShardsCard Give(ShardsEngine engine, int seat, string id, ShardsZone zone)
        {
            var card = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = id, Owner = seat, Zone = zone };
            var player = engine.State.Players[seat];
            switch (zone)
            {
                case ShardsZone.Hand: player.Hand.Add(card); break;
                case ShardsZone.Champions: player.Champions.Add(card); break;
                case ShardsZone.Discard: player.Discard.Add(card); break;
                case ShardsZone.SetAside: player.Destinies.Add(card); break;
                default: Assert.Fail("Unsupported fixture zone"); break;
            }
            return card;
        }

        private static ShardsCard Row(ShardsEngine engine, string id)
        {
            var card = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = id, Owner = -1, Zone = ShardsZone.CenterRow };
            engine.State.CenterRow[0] = card;
            return card;
        }

        private static ShardsAttackChampionAction Attack(ShardsCard card, int amount = 0) => new()
        { PlayerIndex = 0, TargetPlayerIndex = card.Owner, CardInstanceId = card.InstanceId, Amount = amount };

        private static void End(ShardsEngine engine)
        {
            Submit(engine, new ShardsEndTurnAction { PlayerIndex = engine.State.TurnPlayerIndex });
            Drain(engine);
        }

        [Test]
        public void OpeningRow_ExcludesExpensiveCardsExceptComet_WithoutLosingOrRevealingSkippedCards()
        {
            bool sawComet = false;
            for (ulong seed = 1; seed <= 160; seed++)
            {
                var game = Game(seed);
                foreach (var card in game.State.CenterRow)
                {
                    Assert.That(card.Def.Cost <= 5 || card.DefId == "comet", Is.True, card.DefId);
                    sawComet |= card.DefId == "comet";
                }
                Assert.That(game.State.ActiveMonsters, Is.Empty);
                Assert.That(game.Log.FilterFor(-1).OfType<ShardsRowRefilledEvent>().All(e =>
                    ShardsCardDatabase.Get(e.DefId).Cost <= 5 || e.DefId == "comet"), Is.True);
                var actual = game.State.CenterDeck.Concat(game.State.CenterRow).GroupBy(c => c.DefId)
                    .ToDictionary(g => g.Key, g => g.Count());
                var initial = game.InitialCardCounts();
                foreach (var item in actual)
                    Assert.That(item.Value, Is.EqualTo(initial[item.Key]), item.Key);
            }
            Assert.That(sawComet, Is.True, "Comet remains eligible, rather than every cost-6+ card being filtered");
        }

        [Test]
        public void OpeningRestriction_EndsBeforeFirstRefill_AndSeedReplayIsDeterministic()
        {
            var game = Game();
            var repeat = Game();
            Assert.That(game.State.ComputeHash(), Is.EqualTo(repeat.State.ComputeHash()));
            var expensive = game.State.CenterDeck.First(c => !c.Def.IsMonster && c.Def.Cost >= 6 && c.DefId != "comet");
            game.State.CenterDeck.Remove(expensive);
            game.State.CenterDeck.Add(expensive);
            game.State.Players[0].Gems = 20;
            int slot = System.Array.FindIndex(game.State.CenterRow, c => !c.Def.CannotBeRerolled);
            Submit(game, new ShardsRerollRowAction { PlayerIndex = 0, SlotIndex = slot });
            Assert.That(game.State.CenterRow[slot], Is.SameAs(expensive));
            Assert.That(game.State.Round, Is.EqualTo(1));
        }

        [Test]
        public void ChampionAttacks_RequireExactLiveDefense_ResolveBeforeNextAction()
        {
            var game = Game();
            var p = game.State.Players[0];
            var target = Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            int cost = game.EffectiveDefense(game.State.Players[1], target);
            p.Power = cost - 1;
            Assert.That(game.Submit(Attack(target)).Accepted, Is.False);
            Assert.That(game.LegalActions(0).OfType<ShardsAttackChampionAction>(), Is.Empty);
            p.Power = cost + 4;
            Assert.That(game.Submit(Attack(target, cost - 1)).Accepted, Is.False);
            Assert.That(game.Submit(Attack(target, cost + 1)).Accepted, Is.False);
            Assert.That(game.Submit(Attack(target, -1)).Accepted, Is.False);
            int eventStart = game.Log.Count;
            Submit(game, Attack(target));
            var events = game.Log.FilterFor(-1, eventStart);
            Assert.That(events[0], Is.TypeOf<ShardsPowerChangedEvent>());
            Assert.That(((ShardsPowerChangedEvent)events[0]).Delta, Is.EqualTo(-cost));
            Assert.That(events[1], Is.TypeOf<ShardsChampionDamagedEvent>());
            Assert.That(((ShardsChampionDamagedEvent)events[1]).Amount, Is.EqualTo(cost));
            Assert.That(((ShardsChampionDamagedEvent)events[1]).Total, Is.EqualTo(cost));
            Assert.That(events[2], Is.TypeOf<ShardsChampionDestroyedEvent>());
            Assert.That(events.OfType<ShardsPowerChangedEvent>().Count(), Is.EqualTo(1));
            Assert.That(p.Power, Is.EqualTo(4));
            Assert.That(target.Zone, Is.EqualTo(ShardsZone.Discard));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            Assert.That(game.PendingInput.PlayerIndex, Is.EqualTo(0));
            var crystal = Give(game, 0, "crystal", ShardsZone.Hand);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = crystal.InstanceId });
            Assert.That(p.Gems, Is.EqualTo(1));
        }

        [Test]
        public void ChampionAttack_UsesTemporaryDefenseAndExistingMarks_AndEmitsFullHitTotal()
        {
            var game = Game();
            var target = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            target.TemporaryDefenseUntilNextTurn = 3;
            target.DamageThisTurn = 2;
            int remaining = target.Def.Defense + 3 - 2;
            game.State.Players[0].Power = remaining;
            var legal = game.LegalActions(0).OfType<ShardsAttackChampionAction>().Single();
            Assert.That(legal.Amount, Is.EqualTo(remaining));
            Submit(game, legal);
            var hit = game.Log.FilterFor(-1).OfType<ShardsChampionDamagedEvent>().Last();
            Assert.That(hit.Amount, Is.EqualTo(remaining));
            Assert.That(hit.Total, Is.EqualTo(target.Def.Defense + 3));
            Assert.That(target.TemporaryDefenseUntilNextTurn, Is.Zero);
            Assert.That(target.DamageThisTurn, Is.Zero);
            Assert.That(game.State.Players[0].Power, Is.Zero);
        }

        [Test]
        public void DuelKillableGlows_RequireCurrentPriority_DuringOwnChoiceAndOpponentShields()
        {
            var game = Game();
            var player = game.State.Players[0];
            player.CharacterId = "volos";
            player.Mastery = 5;
            player.Power = 5;
            var target = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            Give(game, 1, "fungal_hermit_duel", ShardsZone.Hand);
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).KillableIds.Contains(target.InstanceId), Is.True);

            Submit(game, new ShardsHeroAbilityAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.volos"));
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).KillableIds, Is.Empty);
            Drain(game);
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).KillableIds.Contains(target.InstanceId), Is.True);

            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.shields"));
            Assert.That(game.PendingInput.PlayerIndex, Is.EqualTo(1));
            Assert.That(player.Power, Is.EqualTo(5), "Power is retained until cleanup, so resource checks alone are insufficient");
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).KillableIds, Is.Empty);
        }

        [Test]
        public void DuplicateGuards_StayAttackable_ButProtectOtherChampionsUntilBothLeave()
        {
            var game = Game();
            var first = Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            var second = Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            var protectedCard = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            game.State.Players[0].Power = 100;
            Assert.That(game.LegalActions(0).OfType<ShardsAttackChampionAction>().Select(a => a.CardInstanceId),
                Is.EquivalentTo(new[] { first.InstanceId, second.InstanceId }));
            Assert.That(game.Submit(Attack(protectedCard)).Accepted, Is.False);
            Submit(game, Attack(first));
            Assert.That(game.CanAssignDamageTo(game.State.Players[1]), Is.False);
            Assert.That(game.Submit(Attack(protectedCard)).Accepted, Is.False);
            Submit(game, Attack(second));
            Assert.That(game.CanAssignDamageTo(game.State.Players[1]), Is.True);
            Submit(game, Attack(protectedCard));
        }

        [Test]
        public void ChampionAttackRestrictions_ReevaluateMasteryAndAuraAfterEveryAction()
        {
            var game = Game();
            var target = Give(game, 1, "raidian", ShardsZone.Champions);
            var liHin = Give(game, 1, "li_hin_duel", ShardsZone.Champions);
            var attacker = game.State.Players[0];
            attacker.Power = 100;
            game.State.Players[1].Mastery = 10;
            Assert.That(game.Submit(Attack(target)).Accepted, Is.False);
            attacker.Mastery = 10;
            Assert.That(game.LegalActions(0).OfType<ShardsAttackChampionAction>().Any(a => a.CardInstanceId == target.InstanceId), Is.True);
            Assert.That(game.Submit(Attack(liHin)).Accepted, Is.False);
            Submit(game, Attack(target));
            game.DestroyChampion(game.State.Players[1], liHin, 0);
            Assert.That(liHin.Zone, Is.EqualTo(ShardsZone.Discard), "Destroy effects bypass targeting restrictions");
        }

        [TestCase(5)]
        [TestCase(1000)]
        public void DuelEndTurn_OrdinaryPowerDoesNotAttackChampionsOrBypassGuard(int power)
        {
            var game = Game();
            var guard = Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            game.State.Players[0].Power = power;
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.False);
            End(game);
            Assert.That(game.State.Players[1].Health, Is.EqualTo(50));
            Assert.That(guard.Zone, Is.EqualTo(ShardsZone.Champions));
            Assert.That(game.State.Players[0].Power, Is.Zero);
            Assert.That(game.State.GameOver, Is.False);
        }

        [Test]
        public void DuelMultiplayerSplit_ContainsOnlyUnprotectedPlayers()
        {
            var game = Game(players: 4);
            Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            Give(game, 2, "testudo_vanguard", ShardsZone.Champions);
            game.State.Players[0].Power = 3;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.split"));
            Assert.That(game.PendingInput.Decision.Options.Select(o => o.Id), Is.EquivalentTo(new[] { 2, 3 }));
            Drain(game);
        }

        [Test]
        public void DuelFourPlayerChampionAttacks_ReevaluateEachOwnerAndAuraBeforeSplittingRemainder()
        {
            var game = Game(players: 4);
            var aura = Give(game, 1, "ferrata_guard_duel", ShardsZone.Champions);
            var boosted = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            Give(game, 1, "reactor_drone", ShardsZone.Discard);
            Give(game, 1, "reactor_drone", ShardsZone.Discard);
            var guard = Give(game, 2, "zetta_encryptor", ShardsZone.Champions);
            var protectedCard = Give(game, 2, "testudo_vanguard", ShardsZone.Champions);
            var third = Give(game, 3, "testudo_vanguard", ShardsZone.Champions);
            game.State.Players[0].Power = 40;

            var initial = ShardsSnapshotBuilder.Build(game, 0);
            Assert.That(initial.Players.Select(p => p.Champions.Count), Is.EqualTo(new[] { 0, 2, 2, 1 }));
            Assert.That(initial.Players[1].Champions.Single(c => c.InstanceId == boosted.InstanceId).EffectiveDefense,
                Is.EqualTo(6));
            Assert.That(initial.KillableIds.Contains(guard.InstanceId), Is.True);
            Assert.That(initial.KillableIds.Contains(protectedCard.InstanceId), Is.False);

            Submit(game, Attack(aura));
            var afterAura = ShardsSnapshotBuilder.Build(game, 0);
            Assert.That(afterAura.Players[1].Champions.Single().EffectiveDefense, Is.EqualTo(4));
            Assert.That(game.LegalActions(0).OfType<ShardsAttackChampionAction>()
                .Single(a => a.CardInstanceId == boosted.InstanceId).Amount, Is.EqualTo(4));

            // Interleave owners: killing one owner's aura does not unlock another's guard.
            Assert.That(game.Submit(Attack(protectedCard)).Accepted, Is.False);
            Submit(game, Attack(guard));
            Assert.That(game.CanAssignDamageTo(game.State.Players[2]), Is.True);
            Submit(game, Attack(third));
            Submit(game, Attack(boosted));
            Submit(game, Attack(protectedCard));
            int remainder = 40 - 6 - guard.Def.Defense - 4 - 4 - 4;
            Assert.That(game.State.Players[0].Power, Is.EqualTo(remainder));
            Assert.That(game.State.Players.Skip(1).All(p => p.Champions.Count == 0), Is.True);

            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            var request = game.PendingInput.Decision;
            Assert.That(request.Context, Is.EqualTo("soi.split"));
            Assert.That(request.Max, Is.EqualTo(remainder));
            Assert.That(request.Options.Select(o => o.Id), Is.EqualTo(new[] { 1, 2, 3 }));
            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(Enumerable.Repeat(1, 4));
            answer.ChosenOptionIds.AddRange(Enumerable.Repeat(2, remainder - 4));
            Submit(game, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            Drain(game);
            Assert.That(game.State.Players[1].Health, Is.EqualTo(46));
            Assert.That(game.State.Players[2].Health, Is.EqualTo(50 - (remainder - 4)));
            Assert.That(game.State.Players[3].Health, Is.EqualTo(50));
        }

        [TestCase(3, 4, 9, 0)]
        [TestCase(4, 2, 7, 4)]
        [TestCase(4, 0, 13, 0)]
        public void DuelMultiplayerSplit_AcceptsAnyCompleteAllocationIncludingZero(int players, int first, int second, int third)
        {
            var game = Game(players: players);
            var survivor = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            game.State.Players[0].Power = 13;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            var request = game.PendingInput.Decision;
            Assert.That(request.Context, Is.EqualTo("soi.split"));
            Assert.That(request.Options.All(o => o.CardInstanceId <= 0), Is.True);
            var incomplete = new DecisionAnswer { DecisionId = request.Id };
            incomplete.ChosenOptionIds.AddRange(Enumerable.Repeat(1, 12));
            Assert.That(game.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = incomplete }).Accepted,
                Is.False, "The whole remaining power pool must be assigned");

            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(Enumerable.Repeat(1, first));
            answer.ChosenOptionIds.AddRange(Enumerable.Repeat(2, second));
            answer.ChosenOptionIds.AddRange(Enumerable.Repeat(3, third));
            Submit(game, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            Drain(game);
            Assert.That(game.State.Players[1].Health, Is.EqualTo(50 - first));
            Assert.That(game.State.Players[2].Health, Is.EqualTo(50 - second));
            if (players == 4) Assert.That(game.State.Players[3].Health, Is.EqualTo(50 - third));
            Assert.That(survivor.Zone, Is.EqualTo(ShardsZone.Champions));
            Assert.That(game.State.TurnPlayerIndex, Is.EqualTo(1));
        }

        [Test]
        public void DuelMultiplayer_EliminatedAndGuardedSeatsCannotReceiveAssignments_OneTargetSkipsSplit()
        {
            var game = Game(players: 4);
            var guard = Give(game, 1, "zetta_encryptor", ShardsZone.Champions);
            var departing = Give(game, 2, "testudo_vanguard", ShardsZone.Champions);
            game.LoseHealth(2, game.State.Players[2].Health);
            game.State.Players[0].Power = 9;
            Assert.That(game.LegalActions(0).OfType<ShardsAttackChampionAction>()
                .Any(a => a.TargetPlayerIndex == 2), Is.False);
            Assert.That(game.Submit(Attack(departing)).Accepted, Is.False);
            int start = game.Log.Count;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.Log.FilterFor(-1, start).OfType<Pascension.Engine.Events.DecisionRequestedEvent>(), Is.Empty);
            Assert.That(game.State.Players[1].Health, Is.EqualTo(50));
            Assert.That(guard.Zone, Is.EqualTo(ShardsZone.Champions));
            Assert.That(game.State.Players[3].Health, Is.EqualTo(41));
            Assert.That(game.State.TurnPlayerIndex, Is.EqualTo(1));
        }

        [Test]
        public void DuelFourPlayerSplit_ResolvesIndependentShieldsAfterEarlierOpponentIsEliminated()
        {
            var game = Game(players: 4);
            var departing = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            var secondShield = Give(game, 2, "fungal_hermit_duel", ShardsZone.Hand);
            var thirdShield = Give(game, 3, "fungal_hermit_duel", ShardsZone.Hand);
            game.State.Players[1].Health = 3;
            game.State.Players[0].Power = 13;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            var split = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
            // Answer order must not change clockwise shield resolution.
            split.ChosenOptionIds.AddRange(Enumerable.Repeat(3, 2));
            split.ChosenOptionIds.AddRange(Enumerable.Repeat(2, 7));
            split.ChosenOptionIds.AddRange(Enumerable.Repeat(1, 4));
            Submit(game, new SubmitDecisionAction { PlayerIndex = 0, Answer = split });
            Assert.That(game.State.Players[1].Eliminated, Is.True);
            Assert.That(departing.Zone, Is.EqualTo(ShardsZone.Discard));
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.shields"));
            Assert.That(game.PendingInput.PlayerIndex, Is.EqualTo(2));
            var secondAnswer = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
            secondAnswer.ChosenOptionIds.Add(secondShield.InstanceId);
            Submit(game, new SubmitDecisionAction { PlayerIndex = 2, Answer = secondAnswer });
            Assert.That(game.State.Players[2].Health, Is.EqualTo(45));
            Assert.That(game.PendingInput.PlayerIndex, Is.EqualTo(3));
            var thirdAnswer = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
            thirdAnswer.ChosenOptionIds.Add(thirdShield.InstanceId);
            Submit(game, new SubmitDecisionAction { PlayerIndex = 3, Answer = thirdAnswer });
            Assert.That(game.State.Players[3].Health, Is.EqualTo(50));
            Assert.That(secondShield.Zone, Is.EqualTo(ShardsZone.Hand));
            Assert.That(thirdShield.Zone, Is.EqualTo(ShardsZone.Hand));
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            Assert.That(game.State.TurnPlayerIndex, Is.EqualTo(2));
        }

        [TestCase(3)]
        [TestCase(4)]
        public void InfinityShardAtM30_AutomaticallyDefeatsEveryUnguardedOpponentWithoutDecisions(int players)
        {
            var game = Game(players: players);
            for (int seat = 1; seat < players; seat++)
            {
                Give(game, seat, "testudo_vanguard", ShardsZone.Champions);
                Give(game, seat, "fungal_hermit_duel", ShardsZone.Hand);
            }
            Give(game, 0, "blood_for_blood", ShardsZone.SetAside);
            game.State.Players[0].Mastery = 30;
            var infinity = Give(game, 0, "infinity_shard", ShardsZone.Hand);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = infinity.InstanceId });
            Assert.That(game.State.Players[0].Power, Is.GreaterThan(1000));
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.True);
            Assert.That(ShardsSnapshotBuilder.Build(game, 1).AutomaticEndTurnVictory, Is.False);
            int start = game.Log.Count;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.State.GameOver, Is.True);
            Assert.That(game.State.WinnerIndex, Is.EqualTo(0));
            Assert.That(game.State.Players.Skip(1).All(p => p.Eliminated), Is.True);
            Assert.That(game.Log.FilterFor(-1, start).OfType<Pascension.Engine.Events.DecisionRequestedEvent>(), Is.Empty,
                "No split, shield, or damage-hook decision should delay the automatic victory");
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void InfinityShardAtM30_AutomaticallyWinsThroughZettaAndShieldsWithoutChampionAttacks(int players)
        {
            var game = Game(players: players);
            var guards = new List<ShardsCard>();
            for (int seat = 1; seat < players; seat++)
            {
                guards.Add(Give(game, seat, "zetta_encryptor", ShardsZone.Champions));
                Give(game, seat, "testudo_vanguard", ShardsZone.Champions);
                Give(game, seat, "fungal_hermit_duel", ShardsZone.Hand);
            }
            game.State.Players[0].Mastery = 30;
            var infinity = Give(game, 0, "infinity_shard", ShardsZone.Hand);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = infinity.InstanceId });
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.True);
            Assert.That(guards.All(c => c.Zone == ShardsZone.Champions), Is.True,
                "No guard must be removed before ending the turn");
            int start = game.Log.Count;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.State.Players.Skip(1).All(p => p.Eliminated), Is.True);
            Assert.That(game.State.GameOver, Is.True);
            Assert.That(game.State.WinnerIndex, Is.EqualTo(0));
            Assert.That(game.PendingInput, Is.Null);
            Assert.That(game.Log.FilterFor(-1, start).OfType<Pascension.Engine.Events.DecisionRequestedEvent>(), Is.Empty);
            Assert.That(game.Log.FilterFor(-1, start).OfType<ShardsChampionDamagedEvent>(), Is.Empty);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void Comet_DestroysItsChosenOpponentThroughZettaAndShieldsWithoutDamageAssignment(int players)
        {
            var game = Game(players: players);
            var guards = new List<ShardsCard>();
            for (int seat = 1; seat < players; seat++)
            {
                guards.Add(Give(game, seat, "zetta_encryptor", ShardsZone.Champions));
                Give(game, seat, "fungal_hermit_duel", ShardsZone.Hand);
            }
            var comet = Give(game, 0, "comet", ShardsZone.Hand);
            int target = players - 1;
            int start = game.Log.Count;
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = comet.InstanceId });
            if (players > 2)
            {
                Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.target"));
                Assert.That(game.PendingInput.Decision.Options.Select(o => o.Id),
                    Is.EquivalentTo(Enumerable.Range(1, players - 1)));
                Assert.That(guards.All(c => c.Zone == ShardsZone.Champions), Is.True);
                var answer = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
                answer.ChosenOptionIds.Add(target);
                Submit(game, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            }

            Assert.That(game.State.Players[target].Eliminated, Is.True);
            Assert.That(guards[target - 1].Zone, Is.EqualTo(ShardsZone.Discard));
            for (int seat = 1; seat < target; seat++)
            {
                Assert.That(game.State.Players[seat].Eliminated, Is.False);
                Assert.That(game.State.Players[seat].Health, Is.EqualTo(50));
                Assert.That(guards[seat - 1].Zone, Is.EqualTo(ShardsZone.Champions));
            }
            Assert.That(game.State.Players[0].Power, Is.Zero, "Comet eliminates directly; it does not grant power");
            Assert.That(game.State.GameOver, Is.EqualTo(players == 2));
            if (players == 2) Assert.That(game.State.WinnerIndex, Is.EqualTo(0));
            else Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Priority));
            var events = game.Log.FilterFor(-1, start);
            Assert.That(events.OfType<Pascension.Engine.Events.DecisionRequestedEvent>().Count(),
                Is.EqualTo(players > 2 ? 1 : 0), "Only multiplayer target selection is needed");
            Assert.That(events.OfType<ShardsDamageAssignedEvent>(), Is.Empty);
            Assert.That(events.OfType<ShardsChampionDamagedEvent>(), Is.Empty);
            Assert.That(events.OfType<ShardsShieldsRevealedEvent>(), Is.Empty);
        }

        [Test]
        public void AutomaticVictoryHint_RequiresEnoughPublicPowerAndCurrentPriority()
        {
            var game = Game(players: 3);
            var player = game.State.Players[0];
            player.CharacterId = "volos";
            player.Mastery = 30;
            player.Power = 1000;
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.False);
            player.Power = 1001;
            game.State.Players[1].Health = 1002;
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.False);
            game.State.Players[1].Health = 50;
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.True);
            Submit(game, new ShardsHeroAbilityAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Kind, Is.EqualTo(PendingInputKind.Decision));
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.False);
            Drain(game);
            Assert.That(ShardsSnapshotBuilder.Build(game, 0).AutomaticEndTurnVictory, Is.True);
        }

        [Test]
        public void Testudo_StacksPerLiveShieldPlay_NotShieldValue_AndOnlyForPresentChampions()
        {
            var game = Game();
            var first = Give(game, 0, "testudo_vanguard", ShardsZone.Champions);
            var second = Give(game, 0, "testudo_vanguard", ShardsZone.Champions);
            var shield = Give(game, 0, "fungal_hermit_duel", ShardsZone.Hand);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = shield.InstanceId });
            Assert.That(first.TemporaryDefenseUntilNextTurn, Is.EqualTo(2));
            Assert.That(second.TemporaryDefenseUntilNextTurn, Is.EqualTo(2));
            var later = Give(game, 0, "drakonarius", ShardsZone.Hand);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = later.InstanceId });
            Assert.That(later.TemporaryDefenseUntilNextTurn, Is.Zero);
            game.DestroyChampion(game.State.Players[0], first, 1);
            Assert.That(first.TemporaryDefenseUntilNextTurn, Is.Zero);
            Assert.That(second.TemporaryDefenseUntilNextTurn, Is.EqualTo(2), "Existing grants survive their source");
            Assert.That(game.EffectiveDefense(game.State.Players[0], second), Is.EqualTo(second.Def.Defense + 2));
        }

        [Test]
        public void Testudo_LivePhasicShieldQualifies_AndExpiryIsOwnersNextStart()
        {
            var game = Game();
            var testudo = Give(game, 0, "testudo_vanguard", ShardsZone.Champions);
            Give(game, 0, "phasic_technology", ShardsZone.SetAside);
            var shield = Give(game, 0, "order_initiate_duel", ShardsZone.Hand);
            Assert.That(shield.Def.Shield, Is.Zero);
            Assert.That(game.ShieldValue(game.State.Players[0], shield), Is.Positive);
            Submit(game, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = shield.InstanceId });
            Drain(game);
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.EqualTo(1));
            var snapshot = ShardsSnapshotBuilder.Build(game, 1).Players[0].Champions.Single(c => c.InstanceId == testudo.InstanceId);
            Assert.That(snapshot.EffectiveDefense, Is.EqualTo(testudo.Def.Defense + 1));
            Assert.That(snapshot.TemporaryDefenseUntilNextTurn, Is.EqualTo(1));
            End(game);
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.EqualTo(1));
            End(game);
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.Zero);
        }

        [Test]
        public void Testudo_ShieldRevealAndCopiedEffectDoNotTrigger_ActualFastPlayDoes()
        {
            var game = Game();
            var testudo = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            var shield = Give(game, 1, "fungal_hermit_duel", ShardsZone.Hand);
            game.State.Players[0].Power = 1;
            Submit(game, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.shields"));
            var answer = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
            answer.ChosenOptionIds.Add(shield.InstanceId);
            Submit(game, new SubmitDecisionAction { PlayerIndex = 1, Answer = answer });
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.Zero);
            foreach (var step in shield.Def.PlayEffect.Resolve(new ShardsContext { Engine = game, ControllerIndex = 1, Source = shield }))
                Assert.That(step.Decision, Is.Null);
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.Zero);
            Row(game, "fungal_hermit_duel");
            game.State.Players[1].Gems = 3;
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 1, SlotIndex = 0, FastPlay = true });
            Assert.That(testudo.TemporaryDefenseUntilNextTurn, Is.EqualTo(1));
        }

        [Test]
        public void Dna_ActivationPaysOnce_StackedCopiesUseSameNextRecruit_AndBypassOriginalRouting()
        {
            var game = Game();
            var player = game.State.Players[0];
            var dna = Give(game, 0, "dna", ShardsZone.SetAside);
            player.Gems = 3;
            Assert.That(game.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = dna.InstanceId }).Accepted, Is.False);
            Assert.That(dna.Exhausted, Is.False);
            player.Gems = 20;
            player.Mastery = 20;
            Give(game, 0, "unknown_god", ShardsZone.Champions);
            Submit(game, new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = dna.InstanceId });
            Assert.That(player.Gems, Is.EqualTo(16), "Unknown God's copied effect does not repay the activation cost");
            Assert.That(player.PendingRecruitCopies, Is.EqualTo(2));
            var original = Row(game, "breaker_duel");
            int nextId = game.State.NextInstanceId;
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.That(player.Hand.Contains(original), Is.True);
            var copies = player.Discard.Where(c => c.DefId == original.DefId).ToList();
            Assert.That(copies.Count, Is.EqualTo(2));
            Assert.That(copies.Select(c => c.InstanceId), Is.EquivalentTo(new[] { nextId, nextId + 1 }));
            Assert.That(copies.All(c => c.Owner == 0 && !c.FastPlayed && !c.Exhausted), Is.True);
            Assert.That(player.PendingRecruitCopies, Is.Zero);
            Assert.That(game.State.GeneratedCardCounts[original.DefId], Is.EqualTo(2));
            Assert.That(game.Log.FilterFor(-1).OfType<ShardsCardCopiedEvent>().Count(), Is.EqualTo(2));
            Assert.That(game.State.FindCard(nextId), Is.SameAs(copies[0]));
        }

        [Test]
        public void Dna_FreeToHandRecruitAndCometPurchaseCopyToDiscard()
        {
            var game = Game();
            var player = game.State.Players[0];
            var free = Row(game, "rift_scout");
            game.ArmRecruitCopy(0);
            Assert.That(game.RecruitFromRowFree(0, 0, true), Is.True);
            Assert.That(player.Hand.Contains(free), Is.True);
            Assert.That(player.Discard.Count(c => c.DefId == free.DefId), Is.EqualTo(1));
            var comet = Row(game, "comet");
            game.ArmRecruitCopy(0);
            Assert.That(game.RecruitFromRowFree(0, 0, false), Is.False);
            Assert.That(player.PendingRecruitCopies, Is.EqualTo(1), "Rejected acquisition must not consume DNA");
            player.Gems = 20;
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.That(player.Discard.Count(c => c.DefId == comet.DefId), Is.EqualTo(2));
            Assert.That(player.PendingRecruitCopies, Is.Zero);
        }

        [Test]
        public void Dna_RelicRecruitCopies_ButFastPlayAndDestinyTakingDoNot()
        {
            var game = Game();
            var player = game.State.Players[0];
            game.ArmRecruitCopy(0);
            Row(game, "rift_scout");
            player.Gems = 20;
            Submit(game, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true });
            Drain(game);
            Assert.That(player.PendingRecruitCopies, Is.EqualTo(1));
            player.Mastery = 10;
            var destiny = game.State.DestinyRow.First();
            game.GrantDestiny(player, destiny);
            Assert.That(player.PendingRecruitCopies, Is.EqualTo(1));
            var relic = player.SetAside.First(c => c.Def.Type == ShardsCardType.Relic);
            Submit(game, new ShardsRecruitRelicAction { PlayerIndex = 0, CardInstanceId = relic.InstanceId });
            Assert.That(player.Discard.Count(c => c.DefId == relic.DefId), Is.EqualTo(2));
            Assert.That(player.PendingRecruitCopies, Is.Zero);
        }

        [Test]
        public void ChampionAttacks_AuraRemovalChangesNextCost_AndCascadingLethalMarksResolve()
        {
            var game = Game();
            var owner = game.State.Players[1];
            var aura = Give(game, 1, "ferrata_guard_duel", ShardsZone.Champions);
            var first = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            var second = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            Give(game, 1, "drakonarius", ShardsZone.Discard);
            Assert.That(game.EffectiveDefense(owner, first), Is.EqualTo(first.Def.Defense + 2));
            first.DamageThisTurn = first.Def.Defense;
            second.DamageThisTurn = second.Def.Defense;
            game.State.Players[0].Power = 6;
            Submit(game, Attack(aura));
            Assert.That(owner.Champions, Is.Empty, "All champions marked lethal after losing the aura resolve in a stable wave");
            Assert.That(first.DamageThisTurn, Is.Zero);
            Assert.That(second.DamageThisTurn, Is.Zero);
        }

        [Test]
        public void Dna_CorruptionRewardCopiesChosenRelicOnly_AndNeverRepeatsReward()
        {
            var game = Game();
            var player = game.State.Players[0];
            var monster = game.State.CenterDeck.Single(c => c.DefId == "ingeminex_corruption_duel");
            game.State.CenterDeck.Remove(monster);
            monster.Zone = ShardsZone.MonsterSpace;
            game.State.ActiveMonsters.Add(monster);
            game.State.PendingMonsterAttacks.Add(monster.InstanceId);
            game.ArmRecruitCopy(0);
            player.Power = monster.Def.Defense;
            Submit(game, new ShardsAttackMonsterAction { PlayerIndex = 0, CardInstanceId = monster.InstanceId });
            Assert.That(game.PendingInput.Decision.Context, Is.EqualTo("soi.relic"));
            int selected = game.PendingInput.Decision.Options[0].CardInstanceId;
            var answer = new DecisionAnswer { DecisionId = game.PendingInput.Decision.Id };
            answer.ChosenOptionIds.Add(selected);
            Submit(game, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            var original = player.Hand.Single(c => c.InstanceId == selected);
            Assert.That(player.Discard.Count(c => c.DefId == original.DefId), Is.EqualTo(1));
            Assert.That(player.SetAside.Count, Is.EqualTo(2));
            Assert.That(player.PendingRecruitCopies, Is.Zero);
            Assert.That(game.State.PendingMonsterAttacks.Contains(monster.InstanceId), Is.False);
            Assert.That(game.Log.FilterFor(-1).OfType<ShardsRelicRecruitedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void GeneratedCopiesAndDoomGate_ArePubliclyCounted_WithoutChangingInitialStock()
        {
            var game = Game();
            var initial = game.InitialCardCounts();
            game.ShuffleIngeminexIntoCenterDeck(35);
            Assert.That(game.State.GeneratedCardCounts.Values.Sum(), Is.EqualTo(35));
            Assert.That(game.State.GeneratedCardCounts["ingeminex_corruption_duel"], Is.EqualTo(7));
            Assert.That(game.State.CenterDeck.Any(c => c.DefId == "ingeminex_corruption"), Is.False);
            Assert.That(game.InitialCardCounts(), Is.EquivalentTo(initial));
            var snapshot = ShardsSnapshotBuilder.Build(game, 1);
            Assert.That(snapshot.GeneratedCardCounts, Is.EquivalentTo(game.State.GeneratedCardCounts));
            snapshot.GeneratedCardCounts.Clear();
            Assert.That(game.State.GeneratedCardCounts.Values.Sum(), Is.EqualTo(35), "Snapshot mutations cannot alter engine state");
        }

        [Test]
        public void Dna_UnusedEffectExpires_AndPublicStateChangesHashAndSnapshot()
        {
            var game = Game();
            ulong originalHash = game.State.ComputeHash();
            game.ArmRecruitCopy(0);
            Assert.That(game.State.ComputeHash(), Is.Not.EqualTo(originalHash));
            Assert.That(ShardsSnapshotBuilder.Build(game, 1).Players[0].PendingRecruitCopies, Is.EqualTo(1));
            End(game);
            Assert.That(game.State.Players[0].PendingRecruitCopies, Is.Zero);
            var champ = Give(game, 1, "testudo_vanguard", ShardsZone.Champions);
            ulong beforeGrant = game.State.ComputeHash();
            champ.TemporaryDefenseUntilNextTurn++;
            Assert.That(game.State.ComputeHash(), Is.Not.EqualTo(beforeGrant));
        }
    }
}
