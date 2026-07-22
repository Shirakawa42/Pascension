using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Pascension.Game.UI
{
    /// <summary>
    /// French UI strings, keyed by the exact English source string (see Loc.T), plus
    /// pattern translations for engine-authored decision titles. Terminology follows
    /// the official IELLO French edition (maîtrise, cristaux, la rivière, Activez,
    /// bannir, recruter, enrôler, Destinée…).
    /// </summary>
    public static class LocFrench
    {
        public static readonly Dictionary<string, string> Ui = new()
        {
            // ---------------------------------------------------------- main menu
            ["race the board · build the deck · burst the boss"] =
                "courez le plateau · bâtissez le deck · terrassez le boss",
            ["NEW GAME"] = "NOUVELLE PARTIE",
            ["MULTIPLAYER"] = "MULTIJOUEUR",
            ["SETTINGS"] = "OPTIONS",
            ["QUIT"] = "QUITTER",
            ["BACK"] = "RETOUR",
            ["CHOOSE A GAME"] = "CHOISISSEZ UN JEU",
            ["coming soon"] = "bientôt disponible",
            ["SHARDS OF INFINITY — NEW GAME"] = "SHARDS OF INFINITY — NOUVELLE PARTIE",
            ["YOUR CHARACTER"] = "VOTRE HÉROS",
            ["EXPANSIONS"] = "EXTENSIONS",
            ["OPPONENTS"] = "ADVERSAIRES",
            ["BOT RANK"] = "RANG DES BOTS",
            // The SoI bot ladder (ranks minted over time by the training campaign).
            ["IRON"] = "FER",
            ["BRONZE"] = "BRONZE",
            ["SILVER"] = "ARGENT",
            ["GOLD"] = "OR",
            ["PLATINUM"] = "PLATINE",
            ["EMERALD"] = "ÉMERAUDE",
            ["DIAMOND"] = "DIAMANT",
            ["MASTER"] = "MAÎTRE",
            ["GRANDMASTER"] = "GRAND MAÎTRE",
            ["CHALLENGER"] = "CHALLENGER",
            ["START GAME"] = "LANCER LA PARTIE",
            ["SOLO GAME"] = "PARTIE SOLO",
            ["CHOOSE YOUR HERO"] = "CHOISISSEZ VOTRE HÉROS",
            ["Master volume"] = "Volume principal",
            ["Music volume"] = "Volume de la musique",
            ["Full control (hold priority even when you can only pass)"] =
                "Contrôle total (garder la priorité même sans action possible)",
            ["Audio hooks are stubs until sound lands."] = "Le son n'est pas encore implémenté.",

            // ---------------------------------------------------------- self-update
            ["UPDATE AVAILABLE"] = "MISE À JOUR DISPONIBLE",
            ["DOWNLOADING"] = "TÉLÉCHARGEMENT",
            ["VERIFYING…"] = "VÉRIFICATION…",
            ["INSTALLING…"] = "INSTALLATION…",
            ["RESTARTING…"] = "REDÉMARRAGE…",
            ["OPEN DOWNLOAD PAGE"] = "PAGE DE TÉLÉCHARGEMENT",
            ["Download failed — check your connection."] =
                "Échec du téléchargement — vérifiez votre connexion.",
            ["File verification failed — try again."] =
                "Échec de la vérification du fichier — réessayez.",
            ["Not enough disk space for the update."] =
                "Espace disque insuffisant pour la mise à jour.",
            ["Automatic update unavailable — opening the download page."] =
                "Mise à jour automatique indisponible — ouverture de la page de téléchargement.",
            ["Update failed — see the log."] = "Échec de la mise à jour — voir le journal.",
            ["Update required — use the UPDATE button in the main menu."] =
                "Mise à jour requise — utilisez le bouton MISE À JOUR du menu principal.",
            ["The host runs an older version — they need to update."] =
                "L'hôte utilise une version plus ancienne — il doit la mettre à jour.",

            // ---------------------------------------------------------- lobby
            ["PLAY ONLINE"] = "JOUER EN LIGNE",
            ["host a game and share its ID — no port forwarding needed"] =
                "créez une partie et partagez son code — aucune configuration réseau",
            ["YOUR NAME"] = "VOTRE NOM",
            ["Player name"] = "Nom du joueur",
            ["GAME ID (TO JOIN A FRIEND)"] = "CODE DE LA PARTIE (POUR REJOINDRE UN AMI)",
            ["HOST GAME"] = "CRÉER UNE PARTIE",
            ["JOIN GAME"] = "REJOINDRE",
            ["LOBBY"] = "SALON",
            ["GAME ID:"] = "CODE :",
            ["GAME ID:  "] = "CODE :  ",
            ["COPY"] = "COPIER",
            ["friends join from the menu with this ID"] =
                "vos amis rejoignent depuis le menu avec ce code",
            ["READY"] = "PRÊT",
            ["NOT READY"] = "PAS PRÊT",
            ["UNREADY"] = "ANNULER PRÊT",
            ["LEAVE"] = "PARTIR",
            ["ADD BOT"] = "AJOUTER UNE IA",
            ["Open seat"] = "Place libre",
            ["REMOVE"] = "RETIRER",
            ["KICK"] = "EXCLURE",
            ["HOST"] = "HÔTE",
            ["YOU"] = "VOUS",
            ["BOT"] = "IA",
            ["Waiting for the host to start…"] = "En attente du lancement par l'hôte…",
            ["Connecting…"] = "Connexion…",
            ["Waiting for the lobby…"] = "En attente du salon…",
            ["Creating game…"] = "Création de la partie…",
            ["Joining…"] = "Connexion à la partie…",
            ["Connecting to the host…"] = "Connexion à l'hôte…",
            ["Unexpected error — see the log."] = "Erreur inattendue — voir le journal.",
            ["Starting…"] = "Lancement…",
            ["Connection lost."] = "Connexion perdue.",
            ["RANDOM"] = "ALÉATOIRE",
            ["Random"] = "Aléatoire",
            ["CHANGELOG"] = "NOUVEAUTÉS",
            ["A random hero is assigned when the game starts — never one another player already has."] =
                "Un héros aléatoire vous est attribué au lancement — jamais celui d'un autre joueur.",

            // ---------------------------------------------------------- SoI keyword glossary (hover tooltips)
            ["Unify"] = "Union",
            ["Dominion"] = "Domination",
            ["Inspire"] = "Inspiration",
            ["Echo"] = "Écho",
            ["Warp"] = "Distorsion",
            ["Exhaust"] = "Activez",
            ["Shield"] = "Bouclier",
            ["Mercenary"] = "Mercenaire",
            ["Mastery threshold"] = "Seuil de maîtrise",
            ["Attack"] = "Attaque",
            ["Reward"] = "Récompense",
            // Faction names for the {0} slot in faction-scoped tooltips.
            ["Undergrowth"] = "Maquis",
            ["Wraethe"] = "Spectra",
            ["Order"] = "Ordre",
            ["Reveal it from your hand when attacked to prevent that much damage. It stays in your hand."] =
                "Révélez-la de votre main quand on vous attaque pour prévenir autant de dégâts. Elle reste dans votre main.",
            ["Recruit it, or fast-play it for its cost: effect now, then under the center deck."] =
                "Recrutez-le, ou enrôlez-le pour son coût : effet immédiat, puis sous la pioche commune.",
            ["Tap this ready card to use its ability. It readies at your end phase."] =
                "Activez cette carte prête pour utiliser sa capacité. Elle se redresse en fin de tour.",
            ["Active if you played or reveal another {0} ally as you play this card."] =
                "Actif si vous avez joué ou que vous révélez un autre allié {0} en jouant cette carte.",
            ["Active if you played or revealed a Homodeus, an Undergrowth and a Wraethe card this turn."] =
                "Actif si vous avez joué ou révélé une carte Homodeus, une Maquis et une Spectra ce tour-ci.",
            ["Active while you control a champion."] =
                "Actif tant que vous contrôlez un champion.",
            ["Grows with each {0} card in your discard pile."] =
                "Augmente avec chaque carte {0} dans votre défausse.",
            ["Fast-play a row ally costing up to the shown number for free (no number: any ally). It goes under the center deck at end of turn."] =
                "Enrôlez gratuitement un allié de la rivière coûtant au plus le nombre indiqué (sans nombre : n'importe lequel). Il retourne sous la pioche commune en fin de tour.",
            ["Needs that much Mastery when you play or exhaust this card."] =
                "Requiert autant de maîtrise au moment de jouer ou d'activer la carte.",
            ["Strikes every player at the end of the turn it appeared, unless it is defeated first. These effects aren't damage — shields don't prevent them."] =
                "Frappe tous les joueurs à la fin du tour où il apparaît, sauf s'il est vaincu avant. Ces effets ne sont pas des dégâts — les boucliers n'y font rien.",
            ["Deal it 10 total power to defeat it; only the player who defeats it claims the reward."] =
                "Infligez-lui 10 puissance au total pour le vaincre ; seul le joueur qui le vainc obtient la récompense.",

            // ---------------------------------------------------------- SoI table
            ["Pick a destiny from the glowing row."] = "Choisissez une destinée dans la rangée en surbrillance.",
            ["Pick {0} destinies from the glowing row."] = "Choisissez {0} destinées dans la rangée en surbrillance.",
            ["DECK LIST"] = "LISTE DU DECK",
            ["MY DECK — every card, any zone"] = "MON DECK — toutes vos cartes, toutes zones",
            ["MY CHAMPIONS"] = "MES CHAMPIONS",
            ["MY DESTINIES"] = "MES DESTINÉES",
            ["END TURN"] = "FIN DU TOUR",
            ["NOT YOUR TURN"] = "TOUR ADVERSE",
            ["YOUR TURN"] = "À VOUS DE JOUER",
            ["RECRUIT RELIC"] = "RECRUTER LA RELIQUE",
            ["BACK TO MENU"] = "RETOUR AU MENU",
            ["Center"] = "Pioche commune",
            ["Draw"] = "Pioche",
            ["Played"] = "Jouées",
            ["Discard"] = "Défausse",
            ["Banish"] = "Bannies",
            ["BUY"] = "RECRUTER",
            ["USE"] = "ENRÔLER",
            ["Played this turn"] = "Cartes jouées ce tour",
            ["Discard pile"] = "Défausse",
            ["Banished (removed from the game)"] = "Bannies (retirées du jeu)",
            ["Not your turn."] = "Ce n'est pas votre tour.",
            ["Your character is already exhausted."] = "Votre héros est déjà activé.",
            ["You already focused this turn."] = "Concentration déjà utilisée ce tour.",
            ["Focus costs 1 gem."] = "La Concentration coûte 1 cristal.",
            ["Destinies unlock at Mastery 5 (one per game)."] =
                "Les Destinées se débloquent à 5 de maîtrise (une par partie).",
            ["Nothing there yet."] = "Rien ici pour l'instant.",
            ["The shared center deck — row slots refill from here."] =
                "La pioche commune — la rivière se remplit depuis ici.",
            ["Recruit a relic (free, once per game)"] =
                "Recrutez une relique (gratuit, une fois par partie)",
            ["IT'S A TIE"] = "ÉGALITÉ",
            ["VICTORY!"] = "VICTOIRE !",
            ["GAME OVER"] = "PARTIE TERMINÉE",
            ["VICTORY"] = "VICTOIRE",
            ["It is over."] = "C'est terminé.",

            // Play-log chrome + notes (RECENT bar / full-log window).
            ["ALL"] = "TOUT",
            ["GAME LOG"] = "JOURNAL DE PARTIE",
            ["Champions are destroyed in the end-of-turn damage assignment."] =
                "Les champions ne peuvent être détruits que lors de la répartition des dégâts en fin de tour.",

            ["recruited"] = "recrutée",
            ["activated"] = "activée",
            ["destroyed"] = "détruit",
            ["defeated"] = "vaincu",
            ["attacks!"] = "attaque !",
            ["appears!"] = "apparaît !",
            ["eliminated"] = "éliminé",
            ["banished"] = "bannie",
            ["blocks "] = "pare ",

            // Event-toast fragments (string-concatenated at the call sites).
            [" strikes every player!"] = " frappe tous les joueurs !",
            [" focuses."] = " utilise la Concentration.",
            [" recruits "] = " recrute ",
            [" has been eliminated!"] = " a été éliminé !",
            ["An Ingeminex appears: "] = "Un Ingeminex apparaît : ",
            ["blocked "] = "paré ",
            [" reveals "] = " révèle ",
            [" shields — blocks "] = " boucliers — pare ",
            ["  · eliminated"] = "  · éliminé",
            ["  ← turn"] = "  ← à son tour",

            // Zone captions + stat-line stems.
            ["your hand"] = "votre main",
            ["your discard"] = "votre défausse",
            ["played this turn"] = "jouée ce tour",
            ["your champion"] = "votre champion",
            ["your destiny"] = "votre destinée",
            ["center row"] = "la rivière",
            ["destiny row"] = "rangée des Destinées",
            ["ingeminex"] = "Ingeminex",
            ["banished"] = "bannie",
            ["set aside"] = "mise de côté",
            [" — hand"] = " — main",
            [" — discard"] = " — défausse",
            [" — in play"] = " — en jeu",
            [" — champion"] = " — champion",
            [" — destiny"] = " — destinée",
            ["hand"] = "main",
            ["deck"] = "deck",
            ["discard"] = "défausse",
            ["played"] = "jouées",

            // ---------------------------------------------------------- decision modal
            ["CONFIRM"] = "CONFIRMER",
            ["SKIP"] = "PASSER",
            ["ALL → "] = "TOUT → ",

            // ---------------------------------------------------------- opponent detail
            ["CHAMPIONS"] = "CHAMPIONS",
            ["DESTINIES"] = "DESTINÉES",
            ["CHAMPIONS — none"] = "CHAMPIONS — aucun",
            ["DESTINIES — none"] = "DESTINÉES — aucune",
            ["DISCARD"] = "DÉFAUSSE",
            ["PLAYED THIS TURN"] = "JOUÉES CE TOUR",
            ["CLOSE"] = "FERMER",
            [" — played this turn"] = " — jouées ce tour",
            [" · relic recruited"] = " · relique recrutée",

            // ---------------------------------------------------------- pause overlay
            ["GAME PAUSED"] = "PARTIE EN PAUSE",
            ["REJOIN"] = "REPRENDRE",
            ["The game resumes when everyone is back."] =
                "La partie reprend quand tout le monde est de retour.",
            ["\nOr replace a missing player with a bot."] =
                "\nOu remplacez un joueur absent par une IA.",
            ["REPLACE WITH BOT"] = "REMPLACER PAR UNE IA",
            ["CONNECTION LOST"] = "CONNEXION PERDUE",
            ["The connection to the host was lost."] = "La connexion à l'hôte a été perdue.",
            ["REJOINING…"] = "RECONNEXION…",
            ["Reconnecting to the host…"] = "Reconnexion à l'hôte…",
            ["Rejoin failed — the host may have ended the game."] =
                "Échec de la reconnexion — l'hôte a peut-être terminé la partie.",

            // ---------------------------------------------------------- account
            ["ACCOUNT"] = "COMPTE",
            ["SIGN IN"] = "CONNEXION",
            ["LOG IN"] = "SE CONNECTER",
            ["CREATE ACCOUNT"] = "CRÉER UN COMPTE",
            ["LOG OUT"] = "SE DÉCONNECTER",
            ["PLAY AS GUEST"] = "JOUER EN INVITÉ",
            ["GUEST"] = "INVITÉ",
            ["SIGNING IN…"] = "CONNEXION…",
            ["Signing in…"] = "Connexion…",
            ["offline"] = "hors ligne",
            ["USERNAME"] = "NOM D'UTILISATEUR",
            ["PASSWORD"] = "MOT DE PASSE",
            ["SIGNED IN AS"] = "CONNECTÉ EN TANT QUE",
            ["ACCOUNTS ON THIS DEVICE"] = "COMPTES SUR CET APPAREIL",
            ["ADD ACCOUNT"] = "AJOUTER UN COMPTE",
            ["3-20 characters: letters, digits, . - @ _"] =
                "3 à 20 caractères : lettres, chiffres, . - @ _",
            ["Usernames are 3-20 characters: letters, digits, . - @ _"] =
                "Le nom d'utilisateur fait 3 à 20 caractères : lettres, chiffres, . - @ _",
            ["Enter a password."] = "Entrez un mot de passe.",
            ["An account is required to play online."] =
                "Un compte est requis pour jouer en ligne.",
            ["Playing as guest — multiplayer is disabled."] =
                "Mode invité — le multijoueur est désactivé.",
            ["Wrong username or password."] = "Nom d'utilisateur ou mot de passe incorrect.",
            ["That username is already taken."] = "Ce nom d'utilisateur est déjà pris.",
            ["Your session expired — please sign in again."] =
                "Votre session a expiré — reconnectez-vous.",
            ["Session expired — enter the password for this account."] =
                "Session expirée — entrez le mot de passe de ce compte.",
            ["Online services unavailable — is Username/Password sign-in enabled for this project?"] =
                "Services en ligne indisponibles — la connexion par nom d'utilisateur est-elle activée pour ce projet ?",
            ["No internet connection."] = "Pas de connexion internet.",
            ["Please wait…"] = "Veuillez patienter…",

            // ---------------------------------------------------------- stats
            // ("BACK", "OPPONENTS" and "ALL" already exist above — "ALL" is the
            // play-log button, so the opponent filter shows "TOUT" too.)
            ["STATS"] = "STATISTIQUES",
            ["STATISTICS"] = "STATISTIQUES",
            ["Playing as guest — stats are stored on this device only."] =
                "Mode invité — les statistiques sont enregistrées uniquement sur cet appareil.",
            ["Match history file failed verification and was set aside."] =
                "Le fichier d'historique de parties a échoué la vérification et a été mis de côté.",
            ["VS AI"] = "CONTRE L'IA",
            ["ONLINE 1V1"] = "EN LIGNE 1V1",
            ["ONLINE 3+"] = "EN LIGNE 3+",
            ["OPPONENT: "] = "ADVERSAIRE : ",
            ["OPPONENT: ALL"] = "ADVERSAIRE : TOUS",
            ["ALL OPPONENTS"] = "TOUS LES ADVERSAIRES",
            ["CHOOSE AN OPPONENT"] = "CHOISISSEZ UN ADVERSAIRE",
            ["(bot)"] = "(bot)",
            ["Games: "] = "Parties : ",
            ["Last: "] = "Dernière : ",
            ["Matching games: "] = "Parties correspondantes : ",
            ["OVERVIEW"] = "APERÇU",
            ["HEROES"] = "HÉROS",
            ["CARDS"] = "CARTES",
            ["SYNERGIES"] = "SYNERGIES",
            ["HISTORY"] = "HISTORIQUE",
            ["GAMES"] = "PARTIES",
            ["WINRATE"] = "TAUX DE VICTOIRE",
            ["CURRENT STREAK"] = "SÉRIE EN COURS",
            ["BEST WIN STREAK"] = "MEILLEURE SÉRIE",
            ["AVG LENGTH"] = "DURÉE MOYENNE",
            ["BIGGEST HIT"] = "PLUS GROS COUP",
            ["WINRATE BY MODE"] = "TAUX DE VICTOIRE PAR MODE",
            ["HOW YOU WIN"] = "VOS VICTOIRES",
            ["HOW YOU LOSE"] = "VOS DÉFAITES",
            ["Health to zero"] = "Santé à zéro",
            ["Mastery 30"] = "Maîtrise 30",
            ["Concede"] = "Abandon",
            ["Tie"] = "Égalité",
            ["WIN"] = "VICTOIRE",
            ["LOSS"] = "DÉFAITE",
            ["TIE"] = "ÉGALITÉ",
            ["BEST HERO"] = "MEILLEUR HÉROS",
            ["FAVORITE HERO"] = "HÉROS FAVORI",
            ["FAVORITE CARD"] = "CARTE FAVORITE",
            ["MASTERY PACE"] = "RYTHME DE MAÎTRISE",
            ["round "] = "manche ",
            ["rounds"] = "manches",
            ["reached: "] = "atteint : ",
            ["never"] = "jamais",
            ["games"] = "parties",
            ["winrate"] = "de victoires",
            ["no games yet"] = "aucune partie pour l'instant",
            ["MOST PLAYED"] = "LES PLUS JOUÉES",
            ["MOST BOUGHT"] = "LES PLUS ACHETÉES",
            ["plays"] = "jouées",
            ["buys"] = "achats",
            ["wins"] = "vict.",
            ["SEE ALL"] = "TOUT VOIR",
            ["SEE LESS"] = "RÉDUIRE",
            ["Card pairs you bought in the same game — ranked by how much your winrate climbs when both are in your deck (minimum 5 games together)."] =
                "Paires de cartes achetées dans la même partie — classées selon la hausse de votre taux de victoire quand les deux sont dans votre deck (minimum 5 parties ensemble).",
            ["TOGETHER: "] = "ENSEMBLE : ",
            ["overall "] = "global ",
            ["Not enough data yet — pairs need at least 5 games together."] =
                "Pas encore assez de données — une paire doit compter au moins 5 parties ensemble.",
            ["Click an opponent to focus every tab on games against them."] =
                "Cliquez sur un adversaire pour recentrer tous les onglets sur vos parties contre lui.",
            ["HEAD-TO-HEAD"] = "FACE-À-FACE",
            ["THEIR HEROES"] = "SES HÉROS",
            ["THEIR FAVORITE CARDS"] = "SES CARTES FAVORITES",
            ["YOUR HEROES VS THEM"] = "VOS HÉROS CONTRE LUI",
            ["vs "] = "contre ",
            ["BOUGHT"] = "ACHATS",
            [" — cards bought"] = " — cartes achetées",
            ["AVG DAMAGE / GAME"] = "DÉGÂTS MOYENS / PARTIE",
            ["No games recorded yet — your Shards of Infinity story starts with your first finished game."] =
                "Aucune partie enregistrée — votre histoire dans Shards of Infinity commence avec votre première partie terminée.",
            ["PLAY NOW"] = "JOUER",
            ["No games match these filters."] = "Aucune partie ne correspond à ces filtres.",
            ["RESET FILTERS"] = "RÉINITIALISER LES FILTRES",
            ["min"] = "min",
            // Streak letters, W-L-T caption, history mode tag, hero detail stems.
            ["AI"] = "IA",
            ["W"] = "V",
            ["L"] = "D",
            ["W-L-T"] = "V-D-E",
            ["BEST"] = "MEILLEUR",
            ["avg "] = "moy. ",
            ["M30 avg round "] = "M30 manche moy. ",
            ["biggest hit "] = "plus gros coup ",
        };

        /// <summary>Fixed engine decision-OPTION labels (verb forms — distinct from
        /// pile captions: the "Banish" pile shows "Bannies", the option verb "Bannir").</summary>
        public static readonly Dictionary<string, string> OptionLabels = new()
        {
            ["Reveal"] = "Révéler",
            ["Recruit"] = "Recruter",
            ["Keep"] = "Garder",
            ["Banish"] = "Bannir",
            ["Leave it on top"] = "Laisser dessus",
            ["Repeat"] = "Répéter",
            ["Yes"] = "Oui",
            ["No"] = "Non",
        };

        /// <summary>Lobby start-validation error templates (host-local strings with
        /// interpolated player names) → French. First match wins.</summary>
        public static readonly List<(Regex, string)> LobbyErrors = new()
        {
            (new Regex(@"^(.+) and (.+) have the same hero$"), "$1 et $2 ont le même héros"),
            (new Regex(@"^(.+) is not ready$"), "$1 n'est pas prêt"),
            (new Regex(@"^(.+) has no hero$"), "$1 n'a pas de héros"),
            (new Regex(@"^Need at least 2 players \(add a bot\?\)$"),
                "Il faut au moins 2 joueurs (ajoutez une IA ?)"),
            (new Regex(@"^(.+) needs at least (\d+) players$"), "$1 nécessite au moins $2 joueurs"),
            (new Regex(@"^(.+) supports at most (\d+) players$"), "$1 accepte au plus $2 joueurs"),
            (new Regex(@"^Only the host can start the game$"), "Seul l'hôte peut lancer la partie"),
        };

        /// <summary>Engine decision-title templates (English, values interpolated) →
        /// French. Checked in order; first match wins. Keep the more specific
        /// patterns ("Return a …") before the general ones ("Return …").</summary>
        public static readonly List<(Regex, string)> DecisionTitles = new()
        {
            (new Regex(@"^Assign (\d+) damage between your opponents$"),
                "Répartissez $1 dégâts entre vos adversaires"),
            (new Regex(@"^(.+) assigns (\d+) damage — reveal shields\?$"),
                "$1 vous inflige $2 dégâts — révéler des boucliers ?"),
            (new Regex(@"^Banish a card from your hand or discard pile\?$"),
                "Bannir une carte de votre main ou de votre défausse ?"),
            (new Regex(@"^Banish up to (\d+) cards from your hand/discard\?$"),
                "Bannissez jusqu'à $1 cartes de votre main/défausse ?"),
            (new Regex(@"^Banish up to 3 allies from your hand/discard and gain their effects$"),
                "Bannissez jusqu'à 3 alliés de votre main/défausse et gagnez leurs effets"),
            (new Regex(@"^Blood for Blood: banish a card you played this turn\?$"),
                "Le Sang Appelle le Sang : bannir une carte jouée ce tour ?"),
            (new Regex(@"^Copy the effect of a revealed ally$"),
                "Copiez l'effet d'un allié révélé"),
            (new Regex(@"^Copy the effect of a (.+) you played this turn$"),
                "Copiez l'effet d'une carte ($1) jouée ce tour"),
            (new Regex(@"^Destroy a champion you control to gain 5 power\?$"),
                "Détruire un de vos champions pour gagner 5 puissance ?"),
            (new Regex(@"^Destroy an enemy champion$"),
                "Détruisez un champion adverse"),
            (new Regex(@"^Keep fast-played cards\? \(they join your discard pile\)$"),
                "Conserver les cartes enrôlées ? (elles rejoignent votre défausse)"),
            (new Regex(@"^Put an Aion card from your discard pile on top of your deck\?$"),
                "Placer une carte Aion de votre défausse sur votre deck ?"),
            (new Regex(@"^Put one revealed champion into your hand\?$"),
                "Prendre un champion révélé en main ?"),
            (new Regex(@"^Recruit an additional relic to your hand$"),
                "Recrutez une relique supplémentaire dans votre main"),
            (new Regex(@"^Repeat the effect once\? \(you played an Aion card\)$"),
                "Répéter l'effet une fois ? (carte Aion jouée)"),
            (new Regex(@"^Reset a champion you control\?$"),
                "Redresser un de vos champions ?"),
            (new Regex(@"^Reveal an Infinity Shard to gain 2 mastery\?$"),
                "Révéler un Éclat de l'Infini pour gagner 2 maîtrise ?"),
            (new Regex(@"^Reveal cards to complete Dominion\? \((.+) needed\)$"),
                "Révéler des cartes pour la Domination ? (il manque : $1)"),
            (new Regex(@"^Reveal your deck's top 3 cards\?$"),
                "Révéler les 3 cartes du dessus de votre deck ?"),
            (new Regex(@"^Take an additional destiny from the row$"),
                "Prenez une Destinée supplémentaire de la rangée"),
            (new Regex(@"^Choose an opponent to lose (\d+) mastery$"),
                "Choisissez un adversaire qui perd $1 maîtrise"),
            (new Regex(@"^Discard (\d+) cards?$"),
                "Défaussez $1 carte(s)"),
            (new Regex(@"^Fast-play an ally costing (\d+) or less for free \(you keep it\)\?$"),
                "Enrôler gratuitement un allié coûtant $1 ou moins (vous le gardez) ?"),
            (new Regex(@"^Put (.+) on top of your deck\?$"),
                "Placer $1 sur votre deck ?"),
            (new Regex(@"^Recruit a card costing (\d+) or less for free$"),
                "Recrutez gratuitement une carte coûtant $1 ou moins"),
            (new Regex(@"^Return a champion from your discard pile to your hand(\??)$"),
                "Reprenez un champion de votre défausse en main$1"),
            (new Regex(@"^Return a mercenary from your discard pile to your hand(\??)$"),
                "Reprenez un mercenaire de votre défausse en main$1"),
            (new Regex(@"^Return a (\w+) card from your discard pile to your hand(\??)$"),
                "Reprenez une carte $1 de votre défausse en main$2"),
            (new Regex(@"^Return a (.+) from your discard pile to your hand(\??)$"),
                "Reprenez ($1) de votre défausse en main$2"),
            (new Regex(@"^Return (.+) from your discard pile to your hand\?$"),
                "Reprenez $1 de votre défausse en main ?"),
            (new Regex(@"^Reveal a (.+) ally from your hand to trigger Unify\?$"),
                "Révéler un allié $1 de votre main pour l'Union ?"),
            (new Regex(@"^Take a destiny \((\d+) of 2\)$"),
                "Prenez une Destinée ($1 sur 2)"),
            (new Regex(@"^(.+): recruit it or banish it\?$"),
                "$1 : la recruter ou la bannir ?"),
            (new Regex(@"^Warp: fast-play an ally costing (\d+) or less for free\?$"),
                "Distorsion : enrôlez gratuitement un allié coûtant $1 ou moins ?"),
            (new Regex(@"^Warp: fast-play any ally from the row for free\?$"),
                "Distorsion : enrôlez gratuitement un allié de la rivière ?"),
            (new Regex(@"^Recruit a relic \(free, once per game\)$"),
                "Recrutez une relique (gratuit, une fois par partie)"),
            (new Regex(@"^Keep fast-played cards\?.*$"),
                "Conserver les cartes enrôlées ?"),
        };
    }
}
