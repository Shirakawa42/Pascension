using System;
using System.Collections.Generic;
using System.IO;
using Pascension.Content;
using Pascension.Engine.Cards;
using Pascension.Engine.Heroes;
using UnityEditor;
using UnityEngine;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Editor batch tool for the ComfyUI/Anima art pipeline: lists every card and hero
    /// with its art status, generates missing/selected art sequentially with a
    /// cancelable progress bar, re-rolls single images (seed salt bump, persisted in
    /// EditorPrefs), and rebuilds the CardArtIndex after each batch.
    /// </summary>
    public sealed class ComfyUiArtWindow : EditorWindow
    {
        private sealed class Row
        {
            public string Id;
            public string DisplayName;
            public string Kind; // "card" | "hero"
            public string Prompt;
            public int Width;
            public int Height;
            public string Path;
            public bool Selected;
        }

        private readonly List<Row> _rows = new();
        private Vector2 _scroll;
        private string _serverStatus = "not checked";

        [MenuItem("Pascension/Card Art Generator")]
        public static void Open() => GetWindow<ComfyUiArtWindow>("Card Art Generator");

        private void OnEnable() => RefreshRows();

        private void RefreshRows()
        {
            ContentRegistry.RegisterAll();
            _rows.Clear();
            foreach (var def in CardDatabase.All)
                _rows.Add(new Row
                {
                    Id = def.Id,
                    DisplayName = def.Name,
                    Kind = "card",
                    Prompt = def.ArtPrompt,
                    Width = 880,
                    Height = 1232,
                    Path = $"Assets/Art/Cards/{def.Id}.png"
                });
            foreach (var hero in HeroDatabase.All)
                _rows.Add(new Row
                {
                    Id = hero.Id,
                    DisplayName = hero.Name,
                    Kind = "hero",
                    Prompt = hero.ArtPrompt,
                    Width = 832,
                    Height = 1216,
                    Path = $"Assets/Art/Heroes/{hero.Id}.png"
                });
        }

        private void OnGUI()
        {
            Action deferred = null;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Generate Missing", EditorStyles.toolbarButton))
                    deferred = () => RunBatch(_rows.FindAll(r => !File.Exists(r.Path)));
                if (GUILayout.Button("Generate Selected", EditorStyles.toolbarButton))
                    deferred = () => RunBatch(_rows.FindAll(r => r.Selected));
                if (GUILayout.Button("Rebuild Art Index", EditorStyles.toolbarButton))
                    deferred = CardArtIndexBuilder.Rebuild;
                if (GUILayout.Button("Refresh List", EditorStyles.toolbarButton))
                    deferred = RefreshRows;
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Check Server", EditorStyles.toolbarButton))
                    deferred = CheckServer;
                GUILayout.Label($"ComfyUI: {_serverStatus}", EditorStyles.miniLabel);
            }

            int missing = 0, total = 0;
            foreach (var row in _rows)
            {
                total++;
                if (!File.Exists(row.Path)) missing++;
            }
            EditorGUILayout.LabelField($"{total} entries, {missing} missing art", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.Selected = EditorGUILayout.ToggleLeft($"{row.Id} — {row.DisplayName}", row.Selected, GUILayout.Width(300));
                    GUILayout.Label(row.Kind, GUILayout.Width(40));
                    string status = File.Exists(row.Path) ? "ok"
                        : string.IsNullOrEmpty(row.Prompt) ? "NO PROMPT"
                        : "missing";
                    GUILayout.Label(status, GUILayout.Width(70));
                    GUILayout.Label($"salt {SaltFor(row.Id)}", EditorStyles.miniLabel, GUILayout.Width(50));
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(row.Prompt)))
                    {
                        var captured = row;
                        if (GUILayout.Button("Re-roll", GUILayout.Width(60)))
                            deferred = () =>
                            {
                                BumpSalt(captured.Id);
                                RunBatch(new List<Row> { captured });
                            };
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            // Long blocking work is deferred out of the layout pass.
            if (deferred != null)
                EditorApplication.delayCall += () => deferred();
        }

        private void CheckServer()
        {
            using var client = new ComfyUiClient();
            _serverStatus = client.IsServerUp() ? "up" : "DOWN";
            Repaint();
        }

        /// <summary>Sequential batch with progress + cancel. Rebuilds the art index afterwards.</summary>
        private void RunBatch(List<Row> rows)
        {
            rows.RemoveAll(r => string.IsNullOrEmpty(r.Prompt));
            if (rows.Count == 0)
            {
                ShowNotification(new GUIContent("Nothing to generate"));
                return;
            }

            using var client = new ComfyUiClient();
            if (!client.IsServerUp())
            {
                _serverStatus = "DOWN";
                EditorUtility.DisplayDialog("ComfyUI not reachable", ComfyUiClient.LaunchHint, "OK");
                return;
            }
            _serverStatus = "up";

            int done = 0;
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    float progress = (float)i / rows.Count;
                    string info = $"{row.Id} ({i + 1}/{rows.Count})";
                    if (EditorUtility.DisplayCancelableProgressBar("Pascension card art", $"{info} — submitting", progress))
                        break;

                    var job = new ArtJob
                    {
                        Id = row.Id,
                        Prompt = row.Prompt,
                        Width = row.Width,
                        Height = row.Height,
                        SeedSalt = SaltFor(row.Id),
                        OutputPath = row.Path
                    };
                    client.Generate(job, () =>
                        !EditorUtility.DisplayCancelableProgressBar("Pascension card art", $"{info} — rendering", progress));
                    done++;
                }
            }
            catch (OperationCanceledException)
            {
                // user cancel — keep whatever finished
            }
            catch (Exception e)
            {
                Debug.LogError($"[Pascension] Art generation failed: {e}");
                EditorUtility.DisplayDialog("Generation failed", e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
                if (done > 0)
                    CardArtIndexBuilder.Rebuild();
                Repaint();
            }
        }

        // Re-roll salts persist across sessions so seeds stay reproducible.
        private static string SaltKey(string id) => "Pascension.ArtSeedSalt." + id;
        private static int SaltFor(string id) => EditorPrefs.GetInt(SaltKey(id), 0);
        private static void BumpSalt(string id) => EditorPrefs.SetInt(SaltKey(id), SaltFor(id) + 1);
    }
}
