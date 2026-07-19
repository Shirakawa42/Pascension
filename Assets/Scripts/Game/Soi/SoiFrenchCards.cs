using System.Collections.Generic;

namespace Pascension.Game.Soi
{
    /// <summary>French display strings (official IELLO terminology) for every SoI def:
    /// def id -> (nom, texte). Display-only — engine texts stay English.</summary>
    public static class SoiFrenchCards
    {
        public static readonly Dictionary<string, (string Name, string Text)> Cards = new()
        {
            // ------------------------------------------------------------- starters
            ["crystal"] = ("Cristal", "Gagnez 1 cristal."),
            ["blaster"] = ("Blaster", "Gagnez 1 puissance."),
            ["shard_reactor"] = ("Réacteur d'Éclat", "Gagnez 2 cristaux. M5 : gagnez-en 3 à la place. M15 : gagnez-en 4 à la place."),
            ["infinity_shard"] = ("Éclat de l'Infini", "Gagnez 2 puissance. M10 : 3 à la place. M20 : 5 à la place. M30 : puissance infinie."),

            // ------------------------------------------------------------- base — Homodeus
            ["drakonarius"] = ("Drakonarius", "Ne peut pas être attaqué tant que vous contrôlez Général Décurion. Activez : gagnez 6 puissance."),
            ["evokatus"] = ("Evokatus", "Quand vous jouez cette carte, piochez une carte. Activez : gagnez 1 puissance par champion Homodeus que vous contrôlez."),
            ["general_decurion"] = ("Général Décurion", "Activez : gagnez 3 cristaux. M20 : copiez aussi l'effet de chaque allié Homodeus que vous jouez ou avez joué ce tour-ci."),
            ["kiln_drone"] = ("Drone Kiln", "Gagnez 2 cristaux. Inspiration : gagnez-en 4 à la place si vous contrôlez un champion."),
            ["korvus_legionnaire"] = ("Légionnaire Korvus", "Bouclier 2. Gagnez 2 puissance. Renvoyez un champion de votre défausse dans votre main."),
            ["mining_drones"] = ("Drones Miniers", "Gagnez 1 cristal et piochez une carte."),
            ["numeri_drones"] = ("Drones Numeri", "Activez : gagnez 1 cristal ; le prochain champion Homodeus que vous recrutez ce tour-ci entre directement en jeu."),
            ["optio_crusher"] = ("Broyeur Optio", "Activez : gagnez 3 puissance. M10 : gagnez-en 5 à la place."),
            ["primus_pilus"] = ("Primus Pilus", "Activez : si vous contrôlez trois champions Homodeus ou plus, piochez deux cartes."),
            ["reactor_drone"] = ("Drone Réacteur", "Gagnez 3 cristaux."),
            ["venator_of_the_wastes"] = ("Valkyrie des Landes", "Gagnez 4 puissance. Inspiration : si vous contrôlez un champion, un joueur ennemi perd 2 maîtrise."),

            // ------------------------------------------------------------- base — Ordre
            ["cache_warden"] = ("Garde Mémoire", "Gagnez 1 maîtrise. M10 : piochez une carte (sa propre maîtrise compte)."),
            ["command_seer"] = ("Voyante de Volonté", "Bouclier 5. Gagnez 2 cristaux."),
            ["cryptofist_monk"] = ("Moine Cryptopoing", "Bouclier 8. Piochez une carte."),
            ["data_heretic"] = ("Pirate Hérétique", "Piochez deux cartes."),
            ["giga_source_adept"] = ("Giga, Adepte de la Source", "Quand vous jouez cette carte, piochez une carte. Activez (Domination) : gagnez 3 maîtrise si vous avez joué ou révélez une carte Homodeus, Maquis ET Spectra ce tour-ci."),
            ["omnius"] = ("Omnius, l'Érudit", "Piochez deux cartes. Domination : gagnez 5 maîtrise."),
            ["order_initiate"] = ("Initié de l'Ordre", "Gagnez 2 cristaux. Domination : gagnez 2 maîtrise."),
            ["portal_monk"] = ("Moine du Portail", "Recrutez gratuitement une carte de coût 6 ou moins. M15 : elle va dans votre main à la place."),
            ["shard_abstractor"] = ("Prophète de l'Éclat", "Gagnez 2 maîtrise."),
            ["systema_ai"] = ("I.A. Systema", "Activez : gagnez 1 maîtrise. M20 : piochez aussi deux cartes."),
            ["grand_architect"] = ("Le Grand Architecte", "Gagnez 5 maîtrise."),
            ["zetta_encryptor"] = ("Zetta, l'Encodeuse", "Bouclier 5. Vous et vos autres champions ne pouvez pas être attaqués tant que Zetta est en jeu."),

            // ------------------------------------------------------------- base — Maquis
            ["leshai_knight"] = ("Chevalier Le'shai", "Gagnez 3 puissance. Union : gagnez-en 6 à la place."),
            ["ghostwillow_avenger"] = ("Saule Vengeur", "Gagnez 4 puissance. M15 : détruisez tous les champions ennemis."),
            ["thorn_zealot"] = ("Zélote des Épines", "Bouclier 3. Piochez une carte. Union : détruisez un champion ennemi."),
            ["root_of_the_forest"] = ("Racine de la Forêt", "Gagnez 10 santé. Union : gagnez 10 puissance."),
            ["shardwood_guardian"] = ("Gardien de la Forêt", "Gagnez 2 puissance et piochez une carte. Union : gagnez 6 santé."),
            ["furrowing_elemental"] = ("Élémental du Sillon", "Gagnez 4 santé et piochez une carte. Si vous êtes à 50 PV, gagnez 6 puissance."),
            ["spore_cleric"] = ("Clerc aux Spores", "Gagnez 4 santé."),
            ["undergrowth_aspirant"] = ("Aspirant Maquis", "Gagnez 3 santé. Union : gagnez aussi 5 puissance."),
            ["fungal_hermit"] = ("Ermite Fongique", "Gagnez 1 maîtrise. M10 : gagnez 5 santé (sa propre maîtrise compte)."),
            ["ojas_genesis_druid"] = ("Ojas, Druide de la Genèse", "Copiez l'effet d'une carte non-champion que vous avez jouée ce tour-ci. M20 : copiez-le une fois de plus."),
            ["additri_gaiamancer"] = ("Additri, Gaïamancienne", "Activez : gagnez 2 puissance, plus 2 par allié Maquis que vous avez joué ce tour-ci."),

            // ------------------------------------------------------------- base — Spectra
            ["aetherbreaker"] = ("Brise-Éther", "Gagnez 4 puissance. M10 : gagnez-en 8 à la place."),
            ["fao_cutul"] = ("Fao Cu'tul, l'Informe", "Activez : gagnez 2 puissance. M20 : doublez ensuite votre puissance."),
            ["li_hin"] = ("Li Hin, la Brisée", "Ne peut pas être attaquée avec de la puissance (les effets de cartes peuvent toujours la détruire). Activez : gagnez 1 puissance."),
            ["nil_assassin"] = ("Assassin du Vide", "Gagnez 5 puissance."),
            ["scion_of_nothingness"] = ("Héritier du Néant", "Gagnez 3 puissance. Écho : gagnez 2 de plus pour chaque carte Spectra dans votre défausse."),
            ["shadebound_sentry"] = ("Sentinelle des Ténèbres", "Gagnez 3 puissance. Renvoyez un mercenaire de votre défausse dans votre main."),
            ["shadow_apostle"] = ("Apôtre des Ombres", "Gagnez 2 puissance. Vous pouvez bannir une carte de votre main ou de votre défausse."),
            ["umbral_scourge"] = ("Fléau des Ombres", "Gagnez 1 maîtrise. Vous pouvez bannir une carte de votre main ou de votre défausse."),
            ["wraethe_skirmisher"] = ("Éclaireur Spectral", "Gagnez 2 puissance. Écho : gagnez-en 6 à la place si une carte Spectra se trouve dans votre défausse."),
            ["zara_ra"] = ("Zara Ra, Écorcheur d'Âme", "Gagnez 4 puissance et 1 maîtrise. M10 : vous pouvez bannir jusqu'à deux cartes de votre main et/ou de votre défausse."),
            ["zen_chi_set"] = ("Zen Chi Set, Fléau des Dieux", "Activez : gagnez 3 puissance et renvoyez une carte Spectra de votre défausse dans votre main."),

            // ------------------------------------------------------------- Relics of the Future — rivière
            ["axia"] = ("Axia", "Coûte 1 de moins par champion Homodeus que vous contrôlez. Activez : gagnez 7 puissance."),
            ["limiter_drones"] = ("Drones Limiteurs", "Inspiration : si vous contrôlez un champion, vous pouvez bannir une carte de votre main ou de votre défausse. Piochez une carte."),
            ["ferrata_guard"] = ("Garde Ferrata", "Si votre personnage est Decima, vos champions ont +2 en défense. Activez : gagnez 1 cristal par champion Homodeus que vous contrôlez."),
            ["cloud_oracles"] = ("Oracles du Nuage", "Piochez une carte. Si votre maîtrise est supérieure à celle de chaque autre joueur, gagnez 2 cristaux."),
            ["raidian"] = ("Raidian, Maître du Nuage", "Les joueurs dont la maîtrise est inférieure à la vôtre ne peuvent pas attaquer ce champion. Activez : piochez une carte."),
            ["mainframe_abbot"] = ("Abbé du Terminal", "Bouclier 3. Piochez une carte. Si votre personnage est Tetra, gagnez 1 maîtrise."),
            ["arach_devotees"] = ("Dévots Arach", "Piochez une carte. Union : gagnez 3 santé."),
            ["taur_arachpriest"] = ("Taur, Arachprêtre", "Activez : copiez l'effet d'un allié Maquis que vous avez joué ce tour-ci."),
            ["hounds_of_volos"] = ("Chiens de Volos", "Gagnez 5 santé. Si votre personnage est Volos, gagnez aussi 5 puissance."),
            ["pall_shades"] = ("Spectres du Voile", "Écho : si une carte Spectra se trouve dans votre défausse, gagnez 3 puissance. Piochez une carte."),
            ["ru_bo_vai"] = ("Ru Bo Vai, le Transcendant", "Activez : gagnez 4 puissance. M10 : vos dégâts ignorent les boucliers ce tour-ci."),
            ["the_lost"] = ("Les Oubliés", "Gagnez 6 puissance. Si votre personnage est Ko Syn Wu, vous pouvez bannir une carte de votre main ou de votre défausse."),

            // ------------------------------------------------------------- Relics of the Future — reliques
            ["praetorian_01"] = ("Prétorien-01", "Gagnez 8 puissance. M20 : 12 à la place. Tant que cette carte est dans votre défausse : quand vous jouez un champion, renvoyez-la dans votre main."),
            ["praetorian_02"] = ("Prétorien-02", "Champion relique, défense 9. Tant qu'il est en jeu : bouclier 3 (M20 : 6). Son bouclier ne fonctionne jamais depuis la main."),
            ["datic_robes"] = ("Sphère Réflectrice", "Bouclier égal à votre maîtrise. Piochez une carte. M20 : piochez-en deux à la place."),
            ["terminal_crescents"] = ("Croissants Terminaux", "Gagnez 1 maîtrise, puis de la puissance égale à la moitié de votre maîtrise (arrondie au supérieur). M20 : égale à votre maîtrise totale."),
            ["entropic_talons"] = ("Serres Entropiques", "Piochez deux cartes. Ce tour-ci, gagnez 1 puissance par santé gagnée (même au maximum de 50). M20 : gagnez aussi 10 santé."),
            ["panconscious_crown"] = ("Couronne Panconsciente", "Gagnez 2 maîtrise et 2 santé. M20 Union : gagnez 50 santé."),
            ["heart_of_nothing"] = ("Cœur du Néant", "Gagnez 5 puissance (M20 : 10). Si vous infligez 10 dégâts non prévenus ou plus à un même adversaire ce tour-ci, piochez 3 cartes supplémentaires à la fin du tour."),
            ["world_piercer"] = ("Perce-Mondes", "Gagnez 2 maîtrise. Renvoyez un mercenaire de votre défausse dans votre main. M20 : renvoyez-les TOUS."),

            // ------------------------------------------------------------- Shadow of Salvation
            ["cloud_oracles_sos"] = ("Oracles du Nuage", "Piochez une carte. Si votre maîtrise est supérieure à celle de chaque joueur ennemi, gagnez 2 cristaux."),
            ["swyft"] = ("Swyft", "Activez : gagnez 2 cristaux et 2 puissance. Si votre personnage est Rez, vous pouvez conserver les cartes que vous enrôlez (elles rejoignent votre défausse)."),
            ["breaker"] = ("Iconoclaste", "Bouclier 4. Quand vous recrutez cette carte, elle va dans votre main au lieu de votre défausse. Distorsion : enrôlez gratuitement n'importe quel allié de la rivière."),
            ["brute"] = ("Brute", "Bouclier 2. Gagnez 4 puissance. Distorsion 2 : enrôlez gratuitement un allié de coût 2 ou moins."),
            ["dash"] = ("Flash", "Bouclier 2. Vous pouvez placer une carte Aion de votre défausse au-dessus de votre deck. Piochez une carte."),
            ["lucky"] = ("Chance", "Bouclier 2. Gagnez 2 cristaux. Distorsion 3 : enrôlez gratuitement un allié de coût 3 ou moins."),
            ["slipstream_shard"] = ("Éclat de Vivacité", "Gagnez 1 maîtrise et piochez une carte. M20 : jouez un tour supplémentaire (une fois par partie)."),
            ["warpquartz"] = ("Quartz de Distorsion", "Gagnez 3 cristaux et 3 puissance. M20 : bannissez jusqu'à 3 alliés de votre main et/ou de votre défausse et gagnez leurs effets."),

            // ------------------------------------------------------------- Into the Horizon — rivière
            ["j_chord"] = ("Riff Ralf", "Activez — Distorsion 3 : enrôlez gratuitement un allié de coût 3 ou moins. M15 : Distorsion 5 à la place."),
            ["g_48"] = ("G-48", "Activez : redressez un autre champion que vous contrôlez (il peut à nouveau s'activer ce tour-ci)."),
            ["legion_carrier"] = ("Transporteur de la Légion", "Gagnez 2 cristaux. Vous pouvez révéler les 3 cartes du dessus de votre deck : jusqu'à un champion révélé dans votre main, le reste dans votre défausse."),
            ["torian_commandos"] = ("Commando Torian", "Bouclier 4. Gagnez 2 cristaux et 2 puissance."),
            ["anomaly_cleric"] = ("Clerc de l'Anomalie", "Gagnez 3 cristaux et 1 maîtrise. M10 : la prochaine carte que vous recrutez ce tour-ci va dans votre main."),
            ["duplication_fabricator"] = ("Duplicateur", "Gagnez 1 maîtrise. Chaque joueur révèle la carte du dessus de son deck ; copiez l'effet d'un allié révélé."),
            ["shard_seer"] = ("Voyant de l'Éclat", "Piochez une carte. Vous pouvez révéler un Éclat de l'Infini de votre main pour gagner 2 maîtrise."),
            ["carnivorous_vine"] = ("Lianes Carnivores", "Gagnez 3 santé et 3 puissance, plus 2 santé et 2 puissance par AUTRE allié Maquis que vous avez joué ce tour-ci."),
            ["orm_madu"] = ("Orm Madu", "Activez : gagnez 7 santé ; puis, si vous êtes à 50 PV, gagnez 1 maîtrise."),
            ["the_rotten"] = ("Le Putride", "Gagnez 5 puissance et 1 maîtrise."),
            ["cinder_scars"] = ("Brûlure des Cendres", "Piochez une carte. Si vous avez joué une autre Brûlure des Cendres ce tour-ci, gagnez 3 puissance. M10 : vous pouvez bannir une carte de votre main ou de votre défausse."),
            ["oblivion_gatekeeper"] = ("Sentinelle de l'Oubli", "Activez : révélez la carte du dessus de votre deck, perdez de la santé égale à son coût (ce ne sont pas des dégâts) et mettez-la dans votre main. M20 : chaque adversaire perd cette santé à la place."),
            ["the_dispossessed"] = ("Les Dépossédés", "Gagnez 3 puissance. Tant que cette carte est dans votre défausse : quand vous jouez une carte Spectra, vous pouvez la renvoyer dans votre main."),

            // ------------------------------------------------------------- Into the Horizon — Ingeminex
            ["ingeminex_brutality"] = ("Ingeminex : Brutalité", "Attaque — à la fin du tour où il apparaît (annulée s'il est vaincu avant) : chaque joueur perd 5 santé (les boucliers ne peuvent pas l'empêcher). Récompense (10 puissance) : gagnez 20 santé."),
            ["ingeminex_corruption"] = ("Ingeminex : Corruption", "Attaque — à la fin du tour où il apparaît (annulée s'il est vaincu avant) : chaque joueur perd 3 santé et 1 maîtrise. Récompense (10 puissance) : recrutez une relique supplémentaire directement dans votre main. (Retirée si Les Reliques du Futur est désactivé.)"),
            ["ingeminex_torment"] = ("Ingeminex : Tourment", "Attaque — à la fin du tour où il apparaît (annulée s'il est vaincu avant) : chaque joueur perd 2 maîtrise. Récompense (10 puissance) : gagnez 4 maîtrise."),
            ["ingeminex_agony"] = ("Ingeminex : Agonie", "Attaque — à la fin du tour où il apparaît (annulée s'il est vaincu avant) : chaque joueur défausse 2 cartes. Récompense (10 puissance) : piochez 2 cartes et prenez une Destinée supplémentaire."),
            ["ingeminex_malice"] = ("Ingeminex : Malice", "Attaque — à la fin du tour où il apparaît (annulée s'il est vaincu avant) : chaque joueur détruit son champion au coût le plus élevé. Récompense (10 puissance) : renvoyez un champion de votre défausse dans votre main et prenez une Destinée supplémentaire."),

            // ------------------------------------------------------------- Into the Horizon — Destinées
            ["datic_secrets"] = ("Secrets Datiques", "Activez : si vous avez joué 2 alliés Ordre ou plus ce tour-ci, gagnez 2 cristaux."),
            ["price_of_power"] = ("Le Prix du Pouvoir", "Activez : gagnez 1 puissance par carte Spectra dans votre défausse."),
            ["one_mind_one_army"] = ("Un Esprit, une Armée", "Vos champions ont +2 en défense."),
            ["project_yggdrasil"] = ("Projet Yggdrasil", "Vos cartes Spectra comptent aussi comme des cartes Maquis et inversement."),
            ["phasic_technology"] = ("Technologie Phasique", "Vos cartes Homodeus et Ordre ont +2 en bouclier."),
            ["blood_for_blood"] = ("Le Sang Appelle le Sang", "Quand vous infligez 5 dégâts non bloqués ou plus à un adversaire, vous pouvez bannir une carte que vous avez jouée ce tour-ci."),
            ["bound_for_life"] = ("Lié par les Chaînes", "Activez : TOUS les joueurs (y compris vous) perdent 4 santé. Ce ne sont pas des dégâts — les boucliers ne peuvent pas l'empêcher."),
            ["nature_dominance"] = ("Domination Naturelle", "Activez : gagnez 1 santé et 1 puissance par carte Maquis que vous avez jouée ce tour-ci."),
            ["crystal_gate"] = ("La Porte de Cristal", "Activez : si vous avez joué une carte Ordre et une carte Maquis ce tour-ci, recrutez gratuitement une carte de coût 3 ou moins."),
            ["forged_in_flame"] = ("Forgé dans la Flamme", "Activez : si vous avez joué une carte Spectra et une carte Homodeus ce tour-ci, bannissez une carte de votre main ou de votre défausse."),
            ["paradigm_shift"] = ("Changement de Paradigme", "Activez : si vous avez joué une carte Ordre et une carte Spectra ce tour-ci, gagnez 1 maîtrise."),
            ["deadly_recruits"] = ("Dangereuses Recrues", "Activez : enrôlez gratuitement un allié de coût 1 de la rivière (vous le conservez). M20 : coût 2 ou moins."),
            ["biotech_enhancements"] = ("Améliorations Biotechnologiques", "Activez : si vous avez joué une carte Homodeus et une carte Maquis ce tour-ci, piochez une carte."),
            ["absorption_grid"] = ("Grille d'Absorption", "Activez : gagnez 2 puissance par allié doté d'un bouclier que vous avez joué ce tour-ci."),
            ["maglev_tunnels"] = ("Tunnels Maglev", "Quand vous recrutez un champion Homodeus, vous pouvez le placer au-dessus de votre deck."),
            ["soul_syphon"] = ("Siphon des Âmes", "Activez : si vous avez joué des cartes de 3 factions différentes ou plus ce tour-ci, gagnez 5 santé."),
            ["agony_of_choice"] = ("Un Choix Douloureux", "Activez : si vous avez joué des cartes de 3 factions différentes ou plus ce tour-ci, gagnez 4 puissance."),
            ["shard_defiant"] = ("Éclat Rebelle", "Payez 2 cristaux, Activez : révélez la carte du dessus de la pioche commune ; recrutez-la ou bannissez-la. Si vous avez joué une carte Aion ce tour-ci, vous pouvez répéter cet effet une fois."),
            ["whatever_it_takes"] = ("Quoi qu'il en coûte", "Payez 6 cristaux, Activez : gagnez 9 puissance."),
            ["the_last_city"] = ("La Dernière Ville", "Activez : si vous avez joué 2 mercenaires ou plus ce tour-ci, gagnez 2 cristaux."),
            ["power_struggle"] = ("Lutte pour le Pouvoir", "Activez : détruisez un champion que vous contrôlez pour gagner 5 puissance."),
            ["unconditional_conscription"] = ("Conscription Obligatoire", "Activez : si vous avez joué ce tour-ci 2 alliés ou plus de coût 2 ou moins (hors cartes de départ), gagnez 4 puissance."),
            ["stolen_futures"] = ("Futurs Volés", "M10 — Activez : bannissez cette Destinée ; ajoutez 2 Destinées du deck à la rangée, puis prenez 2 Destinées."),
            ["strategic_mastermind"] = ("Fin Stratège", "Activez : si vous avez 40 PV ou plus, piochez une carte."),
            ["advanced_weapons"] = ("Armes Avancées", "Activez : si vous avez joué 2 cartes ou plus de coût impair ce tour-ci, gagnez 3 puissance. (Les cartes sans coût ne sont ni paires ni impaires.)"),
            ["advanced_medicine"] = ("Médecine Avancée", "Activez : si vous avez joué 2 cartes ou plus de coût pair ce tour-ci, gagnez 4 santé. (Les cartes sans coût ne sont ni paires ni impaires.)"),
            ["healing_hands"] = ("Mains Curatives", "Activez : si vous avez joué un champion ce tour-ci, gagnez 4 santé."),
            ["war_bound"] = ("Frères d'Armes", "Activez : si vous contrôlez 2 champions ou plus, gagnez 4 puissance."),
            ["true_leader"] = ("Chef Incontesté", "Activez : si vous avez joué 3 cartes de la même faction ce tour-ci, gagnez 2 maîtrise."),
            ["synthesis"] = ("Symbiose", "M15 — Activez : piochez une carte."),
        };
    }
}
