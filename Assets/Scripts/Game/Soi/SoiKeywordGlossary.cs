using System.Collections.Generic;
using System.Text.RegularExpressions;
using Shards.Engine;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Hearthstone-style keyword glossary for the SoI hover tooltips: cards carry the
    /// bare keyword, hovering explains how it ACTIVATES. Detection runs on the ENGLISH
    /// def text (engine strings stay English); titles and explanations localize at
    /// display time via Loc.T, so every string here needs a LocFrench entry.
    /// </summary>
    public static class SoiKeywordGlossary
    {
        public readonly struct Entry
        {
            public readonly string Title;   // English key — localize with Loc.T
            public readonly string Text;    // English key — localize with Loc.T

            public Entry(string title, string text)
            {
                Title = title;
                Text = text;
            }
        }

        private static readonly Regex Threshold = new Regex(@"\bM\d+\b");

        /// <summary>Keywords present on this card, most mechanic-defining first.
        /// Callers cap how many they render.</summary>
        public static List<Entry> For(ShardsCardDef def)
        {
            var entries = new List<Entry>();
            if (def == null) return entries;
            string text = def.RulesText ?? "";

            if (def.Shield > 0 || def.DynamicShield != null)
                entries.Add(new Entry("Shield",
                    "While in hand: when an opponent's attack hits you, reveal this to prevent that much damage — it stays in your hand."));
            if (def.Type == ShardsCardType.Mercenary)
                entries.Add(new Entry("Mercenary",
                    "Recruit it normally, or fast-play it for its cost: the effect happens now, then it goes to the bottom of the center deck at end of turn."));
            if (Regex.IsMatch(text, @"\bexhaust", RegexOptions.IgnoreCase)) // "Exhaust:", "exhausts"
                entries.Add(new Entry("Exhaust",
                    "Tap this ready card on your turn to use the ability; it readies again at your end phase."));
            if (Regex.IsMatch(text, @"\bUnify\b"))
                entries.Add(new Entry("Unify",
                    "Active if you played another card of this faction this turn, or reveal one from your hand as you play it."));
            if (Regex.IsMatch(text, @"\bDominion\b"))
                entries.Add(new Entry("Dominion",
                    "Active if you played or revealed a Homodeus, an Undergrowth AND a Wraethe card this turn."));
            if (Regex.IsMatch(text, @"\bInspire\b"))
                entries.Add(new Entry("Inspire",
                    "Active while you control at least one champion."));
            if (Regex.IsMatch(text, @"\bEcho\b"))
                entries.Add(new Entry("Echo",
                    "Counts the matching faction cards in your discard pile."));
            if (Regex.IsMatch(text, @"\bWarp\b"))
                entries.Add(new Entry("Warp",
                    "Fast-play an ally from the row for free: its effect happens now, then it returns to the bottom of the center deck at end of turn."));
            if (Threshold.IsMatch(text))
                entries.Add(new Entry("Mastery threshold",
                    "Mn effects need Mastery n or more, checked the moment you play or exhaust the card."));
            return entries;
        }
    }
}
