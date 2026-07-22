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
            new Entry("2026-07-22",
                "· New bot ranks: PLATINUM and EMERALD — the two toughest opponents yet. PLATINUM plays GOLD's search with a smarter neural network (trained on over a million positions from its own games); EMERALD is the same brain thinking several times longer. Each is a clear step up, and all still answer near-instantly.\n" +
                "· Search bots (SILVER and up) now answer near-instantly — they think a fixed number of moves ahead instead of pausing for a full second, and stop early once the best move is clear.",
                "· Nouveaux rangs de bot : PLATINE et ÉMERAUDE — les deux adversaires les plus coriaces à ce jour. PLATINE joue la recherche d'OR avec un réseau de neurones plus fin (entraîné sur plus d'un million de positions issues de ses propres parties) ; ÉMERAUDE est le même cerveau qui réfléchit plusieurs fois plus longtemps. Chacun est un net cran au-dessus, et tous répondent quasi instantanément.\n" +
                "· Les bots de recherche (ARGENT et plus) répondent désormais presque instantanément — ils anticipent un nombre fixe de coups au lieu de marquer une pause d'une seconde, et s'arrêtent dès que le meilleur coup est clair."),
            new Entry("2026-07-21",
                "· Ingeminex attack after you draw your new hand — their discards now hit the hand you keep.\n" +
                "· Destiny picks happen on the board: the row glows, and your piles stay browsable while you decide.\n" +
                "· DECK LIST button: every card you own, cheapest first, whatever its zone.\n" +
                "· Keyword tooltips beside the card preview (Unify, Warp, Shield…) — and they no longer flicker near it.\n" +
                "· Damage assignment: buttons below the heroes, champion HP on its red disc (green boosted / red reduced), assigned numbers on a backdrop.\n" +
                "· Health, portraits and opponent stats now update live during animations.\n" +
                "· Each hit floats a single damage number (the duplicate smaller one is gone).\n" +
                "· Fixed a crash when Duplication Fabricator copied a revealed Duplication Fabricator (infinite copy loop).\n" +
                "· Bots now climb a ranked ladder: IRON (the old bots), BRONZE (tuned instant AI) and SILVER (search AI that thinks ~1 second per move and plays without seeing your hand) — higher ranks unlock as the AI trains.\n" +
                "· First unlock: GOLD — the search AI now imagines each future with a neural network trained on 60,000 of its own games, and beats SILVER about 4 games out of 5.\n" +
                "· The status line shows when a search bot is thinking, and a bug that could freeze the game during a bot's turn was fixed.\n" +
                "· Returning after a long alt-tab now fast-forwards the replay instead of animating every missed move.",
                "· Les Ingeminex attaquent après la pioche de votre nouvelle main — leurs défausses touchent la main que vous gardez.\n" +
                "· Les destinées se choisissent sur le plateau : la rangée s'illumine et vos piles restent consultables pendant la décision.\n" +
                "· Bouton LISTE DU DECK : toutes vos cartes, de la moins chère à la plus chère, quelle que soit leur zone.\n" +
                "· Infobulles des mots-clés à côté de l'aperçu de carte (Union, Distorsion, Bouclier…) — sans clignoter à son contact.\n" +
                "· Répartition des dégâts : boutons sous les héros, PV des champions sur leur disque rouge (vert si augmentés / rouge si réduits), dégâts assignés sur un fond sombre.\n" +
                "· Santé, portraits et statistiques adverses se mettent à jour en direct pendant les animations.\n" +
                "· Chaque coup n'affiche plus qu'un seul nombre de dégâts (le doublon plus petit a disparu).\n" +
                "· Correction d'un plantage quand le Duplicateur copiait un Duplicateur révélé (boucle de copie infinie).\n" +
                "· Les bots grimpent désormais un classement : FER (les anciens bots), BRONZE (IA optimisée instantanée) et ARGENT (IA à recherche qui réfléchit ~1 seconde par coup, sans voir votre main) — les rangs supérieurs se débloqueront au fil de l'entraînement.\n" +
                "· Premier déblocage : OR — l'IA à recherche imagine désormais chaque futur avec un réseau de neurones entraîné sur 60 000 de ses propres parties, et bat ARGENT environ 4 parties sur 5.\n" +
                "· La ligne d'état indique quand un bot réfléchit, et un bug pouvant geler la partie pendant le tour d'un bot a été corrigé.\n" +
                "· Revenir après un long alt-tab avance rapidement le replay au lieu d'animer chaque coup manqué."),
            new Entry("2026-07-20",
                "· RANDOM character option; no duplicate characters; random first player.",
                "· Option personnage ALÉATOIRE ; plus de personnages en double ; premier joueur tiré au sort."),
        };
    }
}
