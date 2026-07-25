using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>Dumps real mid-game positions as COMPLETE, OMNISCIENT, human-readable text.
    ///
    /// This is not a debug dump — it is the input to expert consultation. Whoever reads one
    /// of these (a strong human or a strong model) must be able to decide the turn without
    /// asking a single follow-up question, so it deliberately shows everything the engine
    /// knows, including both hands and both deck contents, and it inlines the full rules
    /// text of every card that appears. Hidden information is intentional here: we are
    /// asking "what is the right play", not simulating an agent's view.
    ///
    /// Positions are sampled uniformly over the acting player's PRIORITY points via
    /// reservoir sampling, so they are representative of real decisions rather than of
    /// turn starts.</summary>
    public static class DumpPositionsCommand
    {
        public static int Run(Cli cli)
        {
            int games = cli.GetInt("--games", 12);
            int perGame = cli.GetInt("--sample", 2);
            ulong seedBase = cli.GetULong("--seed-base", 505000);
            string botKind = cli.GetStr("--bots", "greedy");
            int budget = cli.GetInt("--budget", 0);
            string outDir = cli.GetStr("--out",
                Path.Combine(SimConfig.FindRepoRoot(), "Tools", "ShardsData", "positions"));
            // Skip the opening turns: round 1-2 positions are nearly forced and teach an
            // evaluator nothing.
            int minRound = cli.GetInt("--min-round", 3);
            cli.RejectUnknown();

            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
            Directory.CreateDirectory(outDir);
            var chars = ShardsContentRegistry.CharactersFor(SimConfig.AllDlc);
            var factory = new BotFactory(botKind, budget);
            var rng = new DeterministicRng(seedBase, 77);

            int written = 0;
            for (int g = 0; g < games; g++)
            {
                ulong seed = seedBase + (ulong)g;
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var specs = new List<PlayerSpec>
                {
                    new() { Name = "P0", CharacterId = chars[a] },
                    new() { Name = "P1", CharacterId = chars[b] }
                };
                var adapter = new ShardsEngineAdapter(
                    ShardsContentRegistry.StandardConfig(seed, specs, SimConfig.AllDlc));
                var engine = adapter.Inner;
                var seats = new IBotAgent[2];
                for (int i = 0; i < 2; i++) seats[i] = factory.Create(seed, i, engine);

                var reservoir = new List<string>(perGame);
                var sampleRng = new DeterministicRng(seed * 7919 + 13, 31);
                int seen = 0, guard = 0;

                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    if (pending.Kind == PendingInputKind.Priority &&
                        engine.State.Round >= minRound)
                    {
                        seen++;
                        int slot = reservoir.Count < perGame ? reservoir.Count : sampleRng.Next(seen);
                        if (slot < perGame)
                        {
                            string text = Render(engine, pending.PlayerIndex, seed, g);
                            if (slot < reservoir.Count) reservoir[slot] = text;
                            else reservoir.Add(text);
                        }
                    }
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }

                foreach (string text in reservoir)
                {
                    File.WriteAllText(Path.Combine(outDir, $"pos-{written:D3}.md"), text);
                    written++;
                }
            }

            Console.WriteLine($"dumped {written} positions from {games} games ({botKind}) -> {outDir}");
            return 0;
        }

        // ------------------------------------------------------------------ rendering

        private static string Render(ShardsEngine engine, int actingIndex, ulong seed, int gameIndex)
        {
            var state = engine.State;
            var me = state.Players[actingIndex];
            var opp = state.Players[1 - actingIndex];
            var sb = new StringBuilder();
            var mentioned = new SortedDictionary<string, ShardsCardDef>(StringComparer.Ordinal);

            sb.AppendLine($"# Shards of Infinity — position (game seed {seed}, #{gameIndex})");
            sb.AppendLine();
            sb.AppendLine($"**Round {state.Round}. It is {me.Name}'s turn, and {me.Name} has priority now.**");
            sb.AppendLine($"DLC active: {state.Dlc}. Mastery cap {state.Rules.MasteryCap}, " +
                          $"starting/max health {state.Rules.StartingHealth}, hand size {state.Rules.HandSize}.");
            sb.AppendLine();
            sb.AppendLine("You are being asked: **what would you do for the rest of this turn, and why?** " +
                          "Every piece of information the engine has is below, including your opponent's " +
                          "hand and both deck contents (which a real player could not see).");
            sb.AppendLine();

            AppendPlayer(sb, engine, me, "YOU (to act)", mentioned);
            AppendPlayer(sb, engine, opp, "OPPONENT", mentioned);

            // ---- the shop
            sb.AppendLine("## Center row (the shop)");
            sb.AppendLine();
            sb.AppendLine("| Slot | Card | Cost | Cost to you | Affordable now |");
            sb.AppendLine("|---|---|---|---|---|");
            for (int i = 0; i < state.CenterRow.Length; i++)
            {
                var card = state.CenterRow[i];
                if (card == null) { sb.AppendLine($"| {i} | *(empty)* | – | – | – |"); continue; }
                Note(mentioned, card.Def);
                int eff = engine.EffectiveCost(me, card.Def);
                sb.AppendLine($"| {i} | {card.Def.Name} | {card.Def.Cost} | {eff} | " +
                              $"{(me.Gems >= eff ? "YES" : "no")} |");
            }
            sb.AppendLine();
            sb.AppendLine($"Center deck has **{state.CenterDeck.Count}** cards left.");
            if ((state.Dlc & ShardsDlc.Duel) != 0)
                sb.AppendLine($"Row reroll would cost **{ShardsEngine.RerollCost(me)}** gems right now " +
                              $"({me.RerollsThisTurn} reroll(s) used this turn; the price climbs 1 per use).");
            sb.AppendLine();

            // ---- shared rows
            if (state.DestinyRow.Count > 0)
            {
                sb.AppendLine("## Destiny row (shared, shrinks and never refills)");
                sb.AppendLine();
                foreach (var card in state.DestinyRow) { Note(mentioned, card.Def); sb.AppendLine($"- {card.Def.Name}"); }
                sb.AppendLine();
                sb.AppendLine($"You {(me.Mastery >= 5 && !me.DestinyTaken ? "**CAN take one right now** (M5+, once per game)" : "cannot take one")}. " +
                              $"{state.DestinyDeck.Count} undealt destinies remain.");
                sb.AppendLine();
            }
            if (state.ActiveMonsters.Count > 0)
            {
                sb.AppendLine("## Ingeminex in play (attack every player at end of the revealing turn)");
                sb.AppendLine();
                foreach (var m in state.ActiveMonsters)
                {
                    Note(mentioned, m.Def);
                    sb.AppendLine($"- **{m.Def.Name}** — defense {engine.EffectiveDefense(m.Owner >= 0 ? state.Players[m.Owner] : me, m)}, " +
                                  $"damage on it this turn {m.DamageThisTurn}");
                }
                sb.AppendLine();
                if (state.PendingMonsterAttacks.Count > 0)
                    sb.AppendLine($"⚠ **{state.PendingMonsterAttacks.Count}** Ingeminex attack(s) will fire at the end of this turn.");
                sb.AppendLine();
            }
            if (state.Banished.Count > 0)
            {
                sb.AppendLine($"Banished pile (removed from the game): {Names(state.Banished)}");
                sb.AppendLine();
            }
            if (state.ExtraTurnForPlayer >= 0)
            {
                sb.AppendLine($"⚠ **{state.Players[state.ExtraTurnForPlayer].Name} takes an EXTRA TURN** after this one.");
                sb.AppendLine();
            }

            AppendRules(sb);
            AppendGlossary(sb, mentioned);
            return sb.ToString();
        }

        private static void AppendPlayer(StringBuilder sb, ShardsEngine engine, ShardsPlayer p,
            string title, SortedDictionary<string, ShardsCardDef> mentioned)
        {
            var state = engine.State;
            sb.AppendLine($"## {title} — {p.Name} ({p.CharacterId})");
            sb.AppendLine();
            sb.AppendLine($"- **Health {p.Health}/{state.Rules.MaxHealth}** · **Mastery {p.Mastery}/{state.Rules.MasteryCap}**" +
                          $" · Gems {p.Gems} · Power {p.Power}");
            int deckSize = p.Deck.Count + p.Hand.Count + p.Discard.Count + p.PlayZone.Count;
            sb.AppendLine($"- Deck size {deckSize} total ({p.Deck.Count} in draw pile, {p.Hand.Count} in hand, " +
                          $"{p.Discard.Count} in discard, {p.PlayZone.Count} in play zone)");
            sb.AppendLine($"- Character {(p.CharacterExhausted ? "EXHAUSTED" : "ready")}" +
                          $" · Focus {(p.FocusedThisTurn ? "used" : "available")} this turn" +
                          $" · destiny pick {(p.DestinyTaken ? "used" : "unused")}" +
                          $" · relic recruit {(p.RelicRecruited ? "used" : "unused")}");

            if ((state.Dlc & ShardsDlc.Duel) != 0)
            {
                var spec = ShardsEngine.HeroAbilityInfo(p.CharacterId);
                if (spec.Name != null)
                    sb.AppendLine($"- Hero ability **{spec.Name}** — {spec.Text} " +
                                  $"({(p.HeroAbilityUsedThisTurn ? "USED this turn" : "available")})");
                if (p.FirstBuyUsedThisTurn) sb.AppendLine("- First buy of the turn already made (Decima's discount spent)");
                if (p.ShieldsDoubledUntilNextTurn) sb.AppendLine("- ⚠ Shields DOUBLED until this player's next turn");
                if (p.NextChampionsIntoPlay > 0) sb.AppendLine($"- Next {p.NextChampionsIntoPlay} champion(s) recruited go straight into play");
            }
            if (p.NextRecruitsToHand > 0) sb.AppendLine($"- Next {p.NextRecruitsToHand} recruit(s) go to HAND");
            if (p.IgnoreShieldsThisTurn) sb.AppendLine("- This turn: opponent shields are IGNORED");
            if (p.ExtraTurnUsed) sb.AppendLine("- Slipstream extra turn already used");
            sb.AppendLine();

            AppendZone(sb, engine, p, "Hand", p.Hand, mentioned, showShield: true);
            AppendZone(sb, engine, p, "Draw pile (order shown is the real draw order)", p.Deck, mentioned);
            AppendZone(sb, engine, p, "Discard pile", p.Discard, mentioned);
            if (p.PlayZone.Count > 0) AppendZone(sb, engine, p, "Played this turn", p.PlayZone, mentioned);

            if (p.Champions.Count > 0)
            {
                sb.AppendLine($"**Champions in play ({p.Champions.Count}):**");
                sb.AppendLine();
                foreach (var c in p.Champions)
                {
                    Note(mentioned, c.Def);
                    int def = engine.EffectiveDefense(p, c);
                    sb.AppendLine($"- **{c.Def.Name}** — defense {def}" +
                                  (c.DamageThisTurn > 0 ? $" ({c.DamageThisTurn} damage on it this turn, clears at end of turn)" : "") +
                                  (c.Def.Taunt ? " · **TAUNT**" : "") +
                                  (c.Exhausted ? " · exhausted" : " · ready"));
                }
                sb.AppendLine();
            }
            if (p.Destinies.Count > 0)
            {
                foreach (var d in p.Destinies) Note(mentioned, d.Def);
                sb.AppendLine($"**Destinies owned:** {Names(p.Destinies)}");
                sb.AppendLine();
            }
            if (p.SetAside.Count > 0)
            {
                foreach (var s in p.SetAside) Note(mentioned, s.Def);
                sb.AppendLine($"**Set aside (not yet earned):** {Names(p.SetAside)}");
                sb.AppendLine();
            }
        }

        private static void AppendZone(StringBuilder sb, ShardsEngine engine, ShardsPlayer p, string title,
            List<ShardsCard> zone, SortedDictionary<string, ShardsCardDef> mentioned, bool showShield = false)
        {
            sb.Append($"**{title} ({zone.Count}):** ");
            if (zone.Count == 0) { sb.AppendLine("*(empty)*"); sb.AppendLine(); return; }
            var parts = new List<string>();
            foreach (var c in zone)
            {
                Note(mentioned, c.Def);
                string extra = "";
                if (showShield)
                {
                    int shield = engine.ShieldValue(p, c);
                    if (shield > 0) extra = $" [shield {shield}]";
                }
                parts.Add(c.Def.Name + extra);
            }
            sb.AppendLine(string.Join(", ", parts));
            sb.AppendLine();
        }

        private static void AppendRules(StringBuilder sb)
        {
            sb.AppendLine("## Rules you need");
            sb.AppendLine();
            sb.AppendLine("- **Winning.** Reduce the opponent to 0 health, or play the Infinity Shard at " +
                          "Mastery 30 (it gives 9999 power = instant lethal). Reaching Mastery 30 does " +
                          "NOT win by itself — you must still draw and play the Shard. Every starting " +
                          "deck contains exactly one.");
            sb.AppendLine("- **Infinity Shard power by mastery:** 2 at M0, 3 at M10, 5 at M20, 9999 at M30.");
            sb.AppendLine("- **Playing costs nothing**; unplayed cards are discarded at end of turn. Gems " +
                          "and power do NOT carry over — unspent gems are simply wasted.");
            sb.AppendLine("- **All power must be assigned** at end of turn (you cannot bank it or decline " +
                          "to attack). It goes to the opponent's face and/or their champions.");
            sb.AppendLine("- **Champions die only in the end-of-turn damage split** (or to destroy effects). " +
                          "Damage on a champion clears at end of turn, so partial damage is wasted.");
            sb.AppendLine("- **Taunt** forces damage into that champion first — it blocks assignment to the " +
                          "owner and their other champions unless the taunt dies in the same split.");
            sb.AppendLine("- **Shields** are revealed from HAND when attacked and STAY in hand (reusable). " +
                          "They never protect champions (except Praetorian-02 / Testudo Vanguard).");
            sb.AppendLine("- **Focus:** exhaust your character + 1 gem → +1 mastery, once per turn.");
            sb.AppendLine("- **Mastery thresholds** unlock card abilities at multiples of 5, a destiny pick " +
                          "at M5, and a relic recruit at M10. Mastery never decreases below earned thresholds.");
            sb.AppendLine("- **Recruited cards go to your DISCARD**, so they dilute the deck until reshuffle. " +
                          "Mercenary fast-play instead returns the card to the bottom of the center deck " +
                          "(pure tempo, no dilution). You can never deck out — the discard reshuffles mid-draw.");
            sb.AppendLine("- **Ingeminex** sit beside the row and attack every player at the end of the turn " +
                          "they were revealed. Killing one gives its reward and cancels its attack.");
            sb.AppendLine();
        }

        private static void AppendGlossary(StringBuilder sb, SortedDictionary<string, ShardsCardDef> mentioned)
        {
            sb.AppendLine("## Full text of every card above");
            sb.AppendLine();
            sb.AppendLine("| Card | Faction | Type | Cost | Def | Shield | Text |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var def in mentioned.Values)
                sb.AppendLine($"| **{def.Name}** | {def.Faction} | {def.Type} | " +
                              $"{(def.Cost > 0 ? def.Cost.ToString() : "–")} | " +
                              $"{(def.Defense > 0 ? def.Defense.ToString() : "–")} | " +
                              $"{(def.Shield > 0 ? def.Shield.ToString() : "–")} | " +
                              $"{def.RulesText.Replace("\n", " ")} |");
            sb.AppendLine();
        }

        private static void Note(SortedDictionary<string, ShardsCardDef> mentioned, ShardsCardDef def)
        {
            if (def != null && !mentioned.ContainsKey(def.Id)) mentioned[def.Id] = def;
        }

        private static string Names(List<ShardsCard> cards)
        {
            var parts = new List<string>();
            foreach (var c in cards) parts.Add(c.Def.Name);
            return parts.Count == 0 ? "*(none)*" : string.Join(", ", parts);
        }
    }
}
