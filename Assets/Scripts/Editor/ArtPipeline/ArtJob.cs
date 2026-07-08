namespace Pascension.Editor.ArtPipeline
{
    /// <summary>One generation request for <see cref="ComfyUiClient"/>: which asset,
    /// the subject-only prompt (prefix/negative are added by the client), target size,
    /// re-roll salt, and where the PNG lands.</summary>
    public sealed class ArtJob
    {
        /// <summary>Card or hero id — also the output file name and the seed source.</summary>
        public string Id;

        /// <summary>Subject part of the prompt only (CardDefinition.ArtPrompt / HeroDefinition.ArtPrompt).</summary>
        public string Prompt;

        public int Width = 880;
        public int Height = 1232;

        /// <summary>Bump to re-roll: seed = stable hash of Id + salt.</summary>
        public int SeedSalt;

        /// <summary>Asset path of the PNG, e.g. "Assets/Art/Cards/fireball.png".</summary>
        public string OutputPath;
    }
}
