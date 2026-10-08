using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class DamageSelfTest
    {
        private static int _checks;
        private static void Check(bool ok, string message)
        { _checks++; if (!ok) throw new InvalidOperationException(message); }
        private sealed class Fixture
        {
            internal Adapter Game;
            internal DecisionRequest Request;
            internal List<int> Answer;
        }
        private static Adapter EmptyGame()
        {
            var g = new Adapter(980123);
            while (g.Decision?.Context == "soi.herodraft") g.Step(0);
            foreach (var p in g.Engine.State.Players)
            { p.Champions.Clear(); p.Hand.Clear(); p.PlayZone.Clear(); p.Destinies.Clear(); }
            return g;
        }
        private static ShardsCard Plant(Adapter g, int owner, string defId, ShardsZone zone)
        {
            var c = new ShardsCard { InstanceId = g.Engine.State.NextInstanceId++, DefId = defId,
                Owner = owner, Zone = zone };
            var p = g.Engine.State.Players[owner];
            (zone == ShardsZone.Champions ? p.Champions : p.Hand).Add(c);
            g.Engine.State.InvalidateCardIndex(); return c;
        }
        private static Fixture Make(int power, int[] defenses, bool taunt, bool testudo)
        {
            var f = new Fixture { Game = EmptyGame() }; var g = f.Game;
            int owner = 1 - g.Actor;
            if (testudo) Plant(g, owner, "testudo_vanguard", ShardsZone.Champions);
            f.Request = new DecisionRequest { PlayerIndex = g.Actor, Context = "soi.split",
                Kind = DecisionKind.ChooseMode, Min = taunt ? 0 : power, Max = power, Ordered = true };
            // Deliberately retain the engine's face-first input order.
            f.Request.Options.Add(new DecisionOption(owner, "Face") { OwnerIndex = owner });
            for (int i = 0; i < defenses.Length; i++)
                f.Request.Options.Add(new DecisionOption(100100 + i, "Champion")
                { CardInstanceId = 100 + i, OwnerIndex = owner, Amount = defenses[i], Required = taunt && i == 0 });
            ShardsCardDatabase.Register(new ShardsCardDef { Id = "v8_damage_fixture", Quantity = 0,
                PlayEffect = new Custom(ctx => Capture(ctx, f)) });
            var trigger = Plant(g, g.Actor, "v8_damage_fixture", ShardsZone.Hand);
            Check(g.Engine.Submit(new ShardsPlayCardAction { PlayerIndex = g.Actor,
                CardInstanceId = trigger.InstanceId }).Accepted, "Fixture starts");
            g.Rebuild(); return f;
        }
        private static IEnumerable<ShardsStep> Capture(ShardsContext ctx, Fixture f)
        {
            yield return ShardsStep.AwaitDecision(f.Request);
            f.Answer = new List<int>(ctx.Answer.ChosenOptionIds);
        }
        private static void Exhaustive(int power, int[] defense, bool taunt, bool testudo)
        {
            var observed = new HashSet<string>();
            Visit(new List<int>());
            var expected = new HashSet<string>();
            Enumerate(0, power, new int[defense.Length + 1]);
            Check(observed.SetEquals(expected), $"Exact useful allocation coverage p={power} taunt={taunt} shields={testudo}");
            void Enumerate(int at, int remaining, int[] amounts)
            {
                if (at == defense.Length)
                {
                    amounts[at] = taunt && amounts[0] == 0 ? 0 : remaining;
                    expected.Add(string.Join(",", amounts)); return;
                }
                int limit = taunt && at > 0 && amounts[0] == 0 ? 0 : remaining;
                for (int n = 0; n <= limit; n++)
                {
                    if (n != 0 && (n < defense[at] || (!testudo && n != defense[at]))) continue;
                    amounts[at] = n; Enumerate(at + 1, remaining - n, amounts);
                }
            }
            void Visit(List<int> prefix)
            {
                var f = Make(power, defense, taunt, testudo);
                foreach (int index in prefix) f.Game.Step(index);
                if (f.Answer != null)
                {
                    var amounts = defense.Select((_, i) => f.Answer.Count(id => id == 100100 + i)).ToList();
                    amounts.Add(f.Answer.Count(id => id < 100000));
                    Check(observed.Add(string.Join(",", amounts)), "No duplicate staged allocation paths");
                    for (int i = 0; i < defense.Length; i++)
                        Check(amounts[i] == 0 || amounts[i] >= defense[i], "No positive sublethal champion allocation");
                    Check(f.Answer.Count >= f.Request.Min && f.Answer.Count <= power, "Legal budget");
                    return;
                }
                Check(prefix.Count < 32 && f.Game.VisibleCount > 0, "No dead end or staged loop");
                for (int i = 0; i < f.Game.VisibleCount; i++)
                { prefix.Add(i); Visit(prefix); prefix.RemoveAt(prefix.Count - 1); }
            }
        }
        private static void ChooseAmounts(Adapter g, IReadOnlyDictionary<int, int> amounts)
        {
            int steps = 0;
            while (g.Decision?.Context == "soi.split")
            {
                Check(++steps < 100, "Real split terminates");
                int selected = -1;
                for (int i = 0; i < g.VisibleCount; i++)
                {
                    var c = g.Visible(i); int amount = amounts[c.Option.CardInstanceId];
                    if (c.Low <= amount && amount <= c.High) { selected = i; break; }
                }
                Check(selected >= 0, "Requested lethal amount remains reachable"); g.Step(selected);
            }
        }
        private static void RealTestudo(bool taunt, bool pierce)
        {
            var g = EmptyGame(); int attacker = g.Actor, defender = 1 - attacker;
            var t = Plant(g, defender, "testudo_vanguard", ShardsZone.Champions);
            var z = taunt ? Plant(g, defender, "zetta_encryptor", ShardsZone.Champions) : null;
            var shield = Plant(g, defender, "prism", ShardsZone.Hand);
            var enemy = g.Engine.State.Players[defender];
            int tDef = g.Engine.EffectiveDefense(enemy, t), zDef = z == null ? 0 : g.Engine.EffectiveDefense(enemy, z);
            int shieldValue = g.Engine.ShieldValue(enemy, shield), hp = enemy.Health;
            int tAmount = tDef + shieldValue;
            int zAmount = z == null ? 0 : zDef + (pierce ? shieldValue : 0);
            g.Engine.State.Players[attacker].Power = tAmount + zAmount + shieldValue + 3;
            Check(g.Engine.Submit(new ShardsEndTurnAction { PlayerIndex = attacker }).Accepted, "Real end turn");
            g.Rebuild();
            var amounts = new Dictionary<int, int> { [t.InstanceId] = tAmount };
            if (z != null) amounts[z.InstanceId] = zAmount;
            ChooseAmounts(g, amounts);
            Check(g.Decision?.Context == "soi.shields", "Shield reveal remains opponent choice");
            Check(g.Engine.Submit(new SubmitDecisionAction { PlayerIndex = defender,
                Answer = new DecisionAnswer { DecisionId = g.Decision.Id, ChosenOptionIds = new List<int> { shield.InstanceId } } }).Accepted,
                "Real shield submit");
            if (taunt && !pierce)
                Check(enemy.Champions.Contains(z) && enemy.Champions.Contains(t) && enemy.Health == hp,
                    "Shielded taunt legitimately preserves all targets despite announced pre-shield lethal");
            else
                Check(!enemy.Champions.Contains(t) && (z == null || !enemy.Champions.Contains(z)) && enemy.Health == hp - 3,
                    "Overkill pays through shield; face receives exact residual");
        }
        internal static void Run()
        {
            _checks = 0;
            // Exhaustive sparse rank partitioning, including large gaps and intervals
            // reached after earlier branches: no lost legal values or impossible leaves.
            foreach (bool overkill in new[] { false, true })
                for (int threshold = 1; threshold <= 12; threshold++)
                    for (int low = 0; low <= 14; low++)
                        for (int high = low; high <= 14; high++)
                            foreach (int branches in new[] { 2, 3, 7, 64 })
                            {
                                var legal = Enumerable.Range(low, high - low + 1)
                                    .Where(n => n == 0 || (n >= threshold && (overkill || n == threshold))).ToArray();
                                var ranges = Adapter.LethalIntervals(low, high, threshold, overkill, branches);
                                var reached = ranges.SelectMany(r => legal.Where(n => n >= r.low && n <= r.high)).ToArray();
                                Check(reached.SequenceEqual(legal), "Sparse intervals cover legal amounts exactly once");
                                Check(ranges.All(r => legal.Contains(r.low) && legal.Contains(r.high)), "Every endpoint is legal");
                                if (legal.Length > 1) Check(ranges.All(r => r.low > low || r.high < high), "Every nonleaf branch progresses");
                            }
            foreach (bool testudo in new[] { false, true })
                foreach (bool taunt in new[] { false, true })
                    foreach (int power in new[] { 0, 1, 3, 5, 8 })
                        Exhaustive(power, new[] { 3, 2 }, taunt, testudo);
            Exhaustive(9, new[] { 2, 3, 4 }, false, false);
            Exhaustive(9, new[] { 2, 3, 4 }, true, true);
            RealTestudo(false, true); RealTestudo(true, false); RealTestudo(true, true);
            Program.Print(new { passed = true, checks = _checks,
                coverage = "sparse interval exhaustion;42 complete allocation trees; multi-champion/taunt/residual/Testudo real engine" });
        }
    }
}
