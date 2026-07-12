using System;
using System.IO;
using Pascension.Game.View;
using UnityEditor;
using UnityEngine;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Scans Assets/Art/Cards and Assets/Art/Heroes for {id}.png sprites and rebuilds
    /// the CardArtIndex ScriptableObject at Assets/Art/CardArtIndex.asset (the runtime
    /// id → Sprite lookup used by the UI). Called from the menu and after every
    /// generation batch.
    /// </summary>
    public static class CardArtIndexBuilder
    {
        public const string AssetPath = "Assets/Art/CardArtIndex.asset";
        private static readonly string[] SourceFolders =
            { "Assets/Art/Cards", "Assets/Art/Heroes", "Assets/Art/Shards/Cards" };

        [MenuItem("Pascension/Rebuild Card Art Index")]
        public static void Rebuild()
        {
            var index = AssetDatabase.LoadAssetAtPath<CardArtIndex>(AssetPath);
            if (index == null)
            {
                Directory.CreateDirectory("Assets/Art");
                AssetDatabase.Refresh();
                index = ScriptableObject.CreateInstance<CardArtIndex>();
                AssetDatabase.CreateAsset(index, AssetPath);
            }

            index.entries.Clear();
            foreach (string folder in SourceFolders)
            {
                if (!Directory.Exists(folder))
                    continue;
                string[] files = Directory.GetFiles(folder, "*.png");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase); // deterministic asset content
                foreach (string file in files)
                {
                    string path = file.Replace('\\', '/');
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sprite == null)
                        continue; // not imported as a sprite (postprocessor handles new files)
                    index.entries.Add(new CardArtIndex.Entry
                    {
                        id = Path.GetFileNameWithoutExtension(path),
                        sprite = sprite
                    });
                }
            }

            EditorUtility.SetDirty(index);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Pascension] Card art index rebuilt: {index.entries.Count} sprites at {AssetPath}");
        }
    }
}
