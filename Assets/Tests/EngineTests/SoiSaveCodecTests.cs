using System;
using System.Text;
using NUnit.Framework;
using Shards.Stats;

namespace Pascension.Engine.Tests
{
    /// <summary>The save format is sig + "\n" + payload with an HMAC-SHA256 over the
    /// payload bytes — any tamper, wrong secret, or foreign profile must fail closed.</summary>
    [TestFixture]
    public class SoiSaveCodecTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("soi-test-secret");

        private static SoiSaveData Sample()
        {
            return new SoiSaveData
            {
                ProfileKey = "lucas",
                Records =
                {
                    new SoiGameRecord
                    {
                        Guid = "g1",
                        EndedAtUtc = "2026-07-23T00:00:00Z",
                        Mode = "ai",
                        MyIndex = 0,
                        WinnerIndex = 0,
                        Termination = "kill",
                        Players =
                        {
                            new SoiSeatRecord { Identity = "lucas", Name = "Lucas", CharacterId = "decima" }
                        }
                    }
                }
            };
        }

        [Test]
        public void Roundtrip()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            Assert.IsTrue(SoiSaveCodec.TryDecode(text, Secret, "lucas", out var data));
            Assert.AreEqual(SoiStatsJson.Serialize(Sample()), SoiStatsJson.Serialize(data));
        }

        [Test]
        public void TamperedPayload_Fails()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            int split = text.IndexOf('\n');
            var chars = text.ToCharArray();
            int target = split + 5;
            chars[target] = chars[target] == 'x' ? 'y' : 'x';
            Assert.IsFalse(SoiSaveCodec.TryDecode(new string(chars), Secret, "lucas", out _));
        }

        [Test]
        public void WrongSecret_Fails()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            Assert.IsFalse(SoiSaveCodec.TryDecode(text,
                Encoding.UTF8.GetBytes("some-other-secret"), "lucas", out _));
        }

        [Test]
        public void MissingOrGarbledSignature_Fails()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            string payload = text.Substring(text.IndexOf('\n') + 1);

            Assert.IsFalse(SoiSaveCodec.TryDecode(payload, Secret, "lucas", out _),
                "no signature line at all");
            Assert.IsFalse(SoiSaveCodec.TryDecode("!!!not-base64!!!\n" + payload, Secret, "lucas", out _),
                "signature is not base64");
            Assert.IsFalse(SoiSaveCodec.TryDecode(Convert.ToBase64String(new byte[32]) + "\n" + payload,
                Secret, "lucas", out _), "valid base64 but wrong signature");
        }

        [Test]
        public void ProfileKeyMismatch_Fails()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            Assert.IsFalse(SoiSaveCodec.TryDecode(text, Secret, "not-lucas", out _));
        }

        [Test]
        public void NullExpectedProfileKey_SkipsCheck()
        {
            string text = SoiSaveCodec.Encode(Sample(), Secret);
            Assert.IsTrue(SoiSaveCodec.TryDecode(text, Secret, null, out var data));
            Assert.AreEqual("lucas", data.ProfileKey);
        }
    }
}
