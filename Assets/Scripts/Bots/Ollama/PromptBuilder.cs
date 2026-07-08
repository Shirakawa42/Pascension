using System.Text;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Heroes;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// Renders a masked ClientSnapshot into compact, deterministic text for the LLM:
    /// game header, all players, market with costs/HP, own hand with rules text, the
    /// stack, and a numbered menu of legal actions (or decision options). Pure
    /// functions over the snapshot + CardDatabase/HeroDatabase — no state, no RNG.
    /// </summary>
    public static class PromptBuilder
    {
        /// <summary>~15-line rules explainer + strict answer-format instruction.</summary>
        public static string SystemPrompt(PendingInputKind kind)
        {
            const string rules =
                "You are playing Pascension, a competitive deck-building race game. Play to win.\n" +
                "Rules in brief:\n" +
                "- 2-4 players race along a 50-step board; each controls a hero that levels 1-10 with XP.\n" +
                "- Each turn you draw 5 cards. Cards you play give action points (AP), damage, or effects; unspent AP and damage are lost at end of turn.\n" +
                "- Spend AP to BUY cards from the 3-tier shared market (Advanced tier needs hero level 4, Elite needs level 8) or to MOVE forward (1 AP per step).\n" +
                "- Spend damage to KILL monsters sitting in the market; kills grant XP and rewards. Marked damage on monsters and the boss clears at end of turn, so a kill must be completed within one turn.\n" +
                "- XP levels your hero, unlocking its active ability (level 3), an upgrade (level 6) and an ultimate (level 9).\n" +
                "- Inns at steps 10/20/30/40 grant a reward and act as a move-back checkpoint.\n" +
                "- Bought and played cards go to your discard pile; the deck reshuffles the discard when it runs out, so purchases strengthen all future hands.\n" +
                "- Playing a card uses a stack: instants may be played in response and the last spell played resolves first. Damage assignment also goes on the stack, so opponents can respond to deny a kill.\n" +
                "- WIN: stand on step 50 and deal 20 damage to the boss, The Gatekeeper, in a single turn (a burst).\n" +
                "Strategy: build AP/damage economy early, kill efficient monsters for XP, then race to step 50 and burst the boss before your opponents do.\n";

            if (kind == PendingInputKind.Decision)
                return rules +
                       "You will be shown the game state and a decision with numbered options. " +
                       "Answer ONLY with the JSON object {\"option_ids\": [<numbers>], \"reason\": \"<short justification>\"} and nothing else.";
            return rules +
                   "You will be shown the game state and a numbered list of your legal actions. " +
                   "Answer ONLY with the JSON object {\"action_index\": <number>, \"reason\": \"<short justification>\"} and nothing else.";
        }

        /// <summary>Full user prompt: state sections + the action menu or decision options.</summary>
        public static string BuildUserPrompt(ClientSnapshot view, PendingSnap pending)
        {
            var sb = new StringBuilder(4096);
            AppendGame(sb, view);
            AppendPlayers(sb, view);
            AppendMarket(sb, view);
            AppendHand(sb, view);
            AppendStack(sb, view);
            if (pending != null && pending.Kind == PendingInputKind.Decision && pending.Decision != null)
                AppendDecision(sb, pending.Decision);
            else
                AppendActions(sb, view, pending);
            return sb.ToString();
        }

        /// <summary>One-line, human-readable description of a legal action, names resolved
        /// from the snapshot + CardDatabase (falls back to action.Describe()).</summary>
        public static string DescribeAction(PlayerAction action, ClientSnapshot view)
        {
            switch (action)
            {
                case PlayCardAction play:
                {
                    var card = FindInHand(view, play.CardInstanceId);
                    if (card == null || !CardDatabase.TryGet(card.DefId, out var def))
                        return play.Describe();
                    return $"Play '{def.Name}' ({def.TypeLine}, cost {def.Cost}): {Clean(def.RulesText)}";
                }
                case BuyCardAction buy:
                {
                    var slot = MarketSlot(view, buy.TierIndex, buy.SlotIndex);
                    if (slot == null || slot.DefId == null || !CardDatabase.TryGet(slot.DefId, out var def))
                        return buy.Describe();
                    return $"Buy '{def.Name}' for {def.Cost} AP ({Market.TierFromIndex(buy.TierIndex)} slot {buy.SlotIndex}): {Clean(def.RulesText)}";
                }
                case AssignDamageAction dmg:
                    return $"Assign {dmg.Amount} damage to {FormatTarget(dmg.Target, view)} (lethal)";
                case ActivateAbilityAction act:
                {
                    var source = FindPermanent(view, act.SourceInstanceId);
                    if (source == null || !CardDatabase.TryGet(source.DefId, out var def) ||
                        act.AbilityIndex < 0 || act.AbilityIndex >= def.ActivatedAbilities.Count)
                        return act.Describe();
                    return $"Activate '{def.Name}': {def.ActivatedAbilities[act.AbilityIndex].Description}";
                }
                case UseHeroAbilityAction heroUse:
                {
                    var hero = SafeHero(view.Players[view.ViewerIndex].HeroId);
                    var ability = heroUse.Ultimate ? hero?.Ultimate : hero?.Active;
                    if (ability == null)
                        return heroUse.Describe();
                    string kind = heroUse.Ultimate ? "ultimate" : "active";
                    return $"Use hero {kind} ({ability.ApCost} AP): {ability.Description}";
                }
                default:
                    return action.Describe();
            }
        }

        // ---------- sections ----------

        private static void AppendGame(StringBuilder sb, ClientSnapshot view)
        {
            sb.Append("== GAME ==\n");
            sb.Append($"Round {view.Round} | Phase {view.Phase} | Turn: P{view.TurnPlayerIndex} | You are P{view.ViewerIndex}\n");
            string bossName = view.Boss != null ? CardName(view.Boss.DefId) : "The Gatekeeper";
            int marked = view.Boss?.MarkedDamage ?? 0;
            sb.Append($"Boss: {bossName} — {view.BossHp} HP ({marked} damage marked). Kill it from step 50 to win.\n");
        }

        private static void AppendPlayers(StringBuilder sb, ClientSnapshot view)
        {
            sb.Append("== PLAYERS ==\n");
            foreach (var p in view.Players)
            {
                string you = p.Index == view.ViewerIndex ? " (YOU)" : "";
                sb.Append($"P{p.Index}{you}: {p.Name}, hero {HeroName(p.HeroId)} | level {p.Level} (XP {p.Xp}) | step {p.Position} | AP {p.Ap} | damage pool {p.DamagePool} | hand {p.HandCount} | deck {p.DeckCount} | discard {p.Discard.Count}");
                if (p.Conceded) sb.Append(" | CONCEDED");
                sb.Append('\n');
                var extras = new StringBuilder();
                for (int i = 0; i < p.Equipment.Length; i++)
                    if (p.Equipment[i] != null)
                        extras.Append($"{(extras.Length > 0 ? ", " : "")}{CardName(p.Equipment[i].DefId)}{(p.Equipment[i].Tapped ? " (tapped)" : "")}");
                foreach (var relic in p.Relics)
                    extras.Append($"{(extras.Length > 0 ? ", " : "")}{CardName(relic.DefId)}{(relic.Tapped ? " (tapped)" : "")}");
                if (extras.Length > 0)
                    sb.Append($"  in play: {extras}\n");
            }
        }

        private static void AppendMarket(StringBuilder sb, ClientSnapshot view)
        {
            sb.Append("== MARKET ==\n");
            for (int t = 0; t < view.MarketRows.Length; t++)
            {
                var tier = Market.TierFromIndex(t);
                string gate = tier == CardTier.Advanced ? ", needs level 4" : tier == CardTier.Elite ? ", needs level 8" : "";
                sb.Append($"{tier} (pile {view.PileCounts[t]}{gate}):\n");
                var row = view.MarketRows[t];
                for (int s = 0; s < row.Length; s++)
                {
                    var card = row[s];
                    if (card == null || card.DefId == null || !CardDatabase.TryGet(card.DefId, out var def))
                    {
                        sb.Append($"  slot {s}: (empty)\n");
                        continue;
                    }
                    if (def.IsMonster)
                        sb.Append($"  slot {s}: {def.Name} — monster, {def.MonsterHp} HP ({card.MarkedDamage} marked): {Clean(def.RulesText)}\n");
                    else
                        sb.Append($"  slot {s}: {def.Name} — cost {def.Cost}\n");
                }
            }
        }

        private static void AppendHand(StringBuilder sb, ClientSnapshot view)
        {
            sb.Append("== YOUR HAND ==\n");
            var hand = view.Players[view.ViewerIndex].Hand;
            if (hand.Count == 0)
                sb.Append("  (empty)\n");
            foreach (var card in hand)
            {
                if (CardDatabase.TryGet(card.DefId, out var def))
                    sb.Append($"  #{card.InstanceId} {def.Name} ({def.TypeLine}, cost {def.Cost}): {Clean(def.RulesText)}\n");
                else
                    sb.Append($"  #{card.InstanceId} unknown card\n");
            }
        }

        private static void AppendStack(StringBuilder sb, ClientSnapshot view)
        {
            if (view.Stack.Count == 0)
                return;
            sb.Append("== STACK (last item resolves first) ==\n");
            for (int i = 0; i < view.Stack.Count; i++)
            {
                var item = view.Stack[i];
                string top = i == view.Stack.Count - 1 ? " <- resolves next" : "";
                string desc = string.IsNullOrEmpty(item.Description) ? CardName(item.DefId) : Clean(item.Description);
                var targets = new StringBuilder();
                foreach (var t in item.Targets)
                    targets.Append($"{(targets.Length > 0 ? ", " : "")}{FormatTarget(t, view)}");
                string targetText = targets.Length > 0 ? $" targeting {targets}" : "";
                sb.Append($"  {i}: {item.Kind} by P{item.ControllerIndex} — {desc}{targetText}{top}\n");
            }
        }

        private static void AppendActions(StringBuilder sb, ClientSnapshot view, PendingSnap pending)
        {
            sb.Append("== YOUR LEGAL ACTIONS ==\n");
            var legal = pending?.LegalActions;
            if (legal == null || legal.Count == 0)
            {
                sb.Append("  (none)\n");
                return;
            }
            for (int i = 0; i < legal.Count; i++)
                sb.Append($"[{i}] {DescribeAction(legal[i], view)}\n");
            sb.Append($"Choose exactly one action_index between 0 and {legal.Count - 1}.\n");
        }

        private static void AppendDecision(StringBuilder sb, DecisionRequest req)
        {
            sb.Append("== DECISION ==\n");
            sb.Append($"{Clean(req.Title)} ({req.Kind})\n");
            foreach (var option in req.Options)
            {
                string label = string.IsNullOrEmpty(option.Label)
                    ? (option.Target.HasValue ? option.Target.Value.ToString() : $"card #{option.CardInstanceId}")
                    : Clean(option.Label);
                sb.Append($"[{option.Id}] {label}\n");
            }
            string ordered = req.Ordered ? ", in your preferred order" : "";
            sb.Append($"Answer with option_ids containing between {req.Min} and {req.Max} ids from the list above{ordered}.\n");
        }

        // ---------- helpers ----------

        private static string CardName(string defId)
        {
            if (string.IsNullOrEmpty(defId)) return "unknown card";
            return CardDatabase.TryGet(defId, out var def) ? def.Name : defId;
        }

        private static string HeroName(string heroId)
        {
            var hero = SafeHero(heroId);
            return hero?.Name ?? heroId ?? "none";
        }

        private static HeroDefinition SafeHero(string heroId)
        {
            if (string.IsNullOrEmpty(heroId)) return null;
            try { return HeroDatabase.Get(heroId); }
            catch (System.Collections.Generic.KeyNotFoundException) { return null; }
        }

        private static CardSnap FindInHand(ClientSnapshot view, int instanceId)
        {
            foreach (var card in view.Players[view.ViewerIndex].Hand)
                if (card.InstanceId == instanceId)
                    return card;
            return null;
        }

        private static CardSnap FindPermanent(ClientSnapshot view, int instanceId)
        {
            var p = view.Players[view.ViewerIndex];
            foreach (var e in p.Equipment)
                if (e != null && e.InstanceId == instanceId)
                    return e;
            foreach (var relic in p.Relics)
                if (relic.InstanceId == instanceId)
                    return relic;
            return null;
        }

        private static CardSnap MarketSlot(ClientSnapshot view, int tierIndex, int slotIndex)
        {
            if (tierIndex < 0 || tierIndex >= view.MarketRows.Length) return null;
            var row = view.MarketRows[tierIndex];
            if (row == null || slotIndex < 0 || slotIndex >= row.Length) return null;
            return row[slotIndex];
        }

        private static string FormatTarget(TargetRef target, ClientSnapshot view)
        {
            switch (target.Kind)
            {
                case TargetKind.Boss:
                    return "the boss";
                case TargetKind.MonsterSlot:
                {
                    var card = MarketSlot(view, Market.TierIndex((CardTier)target.A), target.B);
                    string name = card != null ? CardName(card.DefId) : "monster";
                    return $"'{name}' ({(CardTier)target.A} slot {target.B})";
                }
                case TargetKind.Player:
                    return target.A >= 0 && target.A < view.Players.Count ? $"P{target.A} ({view.Players[target.A].Name})" : $"P{target.A}";
                default:
                    return target.ToString();
            }
        }

        /// <summary>Collapse rules text to a single line for compact prompts.</summary>
        private static string Clean(string text) =>
            string.IsNullOrEmpty(text) ? "" : text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
