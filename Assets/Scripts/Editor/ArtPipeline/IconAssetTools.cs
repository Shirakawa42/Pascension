using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Builds the inline-text icon assets from Assets/Art/Icons/*.png:
    /// a stitched atlas + a TMP_SpriteAsset (glyph names: dmg/ap/xp/step) usable as
    /// &lt;sprite name="ap"&gt; in any TMP text. Run once after (re)generating icons;
    /// SceneConstruction wires the results into UiTheme.
    /// </summary>
    public static class IconAssetTools
    {
        private const string IconDir = "Assets/Art/Icons";
        private const string AtlasPath = IconDir + "/icon_atlas.png";
        private const string SpriteAssetPath = IconDir + "/PascensionIcons.asset";
        private static readonly string[] IconIds = { "icon_dmg", "icon_ap", "icon_xp", "icon_step" };
        private const int Cell = 512;

        [MenuItem("Pascension/Setup/Build Icon Sprite Asset")]
        public static void Build()
        {
            // 1. Ensure readable import settings on the sources.
            foreach (string id in IconIds)
                ConfigureImporter($"{IconDir}/{id}.png", readable: true);
            AssetDatabase.Refresh();

            // 2. Stitch the horizontal atlas.
            var atlas = new Texture2D(Cell * IconIds.Length, Cell, TextureFormat.RGBA32, false);
            var clear = new Color32[Cell * Cell * IconIds.Length];
            atlas.SetPixels32(clear);
            int found = 0;
            for (int i = 0; i < IconIds.Length; i++)
            {
                var src = AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconDir}/{IconIds[i]}.png");
                if (src == null) continue;
                var pixels = src.GetPixels(0, 0, Mathf.Min(Cell, src.width), Mathf.Min(Cell, src.height));
                atlas.SetPixels(i * Cell, 0, Mathf.Min(Cell, src.width), Mathf.Min(Cell, src.height), pixels);
                found++;
            }
            atlas.Apply();
            File.WriteAllBytes(AtlasPath, atlas.EncodeToPNG());
            Object.DestroyImmediate(atlas);
            AssetDatabase.ImportAsset(AtlasPath);
            ConfigureImporter(AtlasPath, readable: false);
            AssetDatabase.Refresh();

            var atlasTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);

            // 3. TMP sprite asset with one glyph per icon.
            var spriteAsset = AssetDatabase.LoadAssetAtPath<TMP_SpriteAsset>(SpriteAssetPath);
            bool fresh = spriteAsset == null;
            if (fresh)
                spriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();

            // A fresh asset without m_Version triggers TMP's legacy upgrade path, which
            // NREs on the null legacy spriteInfoList — mark it current up front.
            typeof(TMP_SpriteAsset)
                .GetField("m_Version", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(spriteAsset, "1.1.0");

            spriteAsset.spriteSheet = atlasTexture;
            spriteAsset.spriteGlyphTable.Clear();
            spriteAsset.spriteCharacterTable.Clear();

            for (int i = 0; i < IconIds.Length; i++)
            {
                string glyphName = IconIds[i].Replace("icon_", "");
                var glyph = new TMP_SpriteGlyph
                {
                    index = (uint)i,
                    glyphRect = new GlyphRect(i * Cell, 0, Cell, Cell),
                    // Metrics tuned so the icon sits on the text baseline at ~1em.
                    metrics = new GlyphMetrics(Cell, Cell, 0f, Cell * 0.9f, Cell),
                    scale = 1f
                };
                spriteAsset.spriteGlyphTable.Add(glyph);

                var character = new TMP_SpriteCharacter(0xE000u + (uint)i, glyph)
                {
                    name = glyphName,
                    scale = 1f
                };
                spriteAsset.spriteCharacterTable.Add(character);
            }

            // Material with the TMP sprite shader.
            var shader = Shader.Find("TextMeshPro/Sprite");
            if (spriteAsset.material == null && shader != null)
            {
                var material = new Material(shader) { mainTexture = atlasTexture, name = "PascensionIcons Material" };
                spriteAsset.material = material;
                if (fresh)
                {
                    AssetDatabase.CreateAsset(spriteAsset, SpriteAssetPath);
                    AssetDatabase.AddObjectToAsset(material, spriteAsset);
                }
            }
            else if (spriteAsset.material != null)
            {
                spriteAsset.material.mainTexture = atlasTexture;
                if (fresh) AssetDatabase.CreateAsset(spriteAsset, SpriteAssetPath);
            }

            spriteAsset.UpdateLookupTables();
            EditorUtility.SetDirty(spriteAsset);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Pascension] Icon sprite asset built: {found}/{IconIds.Length} icons at {SpriteAssetPath}");
        }

        private static void ConfigureImporter(string path, bool readable)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.isReadable = readable;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
        }
    }
}
