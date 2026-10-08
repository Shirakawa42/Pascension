using System.Collections.Generic;

namespace Pascension.Game.UI
{
    /// <summary>
    /// Per-game user-facing changelogs, shown from the main menu's CHANGELOG panel.
    /// Bilingual inline (no Loc dict): each entry carries its English and French body.
    /// MAINTAIN (CLAUDE.md convention): every user-visible change adds a dated entry —
    /// newest first — to the affected game's list, in the same commit as the change.
    /// </summary>
    public static class Changelog
    {
        public readonly struct Entry
        {
            public readonly string Date; // yyyy-mm-dd
            public readonly string En;   // "· " bullet lines, \n separated
            public readonly string Fr;
            public readonly IReadOnlyList<CardChange> Cards;

            public Entry(string date, string en, string fr, IReadOnlyList<CardChange> cards = null)
            {
                Date = date;
                En = en;
                Fr = fr;
                Cards = cards;
            }
        }

        /// <summary>Archived faces, independent of the current gameplay definitions.</summary>
        public readonly struct CardRevision
        {
            private readonly View.CardView.ExternalFace _en, _fr;
            public CardRevision(View.CardView.ExternalFace en, View.CardView.ExternalFace fr)
            { _en = en; _fr = fr; }
            public View.CardView.ExternalFace Face => Loc.French ? _fr : _en;
        }

        public readonly struct CardChange
        {
            public readonly CardRevision Before, After;
            public CardChange(CardRevision before, CardRevision after)
            { Before = before; After = after; }
        }

        public static readonly IReadOnlyList<Entry> Pascension = new[]
        {
            new Entry("2026-09-25",
                "· Matches now use human multiplayer. Solo play and computer opponents have been removed.",
                "· Les parties se jouent désormais en multijoueur humain. Le mode solo et les adversaires informatiques ont été retirés."),
            new Entry("2026-07-23",
                "· Player accounts: create a username + password account or play as a guest — accounts unlock online multiplayer, sign you in automatically at launch, and can be switched from the main menu.",
                "· Comptes joueur : créez un compte nom d'utilisateur + mot de passe ou jouez en invité — le compte débloque le multijoueur en ligne, vous connecte automatiquement au lancement et se change depuis le menu principal."),
            new Entry("2026-07-22",
                "· Frame rate is now capped — 60 FPS in focus, a trickle in the background — so the game no longer drives the GPU and fans at full power while idle or minimized.",
                "· La fréquence d'images est désormais limitée — 60 FPS au premier plan, au ralenti en arrière-plan — le jeu ne pousse plus le GPU ni les ventilateurs à fond au repos ou minimisé."),
            new Entry("2026-07-21",
                "· This changelog — one per game, from the main menu.\n" +
                "· Leaving an online lobby no longer shows a scary \"Disconnected\" message.",
                "· Ce journal des modifications — un par jeu, depuis le menu principal.\n" +
                "· Quitter un salon en ligne n'affiche plus de message « Disconnected » inquiétant."),
            new Entry("2026-07-20",
                "· RANDOM hero option in solo setup and the online lobby.\n" +
                "· Two players can no longer pick the same hero.\n" +
                "· The first player is now random instead of always the host.\n" +
                "· macOS: the UPDATE button now installs directly, even when Gatekeeper had quarantined the app.",
                "· Option héros ALÉATOIRE en solo et dans le salon en ligne.\n" +
                "· Deux joueurs ne peuvent plus choisir le même héros.\n" +
                "· Le premier joueur est désormais tiré au sort au lieu d'être toujours l'hôte.\n" +
                "· macOS : le bouton UPDATE installe directement la mise à jour, même quand Gatekeeper avait mis le jeu en quarantaine."),
        };

