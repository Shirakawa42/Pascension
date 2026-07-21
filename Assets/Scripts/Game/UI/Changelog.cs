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
                "· This changelog — one per game, from the main menu.",
                "· Ce journal des modifications — un par jeu, depuis le menu principal."),
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
            new Entry("2026-07-21",
                "· Ingeminex attack after you draw your new hand — their discards now hit the hand you keep.\n" +
                "· Destiny picks happen on the board: the row glows, and your piles stay browsable while you decide.\n" +
                "· DECK LIST button: every card you own, cheapest first, whatever its zone.\n" +
                "· Keyword tooltips beside the card preview (Unify, Warp, Shield…).",
                "· Les Ingeminex attaquent après la pioche de votre nouvelle main — leurs défausses touchent la main que vous gardez.\n" +
                "· Les destinées se choisissent sur le plateau : la rangée s'illumine et vos piles restent consultables pendant la décision.\n" +
                "· Bouton LISTE DU DECK : toutes vos cartes, de la moins chère à la plus chère, quelle que soit leur zone.\n" +
                "· Infobulles des mots-clés à côté de l'aperçu de carte (Union, Distorsion, Bouclier…)."),
            new Entry("2026-07-20",
                "· RANDOM character option; no duplicate characters; random first player.",
                "· Option personnage ALÉATOIRE ; plus de personnages en double ; premier joueur tiré au sort."),
        };
    }
}
