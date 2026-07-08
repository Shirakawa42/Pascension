using System.Collections.Generic;
using System.IO;
using Pascension.Net;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Pascension.Editor
{
    /// <summary>
    /// One-shot project setup for online play. Authors:
    /// 1. Assets/Resources/Net/GameNetBridge.prefab (NetworkObject + GameNetBridge) —
    ///    never regenerated once it exists, so its GlobalObjectIdHash stays stable.
    /// 2. Assets/Scenes/Lobby.unity containing ONLY: a camera, an EventSystem with
    ///    InputSystemUIInputModule (the project runs Input System-only), an in-scene
    ///    placed "LobbyNet" NetworkObject with LobbyNetBehaviour, and a "LobbyUI"
    ///    object with LobbyUiController (which builds all uGUI at runtime).
    /// 3. Build-settings entries for Lobby (and Game if that scene already exists),
    ///    appended idempotently without touching existing entries.
    /// </summary>
    public static class NetSceneBuilder
    {
        private const string LobbyScenePath = "Assets/Scenes/Lobby.unity";
        private const string GameScenePath = "Assets/Scenes/Game.unity";
        private const string ResourcesDir = "Assets/Resources/Net";
        private const string BridgePrefabPath = ResourcesDir + "/GameNetBridge.prefab";

        [MenuItem("Pascension/Setup/Build Lobby Scene")]
        public static void BuildLobbyScene()
        {
            // No prompts: runs headless via MCP. Always rebuilds; unsaved changes in the
            // open scene are discarded when the new empty scene is created.
            EnsureBridgePrefab();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.11f);
            cameraGo.AddComponent<AudioListener>();

            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<InputSystemUIInputModule>();

            // In-scene placed NetworkObject: gets a baked GlobalObjectIdHash on save and
            // is soft-synced between host and clients — no prefab registration needed.
            var lobbyNetGo = new GameObject("LobbyNet");
            lobbyNetGo.AddComponent<NetworkObject>();
            lobbyNetGo.AddComponent<LobbyNetBehaviour>();

            new GameObject("LobbyUI").AddComponent<LobbyUiController>();

            Directory.CreateDirectory("Assets/Scenes");
            if (!EditorSceneManager.SaveScene(scene, LobbyScenePath))
            {
                Debug.LogError("[NetSceneBuilder] Failed to save " + LobbyScenePath);
                return;
            }

            EnsureBuildSettings();
            Debug.Log("[NetSceneBuilder] Lobby scene built at " + LobbyScenePath);
        }

        private static void EnsureBridgePrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(BridgePrefabPath) != null)
                return; // keep the existing asset — its GlobalObjectIdHash must stay stable

            Directory.CreateDirectory(ResourcesDir);
            AssetDatabase.Refresh();

            var go = new GameObject("GameNetBridge");
            try
            {
                go.AddComponent<NetworkObject>();
                go.AddComponent<GameNetBridge>();
                PrefabUtility.SaveAsPrefabAsset(go, BridgePrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            Debug.Log("[NetSceneBuilder] Created " + BridgePrefabPath);
        }

        private static void EnsureBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool changed = AddSceneEntry(scenes, LobbyScenePath);

            if (File.Exists(GameScenePath))
                changed |= AddSceneEntry(scenes, GameScenePath);
            else
                Debug.LogWarning("[NetSceneBuilder] " + GameScenePath + " not found — add the Game scene " +
                                 "to Build Settings once it exists (NGO loads scenes by name).");

            if (changed)
                EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static bool AddSceneEntry(List<EditorBuildSettingsScene> scenes, string path)
        {
            foreach (var entry in scenes)
                if (entry.path == path)
                    return false; // never clobber existing entries
            scenes.Add(new EditorBuildSettingsScene(path, true));
            return true;
        }
    }
}
