using Pascension.Game.UI;
using Pascension.Game.View;
using Shards.Engine;
using UnityEngine;

namespace Pascension.Game.Soi
{
    /// <summary>Frozen card faces for each balance patch. Both revisions retain the
    /// printed stats and bilingual text from that patch, even after later tuning.</summary>
    public static class SoiBalanceHistory
    {
        public static readonly System.Collections.Generic.IReadOnlyList<Changelog.CardChange> September2026 =
            System.Array.AsReadOnly(new[]
        {
            // panconscious_crown
            new Changelog.CardChange(
                Revision("Panconscious Crown", "Couronne Panconsciente", "Undergrowth Relic Ally", "Relique Maquis — Allié", "Gain 2 mastery and 2 health.\nM20 Unify: gain 50 health.", "Gagnez 2 maîtrise et 2 santé.\nM20 Union : gagnez 50 santé.", "panconscious_crown",
                    ShardsFaction.Undergrowth, -1, 0, 0, false),
                Revision("Panconscious Crown", "Couronne Panconsciente", "Undergrowth Relic Ally", "Relique Maquis — Allié", "Gain 2 mastery and 5 health.\nM20 Unify: gain 50 health.", "Gagnez 2 maîtrise et 5 santé.\nM20 Union : gagnez 50 santé.", "panconscious_crown",
                    ShardsFaction.Undergrowth, -1, 0, 0, false)),
            // comet
            new Changelog.CardChange(
                Revision("Comet", "Comète", "Aion Ally", "Allié Aion", "Destroy target opponent.\nCannot be fast-played — it must be bought with gems.\nCannot be removed from the shop.", "Détruisez un adversaire ciblé.\nCette carte ne peut pas être jouée en distorsion et doit être achetée avec des cristaux.\nCette carte ne peut pas être retirée de la boutique.", "comet",
                    ShardsFaction.Aion, 14, 0, 0, false),
                Revision("Comet", "Comète", "Aion Ally", "Allié Aion", "Destroy target opponent.\nCannot be fast-played — it must be bought with gems.\nCannot be removed from the shop.", "Détruisez un adversaire ciblé.\nCette carte ne peut pas être jouée en distorsion et doit être achetée avec des cristaux.\nCette carte ne peut pas être retirée de la boutique.", "comet",
                    ShardsFaction.Aion, 13, 0, 0, false)),
            // testudo_vanguard
            new Changelog.CardChange(
                Revision("Testudo Vanguard", "Avant-garde Testudo", "Homodeus Champion", "Champion Homodeus", "Your shields are also applied to each of your champions individually.\nExhaust: gain 2 gems.", "Vos boucliers s'appliquent aussi à chacun de vos champions individuellement.\nActivez : gagnez 2 cristaux.", "testudo_vanguard",
                    ShardsFaction.Homodeus, 4, 6, 0, false),
                Revision("Testudo Vanguard", "Avant-garde Testudo", "Homodeus Champion", "Champion Homodeus", "Your shields are also applied to each of your champions individually.\nExhaust: gain 2 gems.", "Vos boucliers s'appliquent aussi à chacun de vos champions individuellement.\nActivez : gagnez 2 cristaux.", "testudo_vanguard",
                    ShardsFaction.Homodeus, 4, 4, 0, false)),
            // praetorian_02_duel
            new Changelog.CardChange(
                Revision("Praetorian-02", "Prétorien-02", "Homodeus Relic Champion", "Relique Homodeus — Champion", "While in play: shield 3.\nM20: shield 6 instead.\nExhaust, pay 3 gems: until your next turn, your shields are doubled. Killing this champion does not remove this effect.", "En jeu : bouclier 3. M20 : 6 à la place.\nActivez, payez 3 cristaux : jusqu'à votre prochain tour, vos boucliers sont doublés. Détruire ce champion ne retire pas cet effet.", "praetorian_02",
                    ShardsFaction.Homodeus, -1, 9, 3, false, true),
                Revision("Praetorian-02", "Prétorien-02", "Homodeus Relic Champion", "Relique Homodeus — Champion", "While in play: shield 3.\nM20: shield 6 instead.\nExhaust, pay 2 gems: until your next turn, your shields are doubled. Killing this champion does not remove this effect.", "En jeu : bouclier 3. M20 : 6 à la place.\nActivez, payez 2 cristaux : jusqu’à votre prochain tour, vos boucliers sont doublés. Détruire ce champion ne retire pas cet effet.", "praetorian_02",
                    ShardsFaction.Homodeus, -1, 9, 3, false, true)),
            // praetorian_03
            new Changelog.CardChange(
                Revision("Praetorian-03", "Prétorien-03", "Homodeus Relic Ally", "Relique Homodeus — Allié", "Gain 1 mastery and draw a card. M15: 2 mastery and 2 cards instead. M25: 3 mastery and 3 cards instead.", "Gagnez 1 maîtrise et piochez une carte. M15 : 2 maîtrise et 2 cartes à la place. M25 : 3 maîtrise et 3 cartes à la place.", "praetorian_03",
                    ShardsFaction.Homodeus, -1, 0, 4, false),
                Revision("Praetorian-03", "Prétorien-03", "Homodeus Relic Ally", "Relique Homodeus — Allié", "Gain 1 mastery and draw a card. M15: 2 mastery and 2 cards instead. M20: 3 mastery and 3 cards instead.", "Gagnez 1 maîtrise et piochez une carte. M15 : 2 maîtrise et 2 cartes à la place. M20 : 3 maîtrise et 3 cartes à la place.", "praetorian_03",
                    ShardsFaction.Homodeus, -1, 0, 4, false)),
            // unknown_god
            new Changelog.CardChange(
                Revision("Unknown God", "Dieu Inconnu", "Undergrowth Relic Champion", "Relique Maquis — Champion", "Exhaust: gain 5 health for each champion you control.\nM20: your Exhaust effects apply twice.", "Activez : gagnez 5 santé par champion que vous contrôlez. M20 : deux fois.", "unknown_god",
                    ShardsFaction.Undergrowth, -1, 5, 0, false),
                Revision("Unknown God", "Dieu Inconnu", "Undergrowth Relic Champion", "Relique Maquis — Champion", "Exhaust: gain 5 health for each champion you control.\nM20: your Exhaust effects apply twice.", "Activez : gagnez 5 santé par champion que vous contrôlez. M20 : deux fois.", "unknown_god",
                    ShardsFaction.Undergrowth, -1, 6, 0, false)),
            // multitask_brain
            new Changelog.CardChange(
                Revision("Multitask Brain", "Cerveau Multitâche", "Order Relic Ally", "Relique Ordre — Allié", "For each different faction you played this turn, gain 2 power and draw a card.\nM20: gain 4 power instead of 2.\nDominion: gain 3 mastery.", "Pour chaque faction différente que vous avez jouée ce tour-ci, gagnez 2 puissance et piochez une carte. M20 : gagnez 4 puissance au lieu de 2.\nDomination : gagnez 3 maîtrise.", "multitask_brain",
                    ShardsFaction.Order, -1, 0, 0, false),
                Revision("Multitask Brain", "Cerveau Multitâche", "Order Relic Ally", "Relique Ordre — Allié", "For each different faction you played this turn, gain 2 power and draw a card.\nM20: gain 4 power instead of 2.", "Pour chaque faction différente que vous avez jouée ce tour-ci, gagnez 2 puissance et piochez une carte. M20 : gagnez 4 puissance au lieu de 2.", "multitask_brain",
                    ShardsFaction.Order, -1, 0, 0, false)),
            // doom_gate
            new Changelog.CardChange(
                Revision("Doom Gate", "Porte du Destin", "Wraethe Relic Champion", "Relique Spectra — Champion", "You are unaffected by Ingeminex attacks.\nWhen you play this champion, shuffle 30 new Ingeminex into the center deck. Once per game.\nExhaust: destroy an Ingeminex.", "Vous êtes insensible aux attaques des Ingeminex.\nQuand vous jouez ce champion, mélangez 30 nouveaux Ingeminex dans la pioche commune. Une fois par partie.\nActivez : détruisez un Ingeminex.", "doom_gate",
                    ShardsFaction.Wraethe, -1, 6, 0, false),
                Revision("Doom Gate", "Porte du Destin", "Wraethe Relic Champion", "Relique Spectra — Champion", "You are unaffected by Ingeminex attacks.\nWhen you play this champion, shuffle 25 new Ingeminex into the center deck. Once per game.\nExhaust: destroy an Ingeminex.", "Vous êtes insensible aux attaques des Ingeminex.\nQuand vous jouez ce champion, mélangez 25 nouveaux Ingeminex dans la pioche commune. Une fois par partie.\nActivez : détruisez un Ingeminex.", "doom_gate",
                    ShardsFaction.Wraethe, -1, 5, 0, false)),
            // heart_of_nothing_duel
            new Changelog.CardChange(
                Revision("The Heart of Nothing", "Cœur du Néant", "Wraethe Relic Ally", "Relique Spectra — Allié", "Gain 6 power.\nM20: gain 10 instead.\nIf you deal 10+ unprevented damage to one opponent this turn, draw 3 extra cards at end of turn.", "Gagnez 6 puissance. M20 : gagnez 10 à la place.\nSi vous infligez 10 dégâts non prévenus ou plus à un même adversaire ce tour-ci, piochez 3 cartes supplémentaires en fin de tour.", "heart_of_nothing",
                    ShardsFaction.Wraethe, -1, 0, 0, false),
                Revision("The Heart of Nothing", "Cœur du Néant", "Wraethe Relic Ally", "Relique Spectra — Allié", "Gain 7 power.\nM20: gain 14 instead.\nIf you deal 10+ unprevented damage to one opponent this turn, draw 3 extra cards at end of turn.", "Gagnez 7 puissance. M20 : gagnez 14 à la place.\nSi vous infligez 10 dégâts non prévenus ou plus à un même adversaire ce tour-ci, piochez 3 cartes supplémentaires en fin de tour.", "heart_of_nothing",
                    ShardsFaction.Wraethe, -1, 0, 0, false)),
            // world_piercer_duel
            new Changelog.CardChange(
                Revision("The World Piercer", "Perce-Mondes", "Wraethe Relic Ally", "Relique Spectra — Allié", "Gain 2 mastery.\nReturn a mercenary from your discard or draw pile to your hand.\nM20: return ALL of them.", "Gagnez 2 maîtrise.\nRenvoyez un mercenaire de votre défausse ou de votre pioche dans votre main. M20 : renvoyez-les TOUS.", "world_piercer",
                    ShardsFaction.Wraethe, -1, 0, 0, false),
                Revision("The World Piercer", "Perce-Mondes", "Wraethe Relic Ally", "Relique Spectra — Allié", "Gain 2 mastery.\nReturn up to two mercenaries from your discard or draw pile to your hand.\nM20: return ALL of them.", "Gagnez 2 maîtrise.\nRenvoyez jusqu’à deux mercenaires de votre défausse ou de votre pioche dans votre main. M20 : renvoyez-les TOUS.", "world_piercer",
                    ShardsFaction.Wraethe, -1, 0, 0, false)),
            // soiability_volos
            new Changelog.CardChange(
                Revision("Volos — First Aid", "Volos — Premiers Soins", "Hero Ability", "Capacité de héros", "M5, once per turn: choose one:\n— Free: gain 3 health.\n— Pay 1 gem: draw 1 card.\n— Pay 2 gems: gain 3 power.\n— Pay 3 gems: gain 1 mastery.", "M5, une fois par tour : choisissez un effet :\n— Gratuit : gagnez 3 santé.\n— Payez 1 cristal : piochez 1 carte.\n— Payez 2 cristaux : gagnez 3 puissance.\n— Payez 3 cristaux : gagnez 1 maîtrise.", "soiability_volos",
                    ShardsFaction.Undergrowth, -1, 0, 0, true),
                Revision("Volos — First Aid", "Volos — Premiers Soins", "Hero Ability", "Capacité de héros", "M5, once per turn: choose one:\n— Free: gain 3 health.\n— Pay 1 gem: gain 2 power.\n— Pay 2 gems: draw 1 card.\n— Pay 3 gems: gain 1 mastery.", "M5, une fois par tour : choisissez un effet :\n— Gratuit : gagnez 3 santé.\n— Payez 1 cristal : gagnez 2 puissance.\n— Payez 2 cristaux : piochez 1 carte.\n— Payez 3 cristaux : gagnez 1 maîtrise.", "soiability_volos",
                    ShardsFaction.Undergrowth, -1, 0, 0, true)),
            // soiability_rez
            new Changelog.CardChange(
                Revision("Rez — Futureproof", "Rez — Pare-Avenir", "Hero Ability", "Capacité de héros", "M5, once per turn: Scry 2 the center deck.", "M5, une fois par tour : Sondez 2 la pioche commune.", "soiability_rez",
                    ShardsFaction.Aion, -1, 0, 0, true),
                Revision("Rez — Futureproof", "Rez — Pare-Avenir", "Hero Ability", "Capacité de héros", "M5, once per turn: Scry 2 the center deck. Your next reroll this turn costs 1 gem less.", "M5, une fois par tour : Sondez 2 la pioche commune. Votre prochaine relance ce tour coûte 1 cristal de moins.", "soiability_rez",
                    ShardsFaction.Aion, -1, 0, 0, true)),
        });

