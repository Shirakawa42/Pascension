using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Duel of Doom — the 2-player-focused pack (set id "duel"). Requires all
    /// three other DLCs (the engine forces them on). Adds new cards, new hero relics, and
    /// errata replacement defs (ReplacesId) that swap out base cards when the DLC is on.
    ///
    /// Rules-side new mechanics live in the engine: the Allegiance keyword
    /// (<see cref="AllegianceEffect"/>), the reworked Dominion, Scry, Prism's counts-as, the
    /// pay-2-gems row reroll. See the shards-cards / shards-engine skills for the registry.</summary>
    public static class ShardsDuelSet
    {
        private const string SET = "duel";
        private const ShardsFaction H = ShardsFaction.Homodeus;
        private const ShardsFaction U = ShardsFaction.Undergrowth;
        private const ShardsFaction O = ShardsFaction.Order;
        private const ShardsFaction W = ShardsFaction.Wraethe;
        private const ShardsFaction A = ShardsFaction.Aion;

        public static void Register()
        {
            RegisterRelics();
            RegisterCards();
            RegisterErrata();
        }

        // ---- New hero relics (one extra per hero; set aside like base relics) ----
        private static void RegisterRelics()
        {
            // Decima (Homodeus) — Ascension/economy relic (non-champion; shield from hand).
            SoiCard.New("praetorian_03", "Praetorian-03").InSet(SET).Faction(H)
                .Type(ShardsCardType.Relic).Character("decima").Qty(1).Shield(4)
                .Plays(new BestByMastery(
                    (0, E.Mix(mastery: 1, draw: 1)),
                    (15, E.Mix(mastery: 2, draw: 2)),
                    (25, E.Mix(mastery: 3, draw: 3))))
                .Text("Gain 1 mastery and draw a card. M15: 2 mastery and 2 cards instead. M25: 3 mastery and 3 cards instead.")
                .Art("an ancient relic warframe helm crowned with a halo of data-glyphs, blue energy conduits, kneeling in a shrine of light").Register();

            // Tetra (Order) — faction-spread engine relic.
            SoiCard.New("multitask_brain", "Multitask Brain").InSet(SET).Faction(O)
                .Type(ShardsCardType.Relic).Character("tetra").Qty(1)
                .Plays(E.Seq(
                    new BestByMastery(
                        (0, new PerCount(ctx => ShardsDuel.DistinctFactionsPlayed(ctx.Controller), power: 2, draw: 1)),
                        (20, new PerCount(ctx => ShardsDuel.DistinctFactionsPlayed(ctx.Controller), power: 4, draw: 1))),
                    new Dominion(E.Mastery(3))))
                .Text("For each different faction you played this turn, gain 2 power and draw a card.\nM20: gain 4 power instead of 2.\nDominion: gain 3 mastery.")
                .Art("a floating multi-lobed cybernetic brain wired into five glowing processor cores of different colors, streams of parallel thought").Register();

            // Volos (Undergrowth) — Bastion relic CHAMPION (Defense > 0).
            var unknownGod = SoiCard.New("unknown_god", "Unknown God").InSet(SET).Faction(U)
                .Type(ShardsCardType.Relic).Character("volos").Qty(1).Defense(5)
                .Exhausts(new PerCount(ctx => ctx.Controller.Champions.Count, health: 5))
                .Text("Exhaust: gain 5 health for each champion you control.\nM20: your Exhaust effects apply twice.")
                .Art("a colossal faceless deity of living wood and crystal rising from an overgrown temple, countless roots and vines, radiant green life-energy");
            unknownGod.Def.DoublesExhaustsAtMastery = 20;
            unknownGod.Register();

            // Rez (Aion) — Onslaught relic CHAMPION: unlimited Warp.
            SoiCard.New("star_seeker", "Star Seeker").InSet(SET).Faction(A)
                .Type(ShardsCardType.Relic).Character("rez").Qty(1).Defense(7)
                .Exhausts(E.Seq(new WarpUpTo(-1), E.At(20, new WarpUpTo(-1))))
                .Text("Exhaust: Warp ∞. M20: Warp ∞ a second time.")
                .Art("a radiant navigator's astrolabe orbited by streaking comets and folded space, threads of teleport light converging on a single crimson star").Register();

            // Ko Syn Wu (Wraethe) — Bastion/monster relic CHAMPION.
            var doomGate = SoiCard.New("doom_gate", "Doom Gate").InSet(SET).Faction(W)
                .Type(ShardsCardType.Relic).Character("kosynwu").Qty(1).Defense(6)
                .Plays(new Custom(DoomGatePlay))
                .Exhausts(new Custom(DoomGateDestroy))
                .Text("You are unaffected by Ingeminex attacks.\nWhen you play this champion, shuffle 30 new Ingeminex into the center deck. Once per game.\nExhaust: destroy an Ingeminex.")
                .Art("a towering obsidian gateway wreathed in violet void-fire, monstrous silhouettes pressing against a rippling portal");
            doomGate.Def.ImmuneToIngeminex = true;
            doomGate.Register();
        }

        // ---- New center-deck cards ----
        private static void RegisterCards()
        {
            // ----- Homodeus -----
            var testudo = SoiCard.New("testudo_vanguard", "Testudo Vanguard").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(2).Defense(6)
                .Exhausts(E.Gems(2))
                .Text("Your shields are also applied to each of your champions individually.\nExhaust: gain 2 gems.")
                .Art("a legionnaire locking tower shields into a glowing testudo wall");
            testudo.Def.ShieldsProtectChampions = true; // engine: deferred champion hits resolve after the shield reveal
            testudo.Register();

            SoiCard.New("century_forge", "Century Forge").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(5).Qty(2).Shield(3)
                .Plays(E.Seq(E.Gems(3), If.Inspire(new Do(ctx => ctx.Controller.NextChampionsIntoPlay++))))
                .Text("Shield 3. Gain 3 gems.\nInspire: the next champion you recruit this turn enters play directly.")
                .Art("a forge-hall stamping out armored soldiers on a conveyor of sparks").Register();

            SoiCard.New("riposte_doctrine", "Riposte Doctrine").InSet(SET).Faction(H)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(2)
                .Plays(E.Seq(E.Power(3), new Custom(RiposteBonus)))
                .Text("Gain 3 power.\nIf you played or reveal a shield card from your hand, gain 6 power instead.")
                .Art("a battered duelist turning a parry into a counter-thrust, banner torn").Register();

            // ----- Order -----
            SoiCard.New("index_of_futures", "Index of Futures").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(2)
                .Plays(E.Seq(E.Draw(1), new ReorderCenterTop(3)))
                .Text("Shield 2. Draw a card. Look at the center deck's top 3 cards; put them back in any order.")
                .Art("a librarian monk leafing through a floating card catalogue of tomorrows").Register();

            SoiCard.New("bulwark_chanter", "Bulwark Chanter").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(4).Qty(2).Shield(4)
                .Plays(E.Seq(E.Gems(2), new Dominion(E.Mix(mastery: 2, draw: 1))))
                .Text("Shield 4. Gain 2 gems.\nDominion: gain 2 mastery and draw a card.")
                .Art("an armored cantor whose hymn raises a rampart of translucent glyphs").Register();

            SoiCard.New("aegis_archivist", "Aegis Archivist").InSet(SET).Faction(O)
                .Type(ShardsCardType.Champion).Cost(4).Qty(1).Defense(5)
                .Exhausts(E.Seq(E.Gems(2), new Dominion(E.Gems(3))))
                .Text("Exhaust: gain 2 gems.\nDominion: gain 5 gems instead.")
                .Art("a curator of glowing bucklers filed like books, one drawn half-open").Register();

            // ----- Undergrowth -----
            SoiCard.New("thornshell_warden", "Thornshell Warden").InSet(SET).Faction(U)
                .Type(ShardsCardType.Champion).Cost(2).Qty(2).Defense(6)
                .Exhausts(E.Seq(E.Health(2), If.FullHealth(E.Power(2))))
                .Text("Exhaust: gain 2 health.\nIf you are at 50 health, also gain 2 power.")
                .Art("a turtle-shelled forest guardian curled behind interlocking bark plates").Register();

            SoiCard.New("nectar_alchemist", "Nectar Alchemist").InSet(SET).Faction(U)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2)
                .Plays(E.Seq(new Do(ctx => ctx.Controller.OverflowHealthToPowerThisTurn = true), E.Health(4)))
                .Text("Gain 4 health.\nThis turn, health you would gain beyond the 50 cap becomes power instead.")
                .Art("a druid distilling overflowing golden sap into a blade-shaped vial").Register();

            SoiCard.New("lifebloom_ritual", "Lifebloom Ritual").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(6).Qty(1)
                .Plays(E.Seq(new Do(ctx => ctx.Controller.HealingDoubledThisTurn = true), new Unify(E.Health(4))))
                .Text("Until end of turn, all healing you receive is doubled.\nUnify: gain 4 health.")
                .Art("a fallen colossus re-sprouting from a ring of luminous flowers").Register();

            // ----- Wraethe -----
            // whisper_extractor lived here (draw-then-strip hand attack) — removed
            // 2026-08-02, user decision: too strong even after the redraw nerf.
            SoiCard.New("doomstalker", "Doomstalker").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(5).Qty(2)
                .Plays(E.Seq(E.Power(5), new If(ctx => ctx.Engine.State.ActiveMonsters.Count > 0, E.Power(3))))
                .Text("Gain 5 power.\nIf an Ingeminex is in play, gain 8 instead.")
                .Art("a void hunter stalking a titanic silhouette across a broken plain").Register();

            SoiCard.New("grim_tutor", "Grim Tutor").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(2)
                .Plays(new Custom(GrimTutor))
                .Text("Search your draw pile for a card and put it into your hand, then shuffle.\nYou lose 3 health.")
                .Art("a gaunt black-robed sage tracing a pact in blood over a floating open grimoire, skulls and candle smoke, sickly green light").Register();

            SoiCard.New("bleak_communion", "Bleak Communion").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(2)
                .Plays(new Custom(BleakCommunion))
                .Text("Lose 4 health — a loss, not damage.\nDraw two cards.\nEcho: an opponent loses that health instead of you.")
                .Art("cultists sharing a chalice of black light, veins glowing").Register();

            // ----- Aion -----
            var comet = SoiCard.New("comet", "Comet").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(14).Qty(1)
                .Plays(new Custom(DestroyOpponent))
                .Text("Destroy target opponent.\nCannot be fast-played — it must be bought with gems.\nCannot be removed from the shop.")
                .Art("a crimson comet-rider streaking low over a row of market stalls");
            comet.Def.CannotBeRerolled = true;
            comet.Def.CannotBeFastPlayed = true; // no Warp/Longshot/free-play path may take it
            comet.Register();

            var prism = SoiCard.New("prism", "Prism").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(2)
                .Plays(E.Draw(1))
                .Text("Shield 2. Draw a card. This card counts as a card of every faction.")
                .Art("a faceted crystal wanderer refracting five colors of light");
            prism.Def.CountsAsEveryFaction = true;
            prism.Register();

            SoiCard.New("longshot", "Longshot").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(4).Qty(2)
                .Plays(new Custom(Longshot))
                .Text("Reveal the top 2 cards of the center deck.\nYou may fast-play any of them that are allies costing 3 or less; the rest go under the center deck.\nM15: cost 5 or less instead.")
                .Art("a red-cloaked gambler flipping the top card of an endless deck").Register();
        }

        // ---- Errata: full replacement defs (ReplacesId swaps out the base card when Duel
        // is on). Faithful re-authoring of each base effect with the change applied. ----
        private static void RegisterErrata()
        {
            RegisterAllegianceErrata();
            RegisterStatErrata();
            RegisterHookErrata();
            RegisterBespokeErrata();
            RegisterDestinyErrata();
        }

        // Hero-conditional cards → the Allegiance keyword (no hero cards in the shop).
        private static void RegisterAllegianceErrata()
        {
            var ferrata = SoiCard.New("ferrata_guard_duel", "Ferrata Guard").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(2).Defense(4)
                .Exhausts(new PerCount(ctx => ShardsBaseSet.CountChampions(ctx, H), gems: 1))
                .Text("Allegiance Homodeus 4: your champions get +2 defense.\nExhaust: gain 1 gem per Homodeus champion you control.");
            ferrata.Def.ReplacesId = "ferrata_guard";
            ferrata.Def.DefenseAura = (owner, source, champion) =>
                AllegianceEffect.OwnedCount(owner, H) >= 4 ? 2 : 0;
            ferrata.Register();

            SoiCard.New("mainframe_abbot_duel", "Mainframe Abbot").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(3)
                .Plays(E.Seq(E.Draw(1), new AllegianceEffect(O, 4, E.Mastery(1))))
                .Text("Shield 3. Draw a card.\nAllegiance Order 4: gain 1 mastery.")
                .Replaces("mainframe_abbot").Register();

            SoiCard.New("hounds_of_volos_duel", "Hounds of Volos").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(2)
                .Plays(E.Seq(E.Health(5), new AllegianceEffect(U, 4, E.Power(5))))
                .Text("Gain 5 health.\nAllegiance Undergrowth 4: also gain 5 power.")
                .Replaces("hounds_of_volos").Register();

            SoiCard.New("the_lost_duel", "The Lost").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(2)
                .Plays(E.Seq(E.Power(5), new AllegianceEffect(W, 4, new BanishUpTo(1))))
                .Text("Gain 5 power.\nAllegiance Wraethe 4: you may banish a card from your hand or discard pile.")
                .Replaces("the_lost").Register();
        }

        // Simple stat/number tweaks — base effect re-authored with changed values.
        private static void RegisterStatErrata()
        {
            SoiCard.New("korvus_legionnaire_duel", "Korvus Legionnaire").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(3).Qty(3).Shield(2)
                .Plays(E.Seq(E.Power(3), new ReturnFromDiscard(d => d.IsChampion, "champion")))
                .Text("Shield 2. Gain 3 power.\nReturn a champion from your discard pile to your hand.")
                .Replaces("korvus_legionnaire").Register();

            SoiCard.New("furrowing_elemental_duel", "Furrowing Elemental").InSet(SET).Faction(U)
                .Type(ShardsCardType.Ally).Cost(5).Qty(2)
                .Plays(E.Seq(E.Mix(health: 4, draw: 1), If.FullHealth(E.Power(4))))
                .Text("Gain 4 health and draw a card.\nIf you are at 50 health, gain 4 power.")
                .Replaces("furrowing_elemental").Register();

            SoiCard.New("shardwood_guardian_duel", "Shardwood Guardian").InSet(SET).Faction(U)
                .Type(ShardsCardType.Ally).Cost(4).Qty(3)
                .Plays(E.Seq(E.Mix(power: 2, draw: 1), new Unify(E.Health(4))))
                .Text("Gain 2 power and draw a card.\nUnify: gain 4 health.")
                .Replaces("shardwood_guardian").Register();

            SoiCard.New("undergrowth_aspirant_duel", "Undergrowth Aspirant").InSet(SET).Faction(U)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Health(3), new Unify(E.Power(4))))
                .Text("Gain 3 health.\nUnify: also gain 4 power.")
                .Replaces("undergrowth_aspirant").Register();

            SoiCard.New("command_seer_duel", "Command Seer").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(4).Qty(2).Shield(5)
                .Plays(E.Gems(3))
                .Text("Shield 5. Gain 3 gems.")
                .Replaces("command_seer").Register();

            SoiCard.New("wraethe_skirmisher_duel", "Wraethe Skirmisher").InSet(SET).Faction(W)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Power(2), If.Echo(E.Power(3))))
                .Text("Gain 2 power.\nEcho: gain 5 instead.")
                .Replaces("wraethe_skirmisher").Register();

            SoiCard.New("root_of_the_forest_duel", "Root of the Forest").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(7).Qty(1)
                .Plays(E.Seq(E.Health(8), new Unify(E.Power(8))))
                .Text("Gain 8 health.\nUnify: gain 8 power.")
                .Replaces("root_of_the_forest").Register();

            SoiCard.New("data_heretic_duel", "Data Heretic").InSet(SET).Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(3)
                .Plays(E.Draw(2))
                .Text("Draw two cards.")
                .Replaces("data_heretic").Register();

            SoiCard.New("nil_assassin_duel", "Nil Assassin").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Power(4))
                .Text("Gain 4 power.")
                .Replaces("nil_assassin").Register();

            SoiCard.New("orm_madu_duel", "Orm Madu").InSet(SET).Faction(U)
                .Type(ShardsCardType.Champion).Cost(7).Qty(1).Defense(7)
                .Exhausts(E.Seq(E.Health(6), If.FullHealth(E.Mastery(1))))
                .Text("Exhaust: gain 6 health.\nIf you are then at 50 health, gain 1 mastery.")
                .Replaces("orm_madu").Register();

            SoiCard.New("j_chord_duel", "J-Chord").InSet(SET).Faction(A)
                .Type(ShardsCardType.Champion).Cost(3).Qty(1).Defense(3)
                .Exhausts(new BestByMastery((0, new WarpUpTo(3)), (15, new WarpUpTo(6))))
                .Text("Exhaust — Warp 3.\nM15: Warp 6 instead.")
                .Replaces("j_chord").Register();

            SoiCard.New("the_rotten_duel", "The Rotten").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Mix(power: 4, mastery: 1))
                .Text("Gain 4 power and 1 mastery.")
                .Replaces("the_rotten").Register();

            SoiCard.New("heart_of_nothing_duel", "The Heart of Nothing").InSet(SET).Faction(W)
                .Type(ShardsCardType.Relic).Character("kosynwu").Qty(1)
                .Plays(E.Seq(E.Power(6), new Do(ctx => ctx.Controller.BonusDrawsOnBigHit = 3), E.At(20, E.Power(4))))
                .Text("Gain 6 power.\nM20: gain 10 instead.\nIf you deal 10+ unprevented damage to one opponent this turn, draw 3 extra cards at end of turn.")
                .Replaces("heart_of_nothing").Register();

            SoiCard.New("terminal_crescents_duel", "Terminal Crescents").InSet(SET).Faction(O)
                .Type(ShardsCardType.Relic).Character("tetra").Qty(1)
                .Plays(E.Seq(E.Mastery(2), new BestByMastery(
                    (0, new PerCount(ctx => (ctx.Controller.Mastery + 1) / 2, power: 1)),
                    (20, new PerCount(ctx => ctx.Controller.Mastery, power: 1)))))
                .Text("Gain 2 mastery, then power equal to half your mastery, rounded up.\nM20: equal to your full mastery.")
                .Replaces("terminal_crescents").Register();

            SoiCard.New("slipstream_shard_duel", "Slipstream Shard").InSet(SET).Faction(A)
                .Type(ShardsCardType.Relic).Character("rez").Qty(1)
                .Plays(E.Seq(E.Mix(mastery: 2, draw: 2), E.At(20, new Custom(ExtraTurn))))
                .Text("Gain 2 mastery and draw two cards.\nM20: take an extra turn. Once per game.")
                .Replaces("slipstream_shard").Register();
        }

        // Hero-agnostic / hook changes.
        private static void RegisterHookErrata()
        {
            SoiCard.New("evokatus_duel", "Evokatus").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(2).Defense(2)
                .Plays(E.Draw(1))
                .Exhausts(new PerCount(ctx => ctx.Controller.Champions.Count, power: 1))
                .Text("When played, draw a card.\nExhaust: gain 1 power per champion you control.")
                .Replaces("evokatus").Register();

            SoiCard.New("primus_pilus_duel", "Primus Pilus").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(2).Qty(1).Defense(5)
                .Exhausts(new If(ctx => ctx.Controller.Champions.Count >= 3, E.Draw(2)))
                .Text("Exhaust: if you control three or more champions, draw two cards.")
                .Replaces("primus_pilus").Register();

            var liHin = SoiCard.New("li_hin_duel", "Li Hin, The Shattered").InSet(SET).Faction(W)
                .Type(ShardsCardType.Champion).Cost(3).Qty(1).Defense(1)
                .Exhausts(E.Power(2))
                .Text("Can't be attacked with power. Card effects can still destroy it.\nExhaust: gain 2 power.")
                .Replaces("li_hin");
            liHin.Def.CanBeAttacked = (state, attacker, owner, card) => false;
            liHin.Register();

            var axia = SoiCard.New("axia_duel", "Axia").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(8).Qty(1).Defense(7)
                .Exhausts(E.Power(7))
                .Text("Costs 1 less per Homodeus champion you control.\nExhaust: gain 7 power.")
                .Replaces("axia");
            axia.Def.CostModifier = owner =>
            {
                int n = 0;
                foreach (var c in owner.Champions) if (c.Def.Faction == H) n++;
                return -n;
            };
            axia.Register();

            SoiCard.New("ru_bo_vai_duel", "Ru Bo Vai, The Transcendant").InSet(SET).Faction(W)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(4)
                .Exhausts(E.Seq(E.Power(3), E.At(10, new Do(ctx => ctx.Controller.IgnoreShieldsThisTurn = true))))
                .Text("Exhaust: gain 3 power.\nM10: your damage ignores shields this turn.")
                .Replaces("ru_bo_vai").Register();
        }

        // Bespoke reworks (their own flows).
        private static void RegisterBespokeErrata()
        {
            SoiCard.New("reactor_drone_duel", "Reactor Drone").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(3).Qty(3).Shield(2)
                .Plays(new Custom(ReactorChoice))
                .Text("Choose one:\n— Gain 2 gems.\n— Gain 3 gems, then banish this card at the end of your turn.")
                .Replaces("reactor_drone").Register();

            SoiCard.New("order_initiate_duel", "Order Initiate").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(new Custom(RemoveFromShop), E.Gems(2), new Dominion(E.Mastery(2))))
                .Text("You may remove a card from the shop. Gain 2 gems. Dominion: gain 2 mastery.")
                .Replaces("order_initiate").Register();

            // "3, Unify: 6 instead" = base 3 + Unify(+3) — reuses the standard Unify
            // (self never counts; hand-reveal alternative offered).
            SoiCard.New("spore_cleric_duel", "Spore Cleric").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Seq(E.Health(3), new Unify(E.Health(3))))
                .Text("Gain 3 health.\nUnify: gain 6 health instead.")
                .Replaces("spore_cleric").Register();

            SoiCard.New("warpquartz_duel", "Warpquartz").InSet(SET).Faction(A)
                .Type(ShardsCardType.Relic).Character("rez").Qty(1)
                .Plays(new Custom(WarpquartzDuel))
                .Text("You may banish a card from your hand or discard pile to gain its effect.\nM20: up to 3 cards instead.\nGain 3 gems and 3 power for each card you banished this turn.")
                .Replaces("warpquartz").Register();

            SoiCard.New("duplication_fabricator_duel", "Duplication Fabricator").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2)
                .Plays(E.Seq(E.Mastery(1), new Custom(FabricatorDuel)))
                .Text("Gain 1 mastery.\nEvery player reveals their deck's top card; copy the effect of one revealed ally.\nM20: you may copy any number of effects from the revealed cards instead.")
                .Replaces("duplication_fabricator").Register();

            SoiCard.New("dash_duel", "Dash").InSet(SET).Faction(A)
                .Type(ShardsCardType.Ally).Cost(2).Qty(2).Shield(2)
                .Plays(E.Seq(new Custom(DashDuel), E.Draw(1)))
                .Text("Shield 2.\nYou may put an Aion card from your discard pile on top of your deck.\nM10: any card instead.\nDraw a card.")
                .Replaces("dash").Register();

            var swyft = SoiCard.New("swyft_duel", "Swyft").InSet(SET).Faction(A)
                .Type(ShardsCardType.Champion).Cost(5).Qty(2).Defense(3)
                .Exhausts(E.Mix(gems: 2, power: 2))
                .Text("Exhaust: gain 2 gems and 2 power.\nM10: you may keep cards you fast-play — they join your discard.")
                .Replaces("swyft");
            swyft.Def.KeepFastPlaysAtMastery = 10;
            swyft.Register();

            SoiCard.New("world_piercer_duel", "The World Piercer").InSet(SET).Faction(W)
                .Type(ShardsCardType.Relic).Character("kosynwu").Qty(1)
                .Plays(E.Seq(E.Mastery(2), new Custom(WorldPiercerDuel)))
                .Text("Gain 2 mastery.\nReturn a mercenary from your discard or draw pile to your hand.\nM20: return ALL of them.")
                .Replaces("world_piercer").Register();

            var prae02 = SoiCard.New("praetorian_02_duel", "Praetorian-02").InSet(SET).Faction(H)
                .Type(ShardsCardType.Relic).Character("decima").Qty(1).Defense(9).Shield(3)
                .Exhausts(new Do(ctx => ctx.Controller.ShieldsDoubledUntilNextTurn = true))
                .ExhaustCosts(3)
                .Text("While in play: shield 3.\nM20: shield 6 instead.\nExhaust, pay 3 gems: until your next turn, your shields are doubled. Killing this champion does not remove this effect.")
                .Replaces("praetorian_02");
            prae02.Def.ShieldInPlay = true;
            prae02.Def.DynamicShield = owner => owner.Mastery >= 20 ? 6 : 3;
            prae02.Register();

            var robes = SoiCard.New("datic_robes_duel", "Datic Robes").InSet(SET).Faction(O)
                .Type(ShardsCardType.Relic).Character("tetra").Qty(1).Shield(1)
                .Plays(E.Draw(2))
                .Text("Shield equal to your mastery. Draw two cards.\nM20: while this card is in your discard pile, you have shield equal to half your mastery, rounded up.")
                .Replaces("datic_robes");
            robes.Def.DynamicShield = owner => owner.Mastery;
            robes.Def.DiscardPassiveShield = owner => owner.Mastery >= 20 ? (owner.Mastery + 1) / 2 : 0;
            robes.Register();

            SoiCard.New("cinder_scars_duel", "Cinder Scars").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(5)
                .Plays(E.Seq(E.Draw(1),
                    new If(ctx => ctx.Controller.PlayedThisTurn.Exists(c =>
                        c.DefId == "cinder_scars_duel" && c != ctx.Source), E.Power(3)),
                    E.At(10, new BanishUpTo(1))))
                .Text("Draw a card.\nIf you played another Cinder Scars this turn, gain 3 power.\nM10: you may banish a card from your hand or discard pile.")
                .Replaces("cinder_scars").Register();

            SoiCard.New("legion_carrier_duel", "Legion Carrier").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(2).Qty(2)
                .Plays(E.Seq(E.Gems(2), new Custom(ctx => RevealTopForChampion(ctx, 5))))
                .Text("Gain 2 gems.\nReveal your deck's top 5 cards: up to one revealed champion to your hand, the rest to your discard pile.")
                .Replaces("legion_carrier").Register();
        }

        // Destiny errata (Type Destiny — swapped in the destiny deal, gated by set).
        private static void RegisterDestinyErrata()
        {
            SoiCard.New("agony_of_choice_duel", "The Agony of Choice").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ShardsDuel.DistinctFactionsPlayed(ctx.Controller) >= 3 &&
                                        ShardsDuel.PlayedFactionCards(ctx.Controller) >= 3, E.Power(5)))
                .Text("Exhaust: if you played cards of 3+ different factions this turn, gain 5 power.")
                .Replaces("agony_of_choice").Register();

            SoiCard.New("datic_secrets_duel", "Datic Secrets").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ctx.Controller.FactionAllyPlays(O) >= 2, E.Mix(gems: 1, mastery: 1)))
                .Text("Exhaust: if you played 2+ Order allies this turn, gain 1 gem and 1 mastery.")
                .Replaces("datic_secrets").Register();

            SoiCard.New("healing_hands_duel", "Healing Hands").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ctx.Controller.PlayedThisTurn.Exists(c => c.Def.IsChampion), E.Health(5)))
                .Text("Exhaust: if you played a champion this turn, gain 5 health.")
                .Replaces("healing_hands").Register();

            SoiCard.New("paradigm_shift_duel", "Paradigm Shift").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ctx.Controller.FactionPlays(O) > 0 && ctx.Controller.FactionPlays(W) > 0,
                    E.Mix(gems: 1, mastery: 1)))
                .Text("Exhaust: if you played an Order card and a Wraethe card this turn, gain 1 gem and 1 mastery.")
                .Replaces("paradigm_shift").Register();

            SoiCard.New("soul_syphon_duel", "Soul Syphon").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ShardsDuel.DistinctFactionsPlayed(ctx.Controller) >= 3 &&
                                        ShardsDuel.PlayedFactionCards(ctx.Controller) >= 3, E.Health(7)))
                .Text("Exhaust: if you played cards of 3+ different factions this turn, gain 7 health.")
                .Replaces("soul_syphon").Register();

            SoiCard.New("the_last_city_duel", "The Last City").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new If(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    c.Def.Type == ShardsCardType.Mercenary).Count >= 2, E.Gems(3)))
                .Text("Exhaust: if you played 2+ mercenaries this turn, gain 3 gems.")
                .Replaces("the_last_city").Register();

            SoiCard.New("deadly_recruits_duel", "Deadly Recruits").InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                .Exhausts(new BestByMastery(
                    (0, new Custom(ctx => DeadlyRecruitsDuel(ctx, 2))),
                    (20, new Custom(ctx => DeadlyRecruitsDuel(ctx, 4)))))
                .Text("Exhaust: fast-play a cost-2 or less ally from the row for free. You may keep it.\nM20: cost 4 or less.")
                .Replaces("deadly_recruits").Register();
        }

        // ----- bespoke card bodies -----

        private static IEnumerable<ShardsStep> ExtraTurn(ShardsContext ctx)
        {
            var player = ctx.Controller;
            if (player.ExtraTurnUsed) yield break;
            player.ExtraTurnUsed = true;
            ctx.Engine.State.ExtraTurnForPlayer = player.Index;
        }

        private static IEnumerable<ShardsStep> ReactorChoice(ShardsContext ctx)
        {
            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseMode,
                Title = "Reactor Drone: choose one",
                Context = "soi.mode",
                Min = 1,
                Max = 1
            };
            req.Options.Add(new DecisionOption(1, "Gain 2 gems"));
            req.Options.Add(new DecisionOption(2, "Gain 3 gems, then banish this card"));
            yield return ShardsStep.AwaitDecision(req);
            if (ctx.Answer.ChosenOptionIds.Count > 0 && ctx.Answer.ChosenOptionIds[0] == 2)
            {
                ctx.Engine.GainGems(ctx.ControllerIndex, 3);
                // "Banish THIS card at the end of your turn": only when the resolving
                // source really is the drone (a COPY — Ojas/Fabricator/Warpquartz — has
                // no card of its own to banish, and must never banish the copier).
                if (ctx.Source != null && ctx.Source.DefId == "reactor_drone_duel" &&
                    ctx.Controller.PlayZone.Contains(ctx.Source))
                    ctx.Source.BanishAtCleanup = true;
            }
            else
            {
                ctx.Engine.GainGems(ctx.ControllerIndex, 2);
            }
        }

        private static IEnumerable<ShardsStep> RemoveFromShop(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var slots = new List<int>();
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
                if (engine.State.CenterRow[s] != null && !engine.State.CenterRow[s].Def.CannotBeRerolled)
                    slots.Add(s);
            if (slots.Count == 0) yield break;
            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Remove a card from the shop?",
                Context = "soi.removeshop",
                Min = 0,
                Max = 1
            };
            foreach (int s in slots)
            {
                var card = engine.State.CenterRow[s];
                req.Options.Add(new DecisionOption(s, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            }
            yield return ShardsStep.AwaitDecision(req);
            if (ctx.Answer.ChosenOptionIds.Count > 0)
                engine.BottomRowCardAndRefill(ctx.Answer.ChosenOptionIds[0]);
        }

        /// <summary>Riposte Doctrine: +3 more power if a shield card was PLAYED this turn,
        /// or by REVEALING one from hand now (session wording: "played or revealed a
        /// shield from your hand").</summary>
        private static IEnumerable<ShardsStep> RiposteBonus(ShardsContext ctx)
        {
            var player = ctx.Controller;
            if (player.PlayedThisTurn.Exists(c => c != ctx.Source && c.Def.Shield > 0))
            {
                ctx.Engine.GainPower(player.Index, 3);
                yield break;
            }
            var shields = player.Hand.FindAll(c => ctx.Engine.ShieldValue(player, c) > 0);
            if (shields.Count == 0) yield break;
            var req = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Reveal a shield card to gain 6 power instead?",
                Context = "soi.reveal",
                Min = 0,
                Max = 1
            };
            foreach (var card in shields)
                req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(req);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = player.Hand.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen == null) yield break;
            ctx.Engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = new List<string> { chosen.DefId } });
            ctx.Engine.GainPower(player.Index, 3);
        }

        private static IEnumerable<ShardsStep> WarpquartzDuel(ShardsContext ctx)
        {
            var player = ctx.Controller;
            int max = player.Mastery >= 20 ? 3 : 1;
            var candidates = new List<ShardsCard>();
            foreach (var card in player.Hand) if (!card.Def.IsChampion) candidates.Add(card);
            foreach (var card in player.Discard) if (!card.Def.IsChampion) candidates.Add(card);
            var chosen = new List<ShardsCard>();
            if (candidates.Count > 0)
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Banish up to {max} card(s) from your hand/discard to gain their effects",
                    Context = "soi.banish",
                    Min = 0,
                    Max = max
                };
                foreach (var card in candidates)
                {
                    string zone = card.Zone == ShardsZone.Hand ? "hand" : "discard";
                    req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name + " (" + zone + ")") { CardInstanceId = card.InstanceId, DefId = card.DefId });
                }
                yield return ShardsStep.AwaitDecision(req);
                foreach (int id in ctx.Answer.ChosenOptionIds)
                {
                    var card = candidates.Find(c => c.InstanceId == id);
                    if (card != null) chosen.Add(card);
                }
            }
            foreach (var card in chosen)
                ctx.Engine.Banish(card, card.Zone == ShardsZone.Hand ? player.Hand : player.Discard);
            // "For each card you banished THIS TURN" — the running per-turn counter
            // (Shadow Apostle etc. earlier in the turn count too), not just its own.
            int banishedThisTurn = player.CardsBanishedThisTurn;
            ctx.Engine.GainGems(player.Index, 3 * banishedThisTurn);
            ctx.Engine.GainPower(player.Index, 3 * banishedThisTurn);
            foreach (var card in chosen)
                if (card.Def.PlayEffect != null)
                    foreach (var step in card.Def.PlayEffect.Resolve(ctx))
                        yield return step;
        }

        private static IEnumerable<ShardsStep> FabricatorDuel(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var state = engine.State;
            var revealed = new List<ShardsCard>();
            var defIds = new List<string>();
            for (int step = 0; step < state.Players.Count; step++)
            {
                var p = state.Players[(state.TurnPlayerIndex + step) % state.Players.Count];
                if (p.Eliminated) continue;
                var top = engine.PeekTopOfDeck(p);
                if (top == null) continue;
                revealed.Add(top);
                defIds.Add(top.DefId);
            }
            if (defIds.Count > 0)
                engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = ctx.ControllerIndex, DefIds = defIds });
            var copyable = revealed.FindAll(c => !c.Def.IsChampion && c.Def.PlayEffect != null && !ctx.InCopyChain(c));
            if (copyable.Count == 0) yield break;

            bool multi = ctx.Controller.Mastery >= 20;
            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = multi ? "Copy the effects of any revealed allies" : "Copy the effect of a revealed ally",
                Context = "soi.copy",
                Min = multi ? 0 : 1,
                Max = multi ? copyable.Count : 1
            };
            foreach (var card in copyable)
                req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(req);
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var chosen = copyable.Find(c => c.InstanceId == id);
                if (chosen == null || ctx.InCopyChain(chosen)) continue;
                ctx.MarkCopied(chosen);
                foreach (var stp in chosen.Def.PlayEffect.Resolve(ctx))
                    yield return stp;
            }
        }

        private static IEnumerable<ShardsStep> DashDuel(ShardsContext ctx)
        {
            var player = ctx.Controller;
            bool anyCard = player.Mastery >= 10;
            var candidates = player.Discard.FindAll(c => anyCard || c.Def.Faction == A);
            if (candidates.Count == 0) yield break;
            var req = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = anyCard ? "Put a card from your discard pile on top of your deck?"
                                : "Put an Aion card from your discard pile on top of your deck?",
                Context = "soi.return",
                Min = 0,
                Max = 1
            };
            foreach (var card in candidates)
                req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(req);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = player.Discard.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen == null) yield break;
            player.Discard.Remove(chosen);
            chosen.Zone = ShardsZone.Deck;
            player.Deck.Add(chosen);
            ctx.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = chosen.InstanceId, DefId = chosen.DefId });
        }

        private static IEnumerable<ShardsStep> WorldPiercerDuel(ShardsContext ctx)
        {
            var player = ctx.Controller;
            bool all = player.Mastery >= 20;
            var matches = new List<ShardsCard>();
            foreach (var c in player.Discard) if (c.Def.Type == ShardsCardType.Mercenary) matches.Add(c);
            foreach (var c in player.Deck) if (c.Def.Type == ShardsCardType.Mercenary) matches.Add(c);
            if (matches.Count == 0) yield break;
            // Deterministic presentation order — never the DECK order (the owner's own
            // draw order is hidden information, even to them).
            matches.Sort((a, b) =>
            {
                int byDef = string.CompareOrdinal(a.DefId, b.DefId);
                return byDef != 0 ? byDef : a.InstanceId.CompareTo(b.InstanceId);
            });

            if (!all && matches.Count > 1)
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Return a mercenary from your discard or draw pile to your hand",
                    Context = "soi.return",
                    Min = 1,
                    Max = 1
                };
                foreach (var card in matches)
                    req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
                yield return ShardsStep.AwaitDecision(req);
                var picked = matches.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                matches = picked != null ? new List<ShardsCard> { picked } : new List<ShardsCard>();
            }
            else if (!all)
            {
                matches = new List<ShardsCard> { matches[0] };
            }
            foreach (var card in matches)
            {
                var src = card.Zone == ShardsZone.Deck ? player.Deck : player.Discard;
                if (!src.Remove(card)) continue;
                card.Zone = ShardsZone.Hand;
                player.Hand.Add(card);
                ctx.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
            }
        }

        /// <summary>Legion Carrier's errata reveal — the same MANDATORY flow as the base
        /// card, just five deep (ShardsHorizonSet owns the implementation).</summary>
        private static IEnumerable<ShardsStep> RevealTopForChampion(ShardsContext ctx, int count) =>
            ShardsHorizonSet.RevealTopForChampion(ctx, count);

        private static IEnumerable<ShardsStep> DeadlyRecruitsDuel(ShardsContext ctx, int maxCost)
        {
            var engine = ctx.Engine;
            var slots = new List<int>();
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
            {
                var card = engine.State.CenterRow[s];
                if (card != null && !card.Def.IsChampion && card.Def.Cost <= maxCost)
                    slots.Add(s);
            }
            if (slots.Count == 0) yield break;
            var req = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Fast-play an ally costing {maxCost} or less for free (you may keep it)?",
                Context = "soi.warp",
                Min = 0,
                Max = 1
            };
            foreach (int s in slots)
            {
                var card = engine.State.CenterRow[s];
                req.Options.Add(new DecisionOption(s, card.Def.Name + " (cost " + card.Def.Cost + ")") { CardInstanceId = card.InstanceId, DefId = card.DefId });
            }
            yield return ShardsStep.AwaitDecision(req);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            int slot = ctx.Answer.ChosenOptionIds[0];
            var picked = engine.State.CenterRow[slot];
            if (picked == null) yield break;

            // "You may keep it" is a real choice (fixed 2026-08-02 — the first build
            // always kept): kept, the card joins the discard at cleanup like a buy;
            // declined, it follows fast-play rules to the bottom of the center deck.
            // Timeout/bot default = keep, the stronger play and the pre-fix behavior.
            var keep = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Keep the fast-played card? (it joins your discard pile)",
                Context = "soi.keepfast",
                Min = 0,
                Max = 1
            };
            keep.Options.Add(new DecisionOption(picked.InstanceId, picked.Def.Name) { CardInstanceId = picked.InstanceId, DefId = picked.DefId });
            keep.DefaultOptionIds.Add(picked.InstanceId);
            yield return ShardsStep.AwaitDecision(keep);
            engine.WarpFromRow(ctx.ControllerIndex, slot, keep: ctx.Answer.ChosenOptionIds.Count > 0);
        }

        /// <summary>Grim Tutor: search the draw pile for any card → hand, shuffle, lose 3
        /// health (a loss, not damage). Options are sorted by DefId/InstanceId — NEVER the
        /// deck order (the owner's own draw order is hidden information).</summary>
        private static IEnumerable<ShardsStep> GrimTutor(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var engine = ctx.Engine;
            if (player.Deck.Count > 0)
            {
                var candidates = new List<ShardsCard>(player.Deck);
                candidates.Sort((a, b) =>
                {
                    int byDef = string.CompareOrdinal(a.DefId, b.DefId);
                    return byDef != 0 ? byDef : a.InstanceId.CompareTo(b.InstanceId);
                });
                var req = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Search your draw pile for a card to put into your hand",
                    Context = "soi.tutor",
                    Min = 1,
                    Max = 1
                };
                foreach (var card in candidates)
                    req.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
                yield return ShardsStep.AwaitDecision(req);

                var chosen = player.Deck.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                if (chosen != null)
                {
                    player.Deck.Remove(chosen);
                    chosen.Zone = ShardsZone.Hand;
                    player.Hand.Add(chosen);
                    engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = chosen.InstanceId, DefId = chosen.DefId });
                }
                engine.State.Rng.Shuffle(player.Deck);
                engine.Emit(new ShardsDeckShuffledEvent { PlayerIndex = player.Index });
            }
            engine.LoseHealth(player.Index, 3);
        }

        private static IEnumerable<ShardsStep> DoomGatePlay(ShardsContext ctx)
        {
            // Once per game PER PLAYER — a destroyed Doom Gate can be redrawn and
            // REPLAYED from the discard, which must not flood again.
            if (ctx.Controller.DoomGateFloodUsed) yield break;
            ctx.Controller.DoomGateFloodUsed = true;
            ctx.Engine.ShuffleIngeminexIntoCenterDeck(30);
        }

        private static IEnumerable<ShardsStep> DoomGateDestroy(ShardsContext ctx)
        {
            var monsters = ctx.Engine.State.ActiveMonsters;
            if (monsters.Count == 0) yield break;
            ShardsCard target;
            if (monsters.Count == 1)
            {
                target = monsters[0];
            }
            else
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Destroy an Ingeminex",
                    Context = "soi.destroy",
                    Min = 1,
                    Max = 1
                };
                foreach (var m in monsters)
                    req.Options.Add(new DecisionOption(m.InstanceId, m.Def.Name) { CardInstanceId = m.InstanceId, DefId = m.DefId });
                yield return ShardsStep.AwaitDecision(req);
                target = monsters.Find(m => m.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            }
            // Destroying it IS defeating it — the controller takes the reward.
            ctx.Engine.DestroyActiveMonster(target, ctx.ControllerIndex);
        }

        private static IEnumerable<ShardsStep> Longshot(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var deck = engine.State.CenterDeck;
            int maxCost = ctx.Controller.Mastery >= 15 ? 5 : 3;

            // Reveal the top 2 (end of the list = top).
            var revealed = new List<ShardsCard>();
            for (int i = 0; i < 2 && deck.Count > 0; i++)
            {
                var top = deck[deck.Count - 1];
                deck.RemoveAt(deck.Count - 1);
                revealed.Add(top);
            }
            if (revealed.Count == 0) yield break;
            var defIds = new List<string>();
            foreach (var c in revealed) defIds.Add(c.DefId);
            engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = ctx.ControllerIndex, DefIds = defIds });

            var playable = revealed.FindAll(c => !c.Def.IsChampion && !c.Def.IsMonster &&
                !c.Def.CannotBeFastPlayed && c.Def.Cost <= maxCost);
            var fastPlayed = new List<ShardsCard>();
            if (playable.Count > 0)
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Fast-play revealed allies costing {maxCost} or less (free)",
                    Context = "soi.warp",
                    Min = 0,
                    Max = playable.Count
                };
                foreach (var c in playable)
                    req.Options.Add(new DecisionOption(c.InstanceId, c.Def.Name + " (cost " + c.Def.Cost + ")") { CardInstanceId = c.InstanceId, DefId = c.DefId });
                yield return ShardsStep.AwaitDecision(req);
                foreach (int id in ctx.Answer.ChosenOptionIds)
                {
                    var chosen = playable.Find(c => c.InstanceId == id);
                    if (chosen != null) fastPlayed.Add(chosen);
                }
            }
            // Fast-play the chosen; the rest go to the bottom of the center deck — WITH
            // the merc-return event so every viewer sees the card slide under the deck
            // (even when nothing was playable, the reveal must not be log-only).
            foreach (var c in revealed)
            {
                if (fastPlayed.Contains(c))
                    engine.FastPlayLoose(ctx.ControllerIndex, c);
                else
                {
                    c.Zone = ShardsZone.CenterDeck;
                    deck.Insert(0, c);
                    engine.Emit(new ShardsMercenaryReturnedEvent { PlayerIndex = ctx.ControllerIndex, DefId = c.DefId });
                }
            }
        }

        private static IEnumerable<ShardsStep> DestroyOpponent(ShardsContext ctx)
        {
            var opps = new List<ShardsPlayer>(ctx.Engine.State.LivingOpponentsOf(ctx.ControllerIndex));
            if (opps.Count == 0) yield break;
            ShardsPlayer target;
            if (opps.Count == 1)
            {
                target = opps[0];
            }
            else
            {
                var req = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.ChooseMode,
                    Title = "Choose an opponent to destroy",
                    Context = "soi.target",
                    Min = 1,
                    Max = 1
                };
                foreach (var o in opps) req.Options.Add(new DecisionOption(o.Index, o.Name));
                yield return ShardsStep.AwaitDecision(req);
                target = ctx.Engine.State.Players[ctx.Answer.ChosenOptionIds[0]];
            }
            ctx.Engine.LoseHealth(target.Index, 1_000_000); // a loss, not damage — no shields
        }

        private static IEnumerable<ShardsStep> BleakCommunion(ShardsContext ctx)
        {
            bool echo = ctx.Controller.Discard.Exists(c => ShardsEngine.CountsAs(ctx.Controller, c.Def, ShardsFaction.Wraethe));
            if (!echo)
            {
                ctx.Engine.LoseHealth(ctx.ControllerIndex, 4);
            }
            else
            {
                var opps = new List<ShardsPlayer>(ctx.Engine.State.LivingOpponentsOf(ctx.ControllerIndex));
                if (opps.Count == 1)
                {
                    ctx.Engine.LoseHealth(opps[0].Index, 4);
                }
                else if (opps.Count > 1)
                {
                    var req = new DecisionRequest
                    {
                        PlayerIndex = ctx.ControllerIndex,
                        Kind = DecisionKind.ChooseMode,
                        Title = "Choose an opponent to lose 4 health",
                        Context = "soi.target",
                        Min = 1,
                        Max = 1
                    };
                    foreach (var o in opps) req.Options.Add(new DecisionOption(o.Index, o.Name));
                    yield return ShardsStep.AwaitDecision(req);
                    ctx.Engine.LoseHealth(ctx.Answer.ChosenOptionIds[0], 4);
                }
            }
            ctx.Engine.DrawCards(ctx.ControllerIndex, 2);
        }
    }
}
