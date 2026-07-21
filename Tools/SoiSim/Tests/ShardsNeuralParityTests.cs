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
            ushort schema = reader.ReadUInt16();
            Assert.AreEqual(ShardsStateEncoder.SchemaVersion, schema,
                "fixture schema differs — regenerate fixtures + expectations");
            uint featureCount = reader.ReadUInt32();
            Assert.AreEqual(ShardsStateEncoder.FeatureCount, (int)featureCount);
            reader.ReadUInt32(); // record size
            reader.ReadBytes(16); // reserved

            var features = new float[ShardsStateEncoder.FeatureCount];
            for (int record = 0; record < expected.Length; record++)
            {
                for (int i = 0; i < features.Length; i++)
                    features[i] = reader.ReadSingle();
                reader.ReadBytes(PositionWriter.RecordSize - features.Length * 4); // labels/meta
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
