using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Base-game card set: 10-card starter deck per player (7/1/1/1) and the
    /// 88-card center deck (22 per faction). Effects composed per the verified rulings;
    /// text fields are our own functional paraphrases.</summary>
    public static class ShardsBaseSet
    {
        private const ShardsFaction H = ShardsFaction.Homodeus;
        private const ShardsFaction O = ShardsFaction.Order;
        private const ShardsFaction U = ShardsFaction.Undergrowth;
        private const ShardsFaction W = ShardsFaction.Wraethe;

        public static void Register()
        {
            RegisterStarters();
            RegisterHomodeus();
            RegisterOrder();
            RegisterUndergrowth();
            RegisterWraethe();
        }

        // ------------------------------------------------------------- starters

        private static void RegisterStarters()
        {
            SoiCard.New("crystal", "Crystal").Type(ShardsCardType.Starter).Qty(7)
                .Plays(E.Gems(1))
                .Text("Gain 1 gem.")
                .Art("a small glowing blue crystal shard held in a gloved hand, sci-fi").Register();

            SoiCard.New("blaster", "Blaster").Type(ShardsCardType.Starter).Qty(1)
                .Plays(E.Power(1))
                .Text("Gain 1 power.")
                .Art("a compact sci-fi energy pistol on a metal table, blue glow").Register();

            SoiCard.New("shard_reactor", "Shard Reactor").Type(ShardsCardType.Starter).Qty(1)
                .Plays(E.Seq(E.Gems(2), E.At(5, E.Gems(1)), E.At(15, E.Gems(1))))
                .Text("Gain 2 gems. M5: gain 3 instead. M15: gain 4 instead.")
                .Art("a humming reactor core built around a luminous crystal, industrial sci-fi").Register();

            SoiCard.New("infinity_shard", "Infinity Shard").Type(ShardsCardType.Starter).Qty(1)
                .Plays(E.Seq(E.Power(2), E.At(10, E.Power(1)), E.At(20, E.Power(2)), E.At(30, E.Power(9994))))
                .Text("Gain 2 power. M10: 3 instead. M20: 5 instead. M30: infinite power.")
                .Art("a vast fractal crystal shard radiating impossible light, cosmic energy").Register();
        }

        // ------------------------------------------------------------- Homodeus (22)

        private static void RegisterHomodeus()
        {
            var drakonarius = SoiCard.New("drakonarius", "Drakonarius").Faction(H)
                .Type(ShardsCardType.Champion).Cost(6).Qty(1).Defense(2)
                .Exhausts(E.Power(6))
                .Text("Can't be attacked while you control General Decurion. Exhaust: gain 6 power.")
                .Art("a colossal mechanical war-drake with plated armor, battlefield smoke");
            drakonarius.Def.CanBeAttacked = (state, attacker, owner, card) =>
                !owner.Champions.Exists(c => c.DefId == "general_decurion");
            drakonarius.Register();

            SoiCard.New("evokatus", "Evokatus").Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(2).Defense(2)
                .Plays(E.Draw(1))
                .Exhausts(new PerCount(ctx => CountChampions(ctx, H), power: 1))
                .Text("When played, draw a card. Exhaust: gain 1 power per Homodeus champion you control.")
                .Art("a veteran cyborg soldier saluting, banner of a machine legion").Register();

            SoiCard.New("general_decurion", "General Decurion").Faction(H)
                .Type(ShardsCardType.Champion).Cost(7).Qty(1).Defense(7)
                .Exhausts(E.Seq(E.Gems(3), E.At(20, new Custom(DecurionCopy))))
                .Text("Exhaust: gain 3 gems. M20: also copy the effect of each Homodeus ally you play or played this turn.")
                .Art("an imposing armored general on a command podium, holographic war map").Register();

            SoiCard.New("kiln_drone", "Kiln Drone").Faction(H)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Gems(2), If.Inspire(E.Gems(2))))
                .Text("Gain 2 gems. Inspire: gain 4 instead if you control a champion.")
                .Art("a small hovering forge drone pouring molten metal, orange sparks").Register();

            SoiCard.New("korvus_legionnaire", "Korvus Legionnaire").Faction(H)
                .Type(ShardsCardType.Ally).Cost(3).Qty(3).Shield(2)
                .Plays(E.Seq(E.Power(2), new ReturnFromDiscard(d => d.IsChampion, "champion")))
                .Text("Shield 2. Gain 2 power. Return a champion from your discard pile to your hand.")
                .Art("an elite legionnaire with a tower shield and energy spear, marching").Register();

            SoiCard.New("mining_drones", "Mining Drones").Faction(H)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Mix(gems: 1, draw: 1))
                .Text("Gain 1 gem and draw a card.")
                .Art("a swarm of mining drones carving glowing crystal from a cliff face").Register();

            SoiCard.New("numeri_drones", "Numeri Drones").Faction(H)
                .Type(ShardsCardType.Champion).Cost(3).Qty(2).Defense(5)
                .Exhausts(E.Seq(E.Gems(1), new Do(ctx => ctx.Controller.NextHomodeusChampionsIntoPlay++)))
                .Text("Exhaust: gain 1 gem; the next Homodeus champion you recruit this turn enters play directly.")
                .Art("a cluster of logistics drones assembling a soldier from parts").Register();

            SoiCard.New("optio_crusher", "Optio Crusher").Faction(H)
                .Type(ShardsCardType.Champion).Cost(5).Qty(2).Defense(4)
                .Exhausts(E.Seq(E.Power(3), E.At(10, E.Power(2))))
                .Text("Exhaust: gain 3 power. M10: gain 5 instead.")
                .Art("a hulking exo-suit trooper with hydraulic crushing fists").Register();

            SoiCard.New("primus_pilus", "Primus Pilus").Faction(H)
                .Type(ShardsCardType.Champion).Cost(2).Qty(1).Defense(6)
                .Exhausts(new If(ctx => CountChampions(ctx, H) >= 3, E.Draw(2)))
                .Text("Exhaust: if you control three or more Homodeus champions, draw two cards.")
                .Art("a scarred first-rank centurion raising a signal standard").Register();

            SoiCard.New("reactor_drone", "Reactor Drone").Faction(H)
                .Type(ShardsCardType.Ally).Cost(3).Qty(3)
                .Plays(E.Gems(3))
                .Text("Gain 3 gems.")
                .Art("a floating power drone with an exposed miniature reactor core").Register();

            SoiCard.New("venator_of_the_wastes", "Venator of the Wastes").Faction(H)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(1)
                .Plays(E.Seq(E.Power(4), If.Inspire(new OpponentLosesMastery(2))))
                .Text("Gain 4 power. Inspire: if you control a champion, an enemy player loses 2 mastery.")
                .Art("a lone bounty hunter in a dust storm, rifle slung, wasteland ruins").Register();
        }

        private static IEnumerable<ShardsStep> DecurionCopy(ShardsContext ctx)
        {
            // M20: replay Homodeus allies already played this turn, and double the effect
            // of any played after this exhaust (see the play path's copy flag).
            ctx.Controller.CopyHomodeusAlliesThisTurn = true;
            var played = ctx.Controller.PlayedThisTurn.FindAll(c =>
                c.Def.Faction == H && !c.Def.IsChampion && c.Def.PlayEffect != null);
            foreach (var card in played)
                foreach (var step in card.Def.PlayEffect.Resolve(ctx))
                    yield return step;
        }

        // ------------------------------------------------------------- Order (22)

        private static void RegisterOrder()
        {
            SoiCard.New("cache_warden", "Cache Warden").Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(2)
                .Plays(E.Seq(E.Mastery(1), E.At(10, E.Draw(1))))
                .Text("Gain 1 mastery. M10: draw a card (its own mastery counts).")
                .Art("a robed archivist guarding a vault of data crystals").Register();

            SoiCard.New("command_seer", "Command Seer").Faction(O)
                .Type(ShardsCardType.Ally).Cost(4).Qty(2).Shield(5)
                .Plays(E.Gems(2))
                .Text("Shield 5. Gain 2 gems.")
                .Art("a blindfolded seer projecting a shimmering barrier of glyphs").Register();

            SoiCard.New("cryptofist_monk", "Cryptofist Monk").Faction(O)
                .Type(ShardsCardType.Ally).Cost(5).Qty(2).Shield(8)
                .Plays(E.Draw(1))
                .Text("Shield 8. Draw a card.")
                .Art("a monk in cipher-inscribed robes mid-kata, radiant fists").Register();

            SoiCard.New("data_heretic", "Data Heretic").Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Draw(2))
                .Text("Draw two cards.")
                .Art("a hooded renegade jacked into a forbidden datastream, glitch light").Register();

            SoiCard.New("giga_source_adept", "Giga, Source Adept").Faction(O)
                .Type(ShardsCardType.Champion).Cost(2).Qty(1).Defense(4)
                .Plays(E.Draw(1))
                .Exhausts(new Dominion(E.Mastery(3)))
                .Text("When played, draw a card. Exhaust (Dominion): gain 3 mastery if you played or reveal a Homodeus, Undergrowth AND Wraethe card this turn.")
                .Art("a young prodigy levitating streams of source code, serene focus").Register();

            SoiCard.New("omnius", "Omnius, The All-Knowing").Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(6).Qty(1)
                .Plays(E.Seq(E.Draw(2), new Dominion(E.Mastery(5))))
                .Text("Draw two cards. Dominion: gain 5 mastery.")
                .Art("an ancient AI oracle of concentric golden rings and eyes").Register();

            SoiCard.New("order_initiate", "Order Initiate").Faction(O)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Gems(2), new Dominion(E.Mastery(2))))
                .Text("Gain 2 gems. Dominion: gain 2 mastery.")
                .Art("a fresh initiate receiving a glowing sigil in a marble hall").Register();

            SoiCard.New("portal_monk", "Portal Monk").Faction(O)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2)
                .Plays(new BestByMastery((0, new RecruitFromRow(6)), (15, new RecruitFromRow(6, toHand: true))))
                .Text("Recruit a card costing 6 or less for free. M15: it goes to your hand instead.")
                .Art("a monk holding open a circular portal of light between worlds").Register();

            SoiCard.New("shard_abstractor", "Shard Abstractor").Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Mastery(2))
                .Text("Gain 2 mastery.")
                .Art("a technician distilling a crystal shard into pure equations").Register();

            SoiCard.New("systema_ai", "Systema A.I.").Faction(O)
                .Type(ShardsCardType.Champion).Cost(3).Qty(1).Defense(4)
                .Exhausts(E.Seq(E.Mastery(1), E.At(20, E.Draw(2))))
                .Text("Exhaust: gain 1 mastery. M20: also draw two cards.")
                .Art("a serene humanoid AI core suspended in a lattice of light").Register();

            SoiCard.New("grand_architect", "The Grand Architect").Faction(O)
                .Type(ShardsCardType.Mercenary).Cost(7).Qty(1)
                .Plays(E.Mastery(5))
                .Text("Gain 5 mastery.")
                .Art("a towering figure drafting constellations with a compass of light").Register();

            var zetta = SoiCard.New("zetta_encryptor", "Zetta, The Encryptor").Faction(O)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(5).Shield(5)
                .Text("Shield 5. You and your other champions can't be attacked while Zetta is in play.")
                .Art("an armored cryptomancer wreathed in rotating cipher rings");
            zetta.Def.Taunt = true;
            zetta.Register();
        }

        // ------------------------------------------------------------- Undergrowth (22)

        private static void RegisterUndergrowth()
        {
            SoiCard.New("leshai_knight", "Le'shai Knight").Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Seq(E.Power(3), new Unify(E.Power(3))))
                .Text("Gain 3 power. Unify: gain 6 instead.")
                .Art("a bark-armored knight on a stag mount, spear of living wood").Register();

            SoiCard.New("ghostwillow_avenger", "Ghostwillow Avenger").Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(1)
                .Plays(E.Seq(E.Power(4), E.At(15, new DestroyEnemyChampions(all: true))))
                .Text("Gain 4 power. M15: destroy all enemy champions.")
                .Art("a spectral dryad archer emerging from a weeping willow, pale glow").Register();

            SoiCard.New("thorn_zealot", "Thorn Zealot").Faction(U)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(3)
                .Plays(E.Seq(E.Draw(1), new Unify(new DestroyEnemyChampions())))
                .Text("Shield 3. Draw a card. Unify: destroy an enemy champion.")
                .Art("a fanatic wrapped in blooming thorned vines, eyes alight").Register();

            SoiCard.New("root_of_the_forest", "Root of the Forest").Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(7).Qty(1)
                .Plays(E.Seq(E.Health(10), new Unify(E.Power(10))))
                .Text("Gain 10 health. Unify: gain 10 power.")
                .Art("an ancient treant colossus rising, hills of moss for shoulders").Register();

            SoiCard.New("shardwood_guardian", "Shardwood Guardian").Faction(U)
                .Type(ShardsCardType.Ally).Cost(4).Qty(3)
                .Plays(E.Seq(E.Mix(power: 2, draw: 1), new Unify(E.Health(6))))
                .Text("Gain 2 power and draw a card. Unify: gain 6 health.")
                .Art("a sentinel of crystal-veined wood guarding a forest shrine").Register();

            SoiCard.New("furrowing_elemental", "Furrowing Elemental").Faction(U)
                .Type(ShardsCardType.Ally).Cost(5).Qty(2)
                .Plays(E.Seq(E.Mix(health: 4, draw: 1), If.FullHealth(E.Power(6))))
                .Text("Gain 4 health and draw a card. If you are at 50 health, gain 6 power.")
                .Art("an earth elemental plowing waves of soil, seedlings sprouting behind").Register();

            SoiCard.New("spore_cleric", "Spore Cleric").Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Health(4))
                .Text("Gain 4 health.")
                .Art("a mushroom-capped healer releasing luminous restorative spores").Register();

            SoiCard.New("undergrowth_aspirant", "Undergrowth Aspirant").Faction(U)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Health(3), new Unify(E.Power(5))))
                .Text("Gain 3 health. Unify: also gain 5 power.")
                .Art("a young acolyte kneeling as vines curl up their arms").Register();

            SoiCard.New("fungal_hermit", "Fungal Hermit").Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(2)
                .Plays(E.Seq(E.Mastery(1), E.At(10, E.Health(5))))
                .Text("Gain 1 mastery. M10: gain 5 health (its own mastery counts).")
                .Art("a reclusive sage in a cave of giant bioluminescent fungi").Register();

            SoiCard.New("ojas_genesis_druid", "Ojas, Genesis Druid").Faction(U)
                .Type(ShardsCardType.Ally).Cost(4).Qty(1)
                .Plays(new BestByMastery(
                    (0, new CopyPlayedEffect(d => !d.IsChampion, "card")),
                    (20, new CopyPlayedEffect(d => !d.IsChampion, "card", copies: 2))))
                .Text("Copy the effect of a non-champion card you played this turn. M20: copy it an additional time.")
                .Art("a druid cradling a sprouting seed that mirrors the world around it").Register();

            SoiCard.New("additri_gaiamancer", "Additri, Gaiamancer").Faction(U)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(5)
                .Exhausts(E.Seq(E.Power(2),
                    new PerCount(ctx => ctx.Controller.FactionAllyPlays(U), power: 2)))
                .Text("Exhaust: gain 2 power, plus 2 more for each Undergrowth ally you played this turn.")
                .Art("a geomancer conducting roots and stone like an orchestra").Register();
        }

        // ------------------------------------------------------------- Wraethe (22)

        private static void RegisterWraethe()
        {
            SoiCard.New("aetherbreaker", "Aetherbreaker").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(2)
                .Plays(E.Seq(E.Power(4), E.At(10, E.Power(4))))
                .Text("Gain 4 power. M10: gain 8 instead.")
                .Art("a wraith warrior shattering a wall of reality like glass").Register();

            SoiCard.New("fao_cutul", "Fao Cu'tul, The Formless").Faction(W)
                .Type(ShardsCardType.Champion).Cost(4).Qty(1).Defense(4)
                .Exhausts(E.Seq(E.Power(2),
                    E.At(20, new Do(ctx => ctx.Engine.GainPower(ctx.ControllerIndex, ctx.Controller.Power)))))
                .Text("Exhaust: gain 2 power. M20: then double your power.")
                .Art("a shifting mass of shadow wearing a cracked porcelain mask").Register();

            var liHin = SoiCard.New("li_hin", "Li Hin, The Shattered").Faction(W)
                .Type(ShardsCardType.Champion).Cost(3).Qty(1).Defense(1)
                .Exhausts(E.Power(1))
                .Text("Can't be attacked with power (card effects can still destroy it). Exhaust: gain 1 power.")
                .Art("a fractured ghostly figure held together by threads of darkness");
            liHin.Def.CanBeAttacked = (state, attacker, owner, card) => false;
            liHin.Register();

            SoiCard.New("nil_assassin", "Nil Assassin").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Power(5))
                .Text("Gain 5 power.")
                .Art("a void-cloaked assassin mid-strike, twin null-blades").Register();

            SoiCard.New("scion_of_nothingness", "Scion of Nothingness").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(5).Qty(2)
                .Plays(E.Seq(E.Power(3),
                    new PerCount(ctx => CountDiscard(ctx, W), power: 2)))
                .Text("Gain 3 power. Echo: gain 2 more for each Wraethe card in your discard pile.")
                .Art("an heir of the void crowned with an inverted halo of darkness").Register();

            SoiCard.New("shadebound_sentry", "Shadebound Sentry").Faction(W)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2)
                .Plays(E.Seq(E.Power(3),
                    new ReturnFromDiscard(d => d.Type == ShardsCardType.Mercenary, "mercenary")))
                .Text("Gain 3 power. Return a mercenary from your discard pile to your hand.")
                .Art("a chained shadow guardian at a gate of whispering mist").Register();

            SoiCard.New("shadow_apostle", "Shadow Apostle").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Seq(E.Power(2), new BanishUpTo(1)))
                .Text("Gain 2 power. You may banish a card from your hand or discard pile.")
                .Art("a preacher of oblivion offering an empty reliquary").Register();

            SoiCard.New("umbral_scourge", "Umbral Scourge").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Seq(E.Mastery(1), new BanishUpTo(1)))
                .Text("Gain 1 mastery. You may banish a card from your hand or discard pile.")
                .Art("a whip of living shadow flaying light from the air").Register();

            SoiCard.New("wraethe_skirmisher", "Wraethe Skirmisher").Faction(W)
                .Type(ShardsCardType.Ally).Cost(1).Qty(3)
                .Plays(E.Seq(E.Power(2), If.Echo(E.Power(4))))
                .Text("Gain 2 power. Echo: gain 6 instead if a Wraethe card is in your discard pile.")
                .Art("a darting shade fighter flickering between shadows").Register();

            SoiCard.New("zara_ra", "Zara Ra, Soulflayer").Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(5).Qty(1)
                .Plays(E.Seq(E.Mix(power: 4, mastery: 1), E.At(10, new BanishUpTo(2))))
                .Text("Gain 4 power and 1 mastery. M10: you may banish up to two cards from your hand and/or discard pile.")
                .Art("a soul-reaver queen unwinding spirits into ribbons of light").Register();

            SoiCard.New("zen_chi_set", "Zen Chi Set, Godkiller").Faction(W)
                .Type(ShardsCardType.Champion).Cost(7).Qty(1).Defense(5)
                .Exhausts(E.Seq(E.Power(3),
                    new ReturnFromDiscard(d => d.Faction == W, "Wraethe card")))
                .Text("Exhaust: gain 3 power and return a Wraethe card from your discard pile to your hand.")
                .Art("a deicide blade-saint standing over a fallen colossus, calm").Register();
        }

        // ------------------------------------------------------------- shared counters

        internal static int CountChampions(ShardsContext ctx, ShardsFaction faction)
        {
            int n = 0;
            foreach (var champion in ctx.Controller.Champions)
                if (champion.Def.Faction == faction)
                    n++;
            return n;
        }

        internal static int CountDiscard(ShardsContext ctx, ShardsFaction faction)
        {
            int n = 0;
            foreach (var card in ctx.Controller.Discard)
                if (ShardsEngine.CountsAs(ctx.Controller, card.Def, faction))
                    n++;
            return n;
        }
    }
}
