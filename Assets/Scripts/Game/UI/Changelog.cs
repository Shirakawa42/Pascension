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

            public Entry(string date, string en, string fr)
            {
                Date = date;
                En = en;
                Fr = fr;
            }
        }

        public static readonly IReadOnlyList<Entry> Pascension = new[]
        {
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
                "· Why: both were priced out of their own game plan. Futureproof is meant to pair with a shop reroll — bury a card that would feed your opponent's faction, or set up something better to reroll into — and you could not afford both in the same turn. Sacrifice was strong but rarely worth 3 gems AND 3 health while an opponent was simply racing damage.\n" +
                "· The AI now uses both. It also judges banishing by WHAT it would remove instead of by a flat 'banishing is worth X': dropping a Blaster from a developed deck is excellent, dropping a good card is not, and it no longer treats those as the same move.",
                "· Deux capacités de héros rééquilibrées. Le Pare-Avenir de Rez est désormais GRATUIT (au lieu d'1 cristal) et le Sacrifice de Ko Syn Wu coûte 2 cristaux au lieu de 3 — les 3 points de vie ne changent pas.\n" +
                "· Pourquoi : les deux étaient trop chères pour leur propre plan de jeu. Le Pare-Avenir est conçu pour se combiner avec une relance en boutique — enterrer une carte qui nourrirait la faction de l'adversaire, ou préparer mieux à relancer — et les deux étaient inabordables dans le même tour. Le Sacrifice était fort mais rarement digne de 3 cristaux ET 3 points de vie face à un adversaire qui course simplement les dégâts.\n" +
                "· L'IA utilise enfin les deux. Elle juge aussi le bannissement selon CE QU'ELLE retire plutôt que par un « bannir vaut X » figé : se défausser d'un Blaster dans un deck développé est excellent, se défausser d'une bonne carte ne l'est pas, et elle ne confond plus les deux."),

            new Entry("2026-07-28",
                "· The AI now actually plays Duel of Doom. Every difficulty was trained and tuned before the expansion existed, so the bots ignored two of its mechanics entirely: they never rerolled a shop card, and they fired their hero ability every single turn whether or not it was worth the cost.\n" +
                "· Bots now reroll dead shop slots — and only dead ones, respecting the climbing price — and use their hero ability when its effect beats what it costs, so Ko Syn Wu no longer pays 3 health for a banish it doesn't want.\n" +
                "· Scrying, reordering the shop deck and stripping an opponent's hand are now worth something to the AI; before this they were valued at exactly zero, which made Rez's entire hero ability invisible to it.",
                "· L'IA joue enfin vraiment à Duel of Doom. Toutes les difficultés avaient été entraînées et réglées avant l'extension : les bots ignoraient purement et simplement deux de ses mécaniques — jamais de relance en boutique, et une capacité de héros déclenchée à chaque tour, qu'elle en vaille la peine ou non.\n" +
                "· Les bots relancent désormais les emplacements sans intérêt — et seulement ceux-là, en tenant compte du prix croissant — et n'utilisent leur capacité de héros que si son effet dépasse son coût : Ko Syn Wu ne paie plus 3 points de vie pour un bannissement dont il ne veut pas.\n" +
                "· Le Sondage, la réorganisation de la pioche centrale et le pillage de la main adverse ont enfin une valeur pour l'IA ; ils valaient exactement zéro, ce qui rendait toute la capacité de Rez invisible à ses yeux."),

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
                "· Seven bot difficulties now available — IRON, BRONZE, SILVER, GOLD, PLATINUM, EMERALD and DIAMOND, each a clear step tougher than the last.\n" +
                "· Frame rate is now capped — 60 FPS in focus, a trickle in the background — so the game no longer drives the GPU and fans at full power while idle or minimized.",
                "· Les mercenaires sont désormais signalés par un triangle rouge marqué d'un « M » noir sur le bord droit de la carte, à la place de l'ancienne bordure rouge.\n" +
                "· Les Ingeminex utilisent désormais deux icônes — des épées croisées pour leur Attaque, un coffre au trésor pour la Récompense, une ligne chacune — la synchro et les règles de défaite étant expliquées dans les infobulles au survol.\n" +
                "· Le texte des cartes et destinées ne répète plus ce que fait un mot-clé (Distorsion, Inspiration, Domination, Écho) — survolez la carte pour voir chaque mot-clé expliqué.\n" +
                "· Sept niveaux de bot désormais disponibles — FER, BRONZE, ARGENT, OR, PLATINE, ÉMERAUDE et DIAMANT, chacun nettement plus coriace que le précédent.\n" +
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
                "· Ranked bot opponents arrive, each playing without seeing your hand; the status line shows when one is thinking, and a bug that could freeze the game during a bot's turn was fixed.\n" +
                "· Returning after a long alt-tab now fast-forwards the replay instead of animating every missed move.",
                "· Les Ingeminex attaquent après la pioche de votre nouvelle main — leurs défausses touchent la main que vous gardez.\n" +
                "· Les destinées se choisissent sur le plateau : la rangée s'illumine et vos piles restent consultables pendant la décision.\n" +
                "· Bouton LISTE DU DECK : toutes vos cartes, de la moins chère à la plus chère, quelle que soit leur zone.\n" +
                "· Infobulles des mots-clés à côté de l'aperçu de carte (Union, Distorsion, Bouclier…) — sans clignoter à son contact.\n" +
                "· Répartition des dégâts : boutons sous les héros, PV des champions sur leur disque rouge (vert si augmentés / rouge si réduits), dégâts assignés sur un fond sombre.\n" +
                "· Santé, portraits et statistiques adverses se mettent à jour en direct pendant les animations.\n" +
                "· Chaque coup n'affiche plus qu'un seul nombre de dégâts (le doublon plus petit a disparu).\n" +
                "· Correction d'un plantage quand le Duplicateur copiait un Duplicateur révélé (boucle de copie infinie).\n" +
                "· Des bots classés font leur entrée, chacun jouant sans voir votre main ; la ligne d'état indique quand l'un d'eux réfléchit, et un bug pouvant geler la partie pendant le tour d'un bot a été corrigé.\n" +
                "· Revenir après un long alt-tab avance rapidement le replay au lieu d'animer chaque coup manqué."),
            new Entry("2026-07-20",
                "· RANDOM character option; no duplicate characters; random first player.",
                "· Option personnage ALÉATOIRE ; plus de personnages en double ; premier joueur tiré au sort."),
        };
    }
}
