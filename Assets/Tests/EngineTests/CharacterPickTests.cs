using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class CharacterPickTests
    {
        private static readonly string[] Roster = { "ignis", "wren", "cornelius", "nyx" };
        private const string R = CharacterPick.RandomId;

        [Test]
        public void ConcretePicksPassThroughUntouched()
        {
            var resolved = CharacterPick.ResolveRandoms(
                new[] { "wren", "ignis" }, Roster, seed: 7);
            CollectionAssert.AreEqual(new[] { "wren", "ignis" }, resolved);
        }

        [Test]
        public void RandomsResolveToUnusedDistinctIds()
        {
            var resolved = CharacterPick.ResolveRandoms(
                new[] { "wren", R, R, R }, Roster, seed: 7);
            Assert.AreEqual("wren", resolved[0]);
            CollectionAssert.AllItemsAreUnique(resolved);
            foreach (var id in resolved)
                CollectionAssert.Contains(Roster, id);
        }

        [Test]
        public void SameSeedSameHeroes()
        {
            var a = CharacterPick.ResolveRandoms(new[] { R, R, "nyx" }, Roster, seed: 42);
            var b = CharacterPick.ResolveRandoms(new[] { R, R, "nyx" }, Roster, seed: 42);
            CollectionAssert.AreEqual(a, b);
        }

        [Test]
        public void ExhaustedPoolWrapsInsteadOfFailing()
        {
            var tiny = new[] { "solo" };
            var resolved = CharacterPick.ResolveRandoms(new[] { R, R, R }, tiny, seed: 1);
            CollectionAssert.AreEqual(new[] { "solo", "solo", "solo" }, resolved);
        }

        [Test]
        public void IsTakenByOther_IgnoresSelfAndSentinel()
        {
            var picks = new List<string> { "wren", R, "ignis" };
            Assert.IsFalse(CharacterPick.IsTakenByOther(picks, 0, "wren"));  // own pick
            Assert.IsTrue(CharacterPick.IsTakenByOther(picks, 1, "wren"));   // someone else's
            Assert.IsFalse(CharacterPick.IsTakenByOther(picks, 0, R));       // sentinel never collides
            Assert.IsFalse(CharacterPick.IsTakenByOther(picks, -1, "nyx"));  // free id, outsider
            Assert.IsTrue(CharacterPick.IsTakenByOther(picks, -1, "ignis"));
        }
    }
}
