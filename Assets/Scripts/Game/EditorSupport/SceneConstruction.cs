#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Pascension.Game.Presentation;
using Pascension.Game.UI;
using Pascension.Game.View;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Pascension.Game.EditorSupport
{
    /// <summary>
    /// Builds MainMenu.unity and Game.unity from scratch — full hierarchy, all
    /// references wired in code, zero manual editor work. Lives in the Game assembly
    /// (editor-only compiled) because the Editor assembly does not reference uGUI/TMP;
    /// the Pascension.Editor SceneBuilder menu item is a thin wrapper over this.
    /// Idempotent: every run rebuilds both scenes.
    /// </summary>
    public static class SceneConstruction
    {
        private const string ScenesFolder = "Assets/Scenes";
        private const string MenuScenePath = ScenesFolder + "/MainMenu.unity";
        private const string GameScenePath = ScenesFolder + "/Game.unity";
        private const string ArtIndexPath = "Assets/Art/CardArtIndex.asset";
        private const string KeyartPath = "Assets/Art/Board/menu_keyart.png";

        public static void BuildAllScenes()
        {
            // No prompts: this runs headless via MCP. Unsaved changes in the currently
            // open scene are discarded when the new scenes are created below.
            if (!EnsureTmpEssentials())
                return;

            EnsureFolder("Assets", "Art");
            EnsureFolder("Assets", "Scenes");

            var artIndex = EnsureCardArtIndex();

            BuildMenuScene(artIndex);
            BuildGameScene(artIndex);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MenuScenePath, true),
                new EditorBuildSettingsScene(GameScenePath, true)
            };

            AssetDatabase.SaveAssets();
            Debug.Log("[Pascension] Built MainMenu.unity + Game.unity, registered build scenes, " +
                      $"CardArtIndex has {artIndex.entries.Count} entries. Open {MenuScenePath} and press Play.");
        }

        // ------------------------------------------------------------------ prerequisites

        private static bool EnsureTmpEssentials()
        {
            if (Resources.Load<TMP_Settings>("TMP Settings") != null)
                return true;

            TMP_PackageResourceImporter.ImportResources(importEssentials: true, importExamples: false, interactive: false);
            AssetDatabase.Refresh();

            if (Resources.Load<TMP_Settings>("TMP Settings") != null)
                return true;

            Debug.LogWarning("[Pascension] TMP Essential Resources were just imported — " +
                             "run 'Pascension/Setup/Build All Scenes' once more.");
            return false;
        }

        private static void EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static CardArtIndex EnsureCardArtIndex()
        {
            var index = AssetDatabase.LoadAssetAtPath<CardArtIndex>(ArtIndexPath);
            if (index == null)
            {
                index = ScriptableObject.CreateInstance<CardArtIndex>();
                AssetDatabase.CreateAsset(index, ArtIndexPath);
            }

            // Convenience: index any art already on disk (id = file name). The art
            // pipeline agent owns this asset; we only add entries that are missing.
            PopulateFromFolder(index, "Assets/Art/Cards");
            PopulateFromFolder(index, "Assets/Art/Heroes");
            EditorUtility.SetDirty(index);
            return index;
        }

        private static void PopulateFromFolder(CardArtIndex index, string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
                return;

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string id = Path.GetFileNameWithoutExtension(path);
                if (index.GetSprite(id) != null)
                    continue;

                var sprite = LoadSprite(path);
                if (sprite == null)
                    continue;

                var existing = index.entries.Find(e => e != null && e.id == id);
                if (existing != null)
                    existing.sprite = sprite;
                else
                    index.entries.Add(new CardArtIndex.Entry { id = id, sprite = sprite });
            }
        }

        /// <summary>Load a texture as a Sprite, switching the importer to Sprite mode if needed.</summary>
        private static Sprite LoadSprite(string path)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null)
                return sprite;

            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.SaveAndReimport();
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }
            return sprite;
        }

        // ------------------------------------------------------------------ shared scene chrome

        private static Sprite Builtin(string name) =>
            AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/" + name);

        private static void BuildCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 5.4f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = UiPalette.Background;
            go.transform.position = new Vector3(0f, 0f, -10f);
            go.AddComponent<AudioListener>();
        }

        private static void BuildEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        private static (Canvas canvas, UiTheme theme) BuildCanvas(CardArtIndex artIndex)
        {
            var go = new GameObject("Canvas");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            var theme = go.AddComponent<UiTheme>();
            theme.Rounded = Builtin("UISprite.psd");
            theme.Circle = Builtin("Knob.psd");
            theme.Soft = Builtin("Background.psd");
            theme.Font = TMP_Settings.defaultFontAsset;
            theme.ArtIndex = artIndex;

            return (canvas, theme);
        }

        private static Image BuildBackground(Transform canvasRoot, UiTheme theme, bool tryKeyart)
        {
            var background = UiFactory.CreateImage("Background", canvasRoot, null, UiPalette.Background);
            UiFactory.Stretch((RectTransform)background.transform);

            bool usedKeyart = false;
            if (tryKeyart && AssetDatabase.LoadAssetAtPath<Texture2D>(KeyartPath) != null)
            {
                var keyart = LoadSprite(KeyartPath);
                if (keyart != null)
                {
                    background.sprite = keyart;
                    background.color = new Color(0.72f, 0.72f, 0.72f, 1f); // darken for readability
                    usedKeyart = true;
                }
            }

            if (!usedKeyart)
            {
                // Subtle warm glow so the flat backdrop reads as intentional.
                var glow = UiFactory.CreateImage("Glow", canvasRoot, theme.Soft,
                    UiPalette.WithAlpha(new Color(0.35f, 0.26f, 0.16f), 0.35f));
                UiFactory.Place((RectTransform)glow.transform, new Vector2(0.5f, 0.5f), new Vector2(60f, 40f),
                    new Vector2(1400f, 900f));
            }
            return background;
        }

        // ------------------------------------------------------------------ menu scene

        private static void BuildMenuScene(CardArtIndex artIndex)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildCamera();
            BuildEventSystem();
            var (canvas, theme) = BuildCanvas(artIndex);

            var background = BuildBackground(canvas.transform, theme, tryKeyart: true);

            var rootRect = UiFactory.CreateRect("MenuRoot", canvas.transform);
            UiFactory.Stretch(rootRect);

            var menu = canvas.gameObject.AddComponent<MainMenu>();
            menu.Theme = theme;
            menu.Background = background;
            menu.Root = rootRect;

            SaveScene(scene, MenuScenePath);
        }

        // ------------------------------------------------------------------ game scene

        private static void BuildGameScene(CardArtIndex artIndex)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildCamera();
            BuildEventSystem();
            var (canvas, theme) = BuildCanvas(artIndex);
            var canvasRoot = canvas.transform;

            BuildBackground(canvasRoot, theme, tryKeyart: false);

            // --- board track (full-screen, behind everything else) ---
            var board = CreateView<BoardTrackView>("Board", canvasRoot, out var boardRect);
            UiFactory.Stretch(boardRect);
            board.Container = boardRect;

            // --- market (center, compact) ---
            var market = CreateView<MarketView>("Market", canvasRoot, out var marketRect);
            UiFactory.Place(marketRect, new Vector2(0.5f, 0.5f), new Vector2(60f, 80f), new Vector2(760f, 500f));
            market.Container = marketRect;

            // --- opponents strip (top-center) ---
            var opponentsBar = UiFactory.CreateRect("OpponentsBar", canvasRoot);
            opponentsBar.anchorMin = new Vector2(0.5f, 1f);
            opponentsBar.anchorMax = new Vector2(0.5f, 1f);
            opponentsBar.pivot = new Vector2(0.5f, 1f);
            opponentsBar.anchoredPosition = new Vector2(-140f, -6f);
            opponentsBar.sizeDelta = new Vector2(1040f, 92f);
            var oppLayout = opponentsBar.gameObject.AddComponent<HorizontalLayoutGroup>();
            oppLayout.spacing = 14f;
            oppLayout.childAlignment = TextAnchor.MiddleCenter;
            oppLayout.childControlWidth = false;
            oppLayout.childControlHeight = false;
            oppLayout.childForceExpandWidth = false;
            oppLayout.childForceExpandHeight = false;

            // --- player sheet (tall left column) ---
            var playerSheet = CreateView<PlayerSheetView>("PlayerSheet", canvasRoot, out var sheetRect);
            sheetRect.anchorMin = Vector2.zero;
            sheetRect.anchorMax = Vector2.zero;
            sheetRect.pivot = Vector2.zero;
            sheetRect.anchoredPosition = new Vector2(12f, 12f);
            sheetRect.sizeDelta = new Vector2(400f, 550f);
            playerSheet.Container = sheetRect;

            // --- corner pile widgets (Slay-the-Spire layout) ---
            var drawPile = BuildPile(canvasRoot, theme, "DrawPile", "Draw", faceDown: true,
                new Vector2(0f, 0f), new Vector2(424f, 12f));
            var playedPile = BuildPile(canvasRoot, theme, "PlayedPile", "Played", faceDown: false,
                new Vector2(0f, 0f), new Vector2(424f, 214f));
            var discardPile = BuildPile(canvasRoot, theme, "DiscardPile", "Discard", faceDown: false,
                new Vector2(1f, 0f), new Vector2(-234f, 12f));
            var exilePile = BuildPile(canvasRoot, theme, "ExilePile", "Exile", faceDown: false,
                new Vector2(1f, 0f), new Vector2(-234f, 214f));

            // --- play-history bar (left edge; populated at runtime) ---
            var history = CreateView<PlayHistoryBar>("HistoryBar", canvasRoot, out var historyRect);
            historyRect.anchorMin = new Vector2(0f, 1f);
            historyRect.anchorMax = new Vector2(0f, 1f);
            historyRect.pivot = new Vector2(0f, 1f);
            historyRect.anchoredPosition = new Vector2(12f, -12f);
            historyRect.sizeDelta = new Vector2(96f, 480f);
            history.Container = historyRect;

            // --- hand (bottom-center) ---
            var hand = CreateView<HandView>("Hand", canvasRoot, out var handRect);
            handRect.anchorMin = new Vector2(0.5f, 0f);
            handRect.anchorMax = new Vector2(0.5f, 0f);
            handRect.pivot = new Vector2(0.5f, 0f);
            handRect.anchoredPosition = new Vector2(60f, -8f);
            handRect.sizeDelta = new Vector2(900f, 300f);
            hand.Container = handRect;
            hand.Theme = theme;

            // --- stack panel (slides in from the right, above the discard column) ---
            var stack = CreateView<StackPanelView>("StackPanel", canvasRoot, out var stackRect);
            stackRect.anchorMin = new Vector2(1f, 0.5f);
            stackRect.anchorMax = new Vector2(1f, 0.5f);
            stackRect.pivot = new Vector2(1f, 0.5f);
            stackRect.anchoredPosition = new Vector2(340f, 260f);
            stackRect.sizeDelta = new Vector2(310f, 460f);
            stack.Container = stackRect;

            // --- log (left-middle, above the sheet) ---
            var log = CreateView<LogPanel>("LogPanel", canvasRoot, out var logRect);
            logRect.anchorMin = new Vector2(0f, 0f);
            logRect.anchorMax = new Vector2(0f, 0f);
            logRect.pivot = new Vector2(0f, 0f);
            logRect.anchoredPosition = new Vector2(116f, 574f);
            logRect.sizeDelta = new Vector2(380f, 300f);
            log.Container = logRect;

            // --- persistent END TURN / PASS button (populated by GameScreen) ---
            var actionBar = UiFactory.CreateRect("ActionBar", canvasRoot);
            actionBar.anchorMin = new Vector2(1f, 0f);
            actionBar.anchorMax = new Vector2(1f, 0f);
            actionBar.pivot = new Vector2(1f, 0f);
            actionBar.anchoredPosition = new Vector2(-234f, 416f);
            actionBar.sizeDelta = new Vector2(210f, 58f);

            // --- response window (above the hand) ---
            var response = CreateOverlayView<ResponseWindowView>("ResponseWindow", canvasRoot, out var responseContainer);
            UiFactory.Place(responseContainer, new Vector2(0.5f, 0f), new Vector2(60f, 316f), new Vector2(640f, 96f));
            response.Container = responseContainer;

            // --- toast (center, above the showcase) ---
            var toast = CreateOverlayView<ToastView>("Toast", canvasRoot, out var toastContainer);
            UiFactory.Place(toastContainer, new Vector2(0.5f, 0.5f), new Vector2(0f, 330f), new Vector2(620f, 60f));
            toast.Container = toastContainer;

            // --- targeting arrow overlay ---
            var arrow = CreateOverlayView<TargetingArrow>("TargetingArrow", canvasRoot, out var arrowContainer);
            UiFactory.Stretch(arrowContainer);
            arrow.Container = arrowContainer;

            // --- modals (top-most, in order) ---
            var decision = CreateOverlayView<DecisionModalView>("DecisionModal", canvasRoot, out var decisionContainer);
            UiFactory.Stretch(decisionContainer);
            decision.Container = decisionContainer;

            var opponentDetail = CreateOverlayView<OpponentDetailModal>("OpponentDetailModal", canvasRoot, out var oppDetailContainer);
            UiFactory.Stretch(oppDetailContainer);
            opponentDetail.Container = oppDetailContainer;

            var cardList = CreateOverlayView<CardListModal>("CardListModal", canvasRoot, out var cardListContainer);
            UiFactory.Stretch(cardListContainer);
            cardList.Container = cardListContainer;

            var gameOver = CreateOverlayView<GameOverPanel>("GameOverPanel", canvasRoot, out var gameOverContainer);
            UiFactory.Stretch(gameOverContainer);
            gameOver.Container = gameOverContainer;

            // --- controllers ---
            var queue = canvas.gameObject.AddComponent<PresentationQueue>();

            var screen = canvas.gameObject.AddComponent<GameScreen>();
            screen.Theme = theme;
            screen.Queue = queue;
            screen.Hand = hand;
            screen.Market = market;
            screen.Board = board;
            screen.PlayerSheet = playerSheet;
            screen.OpponentsBar = opponentsBar;
            screen.StackPanel = stack;
            screen.ResponseWindow = response;
            screen.DecisionModal = decision;
            screen.Arrow = arrow;
            screen.Log = log;
            screen.GameOver = gameOver;
            screen.Toast = toast;
            screen.CardList = cardList;
            screen.ActionBar = actionBar;
            screen.DrawPile = drawPile;
            screen.PlayedPile = playedPile;
            screen.DiscardPile = discardPile;
            screen.ExilePile = exilePile;
            screen.History = history;
            screen.OpponentDetail = opponentDetail;

            var bootstrapGo = new GameObject("GameRoot");
            var bootstrap = bootstrapGo.AddComponent<GameBootstrap>();
            bootstrap.Screen = screen;

            SaveScene(scene, GameScenePath);
        }

        // ------------------------------------------------------------------ helpers

        private static PileWidget BuildPile(Transform canvasRoot, UiTheme theme, string goName,
            string title, bool faceDown, Vector2 anchor, Vector2 pos)
        {
            // Container only — GameScreen.Bind calls Init at runtime (private view refs
            // don't survive scene serialization, same as every other view).
            var pile = CreateView<PileWidget>(goName, canvasRoot, out var rect);
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(140f, 190f);
            pile.Container = rect;
            return pile;
        }

        /// <summary>View whose Container is its own rect (always active).</summary>
        private static T CreateView<T>(string name, Transform parent, out RectTransform rect)
            where T : Component
        {
            rect = UiFactory.CreateRect(name, parent);
            return rect.gameObject.AddComponent<T>();
        }

        /// <summary>
        /// View that toggles its Container on/off: the component lives on an always-active
        /// holder so its coroutines keep running while the container is hidden.
        /// </summary>
        private static T CreateOverlayView<T>(string name, Transform parent, out RectTransform container)
            where T : Component
        {
            var holder = UiFactory.CreateRect(name, parent);
            UiFactory.Stretch(holder);
            container = UiFactory.CreateRect("Container", holder);
            UiFactory.Stretch(container);
            return holder.gameObject.AddComponent<T>();
        }

        private static void SaveScene(Scene scene, string path)
        {
            if (!EditorSceneManager.SaveScene(scene, path))
                Debug.LogError($"[Pascension] Failed to save {path}");
        }
    }
}
#endif
