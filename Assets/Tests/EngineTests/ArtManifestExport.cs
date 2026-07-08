using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Pascension.Content;
using Pascension.Engine.Cards;
using Pascension.Engine.Heroes;
using Pascension.Engine.Serialization;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Not a test of behavior: exports every ArtPrompt from the card/hero databases to
    /// Tools/art_manifest.json so the ComfyUI generation loop uses the exact authored
    /// prompts. Run explicitly: dotnet test --filter ExportArtManifest
    /// </summary>
    [TestFixture]
    [Category("Export")]
    public class ArtManifestExport
    {
        private sealed class Entry
        {
            public string Id;
            public string Kind; // card | hero
            public string Prompt;
            public int Width;
            public int Height;
        }

        [Test]
        public void ExportArtManifest()
        {
            ContentRegistry.RegisterAll();
            var entries = new List<Entry>();
            foreach (var def in CardDatabase.All)
                if (!string.IsNullOrEmpty(def.ArtPrompt))
                    entries.Add(new Entry { Id = def.Id, Kind = "card", Prompt = def.ArtPrompt, Width = 880, Height = 1232 });
            foreach (var hero in HeroDatabase.All)
                if (!string.IsNullOrEmpty(hero.ArtPrompt))
                    entries.Add(new Entry { Id = hero.Id, Kind = "hero", Prompt = hero.ArtPrompt, Width = 832, Height = 1216 });

            string path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
                "..", "..", "..", "..", "art_manifest.json"));
            File.WriteAllText(path, EngineJson.Serialize(entries));
            TestContext.WriteLine($"Wrote {entries.Count} entries to {path}");
            Assert.Greater(entries.Count, 40);
        }
    }
}
