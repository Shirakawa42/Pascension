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
        public static readonly System.Collections.Generic.IReadOnlyList<Changelog.CardChange> October2026 =
            System.Array.AsReadOnly(new[]
        {
            // giga_source_adept
            new Changelog.CardChange(
                Revision("Giga, Source Adept", "Giga, Adepte de la Source", "Order Champion", "Champion Ordre", "When played, draw a card.\nExhaust — Dominion: gain 3 mastery.", "Quand vous jouez cette carte, piochez une carte.\nActivez — Domination : gagnez 3 maîtrise.", "giga_source_adept",
                    ShardsFaction.Order, 2, 4, 0, false),
                Revision("Giga, Source Adept", "Giga, Adepte de la Source", "Order Champion", "Champion Ordre", "When played, draw a card.\nExhaust — Dominion: gain 2 mastery.", "Quand vous jouez cette carte, piochez une carte.\nActivez — Domination : gagnez 2 maîtrise.", "giga_source_adept",
                    ShardsFaction.Order, 2, 4, 0, false)),
            // deadly_recruits_duel
            new Changelog.CardChange(
                Revision("Deadly Recruits", "Dangereuses Recrues", "Destiny", "Destinée", "Exhaust: choose an ally costing 2 or less from the row. Fast-play it OR recruit it for free.\nM20: cost 4 or less.", "Activez : choisissez un allié de coût 2 ou moins de la rivière. Jouez-le en distorsion gratuitement OU recrutez-le gratuitement, sans le jouer. M20 : coût 4 ou moins.", "deadly_recruits",
                    ShardsFaction.None, -1, 0, 0, false),
                Revision("Deadly Recruits", "Dangereuses Recrues", "Destiny", "Destinée", "Pay 1 gem, Exhaust: choose an ally costing 2 or less from the row. Fast-play it OR recruit it.\nM20: cost 4 or less.", "Payez 1 cristal, Activez : choisissez un allié de coût 2 ou moins de la rivière. Enrôlez-le OU recrutez-le.\nM20 : coût 4 ou moins.", "deadly_recruits",
                    ShardsFaction.None, -1, 0, 0, false)),
            // multitask_brain
            new Changelog.CardChange(
                Revision("Multitask Brain", "Cerveau Multitâche", "Order Relic Ally", "Relique Ordre — Allié", "For each different faction you played this turn, gain 2 power and draw a card.\nM20: gain 4 power instead of 2.", "Pour chaque faction différente que vous avez jouée ce tour-ci, gagnez 2 puissance et piochez une carte. M20 : gagnez 4 puissance au lieu de 2.", "multitask_brain",
                    ShardsFaction.Order, -1, 0, 0, false),
                Revision("Multitask Brain", "Cerveau Multitâche", "Order Relic Ally", "Relique Ordre — Allié", "For each different faction you played this turn, gain 1 power and draw a card.\nM20: gain 4 power instead of 1.", "Pour chaque faction différente que vous avez jouée ce tour-ci, gagnez 1 puissance et piochez une carte.\nM20 : gagnez 4 puissance au lieu de 1.", "multitask_brain",
                    ShardsFaction.Order, -1, 0, 0, false)),
            // panconscious_crown_duel
            new Changelog.CardChange(
                Revision("Panconscious Crown", "Couronne Panconsciente", "Undergrowth Relic Ally", "Relique Maquis — Allié", "Gain 2 mastery and 5 health.\nM20 Unify: gain 50 health.", "Gagnez 2 maîtrise et 5 santé.\nM20 Union : gagnez 50 santé.", "panconscious_crown",
                    ShardsFaction.Undergrowth, -1, 0, 0, false),
                Revision("Panconscious Crown", "Couronne Panconsciente", "Undergrowth Relic Ally", "Relique Maquis — Allié", "Gain 2 mastery, 5 health and draw a card.\nM20 Unify: gain 50 health.", "Gagnez 2 maîtrise, 5 santé et piochez une carte.\nM20 Union : gagnez 50 santé.", "panconscious_crown",
                    ShardsFaction.Undergrowth, -1, 0, 0, false)),
            // unconditional_conscription
            new Changelog.CardChange(
                Revision("Unconditional Conscription", "Conscription Obligatoire", "Destiny", "Destinée", "Exhaust: if you played 2+ non-starter allies costing 2 or less this turn, gain 4 power.", "Activez : si vous avez joué ce tour-ci 2 alliés ou plus de coût 2 ou moins, hors cartes de départ, gagnez 4 puissance.", "unconditional_conscription",
                    ShardsFaction.None, -1, 0, 0, false),
                Revision("Unconditional Conscription", "Conscription Obligatoire", "Destiny", "Destinée", "Exhaust: if you played 2+ non-starter allies costing 2 or less this turn, gain 5 power.", "Activez : si vous avez joué ce tour-ci 2 alliés ou plus de coût 2 ou moins, hors cartes de départ, gagnez 5 puissance.", "unconditional_conscription",
                    ShardsFaction.None, -1, 0, 0, false)),
            // soul_syphon_duel
            new Changelog.CardChange(
                Revision("Soul Syphon", "Siphon des Âmes", "Destiny", "Destinée", "Exhaust: if you played cards of 3+ different factions this turn, gain 7 health.", "Activez : si vous avez joué des cartes de 3 factions différentes ou plus ce tour-ci, gagnez 7 santé.", "soul_syphon",
                    ShardsFaction.None, -1, 0, 0, false),
                Revision("Soul Syphon", "Siphon des Âmes", "Destiny", "Destinée", "Exhaust: if you played cards of 2+ different factions this turn, gain 5 health.", "Activez : si vous avez joué des cartes de 2 factions différentes ou plus ce tour-ci, gagnez 5 santé.", "soul_syphon",
                    ShardsFaction.None, -1, 0, 0, false)),
            // furrowing_elemental_duel
            new Changelog.CardChange(
                Revision("Furrowing Elemental", "Élémental du Sillon", "Undergrowth Ally", "Allié Maquis", "Gain 4 health and draw a card.\nIf you are at 50 health, gain 4 power.", "Gagnez 4 santé et piochez une carte.\nSi vous êtes à 50 PV, gagnez 4 puissance.", "furrowing_elemental",
                    ShardsFaction.Undergrowth, 5, 0, 0, false),
                Revision("Furrowing Elemental", "Élémental du Sillon", "Undergrowth Ally", "Allié Maquis", "Gain 4 health and draw a card.\nIf you are at 50 health, gain 4 power.", "Gagnez 4 santé et piochez une carte.\nSi vous êtes à 50 PV, gagnez 4 puissance.", "furrowing_elemental",
                    ShardsFaction.Undergrowth, 4, 0, 0, false)),
            // j_chord_duel
            new Changelog.CardChange(
                Revision("J-Chord", "Riff Ralf", "Aion Champion", "Champion Aion", "Exhaust — Warp 3.\nM15: Warp 6 instead.", "Activez — Distorsion 3. M15 : Distorsion 6 à la place.", "j_chord",
                    ShardsFaction.Aion, 3, 3, 0, false),
                Revision("J-Chord", "Riff Ralf", "Aion Champion", "Champion Aion", "Exhaust — Warp 3.\nM15: Warp 6 instead.", "Activez — Distorsion 3. M15 : Distorsion 6 à la place.", "j_chord",
                    ShardsFaction.Aion, 4, 3, 0, false)),
            // shard_abstractor
            new Changelog.CardChange(
                Revision("Shard Abstractor", "Prophète de l'Éclat", "Order Mercenary", "Mercenaire Ordre", "Gain 2 mastery.", "Gagnez 2 maîtrise.", "shard_abstractor",
                    ShardsFaction.Order, 3, 0, 0, false),
                Revision("Shard Abstractor", "Prophète de l'Éclat", "Order Mercenary", "Mercenaire Ordre", "Gain 1 mastery.\nM10: gain 2 instead.", "Gagnez 1 maîtrise.\nM10 : gagnez-en 2 à la place.", "shard_abstractor",
                    ShardsFaction.Order, 2, 0, 0, false)),
            // volos_first_aid
            new Changelog.CardChange(
                Revision("Volos — First Aid", "Volos — Premiers Soins", "Hero Ability", "Capacité de héros", "M5: once per turn, choose one:\n— Gain 3 health.\n— Pay 1 gem: gain 2 power.\n— Pay 2 gems: draw a card.\n— Pay 3 gems: gain 1 mastery.", "M5 : une fois par tour, choisissez un effet :\n— Gagnez 3 santé.\n— Payez 1 cristal : gagnez 2 puissance.\n— Payez 2 cristaux : piochez une carte.\n— Payez 3 cristaux : gagnez 1 maîtrise.", "soiability_volos",
                    ShardsFaction.Undergrowth, -1, 0, 0, true),
                Revision("Volos — First Aid", "Volos — Premiers Soins", "Hero Ability", "Capacité de héros", "M5: once per turn, choose one:\n— Gain 3 health.\n— Pay 1 gem: gain 3 power.\n— Pay 2 gems: draw a card.\n— Pay 3 gems: gain 1 mastery.", "M5 : une fois par tour, choisissez un effet :\n— Gagnez 3 santé.\n— Payez 1 cristal : gagnez 3 puissance.\n— Payez 2 cristaux : piochez une carte.\n— Payez 3 cristaux : gagnez 1 maîtrise.", "soiability_volos",
                    ShardsFaction.Undergrowth, -1, 0, 0, true)),
            // order_initiate_duel
            new Changelog.CardChange(
                Revision("Order Initiate", "Initié de l'Ordre", "Order Ally", "Allié Ordre", "You may remove a card from the shop. Gain 2 gems. Dominion: gain 2 mastery.", "Vous pouvez retirer une carte de la boutique. Gagnez 2 cristaux.\nDomination : gagnez 2 maîtrise.", "order_initiate",
                    ShardsFaction.Order, 1, 0, 0, false),
                Revision("Order Initiate", "Initié de l'Ordre", "Order Ally", "Allié Ordre", "You may remove a card from the shop.\nGain 2 gems.\nDominion: gain 1 mastery.", "Vous pouvez retirer une carte de la boutique.\nGagnez 2 cristaux.\nDomination : gagnez 1 maîtrise.", "order_initiate",
                    ShardsFaction.Order, 1, 0, 0, false)),
            // shard_seer
            new Changelog.CardChange(
                Revision("Shard Seer", "Voyant de l'Éclat", "Order Ally", "Allié Ordre", "Draw a card.\nYou may reveal an Infinity Shard from your hand to gain 2 mastery.", "Piochez une carte.\nVous pouvez révéler un Éclat de l'Infini de votre main pour gagner 2 maîtrise.", "shard_seer",
                    ShardsFaction.Order, 2, 0, 0, false),
                Revision("Shard Seer", "Voyant de l'Éclat", "Order Ally", "Allié Ordre", "Draw a card.\nYou may reveal an Infinity Shard from your hand to gain 1 mastery.", "Piochez une carte.\nVous pouvez révéler un Éclat de l’Infini de votre main pour gagner 1 maîtrise.", "shard_seer",
                    ShardsFaction.Order, 2, 0, 0, false)),
            // duplication_fabricator_duel
            new Changelog.CardChange(
                Revision("Duplication Fabricator", "Duplicateur", "Order Ally", "Allié Ordre", "Gain 1 mastery.\nEvery player reveals their deck's top card; copy the effect of one revealed ally.\nM20: you may copy any number of effects from the revealed cards instead.", "Gagnez 1 maîtrise.\nChaque joueur révèle la carte du dessus de sa pioche ; copiez l'effet d'un allié révélé. M20 : vous pouvez copier autant d'effets que vous voulez parmi les cartes révélées.", "duplication_fabricator",
                    ShardsFaction.Order, 3, 0, 0, false),
                Revision("Duplication Fabricator", "Duplicateur", "Order Ally", "Allié Ordre", "Gain 1 mastery.\nEvery player reveals their deck's top card; copy the effect of one revealed ally.\nM20: you may copy any number of effects from the revealed cards instead.", "Gagnez 1 maîtrise.\nChaque joueur révèle la carte du dessus de sa pioche ; copiez l'effet d'un allié révélé. M20 : vous pouvez copier autant d'effets que vous voulez parmi les cartes révélées.", "duplication_fabricator",
                    ShardsFaction.Order, 4, 0, 0, false)),
            // breaker
            new Changelog.CardChange(
                Revision("Breaker", "Iconoclaste", "Aion Ally", "Allié Aion", "Shield 4.\nWhen you recruit this, it goes to your hand instead of your discard pile.\nWarp.", "Bouclier 4.\nQuand vous recrutez cette carte, elle va dans votre main au lieu de votre défausse.\nDistorsion.", "breaker",
                    ShardsFaction.Aion, 6, 0, 4, false),
                Revision("Breaker", "Iconoclaste", "Aion Ally", "Allié Aion", "Shield 4.\nWhen you recruit this, it goes to your hand instead of your discard pile.\nWarp 6.", "Bouclier 4.\nQuand vous recrutez cette carte, elle va dans votre main au lieu de votre défausse.\nDistorsion 6.", "breaker",
                    ShardsFaction.Aion, 6, 0, 4, false)),
            // fungal_hermit
            new Changelog.CardChange(
                Revision("Fungal Hermit", "Ermite Fongique", "Undergrowth Mercenary", "Mercenaire Maquis", "Gain 1 mastery.\nM10: gain 5 health. Its own mastery gain counts.", "Gagnez 1 maîtrise. M10 : gagnez 5 santé. Sa propre maîtrise compte.", "fungal_hermit",
                    ShardsFaction.Undergrowth, 3, 0, 0, false),
                Revision("Fungal Hermit", "Ermite Fongique", "Undergrowth Mercenary", "Mercenaire Maquis", "Shield 2.\nGain 1 mastery.\nM10: gain 5 health. Its own mastery gain counts.", "Bouclier 2.\nGagnez 1 maîtrise.\nM10 : gagnez 5 santé. Sa propre maîtrise compte.", "fungal_hermit",
                    ShardsFaction.Undergrowth, 3, 0, 2, false)),
            // ingeminex_corruption
            new Changelog.CardChange(
                Revision("Ingeminex: Corruption", "Ingeminex : Corruption", "Monster", "Ingeminex", "Attack: every player loses 3 health and 1 mastery.\nReward: recruit an additional relic to your hand.", "Attaque : chaque joueur perd 3 santé et 1 maîtrise.\nRécompense : recrutez une relique supplémentaire dans votre main.", "ingeminex_corruption",
                    ShardsFaction.Monster, 0, 10, 0, false),
                Revision("Ingeminex: Corruption", "Ingeminex : Corruption", "Monster", "Ingeminex", "Attack: every player loses 3 health and 1 mastery.\nReward: recruit an additional relic to your hand.", "Attaque : chaque joueur perd 3 santé et 1 maîtrise.\nRécompense : recrutez une relique supplémentaire dans votre main.", "ingeminex_corruption",
                    ShardsFaction.Monster, 0, 15, 0, false)),
            // omnius
            new Changelog.CardChange(
                Revision("Omnius, The All-Knowing", "Omnius, l'Érudit", "Order Mercenary", "Mercenaire Ordre", "Draw two cards.\nDominion: gain 5 mastery.", "Piochez deux cartes.\nDomination : gagnez 5 maîtrise.", "omnius",
                    ShardsFaction.Order, 6, 0, 0, false),
                Revision("Omnius, The All-Knowing", "Omnius, l'Érudit", "Order Mercenary", "Mercenaire Ordre", "Draw two cards.\nDominion: gain 3 mastery.", "Piochez deux cartes.\nDomination : gagnez 3 maîtrise.", "omnius",
                    ShardsFaction.Order, 6, 0, 0, false)),
            // systema_ai
            new Changelog.CardChange(
                Revision("Systema A.I.", "I.A. Systema", "Order Champion", "Champion Ordre", "Exhaust: gain 1 mastery.\nM20: also draw two cards.", "Activez : gagnez 1 maîtrise. M20 : piochez aussi deux cartes.", "systema_ai",
                    ShardsFaction.Order, 3, 4, 0, false),
                Revision("Systema A.I.", "I.A. Systema", "Order Champion", "Champion Ordre", "Exhaust: gain 1 mastery.\nM20: also draw two cards.", "Activez : gagnez 1 maîtrise. M20 : piochez aussi deux cartes.", "systema_ai",
                    ShardsFaction.Order, 4, 4, 0, false)),
            // testudo_vanguard
            new Changelog.CardChange(
                Revision("Testudo Vanguard", "Avant-garde Testudo", "Homodeus Champion", "Champion Homodeus", "Your shields are also applied to each of your champions individually.\nExhaust: gain 2 gems.", "Vos boucliers s'appliquent aussi à chacun de vos champions individuellement.\nActivez : gagnez 2 cristaux.", "testudo_vanguard",
                    ShardsFaction.Homodeus, 4, 4, 0, false),
                Revision("Testudo Vanguard", "Avant-garde Testudo", "Homodeus Champion", "Champion Homodeus", "Whenever you play a card with Shield, each champion you currently control gets +1 defense until the start of your next turn.\nExhaust: gain 2 gems.", "Chaque fois que vous jouez une carte avec Bouclier, chaque champion que vous contrôlez à cet instant gagne +1 défense jusqu’au début de votre prochain tour.\nActivez : gagnez 2 cristaux.", "testudo_vanguard",
                    ShardsFaction.Homodeus, 4, 4, 0, false))
        });

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
                IsMercenary = type.Contains("Mercenary"),
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
