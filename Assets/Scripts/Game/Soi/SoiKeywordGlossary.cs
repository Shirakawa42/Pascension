using System.Collections.Generic;
using System.Text.RegularExpressions;
using Shards.Engine;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Hearthstone-style keyword glossary for the SoI hover tooltips: cards carry the
    /// bare keyword, hovering explains how it ACTIVATES — one short sentence each.
    /// Detection runs on the ENGLISH def text (engine strings stay English); titles and
    /// texts localize at display time via Loc.T, so every string here needs a LocFrench
    /// entry. Faction-scoped keywords (Unify, Echo) carry the card's faction in Arg —
    /// the display formats it into the {0} slot, localized separately.
    /// </summary>
    public static class SoiKeywordGlossary
    {
        public readonly struct Entry
        {
            public readonly string Title; // English key — localize with Loc.T
            public readonly string Text;  // English key (may hold {0}) — localize with Loc.T
            public readonly string Arg;   // English faction word for {0}, or null

            public Entry(string title, string text, string arg = null)
            {
                Title = title;
                Text = text;
                Arg = arg;
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
            string faction = def.Faction.ToString();

            if (def.Shield > 0 || def.DynamicShield != null)
                entries.Add(new Entry("Shield",
                    "Reveal it from your hand when attacked to prevent that much damage. It stays in your hand."));
            if (def.Type == ShardsCardType.Mercenary)
                entries.Add(new Entry("Mercenary",
                    "Recruit it, or fast-play it for its cost: effect now, then under the center deck."));
            if (Regex.IsMatch(text, @"\bexhaust", RegexOptions.IgnoreCase)) // "Exhaust:", "exhausts"
                entries.Add(new Entry("Exhaust",
                    "Tap this ready card to use its ability. It readies at your end phase."));
            if (Regex.IsMatch(text, @"\bUnify\b"))
                entries.Add(new Entry("Unify",
                    "Active if you played or reveal another {0} ally as you play this card.", faction));
            if (Regex.IsMatch(text, @"\bDominion\b"))
                entries.Add(new Entry("Dominion",
                    "Active if you played or revealed a Homodeus, an Undergrowth and a Wraethe card this turn."));
            if (Regex.IsMatch(text, @"\bInspire\b"))
                entries.Add(new Entry("Inspire",
                    "Active while you control a champion."));
            if (Regex.IsMatch(text, @"\bEcho\b"))
                entries.Add(new Entry("Echo",
                    "Grows with each {0} card in your discard pile.", faction));
            if (Regex.IsMatch(text, @"\bWarp\b"))
                entries.Add(new Entry("Warp",
                    "Fast-play an ally from the row for free; it goes under the center deck at end of turn."));
            if (Threshold.IsMatch(text))
                entries.Add(new Entry("Mastery threshold",
                    "Needs that much Mastery when you play or exhaust this card."));
            return entries;
        }
    }
}
