using UnityEditor;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Enforces import settings for all generated art under Assets/Art/: single Sprite,
    /// sRGB, no mipmaps, max size 1024 (2048 for board/backgrounds), automatic
    /// compression. Runs on every (re)import so regenerated PNGs stay consistent.
    /// </summary>
    public sealed class CardArtPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            string path = assetPath.Replace('\\', '/');
            if (!path.StartsWith("Assets/Art/"))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = path.StartsWith("Assets/Art/Board") ? 2048 : 1024;
            importer.textureCompression = TextureImporterCompression.Compressed;
        }
    }
}
