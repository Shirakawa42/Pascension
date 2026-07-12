using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Imports Shards of Infinity card art from a user-maintained manifest
    /// (Tools/soi_art_sources.json: [{"id": "...", "source": "url-or-local-path"}])
    /// into Assets/Art/Shards/Cards/{id}.png and rebuilds the art index.
    ///
    /// PERSONAL USE ONLY: the sources are official card images the user points at —
    /// builds containing them must never be distributed. The whole Assets/Art/Shards
    /// folder is git-ignored for that reason. Missing ids can be generated instead via
    /// the ComfyUI window using the defs' ArtPrompt fields (original fallback art).
    /// </summary>
    public sealed class ShardsArtImportWindow : EditorWindow
    {
        private const string ManifestPath = "Tools/soi_art_sources.json";
        private const string TargetFolder = "Assets/Art/Shards/Cards";

        private Vector2 _scroll;
        private string _status = "";
        private int _total, _withSource, _imported;

        [MenuItem("Pascension/Setup/Shards Art Import")]
        public static void Open()
        {
            var window = GetWindow<ShardsArtImportWindow>("Shards Art Import");
            window.minSize = new Vector2(460f, 240f);
            window.RefreshCounts();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Imports card images listed in " + ManifestPath + " (id → url or file path) " +
                "into " + TargetFolder + ".\n\nPERSONAL USE ONLY — imported official art is " +
                "git-ignored and must never ship in a distributed build. Leave 'source' empty " +
                "to skip an id (the UI falls back to text frames, or generate original art via " +
                "Pascension/Card Art Generator using the defs' art prompts).",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Manifest entries: {_total}   with source: {_withSource}   files on disk: {_imported}");

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Height(28f)))
                    RefreshCounts();
                if (GUILayout.Button("Open manifest", GUILayout.Height(28f)))
                    EditorUtility.OpenWithDefaultApp(Path.GetFullPath(ManifestPath));
                if (GUILayout.Button("Import all with sources", GUILayout.Height(28f)))
                    ImportAll();
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField(_status, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        private List<(string id, string source)> ReadManifest()
        {
            var result = new List<(string, string)>();
            if (!File.Exists(ManifestPath))
                return result;
            var array = JArray.Parse(File.ReadAllText(ManifestPath));
            foreach (var item in array)
                result.Add(((string)item["id"], (string)item["source"] ?? ""));
            return result;
        }

        private void RefreshCounts()
        {
            var manifest = ReadManifest();
            _total = manifest.Count;
            _withSource = 0;
            foreach (var (_, source) in manifest)
                if (!string.IsNullOrWhiteSpace(source))
                    _withSource++;
            _imported = Directory.Exists(TargetFolder) ? Directory.GetFiles(TargetFolder, "*.png").Length : 0;
            _status = File.Exists(ManifestPath) ? "" : "Manifest not found — run `dotnet test --filter ExportShardsCardTable` to create the skeleton.";
        }

        private void ImportAll()
        {
            var manifest = ReadManifest();
            Directory.CreateDirectory(TargetFolder);
            int ok = 0, failed = 0, skipped = 0;
            var log = new System.Text.StringBuilder();

            foreach (var (id, source) in manifest)
            {
                if (string.IsNullOrWhiteSpace(source)) { skipped++; continue; }
                string target = Path.Combine(TargetFolder, id + ".png");
                try
                {
                    if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        using var client = new WebClient();
                        client.Headers.Add("User-Agent", "pascension-personal-art-import");
                        client.DownloadFile(source, target);
                    }
                    else
                    {
                        File.Copy(source, target, overwrite: true);
                    }
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    log.AppendLine(id + ": " + e.Message);
                }
            }

            AssetDatabase.Refresh(); // the art postprocessor imports the PNGs as sprites
            CardArtIndexBuilder.Rebuild();
            RefreshCounts();
            _status = $"Imported {ok}, failed {failed}, skipped (no source) {skipped}.\n" + log;
        }
    }
}
