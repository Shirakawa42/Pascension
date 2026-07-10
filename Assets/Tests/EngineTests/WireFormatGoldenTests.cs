using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Wire-format regression net for the two-game core extraction: serialized actions,
    /// events, and snapshots from a SEEDED game must stay semantically identical
    /// (JToken.DeepEquals — property order may vary, names/values/shape may not).
    /// Fixtures live in Tools/EngineVerify/golden/ and are committed. Regenerate ONLY
    /// when a deliberate wire change ships (delete the files and re-run).
    /// </summary>
    [TestFixture]
    public class WireFormatGoldenTests
    {
        private static string GoldenDir
        {
            get
            {
                // Repo root from either the Unity project dir or EngineVerify's bin dir.
                var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ProjectSettings", "ProjectVersion.txt")))
                    dir = dir.Parent;
                Assert.IsNotNull(dir, "repo root not found");
                string path = Path.Combine(dir.FullName, "Tools", "EngineVerify", "golden");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static void AssertGolden(string name, string json)
        {
            string file = Path.Combine(GoldenDir, name + ".json");
            if (!File.Exists(file))
            {
                // Capture mode (first run / deliberate regeneration): write and continue,
                // so a single run collects every fixture. Committed fixtures make all
                // later runs pure comparisons.
                File.WriteAllText(file, json);
                TestContext.Out.WriteLine($"[golden] captured {name}");
                return;
            }
            var expected = JToken.Parse(File.ReadAllText(file));
            var actual = JToken.Parse(json);
            Assert.IsTrue(JToken.DeepEquals(expected, actual),
                $"Wire format drifted for {name}.\nExpected: {expected}\nActual:   {actual}");
        }

        [Test]
        public void Actions_AllNineTypes_WireStable()
        {
            var actions = new PlayerAction[]
            {
                new PlayCardAction { PlayerIndex = 1, CardInstanceId = 42 },
                new BuyCardAction { PlayerIndex = 2, TierIndex = 1, SlotIndex = 3 },
                new MoveStepsAction { PlayerIndex = 0, Steps = 4 },
                new AssignDamageAction { PlayerIndex = 3, Target = TargetRef.Monster(2, 4), Amount = 7 },
                new ActivateAbilityAction { PlayerIndex = 1, SourceInstanceId = 9, AbilityIndex = 0 },
                new UseHeroAbilityAction { PlayerIndex = 0, Ultimate = true },
                new PassPriorityAction { PlayerIndex = 2 },
                new ConcedeAction { PlayerIndex = 3 },
            };
            var parts = new List<string>();
            foreach (var a in actions)
                parts.Add(EngineJson.SerializeAction(a));
            AssertGolden("actions", "[" + string.Join(",", parts) + "]");

            // Round-trip type fidelity.
            foreach (var a in actions)
                Assert.AreEqual(a.GetType(), EngineJson.DeserializeAction(EngineJson.SerializeAction(a)).GetType());
        }

        [Test]
        public void SeededGame_EventsAndSnapshots_WireStable()
        {
            var config = TestGames.StandardConfig(players: 3, seed: 20260710);
            var engine = new GameEngine(config);

            // Deterministic opening: P0 plays every opening-hand card, then passes to End.
            var p0 = engine.State.Players[0];
            var ids = new List<int>();
            foreach (var c in p0.Hand) ids.Add(c.InstanceId);
            foreach (int id in ids)
            {
                if (engine.PendingInput?.Kind == PendingInputKind.Priority &&
                    engine.PendingInput.PlayerIndex == 0)
                    engine.Submit(new PlayCardAction { PlayerIndex = 0, CardInstanceId = id });
                engine.PassUntilStackEmpty();
            }

            AssertGolden("events_p0", EngineJson.SerializeEvents(engine.Log.FilterFor(0, 0)));
            AssertGolden("events_p1_masked", EngineJson.SerializeEvents(engine.Log.FilterFor(1, 0)));
            AssertGolden("snapshot_p0", EngineJson.Serialize(SnapshotBuilder.Build(engine, 0)));
            AssertGolden("snapshot_p1_masked", EngineJson.Serialize(SnapshotBuilder.Build(engine, 1)));

            // PendingSnap's polymorphic LegalActions use the Unity-side NetJson converter;
            // headless we pin the same content piecewise (kind/player + each legal action).
            var pending = engine.PendingInput;
            Assert.IsNotNull(pending);
            var legalParts = new List<string>();
            if (pending.LegalActions != null)
                foreach (var a in pending.LegalActions)
                    legalParts.Add(EngineJson.SerializeAction(a));
            AssertGolden("pending", "{\"kind\":" + (int)pending.Kind +
                                    ",\"player\":" + pending.PlayerIndex +
                                    ",\"legal\":[" + string.Join(",", legalParts) + "]}");

            // Determinism guard: the seeded state hash is part of the golden contract.
            AssertGolden("state_hash", "{\"hash\":\"" + engine.State.ComputeHash().ToString("x16") + "\"}");
        }
    }
}
