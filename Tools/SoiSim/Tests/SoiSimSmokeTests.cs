using NUnit.Framework;

namespace SoiSim.Tests
{
    /// <summary>CI twin of `soisim smoke` — compiled into Engine.Verify via a source
    /// link (Unity never sees this folder). Seconds of runtime: guards the recorder's
    /// event-derivation assumptions and the scheduler's seat balance on every push.</summary>
    [TestFixture]
    public sealed class SoiSimSmokeTests
    {
        [Test]
        public void SmokeChecks_AllPass()
        {
            var problems = SmokeCommand.Check();
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }
    }
}
