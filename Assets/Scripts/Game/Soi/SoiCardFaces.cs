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

        /// <summary>Duel of Doom: "soiability:&lt;heroId&gt;" renders the hero's unique
        /// ability as its own real card — its OWN art (`soiability_&lt;hero&gt;`) and the M5
        /// gate as the standard mastery pill. Shown beside the portrait in game, in the
        /// hero draft, and in the play log.</summary>
        public const string AbilityPrefix = "soiability:";

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
            if (defId.StartsWith(AbilityPrefix))
                return AbilityFace(defId.Substring(AbilityPrefix.Length));

            if (!ShardsCardDatabase.TryGet(defId, out var def))
                return null;

            bool champion = def.IsChampion || def.IsMonster;
            bool showCost = def.Type == ShardsCardType.Ally ||
                            def.Type == ShardsCardType.Mercenary ||
                            def.Type == ShardsCardType.Champion;
            return new CardView.ExternalFace
            {
                Name = UI.Loc.CardName(def.Id, def.Name),
                TypeLine = TypeLine(def),
                RulesText = Iconize(UI.Loc.CardText(def.Id, def.RulesText)),
                // Duel errata defs (<id>_duel with ReplacesId) are the SAME card visually —
                // they inherit the base card's art instead of pointing at a missing PNG.
                ArtId = string.IsNullOrEmpty(def.ReplacesId) ? def.Id : def.ReplacesId,
                FrameColor = FactionColor(def.Faction),
                ShowCost = showCost,
                CostText = def.Cost.ToString(),
                ShowBadge = champion && def.Defense > 0,
                BadgeText = def.Defense.ToString(),
                ShowShield = def.Shield > 0 || def.DynamicShield != null,
                ShieldValueText = def.DynamicShield != null ? "M" : def.Shield.ToString(),
                IsMercenary = def.Type == ShardsCardType.Mercenary
            };
        }

        /// <summary>The Focus rules line, localized — shared by the character face and the
        /// Duel hero-draft panel.</summary>
        public static string FocusText() => UI.Loc.French
            ? "Concentration — Activez : payez 1 cristal, gagnez 1 maîtrise. Une fois par tour."
            : "Focus — Exhaust: pay 1 gem, gain 1 mastery. Once per turn.";

        private static CardView.ExternalFace CharacterFace(string characterId)
        {
            // Focus only — the Duel hero ability lives on its OWN card (AbilityFace),
            // so the portrait never mentions it.
            return new CardView.ExternalFace
            {
                Name = Shards.Content.ShardsContentRegistry.CharacterDisplayName(characterId),
                TypeLine = UI.Loc.French ? "Héros" : "Character",
                RulesText = Iconize(FocusText()),
                ArtId = "soichar_" + characterId,
                FrameColor = new Color(0.5f, 0.42f, 0.2f, 1f),
                ShowCost = false,
                ShowBadge = false
            };
        }

        /// <summary>The Duel hero ability as a card face. The spec text opens with its
        /// own "M5, …"/"M5 passive:" gate — rewritten to the em-dash form so Iconize
        /// renders the standard gold mastery pill, exactly like any card. Its art is its
        /// OWN piece (`soiability_&lt;hero&gt;`, prompts in Tools/ShardsData/art-prompts-extra.json)
        /// depicting the ability, so it never reads as a duplicate of the hero card.</summary>
        private static CardView.ExternalFace? AbilityFace(string characterId)
        {
            var spec = ShardsEngine.HeroAbilityInfo(characterId);
            if (spec.Name == null) return null;
            string text = Regex.Replace(UI.Loc.T(spec.Text), @"^M(\d+)[,:]?\s*", "M$1 — ");
            return new CardView.ExternalFace
            {
                Name = UI.Loc.T(spec.Name),
                TypeLine = UI.Loc.French ? "Capacité de héros" : "Hero Ability",
                RulesText = Iconize(text),
                ArtId = "soiability_" + characterId,
                FrameColor = new Color(0.5f, 0.42f, 0.2f, 1f),
                ShowCost = false,
                ShowBadge = false
            };
        }

        public static string TypeLine(ShardsCardDef def)
        {
            if (UI.Loc.French)
            {
                // Official IELLO type lines: "Allié Ordre", "Allié Mercenaire Spectra",
                // "Relique Homodeus — Champion", faction renames Maquis/Spectra.
                string fr = FactionFrench(def.Faction);
                string suffix = string.IsNullOrEmpty(fr) ? "" : " " + fr;
                return def.Type switch
                {
                    ShardsCardType.Monster => "Ingeminex",
                    ShardsCardType.Starter => "Objet — Allié",
                    ShardsCardType.Ally => "Allié" + suffix,
                    ShardsCardType.Champion => "Champion" + suffix,
                    ShardsCardType.Mercenary => "Allié Mercenaire" + suffix,
                    ShardsCardType.Relic => "Relique" + suffix + (def.IsChampion ? " — Champion" : " — Allié"),
                    ShardsCardType.Destiny => "Destinée",
                    _ => def.Type.ToString()
                };
            }

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

        /// <summary>Official French faction names (Undergrowth and Wraethe were RENAMED
        /// in the IELLO edition).</summary>
        public static string FactionFrench(ShardsFaction faction) => faction switch
        {
            ShardsFaction.Homodeus => "Homodeus",
            ShardsFaction.Order => "Ordre",
            ShardsFaction.Undergrowth => "Maquis",
            ShardsFaction.Wraethe => "Spectra",
            ShardsFaction.Aion => "Aion",
            _ => ""
        };

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
            // (English and French sentence forms; the French "Bouclier N." sentence
            // starts capitalized — inline lowercase "bouclier" mentions survive.)
            text = Regex.Replace(text, @"Shield (\d+|equal to your \w+)\.\s*", "");
            text = Regex.Replace(text, @"Bouclier (\d+|égal à votre \w+)\.\s*", "");

            // Ingeminex keyword labels at line starts become icons: crossed swords for
            // Attack, a treasure chest for Reward (EN + FR). Line-anchored, so this MUST
            // run while the text still has plain newlines — the paragraph pass below
            // injects a <size> tag at the head of every following line, which would put
            // markup between ^ and the keyword and silently drop the icon.
            text = Regex.Replace(text, @"(?m)^(Attack|Attaque)\s*:\s*", "<sprite name=\"soi_attack\"> ");
            text = Regex.Replace(text, @"(?m)^(Reward|Récompense)\s*:\s*", "<sprite name=\"soi_reward\"> ");

            // Paragraph rhythm (the rule the layout depends on):
            //   • a MASTERY TIER modifies the effect right above it → tight, plain
            //     newline, no gap, so it reads as part of that effect;
            //   • two DISTINCT effects → a visible gap, so nothing reads as a condition
            //     of the line before it.
            // The gap is a half-height blank line, not a full one: a real empty line eats
            // a third of the rules box on a three-effect card.
            const string effectGap = "\n<size=45%>\n</size>";
            // A def may or may not already put a newline before its tier — normalize so
            // the pill's own newline is the ONLY one (that double newline was the "M10
            // floats away from the effect it modifies" bug).
            text = Regex.Replace(text, @"[ \t]*\n[ \t]*(?=M\d+\s*(?::|—|\s(?:Union|Unify|Domination|Dominion)))", "");
            // Every remaining hard break separates distinct effects — EXCEPT before an
            // em-dash bullet, which is a sub-item of the line above it ("Choose one:"),
            // not a new effect.
            text = Regex.Replace(text, @"\n(?!—)", effectGap);

            // Threshold pills (they consume the "M10:" tokens). Threshold syntax comes in
            // three flavors across the sets: inline "M10: …", the destiny leading gate
            // "M10 — …" (em-dash), and the relic "M20 Unify: …" (pill, then the keyword
            // stays). \s also matches NBSP for French "M10 :".
            const string pill =
                "\n<mark=#3A2F1BB4 padding=\"14,14,6,6\"><color=#E4C05A><sprite name=\"soi_mastery\"><b>$1</b></color></mark>  ";
            text = Regex.Replace(text, @"\bM(\d+)\s*(?::|—)\s*", pill);
            text = Regex.Replace(text, @"\bM(\d+)\s+(?=Union|Unify|Domination|Dominion)", pill);
            // Safety net: card texts are paren-free by house style, but if a "(M20: …)"
            // ever slips back in, never leave an opening paren orphaned at a line end.
            text = Regex.Replace(text, @"\(\s*\n", "\n");
            text = text.TrimStart('\n');

            // Tap icon for the exhaust keyword (EN + official FR "Activez :").
            text = Regex.Replace(text, @"\bExhaust\s*:\s*", "<sprite name=\"soi_tap\"> : ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bexhausts?\b", "<sprite name=\"soi_tap\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bActivez\s*(\((\w|\s)+\))?\s*:\s*", "<sprite name=\"soi_tap\">$1 : ");
            text = Regex.Replace(text, @"\bActivez\b\s*", "<sprite name=\"soi_tap\"> "); // em-dash form "Activez — Distorsion 3 :"
            text = Regex.Replace(text, @"\bactivée?s?\b", "<sprite name=\"soi_tap\">");

            // Resource words -> inline icons (EN + FR).
            text = Regex.Replace(text, @"\bgems?\b", "<sprite name=\"soi_gem\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bpower\b", "<sprite name=\"soi_power\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bhealth\b", "<sprite name=\"soi_health\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bmastery\b", "<sprite name=\"soi_mastery\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bhp\b", "<sprite name=\"soi_health\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bcristaux\b|\bcristal\b", "<sprite name=\"soi_gem\">");
            text = Regex.Replace(text, @"\bpuissance\b", "<sprite name=\"soi_power\">");
            text = Regex.Replace(text, @"\bsanté\b", "<sprite name=\"soi_health\">");
            text = Regex.Replace(text, @"\bmaîtrise\b", "<sprite name=\"soi_mastery\">");
            text = Regex.Replace(text, @"\bPV\b", "<sprite name=\"soi_health\">");
            // Inline shield mentions become the shield icon (the leading "Shield N."
            // sentence was already stripped to the corner badge above).
            text = Regex.Replace(text, @"\bshields?\b", "<sprite name=\"soi_shield\">", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bboucliers?\b", "<sprite name=\"soi_shield\">", RegexOptions.IgnoreCase);

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
