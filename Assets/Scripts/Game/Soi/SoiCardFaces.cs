using System.Text.RegularExpressions;
using Pascension.Game.View;
using Shards.Engine;
using UnityEngine;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Bridges Shards of Infinity defs into the shared CardView pipeline: any CardView
    /// bound to a SoI def id (hand cards, flights, showcases, pile tops, previews)
    /// resolves its face here. Faction colors match the tabletop's frames.
    /// Also synthesizes character-portrait faces ("soichar:decima").
    /// </summary>
    public static class SoiCardFaces
    {
        public const string CharacterPrefix = "soichar:";

        public static void Install()
        {
            // The card database must be registered wherever CardViews resolve SoI faces.
            // On a networked CLIENT this is the ONLY place it happens: the client never
            // builds an engine (SoiBootstrap returns early on the session path), and the
            // engine constructor is what normally registers the DB. Without this the
            // client renders blank cards AND cannot buy (OnRowSlotClicked's DB lookup
            // fails, so the click is dropped). Idempotent.
            Shards.Content.ShardsContentRegistry.EnsureRegistered();
            CardView.ExternalFaceResolver = Resolve;
        }

        public static CardView.ExternalFace? Resolve(string defId)
        {
            if (defId.StartsWith(CharacterPrefix))
                return CharacterFace(defId.Substring(CharacterPrefix.Length));

            if (!ShardsCardDatabase.TryGet(defId, out var def))
                return null;

            bool champion = def.IsChampion || def.IsMonster;
            bool showCost = def.Type == ShardsCardType.Ally ||
                            def.Type == ShardsCardType.Mercenary ||
                            def.Type == ShardsCardType.Champion;
            return new CardView.ExternalFace
            {
                Name = def.Name,
                TypeLine = TypeLine(def),
                RulesText = Iconize(def.RulesText),
                ArtId = def.Id,
                FrameColor = FactionColor(def.Faction),
                ShowCost = showCost,
                CostText = def.Cost.ToString(),
                ShowBadge = champion && def.Defense > 0,
                BadgeText = def.Defense.ToString(),
                ShowShield = def.Shield > 0 || def.DynamicShield != null,
                ShieldValueText = def.DynamicShield != null ? "M" : def.Shield.ToString()
            };
        }

        private static CardView.ExternalFace CharacterFace(string characterId)
        {
            string name = Shards.Content.ShardsContentRegistry.CharacterDisplayName(characterId);
            return new CardView.ExternalFace
            {
                Name = name,
                TypeLine = "Character",
                RulesText = Iconize("Focus — Exhaust: pay 1 gem, gain 1 mastery (once per turn)."),
                ArtId = "soichar_" + characterId,
                FrameColor = new Color(0.5f, 0.42f, 0.2f, 1f),
                ShowCost = false,
                ShowBadge = false
            };
        }

        public static string TypeLine(ShardsCardDef def)
        {
            string faction = def.Faction == ShardsFaction.None || def.Faction == ShardsFaction.Monster
                ? "" : def.Faction + " ";
            string line = def.Type switch
            {
                ShardsCardType.Monster => "Ingeminex",
                ShardsCardType.Starter => "Item — Ally",
                ShardsCardType.Mercenary => faction + "Mercenary Ally",
                ShardsCardType.Relic => faction + "Relic" + (def.IsChampion ? " Champion" : " Ally"),
                _ => faction + def.Type
            };
            return line;
        }

        /// <summary>Display-level rich-text pass applied to every SoI rules text:
        /// - the resource words become inline icons (health/mastery/gems/power),
        /// - the exhaust keyword becomes the tap-arrow icon,
        /// - mastery thresholds ("M10: …") start a new line opened by a stylized gold
        ///   pill holding the mastery icon + number (the printed-card look, no "M").
        /// Card DEFINITIONS keep plain words — this is presentation only.</summary>
        public static string Iconize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // The shield value lives in the card's shield badge, not the text box.
            text = Regex.Replace(text, @"Shield (\d+|equal to your \w+)\.\s*", "");

            // Threshold pills first (they consume the "M10:" tokens).
            text = Regex.Replace(text, @"\bM(\d+)\s*[:]\s*",
                "\n<mark=#3A2F1BB4 padding=\"14,14,6,6\"><color=#E4C05A><sprite name=\"soi_mastery\"><b>$1</b></color></mark>  ");

            // Tap icon for the exhaust keyword.
            text = Regex.Replace(text, @"\bExhaust\s*:\s*", "<sprite name=\"soi_tap\"> : ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bexhausts?\b", "<sprite name=\"soi_tap\">", RegexOptions.IgnoreCase);

            // Resource words -> inline icons.
            text = Regex.Replace(text, @"\bgems?\b", "<sprite name=\"soi_gem\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bpower\b", "<sprite name=\"soi_power\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bhealth\b", "<sprite name=\"soi_health\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bmastery\b", "<sprite name=\"soi_mastery\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bhp\b", "<sprite name=\"soi_health\">", RegexOptions.IgnoreCase);
            return text;
        }

        public static Color FactionColor(ShardsFaction faction) => faction switch
        {
            ShardsFaction.Homodeus => new Color(0.52f, 0.46f, 0.26f, 1f),
            ShardsFaction.Order => new Color(0.24f, 0.38f, 0.58f, 1f),
            ShardsFaction.Undergrowth => new Color(0.24f, 0.48f, 0.28f, 1f),
            ShardsFaction.Wraethe => new Color(0.42f, 0.28f, 0.52f, 1f),
            ShardsFaction.Aion => new Color(0.62f, 0.32f, 0.22f, 1f),
            ShardsFaction.Monster => new Color(0.56f, 0.18f, 0.18f, 1f),
            _ => new Color(0.32f, 0.32f, 0.36f, 1f)
        };
    }
}
