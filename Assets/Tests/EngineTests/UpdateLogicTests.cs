using NUnit.Framework;
using Pascension.Core;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class UpdateLogicTests
    {
        // ------------------------------------------------------------ VersionCompare

        [TestCase("1.0", "1.0", 0)]
        [TestCase("1.0", "1.0.0", 0)]
        [TestCase("1.0", "1.0.123", -1)]
        [TestCase("1.0.9", "1.0.10", -1)]
        [TestCase("2.0", "1.9.999", 1)]
        [TestCase("", "1.0", -1)]
        [TestCase(null, "0.0", 0)]
        [TestCase("1.0.abc", "1.0.0", 0)]  // garbage segment counts as 0
        [TestCase("1.0.10-beta", "1.0.9", 1)] // leading digits win
        public void VersionCompare_Compares(string a, string b, int expected)
        {
            Assert.AreEqual(expected, VersionCompare.Compare(a, b));
            Assert.AreEqual(-expected, VersionCompare.Compare(b, a), "antisymmetry");
        }

        [Test]
        public void VersionCompare_IsNewer()
        {
            Assert.IsTrue(VersionCompare.IsNewer("1.0.123", "1.0"));
            Assert.IsFalse(VersionCompare.IsNewer("1.0", "1.0.123"));
            Assert.IsFalse(VersionCompare.IsNewer("1.0", "1.0"));
        }

        // ------------------------------------------------------------ UpdateManifest

        private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static string ManifestJson(string version = "1.0.123", bool macos = true,
            string winSha = Sha) =>
            "{ \"version\": \"" + version + "\", \"tag\": \"v" + version + "\"," +
            "  \"publishedAt\": \"2026-07-19T14:03:22Z\"," +
            "  \"platforms\": {" +
            "    \"windows\": { \"url\": \"https://x/w.zip\", \"sha256\": \"" + winSha + "\", \"sizeBytes\": 123 }" +
            (macos ? ", \"macos\": { \"url\": \"https://x/m.tar.gz\", \"sha256\": \"" + Sha + "\", \"sizeBytes\": 456 }" : "") +
            "  } }";

        [Test]
        public void Manifest_ParsesCiShape()
        {
            Assert.IsTrue(UpdateManifest.TryParse(ManifestJson(), out var m, out var error), error);
            Assert.AreEqual("1.0.123", m.Version);
            Assert.AreEqual("v1.0.123", m.Tag);
            Assert.AreEqual("https://x/w.zip", m.Platforms.Windows.Url);
            Assert.AreEqual(123, m.Platforms.Windows.SizeBytes);
            Assert.AreEqual(Sha, m.Platforms.Macos.Sha256);
        }

        [Test]
        public void Manifest_MissingPlatformIsLegal()
        {
            Assert.IsTrue(UpdateManifest.TryParse(ManifestJson(macos: false), out var m, out _));
            Assert.IsNull(m.Platforms.Macos);
            Assert.IsNotNull(m.Platforms.Windows);
        }

        [Test]
        public void Manifest_RejectsGarbage()
        {
            Assert.IsFalse(UpdateManifest.TryParse("not json at all {", out _, out var e1));
            StringAssert.Contains("Malformed", e1);
            Assert.IsFalse(UpdateManifest.TryParse("{}", out _, out var e2));
            StringAssert.Contains("version", e2);
            Assert.IsFalse(UpdateManifest.TryParse(ManifestJson(winSha: "deadbeef"), out _, out var e3));
            StringAssert.Contains("sha256", e3);
            Assert.IsFalse(UpdateManifest.TryParse("null", out _, out var e4));
            StringAssert.Contains("empty", e4);
        }

        // ------------------------------------------------------------ swap scripts

        [Test]
        public void WindowsCmd_HasTheLoadBearingParts()
        {
            string cmd = UpdateSwapScripts.WindowsCmd(4242,
                @"C:\Users\p\AppData\LocalLow\X\pascension\updates\staged",
                @"D:\Games\Pascension", "pascension.exe",
                @"C:\Users\p\AppData\LocalLow\X\pascension\updates\swap.log");

            StringAssert.Contains("set \"PID=4242\"", cmd);
            StringAssert.Contains(@"set ""SRC=C:\Users\p\AppData\LocalLow\X\pascension\updates\staged""", cmd);
            StringAssert.Contains(@"set ""DST=D:\Games\Pascension""", cmd);
            StringAssert.Contains("robocopy \"%SRC%\" \"%DST%\"", cmd);
            StringAssert.DoesNotContain("/PURGE", cmd);      // never delete unknown install files
            StringAssert.Contains("if errorlevel 8", cmd);   // robocopy <8 = success
            StringAssert.Contains("start \"\" \"%DST%\\%EXE%\"", cmd);
            StringAssert.Contains("(goto) 2>nul & del \"%~f0\"", cmd); // self-delete
            StringAssert.Contains("\r\n", cmd);              // cmd wants CRLF
        }

        [Test]
        public void MacosSh_HasTheLoadBearingParts()
        {
            string sh = UpdateSwapScripts.MacosSh(777,
                "/Users/p/Library/Application Support/X/pascension/updates/staged/pascension.app",
                "/Applications/pascension.app",
                "/Users/p/Library/Application Support/X/pascension/updates/swap.log");

            StringAssert.Contains("PID=777", sh);
            StringAssert.Contains("while kill -0 \"$PID\"", sh);
            StringAssert.Contains("mv \"$DST\" \"$DST.old\"", sh);            // rollback path
            StringAssert.Contains("mv \"$DST.old\" \"$DST\"", sh);            // restore on failure
            StringAssert.Contains("xattr -dr com.apple.quarantine \"$DST\"", sh);
            StringAssert.Contains("open \"$DST\"", sh);
            StringAssert.Contains("rm -f -- \"$0\"", sh);
            StringAssert.DoesNotContain("\r", sh);            // bash wants LF only
        }

        // ------------------------------------------------------------ path logic

        [TestCase("/X/pascension.app/Contents/Resources/Data", "/X/pascension.app")]
        [TestCase("/Applications/My Game.app/Contents/Resources/Data", "/Applications/My Game.app")]
        [TestCase("/X/pascension.app", "/X/pascension.app")]
        [TestCase(@"C:\Games\pascension_Data", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void FindAppBundleRoot_Works(string dataPath, string expected)
        {
            Assert.AreEqual(expected, UpdateSwapScripts.FindAppBundleRoot(dataPath));
        }

        [Test]
        public void ResolveStagedRoot_Works()
        {
            // flat layout (zip root == build root)
            Assert.AreEqual("/s", UpdateSwapScripts.ResolveStagedRoot("/s",
                new string[0], new[] { "/s/pascension.exe" }));
            // single wrapper dir, no files → descend
            Assert.AreEqual("/s/build", UpdateSwapScripts.ResolveStagedRoot("/s",
                new[] { "/s/build" }, new string[0]));
            // dir + files at top level → stay put
            Assert.AreEqual("/s", UpdateSwapScripts.ResolveStagedRoot("/s",
                new[] { "/s/pascension_Data" }, new[] { "/s/pascension.exe" }));
        }
    }
}
