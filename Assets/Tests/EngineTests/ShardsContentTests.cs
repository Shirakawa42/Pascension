using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Count validation + per-ruling tests on the REAL Shards of Infinity card
    /// database (photo-verified quantities: 88 base center / 24 RotF / 12 SoS / 30 ItH).</summary>
    public sealed class ShardsContentTests
    {
        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static int CenterCount(string set, ShardsFaction? faction = null)
        {
            int n = 0;
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Set != set) continue;
                if (def.Type == ShardsCardType.Starter || def.Type == ShardsCardType.Relic ||
                    def.Type == ShardsCardType.Destiny)
                    continue;
                if (faction != null && def.Faction != faction) continue;
                n += def.Quantity;
            }
            return n;
        }

        [Test]
        public void Counts_MatchPublishedComponentLists()
        {
            // Base: 88 center cards, 22 per faction.
            Assert.AreEqual(88, CenterCount("base"), "base center deck");
            foreach (var f in new[] { ShardsFaction.Homodeus, ShardsFaction.Order, ShardsFaction.Undergrowth, ShardsFaction.Wraethe })
                Assert.AreEqual(22, CenterCount("base", f), $"base {f}");

            // Starters: 10 cards (7 Crystal / 1 Blaster / 1 Shard Reactor / 1 Infinity Shard).
            int starters = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Type == ShardsCardType.Starter)
                    starters += def.Quantity;
            Assert.AreEqual(10, starters, "starter deck size");

            // RotF: 24 center (6 per faction) + 8 relics (2 per base character).
            Assert.AreEqual(24, CenterCount("relics_of_the_future"), "RotF center cards");
            int relics = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Set == "relics_of_the_future" && def.Type == ShardsCardType.Relic)
                    relics += def.Quantity;
            Assert.AreEqual(8, relics, "RotF relics");

            // SoS competitive: 12 center cards + Rez's 2 relics.
            Assert.AreEqual(12, CenterCount("shadow_of_salvation"), "SoS center cards");
            int rezRelics = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Set == "shadow_of_salvation" && def.Type == ShardsCardType.Relic)
                    rezRelics += def.Quantity;
            Assert.AreEqual(2, rezRelics, "Rez relics");

            // ItH: 30 center cards (25 faction + 5 Ingeminex) + 30 destinies.
            Assert.AreEqual(30, CenterCount("into_the_horizon"), "ItH center cards");
            int monsters = 0, destinies = 0;
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Set != "into_the_horizon") continue;
                if (def.Type == ShardsCardType.Monster) monsters += def.Quantity;
                if (def.Type == ShardsCardType.Destiny) destinies += def.Quantity;
            }
            Assert.AreEqual(5, monsters, "Ingeminex");
            Assert.AreEqual(30, destinies, "destinies");
        }

        private static ShardsEngineAdapter NewGame(ShardsDlc dlc = ShardsDlc.None, int players = 2, ulong seed = 42)
        {
            var specs = new List<PlayerSpec>();
            var characters = ShardsContentRegistry.CharactersFor(dlc);
            for (int i = 0; i < players; i++)
                specs.Add(new PlayerSpec { Name = "P" + i, CharacterId = characters[i % characters.Count] });
            return new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, dlc));
        }

        /// <summary>Resolve the Duel turn-1 hero draft (each seat takes its lobby default)
        /// so the game advances to player 0's turn.</summary>
        private static void CompleteDraft(ShardsEngineAdapter adapter)
        {
            int guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 8)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
        }

        [Test]
        public void Duel_HeroDraft_ReverseOrder_NoDuplicates()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 5);
            var engine = adapter.Inner;
            // The game opens on a hero-draft decision for the LAST seat.
            Assert.AreEqual(PendingInputKind.Decision, adapter.PendingInput.Kind, "opens in the draft");
            Assert.AreEqual(1, adapter.PendingInput.PlayerIndex, "last player drafts first");
            Assert.IsNull(engine.State.Players[0].CharacterId, "characters unassigned until drafted");

            CompleteDraft(adapter);

            Assert.IsNotNull(engine.State.Players[0].CharacterId);
            Assert.IsNotNull(engine.State.Players[1].CharacterId);
            Assert.AreNotEqual(engine.State.Players[0].CharacterId, engine.State.Players[1].CharacterId, "no duplicate heroes");
            Assert.AreEqual(0, engine.State.TurnPlayerIndex, "player 0 starts after the draft");
            Assert.AreEqual(PendingInputKind.Priority, adapter.PendingInput.Kind);
            Assert.AreEqual(3, engine.State.Players[0].SetAside.Count, "relics set aside after the pick");
        }

        [Test]
        public void Setup_BaseOnly_88CardCenterDeck_NoExpansionCards()
        {
            var engine = NewGame().Inner;
            int total = engine.State.CenterDeck.Count + 6; // 6 already dealt to the row
            Assert.AreEqual(88, total, "center deck + row = full base set");
            Assert.AreEqual(0, engine.State.DestinyRow.Count, "no destinies without ItH");
            foreach (var p in engine.State.Players)
                Assert.AreEqual(0, p.SetAside.Count, "no relics without RotF");
        }

        [Test]
        public void Setup_AllDlc_AddsEverything_CloudOraclesErrataReplaces()
        {
            var all = ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;
            var engine = NewGame(all, players: 2, seed: 7).Inner;

            // 88 + 24 + 12 + 30 = 154, minus the 3 RotF Cloud Oracles the SoS errata
            // replaces = 151 total center cards (row + deck + any revealed Ingeminex).
            int total = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count;
            foreach (var slot in engine.State.CenterRow)
                if (slot != null)
                    total++;
            Assert.AreEqual(151, total, "combined center deck respects the errata replacement");

            int rotfOracles = 0, sosOracles = 0;
            foreach (var card in engine.State.CenterDeck)
            {
                if (card.DefId == "cloud_oracles") rotfOracles++;
                if (card.DefId == "cloud_oracles_sos") sosOracles++;
            }
            Assert.AreEqual(0, rotfOracles, "RotF Cloud Oracles replaced by the errata copies");

            Assert.AreEqual(6, engine.State.DestinyRow.Count, "6 destinies dealt face up");
            Assert.AreEqual(24, engine.State.DestinyDeck.Count, "24 destinies remain in the deck");
            foreach (var p in engine.State.Players)
                Assert.AreEqual(2, p.SetAside.Count, "each character sets aside their 2 relics");
        }

        [Test]
        public void Duel_ForcesOtherDlcs_SwapsErrata_AddsNewCards()
        {
            // Duel alone must normalize to all four DLCs.
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 5);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var expected = ShardsDlc.Duel | ShardsDlc.RelicsOfTheFuture |
                           ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;
            Assert.AreEqual(expected, engine.State.Dlc, "Duel forces the other three DLCs on");

            // Errata: the base hero-conditional cards are swapped for their _duel versions.
            // Count across the whole pool (deck + row) — a copy may be dealt to the row.
            int baseLost = 0, duelLost = 0, baseFerrata = 0, duelFerrata = 0, prism = 0;
            var pool = new List<ShardsCard>(engine.State.CenterDeck);
            foreach (var slot in engine.State.CenterRow)
                if (slot != null) pool.Add(slot);
            foreach (var card in pool)
            {
                switch (card.DefId)
                {
                    case "the_lost": baseLost++; break;
                    case "the_lost_duel": duelLost++; break;
                    case "ferrata_guard": baseFerrata++; break;
                    case "ferrata_guard_duel": duelFerrata++; break;
                    case "prism": prism++; break;
                }
            }
            Assert.AreEqual(0, baseLost, "base The Lost replaced by errata");
            Assert.AreEqual(0, baseFerrata, "base Ferrata Guard replaced by errata");
            Assert.AreEqual(2, duelLost, "the_lost_duel present (qty 2)");
            Assert.AreEqual(2, duelFerrata, "ferrata_guard_duel present (qty 2)");
            Assert.AreEqual(2, prism, "new Aion card Prism present (qty 2 per the design session)");

            // Duel gives decima/tetra a 3rd set-aside relic (2 base + 1 new).
            Assert.AreEqual(3, engine.State.Players[0].SetAside.Count, "decima: 2 base + praetorian_03");
            Assert.AreEqual(3, engine.State.Players[1].SetAside.Count, "tetra: 2 base + multitask_brain");

            // A stat errata swapped in the center pool.
            int baseNil = 0, duelNil = 0;
            foreach (var card in pool)
            {
                if (card.DefId == "nil_assassin") baseNil++;
                if (card.DefId == "nil_assassin_duel") duelNil++;
            }
            Assert.AreEqual(0, baseNil, "base Nil Assassin replaced");
            Assert.AreEqual(3, duelNil, "nil_assassin_duel present (qty 3)");

            // A destiny errata swapped in the destiny pool (row + deck).
            var destinies = new List<ShardsCard>(engine.State.DestinyRow);
            destinies.AddRange(engine.State.DestinyDeck);
            int baseHeal = 0, duelHeal = 0;
            foreach (var d in destinies)
            {
                if (d.DefId == "healing_hands") baseHeal++;
                if (d.DefId == "healing_hands_duel") duelHeal++;
            }
            Assert.AreEqual(0, baseHeal, "base Healing Hands destiny replaced");
            Assert.AreEqual(1, duelHeal, "healing_hands_duel destiny present");
            Assert.AreEqual(30, destinies.Count, "still 30 destinies total after errata swap");
        }

        [Test]
        public void Duel_RowReroll_ClimbingPricePerTurn_RefillsSlot()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 9);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.Gems = 4;

            // Pick a slot whose card can be rerolled.
            int slot = -1;
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
                if (engine.State.CenterRow[s] != null && !engine.State.CenterRow[s].Def.CannotBeRerolled)
                { slot = s; break; }
            Assert.GreaterOrEqual(slot, 0, "a rerollable slot exists");

            // First reroll of the turn costs 1 gem.
            var removed = engine.State.CenterRow[slot];
            Assert.IsTrue(engine.Submit(new ShardsRerollRowAction { PlayerIndex = 0, SlotIndex = slot }).Accepted);
            Assert.AreEqual(3, p0.Gems, "first reroll costs 1");
            Assert.AreEqual(removed, engine.State.CenterDeck[0], "removed card went to the bottom of the center deck");
            Assert.IsNotNull(engine.State.CenterRow[slot], "slot refilled");

            // Second reroll in the SAME turn costs 2.
            int slot2 = -1;
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
                if (engine.State.CenterRow[s] != null && !engine.State.CenterRow[s].Def.CannotBeRerolled)
                { slot2 = s; break; }
            Assert.IsTrue(engine.Submit(new ShardsRerollRowAction { PlayerIndex = 0, SlotIndex = slot2 }).Accepted);
            Assert.AreEqual(1, p0.Gems, "second reroll costs 2");
            Assert.AreEqual(2, p0.RerollsThisTurn);

            // Third would cost 3 — unaffordable with 1 gem left.
            Assert.IsFalse(engine.Submit(new ShardsRerollRowAction { PlayerIndex = 0, SlotIndex = slot2 }).Accepted,
                "third reroll costs 3 and must be rejected at 1 gem");

            // The price resets to 1 next turn.
            engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 });
            int guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 30)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
            Assert.AreEqual(0, p0.RerollsThisTurn, "reroll counter resets at end of turn");
        }

        [Test]
        public void Duel_HeroAbility_IsSeparateFromFocus_OncePerTurn()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 3);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.CharacterId = "volos"; // pay 1 gem: gain 3 health
            p0.Mastery = 5;
            p0.Gems = 5;
            p0.Health = 40;

            Assert.IsTrue(engine.Submit(new ShardsHeroAbilityAction { PlayerIndex = 0 }).Accepted, "ability usable");
            Assert.AreEqual(43, p0.Health, "Volos gained 3 health");
            Assert.AreEqual(4, p0.Gems, "paid 1 gem");
            Assert.IsTrue(p0.HeroAbilityUsedThisTurn);

            // Focus is still available in the SAME turn (ability is separate).
            var focusStillLegal = engine.LegalActions(0).Exists(a => a is ShardsFocusAction);
            Assert.IsTrue(focusStillLegal, "Focus is independent of the hero ability");

            // Once per turn.
            Assert.IsFalse(engine.Submit(new ShardsHeroAbilityAction { PlayerIndex = 0 }).Accepted, "ability is once per turn");
        }

        [Test]
        public void Duel_Decima_FirstBuyCostsOneLess_AtMastery5()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 3);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0]; // decima
            p0.Mastery = 5;
            var def = ShardsCardDatabase.Get("nil_assassin_duel"); // cost 2
            Assert.AreEqual(1, engine.EffectiveCost(p0, def), "first buy costs 1 less");
            p0.FirstBuyUsedThisTurn = true;
            Assert.AreEqual(2, engine.EffectiveCost(p0, def), "subsequent buys are full price");
        }

        [Test]
        public void Duel_HeroDraft_TakenDefault_FallsBackToAvailableHero()
        {
            // Both lobby slots default to the SAME hero: the second drafter's default is
            // taken, so its timeout/bot answer must land on an available hero instead.
            var specs = new List<PlayerSpec>
            {
                new PlayerSpec { Name = "P0", CharacterId = "volos" },
                new PlayerSpec { Name = "P1", CharacterId = "volos" }
            };
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(21, specs, ShardsDlc.Duel));
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            Assert.IsNotNull(engine.State.Players[0].CharacterId);
            Assert.IsNotNull(engine.State.Players[1].CharacterId);
            Assert.AreNotEqual(engine.State.Players[0].CharacterId, engine.State.Players[1].CharacterId,
                "duplicate defaults must not produce duplicate heroes");
            Assert.AreEqual("volos", engine.State.Players[1].CharacterId,
                "the FIRST drafter (last seat) keeps the shared default");
        }

        [Test]
        public void Duel_HeroAbilitySpecs_MatchActivationCosts()
        {
            // The public UI spec and the engine activation must agree for every hero.
            foreach (var id in new[] { "decima", "tetra", "volos", "kosynwu", "rez" })
            {
                var spec = ShardsEngine.HeroAbilityInfo(id);
                Assert.IsNotNull(spec.Name, id + " has a spec");
                Assert.AreEqual(5, spec.Mastery, id + " unlocks at M5");
            }
            Assert.IsFalse(ShardsEngine.HeroAbilityInfo("decima").Active, "decima's ability is a passive");
            Assert.IsTrue(ShardsEngine.HeroAbilityInfo("rez").Active);

            // Ability + Focus are independent: use the ability, then Focus, same turn.
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.CharacterId = "tetra"; // pay 3 gems: draw 1
            p0.Mastery = 5;
            p0.Gems = 5;
            int handBefore = p0.Hand.Count;
            Assert.IsTrue(engine.Submit(new ShardsHeroAbilityAction { PlayerIndex = 0 }).Accepted);
            Assert.AreEqual(handBefore + 1, p0.Hand.Count, "tetra drew a card");
            Assert.IsFalse(p0.CharacterExhausted, "the ability does NOT exhaust the character");
            Assert.IsTrue(engine.Submit(new ShardsFocusAction { PlayerIndex = 0 }).Accepted,
                "Focus still available in the same turn");
        }

        [Test]
        public void Duel_Praetorian02_DoubledShields_ExpireAtOwnersNextTurn()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.ShieldsDoubledUntilNextTurn = true;

            // A shield-5 card doubles to 10 while the flag holds.
            var seer = engine.State.CenterDeck.Find(c => c.DefId == "command_seer_duel");
            Assert.IsNotNull(seer, "duel errata command seer in the deck");
            Assert.AreEqual(10, engine.ShieldValue(p0, seer), "shield doubled");

            // P0 ends the turn; when their NEXT turn starts the window closes.
            engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 });
            int guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 30)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
            Assert.IsTrue(p0.ShieldsDoubledUntilNextTurn, "still doubled during the opponent's turn");
            engine.Submit(new ShardsEndTurnAction { PlayerIndex = 1 });
            guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 30)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
            Assert.IsFalse(p0.ShieldsDoubledUntilNextTurn, "cleared at the owner's next turn");
            Assert.AreEqual(5, engine.ShieldValue(p0, seer));
        }

        /// <summary>Answer the currently pending decision with an explicit option list.</summary>
        private static void AnswerPending(ShardsEngine engine, IEnumerable<int> optionIds)
        {
            var answer = new DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            answer.ChosenOptionIds.AddRange(optionIds);
            Assert.IsTrue(engine.Submit(new SubmitDecisionAction
            {
                PlayerIndex = engine.PendingInput.PlayerIndex,
                Answer = answer
            }).Accepted, "decision answer accepted");
        }

        private static ShardsCard Plant(ShardsEngine engine, ShardsPlayer owner, string defId, ShardsZone zone)
        {
            var card = new ShardsCard
            {
                InstanceId = engine.State.NextInstanceId++,
                DefId = defId,
                Owner = owner.Index,
                Zone = zone
            };
            (zone == ShardsZone.Champions ? owner.Champions : owner.Hand).Add(card);
            engine.State.InvalidateCardIndex();
            return card;
        }

        [Test]
        public void Duel_Testudo_OverAssignment_PaysThroughShields()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            var testudo = Plant(engine, p1, "testudo_vanguard", ShardsZone.Champions); // defense 6
            var prism = Plant(engine, p1, "prism", ShardsZone.Hand);                   // shield 2
            p0.Power = 10;
            int healthBefore = p1.Health;

            Assert.IsTrue(engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 }).Accepted);
            Assert.AreEqual("soi.split", engine.PendingInput.Decision.Context);
            // Over-assign: 8 points on the defense-6 champion (2 spare pay through the
            // shield), the mandatory remainder on the face.
            var split = new List<int>();
            for (int i = 0; i < 8; i++) split.Add(ShardsEngine.ChampionSplitBase + testudo.InstanceId);
            for (int i = 0; i < 2; i++) split.Add(p1.Index);
            AnswerPending(engine, split);

            Assert.AreEqual("soi.shields", engine.PendingInput.Decision.Context);
            AnswerPending(engine, new[] { prism.InstanceId });

            Assert.IsFalse(p1.Champions.Contains(testudo), "8 assigned - 2 shield = 6 kills through the shield");
            Assert.AreEqual(healthBefore, p1.Health, "face damage 2 - 2 shield = 0");
        }

        [Test]
        public void Duel_Testudo_ExactLethal_IsSavedByAnyShield()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            var testudo = Plant(engine, p1, "testudo_vanguard", ShardsZone.Champions);
            var prism = Plant(engine, p1, "prism", ShardsZone.Hand);
            p0.Power = 10;
            int healthBefore = p1.Health;

            Assert.IsTrue(engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 }).Accepted);
            var split = new List<int>();
            for (int i = 0; i < 6; i++) split.Add(ShardsEngine.ChampionSplitBase + testudo.InstanceId);
            for (int i = 0; i < 4; i++) split.Add(p1.Index);
            AnswerPending(engine, split);
            AnswerPending(engine, new[] { prism.InstanceId });

            Assert.IsTrue(p1.Champions.Contains(testudo), "exact-lethal 6 - 2 shield = 4 < 6: saved");
            Assert.AreEqual(healthBefore - 2, p1.Health, "face 4 - 2 shield = 2");
        }

        [Test]
        public void Duel_Testudo_TauntHeld_ZeroesEverythingBehindIt()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            var zetta = Plant(engine, p1, "zetta_encryptor", ShardsZone.Champions);    // taunt, defense 5
            var testudo = Plant(engine, p1, "testudo_vanguard", ShardsZone.Champions); // defense 6
            var prism = Plant(engine, p1, "prism", ShardsZone.Hand);                   // shield 2
            p0.Power = 13;
            int healthBefore = p1.Health;

            Assert.IsTrue(engine.Submit(new ShardsEndTurnAction { PlayerIndex = 0 }).Accepted);
            // Pre-shield lethal on the taunt (5) unlocks the rest: 6 on Testudo, 2 face.
            var split = new List<int>();
            for (int i = 0; i < 5; i++) split.Add(ShardsEngine.ChampionSplitBase + zetta.InstanceId);
            for (int i = 0; i < 6; i++) split.Add(ShardsEngine.ChampionSplitBase + testudo.InstanceId);
            for (int i = 0; i < 2; i++) split.Add(p1.Index);
            AnswerPending(engine, split);
            AnswerPending(engine, new[] { prism.InstanceId });

            // Zetta resolves FIRST: 5 - 2 shield = 3 < 5, the wall held — every other
            // champion hit AND the face damage resolve as zero.
            Assert.IsTrue(p1.Champions.Contains(zetta), "the taunt champion survives its shielded hit");
            Assert.IsTrue(p1.Champions.Contains(testudo), "hits behind a held taunt are dropped");
            Assert.AreEqual(healthBefore, p1.Health, "face damage behind a held taunt is dropped");
        }

        [Test]
        public void Duel_GrimTutor_SearchesDeckToHand_LosesThreeHealth()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 11);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var tutor = Plant(engine, p0, "grim_tutor", ShardsZone.Hand);
            int deckBefore = p0.Deck.Count;
            int healthBefore = p0.Health;
            Assert.Greater(deckBefore, 0, "draw pile non-empty at turn start");

            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = tutor.InstanceId }).Accepted);
            var request = engine.PendingInput.Decision;
            Assert.AreEqual("soi.tutor", request.Context);
            Assert.AreEqual(deckBefore, request.Options.Count, "every draw-pile card is an option");
            for (int i = 1; i < request.Options.Count; i++)
            {
                int byDef = string.CompareOrdinal(request.Options[i - 1].DefId, request.Options[i].DefId);
                Assert.LessOrEqual(byDef, 0, "options sorted by def id — deck order must never leak");
                if (byDef == 0)
                    Assert.Less(request.Options[i - 1].CardInstanceId, request.Options[i].CardInstanceId,
                        "def-id ties sorted by instance id");
            }

            int chosenId = request.Options[request.Options.Count - 1].Id;
            AnswerPending(engine, new[] { chosenId });

            Assert.IsTrue(p0.Hand.Exists(c => c.InstanceId == chosenId), "searched card went to hand");
            Assert.IsFalse(p0.Deck.Exists(c => c.InstanceId == chosenId));
            Assert.AreEqual(deckBefore - 1, p0.Deck.Count);
            Assert.AreEqual(healthBefore - 3, p0.Health, "3 health LOSS (not damage)");
        }

        [Test]
        public void Duel_WhisperExtractor_OpponentDrawsThenDiscardsYourPick()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 23);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            var whisper = Plant(engine, p0, "whisper_extractor", ShardsZone.Hand);
            int victimHandBefore = p1.Hand.Count;
            int victimDeckBefore = p1.Deck.Count;
            int victimDiscardBefore = p1.Discard.Count;

            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = whisper.InstanceId }).Accepted);

            // Two players: the target is forced, so the only decision is the hand pick —
            // over the victim's hand AFTER their draw.
            var request = engine.PendingInput.Decision;
            Assert.AreEqual("soi.handpick", request.Context);
            Assert.AreEqual(victimHandBefore + 1, request.Options.Count, "the victim drew before the pick");
            Assert.AreEqual(victimDeckBefore - 1, p1.Deck.Count, "the draw came off their deck");
            for (int i = 1; i < request.Options.Count; i++)
            {
                int byDef = string.CompareOrdinal(request.Options[i - 1].DefId, request.Options[i].DefId);
                Assert.LessOrEqual(byDef, 0, "hand options sorted by def id — the drawn card must not be identifiable");
                if (byDef == 0)
                    Assert.Less(request.Options[i - 1].CardInstanceId, request.Options[i].CardInstanceId);
            }

            int pickedId = request.Options[0].Id;
            AnswerPending(engine, new[] { pickedId });

            Assert.AreEqual(victimHandBefore, p1.Hand.Count, "draw + discard is card-neutral for the victim");
            Assert.IsFalse(p1.Hand.Exists(c => c.InstanceId == pickedId), "the chosen card left their hand");
            Assert.AreEqual(victimDiscardBefore + 1, p1.Discard.Count);
            Assert.IsTrue(p1.Discard.Exists(c => c.InstanceId == pickedId), "it went to their discard");
            Assert.AreEqual(2, p0.Power, "the card's own 2 power still applies");
        }

        [Test]
        public void Duel_WhisperExtractor_EmptyVictim_ResolvesWithoutDecision()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 23);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.Clear();
            p1.Deck.Clear();
            p1.Discard.Clear();
            engine.State.InvalidateCardIndex();
            var whisper = Plant(engine, p0, "whisper_extractor", ShardsZone.Hand);

            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = whisper.InstanceId }).Accepted);
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind,
                "nothing to draw and nothing to pick — the effect must not park on a decision");
            Assert.AreEqual(2, p0.Power);
        }

        [Test]
        public void Duel_LegionCarrier_AlwaysReveals_ShowsEveryCardWithNonChampionsGreyed()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 31);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            var carrier = Plant(engine, p0, "legion_carrier_duel", ShardsZone.Hand);
            int deckBefore = p0.Deck.Count;

            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = carrier.InstanceId }).Accepted);

            // No "reveal?" confirmation any more — it goes straight to the pick.
            var request = engine.PendingInput.Decision;
            Assert.AreEqual("soi.reveal", request.Context, "the reveal is mandatory — no confirm step");
            int expected = System.Math.Min(5, deckBefore);
            Assert.AreEqual(expected, request.Options.Count, "every revealed card is shown, not just the champions");
            Assert.AreEqual(0, request.Min, "passing is always allowed");
            foreach (var option in request.Options)
            {
                bool isChampion = ShardsCardDatabase.Get(option.DefId).IsChampion;
                Assert.AreEqual(!isChampion, option.Disabled,
                    option.DefId + ": non-champions are shown but greyed out");
            }

            // A greyed option can never be chosen, however the client asks.
            var greyed = request.Options.Find(o => o.Disabled);
            if (greyed != null)
            {
                var bad = new DecisionAnswer { DecisionId = request.Id };
                bad.ChosenOptionIds.Add(greyed.Id);
                Assert.IsFalse(engine.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = bad }).Accepted,
                    "the engine rejects a greyed option");
            }

            AnswerPending(engine, new int[0]); // pass
            Assert.AreEqual(expected, p0.Discard.Count, "everything revealed went to the discard");
        }

        [Test]
        public void LegionCarrier_ShortDeck_ReshufflesDiscardToFinishTheReveal()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 31);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            // Two cards left to draw from, four sitting in the discard.
            while (p0.Deck.Count > 2)
            {
                var moved = p0.Deck[0];
                p0.Deck.RemoveAt(0);
                moved.Zone = ShardsZone.Discard;
                p0.Discard.Add(moved);
            }
            Assert.GreaterOrEqual(p0.Discard.Count, 3, "discard has cards to shuffle back");
            int pool = p0.Deck.Count + p0.Discard.Count;
            var carrier = Plant(engine, p0, "legion_carrier_duel", ShardsZone.Hand);

            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = carrier.InstanceId }).Accepted);
            var request = engine.PendingInput.Decision;
            Assert.AreEqual(System.Math.Min(5, pool), request.Options.Count,
                "running out of deck mid-reveal shuffles the discard back in instead of stopping at 2");
        }

        [Test]
        public void Duel_ReactorDrone_ChooseOne_UsesTheNerfedValues()
        {
            foreach (int mode in new[] { 1, 2 })
            {
                var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 31);
                CompleteDraft(adapter);
                var engine = adapter.Inner;
                var p0 = engine.State.Players[0];
                p0.Gems = 0;
                var drone = Plant(engine, p0, "reactor_drone_duel", ShardsZone.Hand);

                Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = drone.InstanceId }).Accepted);
                Assert.AreEqual("soi.mode", engine.PendingInput.Decision.Context);
                AnswerPending(engine, new[] { mode });

                Assert.AreEqual(mode == 1 ? 2 : 3, p0.Gems, "mode " + mode + " gem gain");
                Assert.AreEqual(mode == 2, drone.BanishAtCleanup, "only the greedy mode banishes itself");
            }
        }

        [Test]
        public void Duel_GrimTutor_ShuffleIsDeterministicPerSeed()
        {
            ulong Run()
            {
                var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 17);
                CompleteDraft(adapter);
                var engine = adapter.Inner;
                var p0 = engine.State.Players[0];
                var tutor = Plant(engine, p0, "grim_tutor", ShardsZone.Hand);
                Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = tutor.InstanceId }).Accepted);
                AnswerPending(engine, new[] { engine.PendingInput.Decision.Options[0].Id });
                return engine.State.ComputeFullHash();
            }
            Assert.AreEqual(Run(), Run(), "identical seed + submits reproduce the post-shuffle state hash");
        }

        [Test]
        public void Duel_DecimaDiscount_NeverProducesNegativeCost()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 8);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.CharacterId = "decima";
            p0.Mastery = 5;
            // Axia costs 8 with -1 per Homodeus champion; stack champions until the raw
            // cost would go below zero — EffectiveCost must clamp at 0.
            var axia = ShardsCardDatabase.Get("axia_duel");
            for (int i = 0; i < 12; i++)
            {
                var champ = engine.State.CenterDeck.Find(c => c.Def.Faction == ShardsFaction.Homodeus && c.Def.IsChampion);
                if (champ == null) break;
                engine.State.CenterDeck.Remove(champ);
                champ.Owner = 0;
                champ.Zone = ShardsZone.Champions;
                p0.Champions.Add(champ);
            }
            engine.State.InvalidateCardIndex();
            Assert.GreaterOrEqual(engine.EffectiveCost(p0, axia), 0, "cost clamps at 0");
        }

        [Test]
        public void Duel_Comet_CannotBeWarpedOrFastPlayed()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            // Plant Comet in row slot 0.
            var comet = engine.State.CenterDeck.Find(c => c.DefId == "comet");
            Assert.IsNotNull(comet, "comet in the duel pool");
            engine.State.CenterDeck.Remove(comet);
            var displaced = engine.State.CenterRow[0];
            if (displaced != null) { displaced.Zone = ShardsZone.CenterDeck; engine.State.CenterDeck.Add(displaced); }
            comet.Zone = ShardsZone.CenterRow;
            engine.State.CenterRow[0] = comet;
            engine.State.InvalidateCardIndex();

            // Unlimited Warp (Breaker/Star Seeker) must refuse it outright.
            Assert.IsFalse(engine.WarpFromRow(0, 0), "Comet can never be warped");
            Assert.AreSame(comet, engine.State.CenterRow[0], "comet stays in the row");
            // Fast-play purchase path refuses too (it is not a mercenary anyway).
            engine.State.Players[0].Gems = 20;
            Assert.IsFalse(engine.Submit(new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true }).Accepted);
            // A normal 14-gem BUY is the only way.
            Assert.IsTrue(engine.EffectiveCost(engine.State.Players[0], comet.Def) >= 13, "no free comet");
        }

        [Test]
        public void Duel_DoomGate_FloodsOnlyOncePerGame()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            int before = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count;
            // Grant + play Doom Gate twice (destroyed + replayed scenario).
            for (int round = 0; round < 2; round++)
            {
                var gate = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "doom_gate", Owner = 0, Zone = ShardsZone.Hand };
                p0.Hand.Add(gate);
                engine.State.InvalidateCardIndex();
                Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = gate.InstanceId }).Accepted);
                p0.Champions.Remove(gate); // simulate destruction between plays
            }
            int added = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count - before;
            Assert.AreEqual(30, added, "exactly ONE 30-Ingeminex flood despite two plays");
            Assert.IsTrue(p0.DoomGateFloodUsed);
        }

        [Test]
        public void Duel_DoomGate_DestroyingAnIngeminex_GrantsItsReward()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];

            // Torment: "Reward: gain 4 mastery" — an unambiguous, uncapped delta (the
            // +20 health rewards are invisible at a full 50/50).
            var monster = new ShardsCard
            {
                InstanceId = engine.State.NextInstanceId++,
                DefId = "ingeminex_torment",
                Zone = ShardsZone.CenterDeck
            };
            engine.State.ActiveMonsters.Clear();
            engine.State.ActiveMonsters.Add(monster);
            var gate = Plant(engine, p0, "doom_gate", ShardsZone.Champions);
            engine.State.InvalidateCardIndex();
            int masteryBefore = p0.Mastery;

            Assert.IsTrue(engine.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = gate.InstanceId }).Accepted);
            int guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 10)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));

            Assert.AreEqual(0, engine.State.ActiveMonsters.Count, "the Ingeminex was destroyed");
            Assert.AreEqual(masteryBefore + 4, p0.Mastery,
                "destroying an Ingeminex by effect must pay its reward, exactly like killing it with power");
        }

        [Test]
        public void Duel_Dominion_PrismAloneIsNotEnough_ThreeCardsAreRequired()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            // Hand: a Prism (counts as every faction) + an Order Initiate (Dominion card).
            var prism = engine.State.CenterDeck.Find(c => c.DefId == "prism");
            var initiate = engine.State.CenterDeck.Find(c => c.DefId == "order_initiate_duel");
            Assert.IsNotNull(prism); Assert.IsNotNull(initiate);
            foreach (var c in new[] { prism, initiate })
            {
                engine.State.CenterDeck.Remove(c);
                c.Owner = 0; c.Zone = ShardsZone.Hand; p0.Hand.Add(c);
            }
            // Empty the rest of the hand so no reveal can complete Dominion.
            foreach (var c in new List<ShardsCard>(p0.Hand))
                if (c != prism && c != initiate)
                { p0.Hand.Remove(c); c.Zone = ShardsZone.Discard; p0.Discard.Add(c); }
            engine.State.InvalidateCardIndex();

            int masteryBefore = p0.Mastery;
            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = prism.InstanceId }).Accepted);
            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = initiate.InstanceId }).Accepted);
            // Resolve any pending decisions (shop removal offer) with defaults.
            int guard = 0;
            while (adapter.PendingInput != null && adapter.PendingInput.Kind == PendingInputKind.Decision && guard++ < 10)
                adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput));
            // Prism = 1 other card covering all factions — Dominion (3 OTHER cards) must NOT fire.
            Assert.AreEqual(masteryBefore, p0.Mastery, "one wildcard card cannot satisfy the 3-card Dominion");
        }

        [Test]
        public void Duel_ValidateAnswer_RejectsDuplicateOptionIds()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            // Whisper Extractor's hand-pick: try answering with the same option twice.
            var extractor = engine.State.CenterDeck.Find(c => c.DefId == "whisper_extractor");
            engine.State.CenterDeck.Remove(extractor);
            extractor.Owner = 0; extractor.Zone = ShardsZone.Hand; p0.Hand.Add(extractor);
            engine.State.InvalidateCardIndex();
            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = extractor.InstanceId }).Accepted);
            Assert.IsNotNull(adapter.PendingInput);
            Assert.AreEqual(PendingInputKind.Decision, adapter.PendingInput.Kind);
            var request = adapter.PendingInput.Decision;
            var dup = new DecisionAnswer { DecisionId = request.Id };
            dup.ChosenOptionIds.Add(request.Options[0].Id);
            dup.ChosenOptionIds.Add(request.Options[0].Id);
            Assert.IsFalse(engine.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = dup }).Accepted,
                "duplicate option ids must be rejected outside the damage split");
        }

        [Test]
        public void Duel_SporeClericErrata_DoesNotUnifyWithItself()
        {
            var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: 13);
            CompleteDraft(adapter);
            var engine = adapter.Inner;
            var p0 = engine.State.Players[0];
            p0.Health = 30;
            var spore = engine.State.CenterDeck.Find(c => c.DefId == "spore_cleric_duel");
            engine.State.CenterDeck.Remove(spore);
            spore.Owner = 0; spore.Zone = ShardsZone.Hand; p0.Hand.Add(spore);
            // No other Undergrowth ally anywhere reachable: empty the hand of them.
            foreach (var c in new List<ShardsCard>(p0.Hand))
                if (c != spore && ShardsEngine.CountsAs(p0, c.Def, ShardsFaction.Undergrowth))
                { p0.Hand.Remove(c); c.Zone = ShardsZone.Discard; p0.Discard.Add(c); }
            engine.State.InvalidateCardIndex();
            Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = spore.InstanceId }).Accepted);
            Assert.AreEqual(33, p0.Health, "3 health only — the card never satisfies its own Unify");
        }

        [Test]
        public void Duel_RowReroll_RejectedWithoutDuelFlag()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon | ShardsDlc.RelicsOfTheFuture, players: 2, seed: 9).Inner;
            engine.State.Players[0].Gems = 4;
            var result = engine.Submit(new ShardsRerollRowAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.IsFalse(result.Accepted, "row reroll is a Duel-only rule");
        }

        [Test]
        public void Setup_ItHWithoutRotF_RemovesCorruption()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 11).Inner;
            foreach (var card in engine.State.CenterDeck)
                Assert.AreNotEqual("ingeminex_corruption", card.DefId,
                    "Corruption needs relics — removed without RotF");
            foreach (var card in engine.State.ActiveMonsters)
                Assert.AreNotEqual("ingeminex_corruption", card.DefId);
        }

        [Test]
        public void FullGame_HeuristiclessBots_AlwaysTerminates()
        {
            // Random-legal-action players must finish a real-cards game (the Infinity
            // Shard guarantees lethal at M30; last survivor or tie ends it).
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var adapter = NewGame(ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon,
                    players: 3, seed: seed);
                var engine = adapter.Inner;
                var rng = new System.Random((int)seed);
                int guard = 0;
                while (!engine.State.GameOver && guard++ < 20000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending, $"seed {seed}: no pending input but game not over (step {guard})");
                    Pascension.Engine.Actions.PlayerAction action;
                    if (pending.Kind == PendingInputKind.Decision)
                    {
                        action = adapter.DefaultActionFor(pending);
                    }
                    else
                    {
                        var legal = engine.LegalActions(pending.PlayerIndex);
                        // Bias: play everything, then buy/focus, end turn only when forced.
                        var plays = legal.FindAll(a => a is ShardsPlayCardAction);
                        var mid = legal.FindAll(a => a is ShardsBuyCardAction || a is ShardsFocusAction ||
                                                     a is ShardsExhaustAction || a is ShardsAttackChampionAction ||
                                                     a is ShardsAttackMonsterAction || a is ShardsTakeDestinyAction ||
                                                     a is ShardsRecruitRelicAction);
                        if (plays.Count > 0) action = plays[rng.Next(plays.Count)];
                        else if (mid.Count > 0 && rng.Next(3) > 0) action = mid[rng.Next(mid.Count)];
                        else action = new ShardsEndTurnAction { PlayerIndex = pending.PlayerIndex };
                    }
                    var result = adapter.Submit(action);
                    Assert.IsTrue(result.Accepted, $"seed {seed}: rejected {action.Describe()}: {result.Error}");
                }
                Assert.IsTrue(engine.State.GameOver, $"seed {seed}: game did not terminate in 20000 steps (round {engine.State.Round})");
            }
        }

        [Test]
        public void Duel_FullGame_RandomBots_ResolvesNewMechanicsAndTerminates()
        {
            // Duel-on random-legal-action games must finish, exercising the row reroll,
            // hero abilities, and every new decision (scry, hand-pick, mode, target).
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var adapter = NewGame(ShardsDlc.Duel, players: 2, seed: seed);
                var engine = adapter.Inner;
                var rng = new System.Random((int)seed * 37);
                int guard = 0;
                while (!engine.State.GameOver && guard++ < 20000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending, $"seed {seed}: no pending input but game not over (step {guard})");
                    Pascension.Engine.Actions.PlayerAction action;
                    if (pending.Kind == PendingInputKind.Decision)
                    {
                        action = adapter.DefaultActionFor(pending);
                    }
                    else
                    {
                        var legal = engine.LegalActions(pending.PlayerIndex);
                        var plays = legal.FindAll(a => a is ShardsPlayCardAction);
                        var mid = legal.FindAll(a => a is ShardsBuyCardAction || a is ShardsFocusAction ||
                            a is ShardsExhaustAction || a is ShardsAttackMonsterAction || a is ShardsTakeDestinyAction ||
                            a is ShardsRecruitRelicAction || a is ShardsRerollRowAction || a is ShardsHeroAbilityAction);
                        if (plays.Count > 0) action = plays[rng.Next(plays.Count)];
                        else if (mid.Count > 0 && rng.Next(3) > 0) action = mid[rng.Next(mid.Count)];
                        else action = new ShardsEndTurnAction { PlayerIndex = pending.PlayerIndex };
                    }
                    var result = adapter.Submit(action);
                    Assert.IsTrue(result.Accepted, $"seed {seed}: rejected {action.Describe()}: {result.Error}");
                }
                Assert.IsTrue(engine.State.GameOver, $"seed {seed}: Duel game did not terminate (round {engine.State.Round})");
            }
        }

        [Test]
        public void CardConservation_AfterManyTurns()
        {
            var adapter = NewGame(ShardsDlc.IntoTheHorizon, players: 2, seed: 99);
            var engine = adapter.Inner;
            int CountAll()
            {
                int n = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count + engine.State.Banished.Count;
                foreach (var slot in engine.State.CenterRow)
                    if (slot != null)
                        n++;
                foreach (var p in engine.State.Players)
                    n += p.Deck.Count + p.Hand.Count + p.Discard.Count + p.PlayZone.Count + p.Champions.Count;
                return n;
            }
            int before = CountAll();
            var rng = new System.Random(5);
            for (int step = 0; step < 600 && !engine.State.GameOver; step++)
            {
                var pending = adapter.PendingInput;
                if (pending.Kind == PendingInputKind.Decision)
                {
                    adapter.Submit(adapter.DefaultActionFor(pending));
                    continue;
                }
                var legal = engine.LegalActions(pending.PlayerIndex);
                legal.RemoveAll(a => a is Pascension.Engine.Actions.ConcedeAction);
                adapter.Submit(legal[rng.Next(legal.Count)]);
            }
            Assert.AreEqual(before, CountAll(), "no card created or lost across play (banish pile included)");
        }
    }
}