        private static Changelog.CardRevision Revision(string name, string frenchName,
            string type, string frenchType, string text, string frenchText, string artId,
            ShardsFaction faction, int cost, int defense, int shield, bool heroAbility, bool dynamicShield = false)
        {
            if (heroAbility)
            {
                text = System.Text.RegularExpressions.Regex.Replace(text, @"^M(\d+)[,:]?\s*", "M$1 — ");
                frenchText = System.Text.RegularExpressions.Regex.Replace(frenchText, @"^M(\d+)[,:]?\s*", "M$1 — ");
            }
            var en = new CardView.ExternalFace
            {
                Name = name, TypeLine = type, RulesText = SoiCardFaces.Iconize(text), ArtId = artId,
                FrameColor = heroAbility ? new Color(0.5f, 0.42f, 0.2f, 1f) : SoiCardFaces.FactionColor(faction),
                ShowCost = cost >= 0, CostText = cost.ToString(),
                ShowBadge = defense > 0, BadgeText = defense.ToString(),
                ShowShield = shield > 0, ShieldValueText = dynamicShield ? "M" : shield.ToString()
            };
            var fr = en;
            fr.Name = frenchName;
            fr.TypeLine = frenchType;
            fr.RulesText = SoiCardFaces.Iconize(frenchText);
            return new Changelog.CardRevision(en, fr);
        }
    }
}