        public static readonly IReadOnlyList<Entry> Shards = new[]
        {
            new Entry("2026-10-08",
                "· END TURN skips the champion window when no champion can be defeated; multiplayer still asks how to split power between eligible opponents.\n" +
                "· Champion HP is bright green above printed defense, white at its printed value and red below it, on a dark badge.\n" +
                "· Champions defeated in the combat window flash, shrink and fade out.\n" +
                "· Mastery-30 Infinity Shard bypasses Zetta and resolves automatically without the combat reminder. Comet also ignores Zetta when destroying its target.",
                "· FIN DU TOUR passe la fenêtre des champions si aucun ne peut être vaincu ; en multijoueur, la répartition de la puissance entre les adversaires pouvant être attaqués reste proposée.\n" +
                "· Les PV des champions sont vert clair au-dessus de la défense imprimée, blancs à sa valeur imprimée et rouges en dessous, sur une pastille sombre.\n" +
                "· Les champions vaincus dans la fenêtre de combat clignotent, rétrécissent et disparaissent progressivement.\n" +
                "· À 30 de maîtrise, l'Éclat de l'Infini ignore Zetta et se résout automatiquement, sans rappel de combat. Comète ignore aussi Zetta lorsqu'elle détruit sa cible."),
            new Entry("2026-10-08",
                "· The relic button is now CLAIM RELIC; its choice window, hero details and notification use the same wording.\n" +
                "· Duel: END TURN opens a combat window showing opposing heroes and champions. Click a champion to defeat it immediately; remaining power and champion HP update after each kill. You can still attack champions directly during your turn.",
                "· Le bouton de relique s’appelle désormais OBTENIR UNE RELIQUE ; la fenêtre de choix, les détails du héros et la notification emploient le même vocabulaire.\n" +
                "· Duel : FIN DU TOUR ouvre une fenêtre de combat montrant les héros et champions adverses. Cliquez sur un champion pour le vaincre immédiatement ; la puissance restante et les PV des champions se mettent à jour après chaque élimination. Vous pouvez toujours attaquer les champions directement pendant votre tour."),
            new Entry("2026-10-08",
                "· Duel of Doom balance patch: 19 existing cards and hero abilities updated; the comparisons below show every changed face.\n" +
                "· Three new market cards: Horizon Seeker, Riftbreaker and Rift Scout, two copies each. New destiny: DNA — pay 4 gems to copy the next card you recruit this turn into your discard pile.\n" +
                "· The initial six shop slots contain cards costing at most 5, with Comet as the only exception. Later refills are unrestricted.\n" +
                "· Spend power to defeat champions during your turn, in your chosen order. Remaining power attacks players at turn end. Each Zetta protects its owner and champions other than Zetta copies.\n" +
                "· Testudo grants each current champion +1 defense whenever you play a shield card, lasting until your next turn. Hand shield reveals no longer protect champions.",
                "· Équilibrage de Duel of Doom : 19 cartes existantes et capacités de héros modifiées ; les comparaisons ci-dessous montrent chaque changement.\n" +
                "· Trois nouvelles cartes dans la rivière : Quêteur d’Horizon, Briseur de Faille et Éclaireur des Failles, en deux exemplaires chacune. Nouvelle destinée : ADN — payez 4 cristaux pour ajouter à votre défausse un exemplaire de la prochaine carte recrutée ce tour-ci.\n" +
                "· Les six cartes initiales de la rivière coûtent au plus 5, à l’exception de Comète. Les remplacements suivants ne sont pas limités.\n" +
                "· Dépensez de la puissance pour vaincre les champions pendant votre tour, dans l’ordre de votre choix. La puissance restante attaque les joueurs en fin de tour. Chaque Zetta protège son propriétaire et les champions qui ne sont pas des Zetta.\n" +
                "· Testudo donne +1 défense à chaque champion présent quand vous jouez une carte avec Bouclier, jusqu’à votre prochain tour. Révéler des boucliers de la main ne protège plus les champions.",
                Soi.SoiBalanceHistory.October2026),
            new Entry("2026-10-08",
                "· Choose your solo opponent: Auld Haïai keeps the original AI; Nyou Haïai uses the new trained model and its search settings. Match statistics distinguish both opponents.",
                "· Choisissez votre adversaire en solo : Auld Haïai conserve l’IA d’origine ; Nyou Haïai utilise le nouveau modèle entraîné et ses réglages de recherche. Les statistiques distinguent les deux adversaires."),
            new Entry("2026-09-28",
                "· 1v1: the second player now starts with 5 cards, 1 mastery and 1 crystal for the first turn. Decima's mastery-5 first-purchase discount is 2 crystals.\n" +
                "· Terminal Crescents: gain 1 mastery; at mastery 20, gain power equal to mastery minus 5. Deadly Recruits: freely fast-play OR recruit an ally, without doing both.\n" +
                "· Swyft: 5 defense. Ferrata Guard: 1 extra base crystal. Torian Commandos: 3 crystals. Le'shai Knight: 4 power, still 6 with Unify. Advanced Medicine: heal 6. Power Struggle: 6 power.\n" +
                "· Doom Gate shuffles 35 Ingeminex. Warpquartz draws a card before banishing. Datic Robes' discard shield starts at mastery 15.\n" +
                "· Solo play includes the latest validated AI model and hybrid search settings, with hero choices refreshed for this balance patch.",
                "· En duel : le second joueur commence désormais avec 5 cartes, 1 maîtrise et 1 cristal pour son premier tour. À 5 maîtrise, Decima réduit le premier achat de 2 cristaux.\n" +
                "· Croissants Terminaux : gagnez 1 maîtrise ; à 20 maîtrise, gagnez une puissance égale à la maîtrise moins 5. Dangereuses Recrues : jouez rapidement OU recrutez gratuitement un allié, sans cumuler les deux.\n" +
                "· Swyft : 5 défense. Garde Ferrata : 1 cristal de base supplémentaire. Commandos Torians : 3 cristaux. Chevalier Le'shai : 4 puissance, toujours 6 avec Unification. Médecine avancée : soigne 6. Lutte de pouvoir : 6 puissance.\n" +
                "· La Porte du Destin mélange 35 Ingeminex. Le Quartz de Distorsion pioche une carte avant de bannir. Le bouclier de défausse des Robes Datiques commence à 15 maîtrise.\n" +
                "· Le mode solo intègre le dernier modèle d'IA validé et ses réglages de recherche hybride, avec des choix de héros actualisés pour cet équilibrage."),
            new Entry("2026-09-28",
                "· Fixed card lookup for newly revealed destinies, including those obtained through Stolen Futures.",
                "· Correction de l'identification des destinées nouvellement révélées, notamment celles obtenues grâce à Futurs volés."),
            new Entry("2026-09-28",
                "· The solo AI now uses hybrid turn planning: it groups equivalent resource plays and compares longer action sequences before deciding what to do.",
                "· L'IA solo utilise désormais une planification hybride : elle regroupe les actions de ressources équivalentes et compare des séquences plus longues avant de choisir quoi faire."),
            new Entry("2026-09-27",
                "· The AI now compares simulated continuations from the opening turn, including choices that do not immediately win. It retains its winning-sequence checks and thinks in the background.",
                "· L'IA compare désormais des suites d'actions simulées dès le premier tour, même sans victoire immédiate. Elle conserve ses vérifications des séquences gagnantes et réfléchit en arrière-plan."),
            new Entry("2026-09-27",
                "· The AI now looks ahead for winning sequences, including hero powers, mastery thresholds and card ordering. It thinks in the background and keeps the one-second pause between actions.",
                "· L'IA anticipe désormais les séquences gagnantes, y compris les pouvoirs des héros, les seuils de maîtrise et l'ordre des cartes. Elle réfléchit en arrière-plan et conserve la pause d'une seconde entre ses actions."),
            new Entry("2026-09-27",
                "· The AI now retains revealed center cards through unrelated reveals and Longshot, and represents their effects in order.",
                "· L'IA mémorise désormais les cartes révélées de la pioche commune après les autres révélations et Longshot, et représente leurs effets dans l'ordre."),
            new Entry("2026-09-27",
                "· 1v1: the second player opens with 6 cards and keeps 1 starting mastery. Later hands still draw 5.\n· Ko Syn Wu's ability costs 1 health; Tetra's costs 3 gems.\n· Rez scries 3; at mastery 5 every reroll costs 1 gem less, without activation.\n· Warpquartz resolves each banished card's effect twice. Doom Gate has 7 defense. Praetorian-02 shields for 4, or 8 at mastery 20.\n· Cinder Scars: 4 copies in the Duel center deck, down from 5.",
                "· En duel : le second joueur commence avec 6 cartes et conserve 1 maîtrise initiale. Les mains suivantes restent à 5 cartes.\n· La capacité de Ko Syn Wu coûte 1 santé ; celle de Tetra coûte 3 cristaux.\n· Rez Sonde 3 ; à 5 maîtrise, toutes ses relances coûtent 1 cristal de moins, sans activation.\n· Le Quartz de Distorsion résout deux fois l'effet de chaque carte bannie. La Porte du Destin a 7 défense. Le Prétorien-02 offre 4 bouclier, ou 8 à 20 maîtrise.\n· Cicatrices de braise : 4 exemplaires dans la pioche commune de Duel, au lieu de 5."),
            new Entry("2026-09-27",
                "· The AI now pauses one second between its decisions, making its turns easier to follow.",
                "· L'IA marque désormais une pause d'une seconde entre ses décisions pour rendre ses tours plus faciles à suivre."),
            new Entry("2026-09-27",
                "· The AI now chooses heroes using their matchup and turn order. Removing a hero from the draft no longer makes it confuse the remaining choices.",
                "· L'IA choisit désormais ses héros selon les confrontations et l'ordre de jeu. Retirer un héros du choix ne lui fait plus confondre les options restantes."),
            new Entry("2026-09-27",
                "· Play against the trained AI from the main menu: local 1v1, all expansions including Duel of Doom, with hero draft.\n· Reactor Drone now correctly banishes itself after a temporary play when its banish mode was chosen.",
                "· Affrontez l'IA entraînée depuis le menu principal : duel local avec toutes les extensions, y compris Duel of Doom, et choix des héros.\n· Le Drone réacteur est désormais correctement banni après un jeu temporaire lorsque son mode de bannissement a été choisi."),
            new Entry("2026-09-25",
                "· Opponent condition glows no longer reveal information about hidden cards. Your own hints remain available.\n" +
                "· Comet can only be acquired through a normal gem purchase. The Shard Defiant must banish it; free recruitment cannot take it.",
                "· Les indicateurs de condition adverses ne révèlent plus d'informations sur les cartes cachées. Vos propres indications restent disponibles.\n" +
                "· Comète ne peut être acquise que par un achat normal avec des cristaux. L'Éclat Rebelle doit la bannir ; le recrutement gratuit ne peut pas la prendre."),
            new Entry("2026-09-25",
                "· Matches now use human multiplayer. Solo play and computer opponents have been removed.",
                "· Les parties se jouent désormais en multijoueur humain. Le mode solo et les adversaires informatiques ont été retirés."),
            new Entry("2026-09-18",
                "Duel of Doom balance update: 12 card and hero ability changes.",
                "Équilibrage de Duel of Doom : 12 changements de cartes et de capacités de héros.",
                Soi.SoiBalanceHistory.September2026),
            new Entry("2026-09-16",
                "· Unify automatically reveals the first matching card in your hand when needed.\n" +
                "· Card hover tooltips show the number of copies at the start of the match.\n" +
                "· Current health is green at 30+, yellow at 10–29, and red below 10.\n" +
                "· Volos: at M5, once per turn, choose one of four cards — free: heal 3; 1 gem: draw 1; 2 gems: gain 3 power; 3 gems: gain 1 mastery.",
                "· Unification révèle automatiquement la première carte correspondante de votre main si nécessaire.\n" +
                "· Les infobulles des cartes indiquent le nombre d'exemplaires au début de la partie.\n" +
                "· La santé actuelle est verte à 30+, jaune de 10 à 29, rouge en dessous de 10.\n" +
                "· Volos : à M5, une fois par tour, choisissez une carte parmi quatre — gratuit : 3 santé ; 1 cristal : piochez 1 ; 2 cristaux : 3 puissance ; 3 cristaux : 1 maîtrise."),
            new Entry("2026-08-23",
                "· Unify now counts CHAMPIONS. An Undergrowth champion satisfies Unify whether you played it this turn or reveal it from your hand — previously it was dead weight for every Unify card in the same hand.\n" +
                "· Three hero abilities rebalanced. Tetra's Perception draws 2 cards instead of 1. Volos' First Aid is now FREE and heals 4 instead of 3. Ko Syn Wu's Sacrifice costs no gems at all — the 3 health is the whole price.\n" +
                "· Why: at their old prices these competed with simply buying a card, and losing a whole buy for one draw or 3 health was almost never the right line.\n" +
                "· Cards put on top of your deck — Dash, Maglev Tunnels — now fly to the DRAW pile instead of to your hand, and cards pulled out of your draw pile fly from it. Dash's draw always worked; the animation claimed the card had already reached your hand, so the draw that followed looked like it did nothing.",
                "· L'Union compte désormais les CHAMPIONS. Un champion Maquis déclenche l'Union que vous l'ayez joué ce tour-ci ou que vous le révéliez de votre main — auparavant il ne servait à rien pour les cartes Union de la même main.\n" +
                "· Trois capacités de héros rééquilibrées. La Perception de Tetra pioche 2 cartes au lieu d'1. Les Premiers Soins de Volos sont désormais GRATUITS et rendent 4 points de vie au lieu de 3. Le Sacrifice de Ko Syn Wu ne coûte plus aucun cristal — les 3 points de vie sont le prix entier.\n" +
                "· Pourquoi : à leur ancien prix, ces capacités concurrençaient l'achat d'une carte, et perdre un achat entier pour une pioche ou 3 points de vie n'était presque jamais la bonne ligne.\n" +
                "· Les cartes placées au-dessus de votre deck — Flash, Tunnels Maglev — volent désormais vers la PIOCHE et non vers votre main, et les cartes tirées de votre pioche en partent. La pioche de Flash a toujours fonctionné ; l'animation prétendait que la carte était déjà dans votre main, ce qui faisait passer la pioche suivante pour un effet nul."),
            new Entry("2026-08-02",
                "· Whisper Extractor is removed from the Duel of Doom pool — stealing the opponent's best card every cycle was too strong even after its earlier nerf.\n" +
                "· Deadly Recruits (Duel version) now really asks whether you keep the fast-played ally, as its text always said — decline and it returns to the bottom of the common deck instead of joining your discard pile.\n" +
                "· Tetra's Perception ability now costs 2 gems instead of 3.\n" +
                "· Shop prices now show what YOU would pay: cost reductions like Decima's first-buy discount or Axia's aura appear right on the card, in green.",
                "· L'Extracteur de Murmures est retiré du pool Duel of Doom — voler la meilleure carte de l'adversaire à chaque cycle restait trop fort même après sa précédente correction.\n" +
                "· Dangereuses Recrues (version Duel) demande désormais vraiment si vous conservez l'allié enrôlé, comme son texte l'a toujours dit — refusez et il retourne sous la pioche commune au lieu de rejoindre votre défausse.\n" +
                "· La capacité Perception de Tetra coûte désormais 2 cristaux au lieu de 3.\n" +
                "· La boutique affiche désormais le prix que VOUS paieriez : les réductions comme la remise premier-achat de Decima ou l'aura d'Axia apparaissent directement sur la carte, en vert."),
            new Entry("2026-07-29",
                "· Two hero abilities rebalanced. Rez's Futureproof is now FREE (was 1 gem) and Ko Syn Wu's Sacrifice costs 2 gems instead of 3 — the 3 health is unchanged.\n" +
                "· Why: both were priced out of their own game plan. Futureproof is meant to pair with a shop reroll — bury a card that would feed your opponent's faction, or set up something better to reroll into — and you could not afford both in the same turn. Sacrifice was strong but rarely worth 3 gems AND 3 health while an opponent was simply racing damage.",
                "· Deux capacités de héros rééquilibrées. Le Pare-Avenir de Rez est désormais GRATUIT (au lieu d'1 cristal) et le Sacrifice de Ko Syn Wu coûte 2 cristaux au lieu de 3 — les 3 points de vie ne changent pas.\n" +
                "· Pourquoi : les deux étaient trop chères pour leur propre plan de jeu. Le Pare-Avenir est conçu pour se combiner avec une relance en boutique — enterrer une carte qui nourrirait la faction de l'adversaire, ou préparer mieux à relancer — et les deux étaient inabordables dans le même tour. Le Sacrifice était fort mais rarement digne de 3 cristaux ET 3 points de vie face à un adversaire qui course simplement les dégâts."),

            new Entry("2026-07-27",
                "· New DLC — Duel of Doom (requires all other expansions), built for two-player skill:\n" +
                "· Heroes are drafted on turn 1 — the shop is dealt first, then players pick in reverse seat order with no duplicates, so your pick answers the opening shop.\n" +
                "· Each hero gains a unique second ability, usable alongside Focus (Decima: cheaper first buy; Tetra: draw; Volos: heal; Ko Syn Wu: banish; Rez: Scry). It sits beside your portrait as a real card with its own art, and glows the moment you can use it.\n" +
                "· Reroll any shop card: 1 gem, +1 for each further reroll the same turn.\n" +
                "· New Allegiance keyword (a bonus for owning 4+ cards of a faction); Dominion reworked to reward 3 different factions; new cards in every faction plus an extra relic per hero.\n" +
                "· Testudo Vanguard makes your shields protect your champions too — attackers can assign more than lethal to punch through them.\n" +
                "· Dozens of cards rebalanced with the DLC on; the base game is untouched.\n" +
                "· Card texts rewritten for readability: no parentheses, one effect per line, a mastery upgrade tucked under the effect it changes, shields and keywords as icons — in English and French.\n" +
                "· Card choice windows redesigned: no boxed panel, big cards centred over a dimmed table, big buttons, and a HIDE button so you can study the shop or your piles before deciding.\n" +
                "· Reveals now always show every card they turned up — the ones you can't take are greyed out and you PASS — and if your draw pile runs short mid-reveal your discard is shuffled back in to finish it. Legion Carrier no longer asks whether to reveal.\n" +
                "· Card previews no longer blink out: hovering survives effects, animations and board refreshes, and pile browsers show the big preview too.\n" +
                "· Cards whose condition is met now twinkle with stars instead of glowing; mercenaries carry a red inset line from their triangle, and the shield badge is bigger on the card's left edge.\n" +
                "· Opponents' reveals now play an animation on your screen, and the Echo tooltip finally explains what Echo actually does.",
                "· Nouvelle extension — Duel of Doom (nécessite toutes les autres extensions), pensée pour le duel :\n" +
                "· Les héros se draftent au tour 1 — la boutique est distribuée d'abord, puis les joueurs choisissent en ordre inverse et sans doublon, pour que le choix réponde à la boutique de départ.\n" +
                "· Chaque héros gagne une seconde capacité unique, utilisable en plus de la Concentration (Decima : premier achat moins cher ; Tetra : pioche ; Volos : soin ; Ko Syn Wu : bannissement ; Rez : Sondage). Elle siège à côté de votre portrait comme une vraie carte avec sa propre illustration, et s'illumine dès que vous pouvez l'utiliser.\n" +
                "· Relancez n'importe quelle carte de la boutique : 1 cristal, +1 par relance supplémentaire le même tour.\n" +
                "· Nouveau mot-clé Allégeance (un bonus si vous possédez 4 cartes ou plus d'une faction) ; Domination remaniée pour récompenser 3 factions différentes ; de nouvelles cartes dans chaque faction et une relique supplémentaire par héros.\n" +
                "· L'Avant-garde Testudo fait aussi protéger vos champions par vos boucliers — les attaquants peuvent assigner plus que nécessaire pour percer.\n" +
                "· Des dizaines de cartes rééquilibrées avec l'extension active ; le jeu de base reste inchangé.\n" +
                "· Textes de cartes réécrits pour la lisibilité : plus de parenthèses, un effet par ligne, l'amélioration de maîtrise collée à l'effet qu'elle modifie, boucliers et mots-clés en icônes — en français comme en anglais.\n" +
                "· Fenêtres de choix de cartes redessinées : plus de panneau encadré, de grandes cartes centrées sur une table assombrie, de grands boutons, et un bouton MASQUER pour consulter la boutique ou vos piles avant de décider.\n" +
                "· Les révélations montrent désormais toutes les cartes retournées — celles que vous ne pouvez pas prendre sont grisées et vous PASSEZ — et si votre pioche s'épuise en cours de révélation, votre défausse y est remélangée pour terminer. Le Transporteur de la Légion ne demande plus s'il faut révéler.\n" +
                "· L'aperçu de carte ne clignote plus : le survol survit aux effets, aux animations et aux rafraîchissements du plateau, et les navigateurs de piles affichent aussi le grand aperçu.\n" +
                "· Les cartes dont la condition est remplie scintillent d'étoiles au lieu de briller ; les mercenaires portent une ligne rouge partant de leur triangle, et le badge bouclier est plus grand sur le bord gauche.\n" +
                "· Les révélations adverses jouent une animation sur votre écran, et l'infobulle Écho explique enfin ce qu'Écho fait vraiment."),
            new Entry("2026-07-23",
                "· Player accounts: create a username + password account or play as a guest — accounts unlock online multiplayer, sign you in automatically at launch, and can be switched from the main menu.\n" +
                "· Every finished game is now recorded to your match history — result, heroes, cards bought and played, per opponent — stored per account and synced to the cloud when signed in (guests keep a device-only history).\n" +
                "· STATS button on the main menu: winrates, hero and card leaderboards with full card art, buy synergies, head-to-head records against any player or bot, and your complete match history — filter by mode or focus on a single opponent.",
                "· Comptes joueur : créez un compte nom d'utilisateur + mot de passe ou jouez en invité — le compte débloque le multijoueur en ligne, vous connecte automatiquement au lancement et se change depuis le menu principal.\n" +
                "· Chaque partie terminée est désormais enregistrée dans votre historique — résultat, héros, cartes achetées et jouées, par adversaire — conservé par compte et synchronisé dans le cloud une fois connecté (les invités gardent un historique local).\n" +
                "· Bouton STATISTIQUES dans le menu principal : taux de victoire, classements des héros et des cartes avec leurs illustrations, synergies d'achats, face-à-face contre n'importe quel joueur ou bot, et l'historique complet de vos parties — filtrez par mode ou concentrez-vous sur un seul adversaire."),
            new Entry("2026-07-22",
                "· Mercenaries are now flagged with a red triangle bearing a black \"M\" on the card's right edge, replacing the old red border.\n" +
                "· Ingeminex now use two icons — crossed swords for their Attack, a treasure chest for the Reward, one line each — with the timing and defeat rules explained in the hover tooltips.\n" +
                "· Card and destiny text no longer repeats what a keyword does (Warp, Inspire, Dominion, Echo) — hover the card to see each keyword explained.\n" +
                "· Frame rate is now capped — 60 FPS in focus, a trickle in the background — so the game no longer drives the GPU and fans at full power while idle or minimized.",
                "· Les mercenaires sont désormais signalés par un triangle rouge marqué d'un « M » noir sur le bord droit de la carte, à la place de l'ancienne bordure rouge.\n" +
                "· Les Ingeminex utilisent désormais deux icônes — des épées croisées pour leur Attaque, un coffre au trésor pour la Récompense, une ligne chacune — la synchro et les règles de défaite étant expliquées dans les infobulles au survol.\n" +
                "· Le texte des cartes et destinées ne répète plus ce que fait un mot-clé (Distorsion, Inspiration, Domination, Écho) — survolez la carte pour voir chaque mot-clé expliqué.\n" +
                "· La fréquence d'images est désormais limitée — 60 FPS au premier plan, au ralenti en arrière-plan — le jeu ne pousse plus le GPU ni les ventilateurs à fond au repos ou minimisé."),
            new Entry("2026-07-21",
                "· Ingeminex attack after you draw your new hand — their discards now hit the hand you keep.\n" +
                "· Destiny picks happen on the board: the row glows, and your piles stay browsable while you decide.\n" +
                "· DECK LIST button: every card you own, cheapest first, whatever its zone.\n" +
                "· Keyword tooltips beside the card preview (Unify, Warp, Shield…) — and they no longer flicker near it.\n" +
                "· Damage assignment: buttons below the heroes, champion HP on its red disc (green boosted / red reduced), assigned numbers on a backdrop.\n" +
                "· Health, portraits and opponent stats now update live during animations.\n" +
                "· Each hit floats a single damage number (the duplicate smaller one is gone).\n" +
                "· Fixed a crash when Duplication Fabricator copied a revealed Duplication Fabricator (infinite copy loop).\n" +
                "· Returning after a long alt-tab now fast-forwards the replay instead of animating every missed move.",
                "· Les Ingeminex attaquent après la pioche de votre nouvelle main — leurs défausses touchent la main que vous gardez.\n" +
                "· Les destinées se choisissent sur le plateau : la rangée s'illumine et vos piles restent consultables pendant la décision.\n" +
                "· Bouton LISTE DU DECK : toutes vos cartes, de la moins chère à la plus chère, quelle que soit leur zone.\n" +
                "· Infobulles des mots-clés à côté de l'aperçu de carte (Union, Distorsion, Bouclier…) — sans clignoter à son contact.\n" +
                "· Répartition des dégâts : boutons sous les héros, PV des champions sur leur disque rouge (vert si augmentés / rouge si réduits), dégâts assignés sur un fond sombre.\n" +
                "· Santé, portraits et statistiques adverses se mettent à jour en direct pendant les animations.\n" +
                "· Chaque coup n'affiche plus qu'un seul nombre de dégâts (le doublon plus petit a disparu).\n" +
                "· Correction d'un plantage quand le Duplicateur copiait un Duplicateur révélé (boucle de copie infinie).\n" +
                "· Revenir après un long alt-tab avance rapidement le replay au lieu d'animer chaque coup manqué."),
            new Entry("2026-07-20",
                "· RANDOM character option; no duplicate characters; random first player.",
                "· Option personnage ALÉATOIRE ; plus de personnages en double ; premier joueur tiré au sort."),
        };
    }
}
