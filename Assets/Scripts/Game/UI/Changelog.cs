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
