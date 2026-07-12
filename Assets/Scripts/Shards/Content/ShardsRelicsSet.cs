using System.Collections.Generic;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Relics of the Future (DLC 1): 24 center cards (6 per faction) and the 8
    /// character relics (2 each; recruit ONE free at Mastery 10, once per game).</summary>
    public static class ShardsRelicsSet
    {
        private const string SET = "relics_of_the_future";
        private const ShardsFaction H = ShardsFaction.Homodeus;
        private const ShardsFaction O = ShardsFaction.Order;
        private const ShardsFaction U = ShardsFaction.Undergrowth;
        private const ShardsFaction W = ShardsFaction.Wraethe;

        public static void Register()
        {
            RegisterCenter();
            RegisterRelics();
        }

        private static void RegisterCenter()
        {
            // ---- Homodeus (6) ----
            var axia = SoiCard.New("axia", "Axia").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(7).Qty(1).Defense(7)
                .Exhausts(E.Power(7))
                .Text("Costs 1 less per Homodeus champion you control. Exhaust: gain 7 power.")
                .Art("a prototype war-titan being lowered from an assembly gantry");
            axia.Def.CostModifier = buyer =>
            {
                int n = 0;
                foreach (var c in buyer.Champions)
                    if (c.Def.Faction == H)
                        n--;
                return n;
            };
            axia.Register();

            SoiCard.New("limiter_drones", "Limiter Drones").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(If.Inspire(new BanishUpTo(1)), E.Draw(1)))
                .Text("Inspire: if you control a champion, you may banish a card from your hand or discard pile. Draw a card.")
                .Art("regulator drones clamping suppression rings onto a reactor").Register();

            var ferrata = SoiCard.New("ferrata_guard", "Ferrata Guard").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(2).Defense(4)
                .Exhausts(new PerCount(ctx => ShardsBaseSet.CountChampions(ctx, H), gems: 1))
                .Text("If your character is Decima, your champions get +2 defense. Exhaust: gain 1 gem per Homodeus champion you control.")
                .Art("an iron-clad praetorian guard with an energized bulwark");
            ferrata.Def.DefenseAura = (owner, source, champion) =>
                owner.CharacterId == "decima" ? 2 : 0;
            ferrata.Register();

            // ---- Order (6) ----
            SoiCard.New("cloud_oracles", "Cloud Oracles").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(E.Draw(1), new If(HighestMastery, E.Gems(2))))
                .Text("Draw a card. If your mastery is higher than every other player's, gain 2 gems.")
                .Art("floating oracles conferring inside a storm cloud of data").Register();

            var raidian = SoiCard.New("raidian", "Raidian, Cloud Master").InSet(SET).Faction(O)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(3)
                .Exhausts(E.Draw(1))
                .Text("Players with lower mastery than yours can't attack this champion. Exhaust: draw a card.")
                .Art("a sky-lord standing on a platform of compressed cloud, staff raised");
            raidian.Def.CanBeAttacked = (state, attacker, owner, card) =>
                attacker.Mastery >= owner.Mastery;
            raidian.Register();

            SoiCard.New("mainframe_abbot", "Mainframe Abbot").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(3).Qty(2).Shield(3)
                .Plays(E.Seq(E.Draw(1), If.Character("tetra", E.Mastery(1))))
                .Text("Shield 3. Draw a card. If your character is Tetra, gain 1 mastery.")
                .Art("a monk tending a cathedral-sized computer core by candlelight").Register();

            // ---- Undergrowth (6) ----
            SoiCard.New("arach_devotees", "Arach Devotees").InSet(SET).Faction(U)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(E.Draw(1), new Unify(E.Health(3))))
                .Text("Draw a card. Unify: gain 3 health.")
                .Art("robed devotees weaving silk offerings beneath a great web").Register();

            SoiCard.New("taur_arachpriest", "Taur, Arachpriest").InSet(SET).Faction(U)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(4)
                .Exhausts(new CopyPlayedEffect(d => d.Faction == U && !d.IsChampion, "Undergrowth ally"))
                .Text("Exhaust: copy the effect of an Undergrowth ally you played this turn.")
                .Art("a spider-blessed priest with eight luminous eyes, silk vestments").Register();

            SoiCard.New("hounds_of_volos", "Hounds of Volos").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(2)
                .Plays(E.Seq(E.Health(5), If.Character("volos", E.Power(5))))
                .Text("Gain 5 health. If your character is Volos, also gain 5 power.")
                .Art("a pack of moss-furred hunting beasts bounding through ferns").Register();

            // ---- Wraethe (6) ----
            SoiCard.New("pall_shades", "Pall Shades").InSet(SET).Faction(W)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(If.Echo(E.Power(3)), E.Draw(1)))
                .Text("Echo: if a Wraethe card is in your discard pile, gain 3 power. Draw a card.")
                .Art("funeral shrouds drifting upright through a darkened hall").Register();

            SoiCard.New("ru_bo_vai", "Ru Bo Vai, The Transcendant").InSet(SET).Faction(W)
                .Type(ShardsCardType.Champion).Cost(5).Qty(1).Defense(4)
                .Exhausts(E.Seq(E.Power(4),
                    E.At(10, new Do(ctx => ctx.Controller.IgnoreShieldsThisTurn = true))))
                .Text("Exhaust: gain 4 power. M10: your damage ignores shields this turn.")
                .Art("an ascended wraith phasing through a wall of shields").Register();

            SoiCard.New("the_lost", "The Lost").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(2)
                .Plays(E.Seq(E.Power(6), If.Character("kosynwu", new BanishUpTo(1))))
                .Text("Gain 6 power. If your character is Ko Syn Wu, you may banish a card from your hand or discard pile.")
                .Art("a procession of hollow-eyed wanderers with broken weapons").Register();
        }

        private static bool HighestMastery(ShardsContext ctx)
        {
            foreach (var other in ctx.Engine.State.Players)
                if (other.Index != ctx.ControllerIndex && !other.Eliminated &&
                    other.Mastery >= ctx.Controller.Mastery)
                    return false;
            return true;
        }

        private static void RegisterRelics()
        {
            // Decima (Homodeus)
            var p01 = SoiCard.New("praetorian_01", "Praetorian-01").InSet(SET).Faction(H)
                .Type(ShardsCardType.Relic).Character("decima").Qty(1)
                .Plays(E.Seq(E.Power(8), E.At(20, E.Power(4))))
                .Text("Gain 8 power. M20: 12 instead. While in your discard pile: when you play a champion, return this to your hand.")
                .Art("a relic warframe gauntlet crackling with stored power");
            p01.Def.ReturnsFromDiscardOnChampionPlay = true;
            p01.Register();

            var p02 = SoiCard.New("praetorian_02", "Praetorian-02").InSet(SET).Faction(H)
                .Type(ShardsCardType.Relic).Character("decima").Qty(1).Defense(9).Shield(3)
                .Text("Relic champion, defense 9. While in play: shield 3 (M20: 6). Its shield never works from hand.")
                .Art("an ancient guardian automaton kneeling with a deployed barrier");
            p02.Def.ShieldInPlay = true;
            p02.Def.DynamicShield = owner => owner.Mastery >= 20 ? 6 : 3;
            p02.Register();
            // NOTE: recruited to the discard like any relic, then PLAYED like a champion
            // (Type Relic + Defense>0 → the engine deploys via IsChampion checks below).

            // Tetra (Order)
            var robes = SoiCard.New("datic_robes", "Datic Robes").InSet(SET).Faction(O)
                .Type(ShardsCardType.Relic).Character("tetra").Qty(1).Shield(1)
                .Plays(new BestByMastery((0, E.Draw(1)), (20, E.Draw(2))))
                .Text("Shield equal to your mastery. Draw a card. M20: draw two instead.")
                .Art("flowing robes stitched from woven strands of pure data");
            robes.Def.DynamicShield = owner => owner.Mastery;
            robes.Register();

            SoiCard.New("terminal_crescents", "Terminal Crescents").InSet(SET).Faction(O)
                .Type(ShardsCardType.Relic).Character("tetra").Qty(1)
                .Plays(E.Seq(E.Mastery(1), new BestByMastery(
                    (0, new PerCount(ctx => (ctx.Controller.Mastery + 1) / 2, power: 1)),
                    (20, new PerCount(ctx => ctx.Controller.Mastery, power: 1)))))
                .Text("Gain 1 mastery, then power equal to half your mastery (rounded up). M20: equal to your full mastery.")
                .Art("twin crescent blades orbiting each other like binary moons").Register();

            // Volos (Undergrowth)
            SoiCard.New("entropic_talons", "Entropic Talons").InSet(SET).Faction(U)
                .Type(ShardsCardType.Relic).Character("volos").Qty(1)
                .Plays(E.Seq(E.Draw(2),
                    new Do(ctx => ctx.Controller.HealthToPowerThisTurn = true),
                    E.At(20, E.Health(10))))
                .Text("Draw two cards. This turn, gain 1 power per health you gain (even at the 50 cap). M20: also gain 10 health.")
                .Art("claws of crystallized decay dripping motes of green light").Register();

            SoiCard.New("panconscious_crown", "Panconscious Crown").InSet(SET).Faction(U)
                .Type(ShardsCardType.Relic).Character("volos").Qty(1)
                .Plays(E.Seq(E.Mix(mastery: 2, health: 2), E.At(20, new Unify(E.Health(50)))))
                .Text("Gain 2 mastery and 2 health. M20 Unify: gain 50 health.")
                .Art("a living crown of root and crystal linking many minds").Register();

            // Ko Syn Wu (Wraethe)
            SoiCard.New("heart_of_nothing", "The Heart of Nothing").InSet(SET).Faction(W)
                .Type(ShardsCardType.Relic).Character("kosynwu").Qty(1)
                .Plays(E.Seq(E.Power(5),
                    new Do(ctx => ctx.Controller.BonusDrawsOnBigHit = 3),
                    E.At(20, E.Power(5))))
                .Text("Gain 5 power (M20: 10). If you deal 10+ unprevented damage to one opponent this turn, draw 3 extra cards at end of turn.")
                .Art("a black void-heart suspended in a cage of frozen light").Register();

            SoiCard.New("world_piercer", "The World Piercer").InSet(SET).Faction(W)
                .Type(ShardsCardType.Relic).Character("kosynwu").Qty(1)
                .Plays(E.Seq(E.Mastery(2), new BestByMastery(
                    (0, new ReturnFromDiscard(d => d.Type == ShardsCardType.Mercenary, "mercenary")),
                    (20, new ReturnFromDiscard(d => d.Type == ShardsCardType.Mercenary, "mercenary", all: true)))))
                .Text("Gain 2 mastery. Return a mercenary from your discard pile to your hand. M20: return ALL of them.")
                .Art("a needle-thin lance long enough to thread the horizon").Register();
        }
    }
}
