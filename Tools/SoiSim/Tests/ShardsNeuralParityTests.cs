using System;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json;
using Shards.Bots;
using SoiSim;

namespace Pascension.Engine.Tests
{
    /// <summary>Pins the C# forward pass against PyTorch: train.py stamps expected
    /// outputs for the committed fixture states (with f16-roundtripped weights, so the
    /// comparison is exact-model vs exact-model). Any encoder drift, export bug or
    /// forward-pass bug breaks this in CI. Ignored until a net is embedded.</summary>
    public sealed class ShardsNeuralParityTests
    {
        [Test]
        public void CSharpForward_MatchesPyTorch_OnCommittedFixtures()
        {
            if (!ShardsNetWeights.Available)
                Assert.Ignore("no trained net embedded yet");

            string root = FindRepoRoot();
            string fixturePath = Path.Combine(root, "Tools", "ShardsData", "neural", "fixtures.soip");
            string expectedPath = Path.Combine(root, "Tools", "ShardsData", "neural", "expected.json");
            Assert.IsTrue(File.Exists(fixturePath), "fixtures.soip missing — run soisim netfixture");
            Assert.IsTrue(File.Exists(expectedPath), "expected.json missing — run train.py --fixtures");

            var expected = JsonConvert.DeserializeObject<double[]>(File.ReadAllText(expectedPath));
            var eval = ShardsNeuralEval.LoadCurrent();

            using var reader = new BinaryReader(File.OpenRead(fixturePath));
            uint magic = reader.ReadUInt32();
            Assert.AreEqual(PositionWriter.Magic, magic, "fixture magic");
            reader.ReadUInt16(); // format version
            // Schema-agnostic: the current net may deploy the frozen v1 pooled
            // encoding (768) or the current encoder (v2, 1140). Take the width and
            // record size from the fixture's OWN header — they were written to match
            // the net these fixtures pin, and eval.Forward sizes its input to the net.
            ushort schema = reader.ReadUInt16();
            int featureCount = (int)reader.ReadUInt32();
            int recordSize = (int)reader.ReadUInt32();
            Assert.IsTrue(schema == 1 || schema == ShardsStateEncoder.SchemaVersion,
                $"fixture schema v{schema} is neither v1 nor the current encoder");
            reader.ReadBytes(16); // reserved

            var features = new float[featureCount];
            for (int record = 0; record < expected.Length; record++)
            {
                for (int i = 0; i < featureCount; i++)
                    features[i] = reader.ReadSingle();
                reader.ReadBytes(recordSize - featureCount * 4); // labels/meta
                double actual = eval.Forward(features);
                Assert.AreEqual(expected[record], actual, 1e-4,
                    $"fixture {record}: C# forward diverged from PyTorch");
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root (CLAUDE.md) not found");
            return dir.FullName;
        }
    }
}
