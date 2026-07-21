using System;
using System.Collections.Generic;
using Pascension.Content;
using Pascension.Core;
using Pascension.Engine.Heroes;
using Pascension.Game.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.UI
{
    /// <summary>
    /// Main menu controller. The static scene (camera/canvas/background/theme) comes
    /// from the SceneBuilder; the panels are constructed here at runtime from the theme,
    /// so the menu always matches the registered content (heroes etc.).
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        public UiTheme Theme;
        public Image Background;
        public RectTransform Root;

        private RectTransform _homePanel;
        private RectTransform _gameSelectPanel;
        private RectTransform _soloPanel;
        private RectTransform _soiSoloPanel;
        private RectTransform _settingsPanel;
        private RectTransform _changelogPanel;
        private RectTransform _changelogContent;

        private IReadOnlyList<HeroDefinition> _heroes;
        private int _selectedHero;
        private readonly List<Outline> _heroOutlines = new List<Outline>();
        private int _botCount = 1;
        private readonly int[] _botHero = new int[3];
        private readonly BotKind[] _botKind = new BotKind[3];
        private readonly List<Button> _countButtons = new List<Button>();
        private readonly List<RectTransform> _botRows = new List<RectTransform>();
        private readonly List<TextMeshProUGUI> _botHeroLabels = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _botKindLabels = new List<TextMeshProUGUI>();

        private void Start()
        {
            // No legitimate flow reaches the menu with networking alive (host/join go
            // straight to the Lobby scene) — a listening NetworkManager here is always
            // a leak from a finished online match, and it would hijack the next solo
            // start (NetBootstrap rebuilds the old networked match in the Game scene).
            if (Unity.Netcode.NetworkManager.Singleton != null &&
                Unity.Netcode.NetworkManager.Singleton.IsListening)
                Pascension.Net.NetLauncher.Shutdown();

            ContentRegistry.RegisterAll();
            _heroes = HeroDatabase.All;

            AudioListener.volume = PlayerPrefs.GetFloat(SceneFlow.PrefMasterVolume, 1f);

            for (int i = 0; i < 3; i++)
            {
                _botHero[i] = Mathf.Min(i + 1, _heroes.Count - 1);
                _botKind[i] = BotKind.Heuristic;
            }

            BuildHome();
            BuildGameSelect();
            BuildSolo();
            BuildSoloShards();
            BuildSettings();
            BuildChangelog();
            ShowPanel(_homePanel);

            // Language toggle — parented to the Root (not a panel) so ShowPanel never
            // hides it; visible on every menu screen.
            var lang = UiFactory.CreateButton(Theme, "LanguageToggle", Parent,
                Loc.French ? "LANGUE : FR" : "LANGUAGE: EN", 15f);
            UiFactory.Place((RectTransform)lang.transform, new Vector2(1f, 0f), new Vector2(-24f, 24f), new Vector2(190f, 44f));
            lang.onClick.AddListener(() =>
            {
                Loc.SetFrench(!Loc.French);
                SceneFlow.LoadMenu(); // rebuild the menu in the new language
            });

            // Version label + self-update button — Root-parented corner widgets like
            // the language toggle, so ShowPanel never hides them.
            gameObject.AddComponent<Pascension.Game.Update.UpdateMenuControl>().Init(Theme, Parent);
        }

        private Transform Parent => Root != null ? (Transform)Root : transform;

        // ------------------------------------------------------------------ home

        private void BuildHome()
        {
            _homePanel = UiFactory.CreateRect("HomePanel", Parent);
            UiFactory.Stretch(_homePanel);

            var title = UiFactory.CreateText(Theme, "Title", _homePanel, "PASCENSION", 96f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 18f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -110f), new Vector2(1200f, 110f));
            var titleShadow = title.gameObject.AddComponent<Shadow>();
            titleShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            titleShadow.effectDistance = new Vector2(0f, -5f);

            var subtitle = UiFactory.CreateText(Theme, "Subtitle", _homePanel,
                Loc.T("race the board · build the deck · burst the boss"), 22f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -215f), new Vector2(900f, 30f));

            float y = -40f;
            var solo = MenuButton(_homePanel, "NEW GAME", ref y);
            solo.onClick.AddListener(() => ShowPanel(_gameSelectPanel));

            // Straight to the lobby: the game is picked THERE (host game-cycle button,
            // replicated) — a pre-lobby choice was ignored and just confused people.
            var multi = MenuButton(_homePanel, "MULTIPLAYER", ref y);
            multi.onClick.AddListener(() =>
                UnityEngine.SceneManagement.SceneManager.LoadScene(Pascension.Net.NetLauncher.LobbySceneName));

            var settings = MenuButton(_homePanel, "SETTINGS", ref y);
            settings.onClick.AddListener(() => ShowPanel(_settingsPanel));

            var changelog = MenuButton(_homePanel, "CHANGELOG", ref y);
            changelog.onClick.AddListener(() => ShowPanel(_changelogPanel));

            var quit = MenuButton(_homePanel, "QUIT", ref y);
            quit.onClick.AddListener(Application.Quit);
        }

        private Button MenuButton(Transform parent, string label, ref float y)
        {
            // The GameObject NAME stays English (other code finds buttons by name);
            // only the display text translates.
            var button = UiFactory.CreateButton(Theme, label, parent, Loc.T(label), 26f);
            UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(380f, 64f));
            y -= 84f;
            return button;
        }

        // ------------------------------------------------------------------ game select

        private void BuildGameSelect()
        {
            _gameSelectPanel = UiFactory.CreateRect("GameSelectPanel", Parent);
            UiFactory.Stretch(_gameSelectPanel);

            var title = UiFactory.CreateText(Theme, "Title", _gameSelectPanel, Loc.T("CHOOSE A GAME"), 44f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 6f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(800f, 56f));

            var modules = Pascension.Net.GameCatalog.All;
            float x = -(modules.Count - 1) * 240f;
            foreach (var module in modules)
            {
                var banner = UiFactory.CreateButton(Theme, "Game_" + module.GameId, _gameSelectPanel,
                    module.DisplayName.ToUpperInvariant(), 30f,
                    module.Playable ? UiPalette.Gold : UiPalette.PanelLight, UiPalette.Background);
                UiFactory.Place((RectTransform)banner.transform, new Vector2(0.5f, 0.5f),
                    new Vector2(x, 20f), new Vector2(420f, 220f));
                banner.interactable = module.Playable;
                string gameId = module.GameId;
                banner.onClick.AddListener(() => OnGameChosen(gameId));

                if (!module.Playable)
                {
                    var soon = UiFactory.CreateText(Theme, "Soon", banner.transform, Loc.T("coming soon"), 16f,
                        UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
                    UiFactory.Place(soon.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(300f, 22f));
                }
                x += 480f;
            }

            var back = UiFactory.CreateButton(Theme, "Back", _gameSelectPanel, Loc.T("BACK"), 20f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(220f, 54f));
            back.onClick.AddListener(() => ShowPanel(_homePanel));
        }

        private void OnGameChosen(string gameId)
        {
            MatchSetup.GameId = gameId;
            MatchSetup.DlcFlags = 0;
            if (gameId == "shards")
                ShowPanel(_soiSoloPanel);
            else
                ShowPanel(_soloPanel);
        }

        // ------------------------------------------------------------------ shards solo setup

        private string _soiCharacter = "decima";
        private int _soiDlc;
        private int _soiBots = 1;
        private BotKind _soiDifficulty = BotKind.Greedy;
        private RectTransform _soiCharacterRow;
        private readonly List<Button> _soiBotButtons = new List<Button>();
        private TextMeshProUGUI _soiDifficultyLabel;

        private void BuildSoloShards()
        {
            _soiSoloPanel = UiFactory.CreateRect("SoiSoloPanel", Parent);
            UiFactory.Stretch(_soiSoloPanel);

            var title = UiFactory.CreateText(Theme, "Title", _soiSoloPanel, Loc.T("SHARDS OF INFINITY — NEW GAME"), 40f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 4f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -100f), new Vector2(1100f, 52f));

            var charLabel = UiFactory.CreateText(Theme, "CharLabel", _soiSoloPanel, Loc.T("YOUR CHARACTER"), 18f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(charLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(600f, 26f));
            _soiCharacterRow = UiFactory.CreateRect("Characters", _soiSoloPanel);
            UiFactory.Place(_soiCharacterRow, new Vector2(0.5f, 1f), new Vector2(0f, -260f), new Vector2(1200f, 110f));

            var dlcLabel = UiFactory.CreateText(Theme, "DlcLabel", _soiSoloPanel, Loc.T("EXPANSIONS"), 18f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(dlcLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -360f), new Vector2(600f, 26f));
            var module = Pascension.Net.GameCatalog.Get("shards");
            float dy = -400f;
            foreach (var dlc in module.DlcOptions)
            {
                var toggle = UiFactory.CreateToggle(Theme, "Dlc_" + dlc.Flag, _soiSoloPanel,
                    dlc.Name + "  —  " + dlc.Description);
                UiFactory.Place((RectTransform)toggle.transform, new Vector2(0.5f, 1f), new Vector2(-40f, dy), new Vector2(860f, 34f));
                dy -= 42f;
                int flag = dlc.Flag;
                toggle.onValueChanged.AddListener(on =>
                {
                    if (on) _soiDlc |= flag;
                    else _soiDlc &= ~flag;
                    RebuildSoiCharacters(); // Rez appears only with Shadow of Salvation
                });
            }

            var botsLabel = UiFactory.CreateText(Theme, "BotsLabel", _soiSoloPanel, Loc.T("OPPONENTS"), 18f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(botsLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-160f, -560f), new Vector2(320f, 26f));
            _soiBotButtons.Clear();
            for (int i = 1; i <= 3; i++)
            {
                var button = UiFactory.CreateButton(Theme, "Bots_" + i, _soiSoloPanel, i.ToString(), 22f);
                UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 1f),
                    new Vector2(-250f + (i - 1) * 90f, -614f), new Vector2(74f, 56f));
                int count = i;
                button.onClick.AddListener(() =>
                {
                    _soiBots = count;
                    RefreshSoiBotButtons();
                });
                _soiBotButtons.Add(button);
            }

            var difficultyLabel = UiFactory.CreateText(Theme, "DifficultyLabel", _soiSoloPanel, Loc.T("DIFFICULTY"), 18f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(difficultyLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(180f, -560f), new Vector2(320f, 26f));
            var difficultyButton = UiFactory.CreateButton(Theme, "DifficultyCycle", _soiSoloPanel, "", 18f);
            UiFactory.Place((RectTransform)difficultyButton.transform, new Vector2(0.5f, 1f),
                new Vector2(180f, -614f), new Vector2(300f, 56f));
            difficultyButton.onClick.AddListener(CycleSoiDifficulty);
            _soiDifficultyLabel = UiFactory.ButtonLabel(difficultyButton);
            RefreshSoiDifficulty();

            var start = UiFactory.CreateButton(Theme, "Start", _soiSoloPanel, Loc.T("START GAME"), 24f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)start.transform, new Vector2(0.5f, 0f), new Vector2(0f, 150f), new Vector2(360f, 64f));
            start.onClick.AddListener(StartSoiSolo);

            var back = UiFactory.CreateButton(Theme, "Back", _soiSoloPanel, Loc.T("BACK"), 20f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 70f), new Vector2(220f, 54f));
            back.onClick.AddListener(() => ShowPanel(_gameSelectPanel));

            RebuildSoiCharacters();
            RefreshSoiBotButtons();
        }

        private void RebuildSoiCharacters()
        {
            foreach (Transform child in _soiCharacterRow)
                Destroy(child.gameObject);
            var module = Pascension.Net.GameCatalog.Get("shards");
            var characters = module.CharactersFor(_soiDlc);
            bool stillValid = CharacterPick.IsRandom(_soiCharacter);
            foreach (var c in characters)
                if (c.Id == _soiCharacter)
                    stillValid = true;
            if (!stillValid) _soiCharacter = characters[0].Id;

            // Roster buttons + a RANDOM button; buttons shrink (keeping a fixed gap)
            // when Rez (SoS) pushes the row past its 1200-wide container.
            int total = characters.Count + 1;
            const float gap = 20f;
            float buttonW = Mathf.Min(200f, (1200f - (total - 1) * gap) / total);
            float step = buttonW + gap;
            float x = -(total - 1) * step * 0.5f;
            for (int i = 0; i < total; i++)
            {
                bool isRandom = i == characters.Count;
                string id = isRandom ? CharacterPick.RandomId : characters[i].Id;
                string label = isRandom ? Loc.T("RANDOM") + " ?" : characters[i].DisplayName.ToUpperInvariant();
                bool selected = id == _soiCharacter;
                var button = UiFactory.CreateButton(Theme, "Char_" + id, _soiCharacterRow, label, 16f,
                    selected ? UiPalette.Gold : UiPalette.PanelLight, UiPalette.Background);
                UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 0.5f), new Vector2(x, 0f), new Vector2(buttonW, 88f));
                x += step;
                string picked = id;
                button.onClick.AddListener(() =>
                {
                    _soiCharacter = picked;
                    RebuildSoiCharacters();
                });
            }
        }

        private void RefreshSoiBotButtons()
        {
            for (int i = 0; i < _soiBotButtons.Count; i++)
            {
                var image = _soiBotButtons[i].GetComponent<Image>();
                if (image != null)
                    image.color = (i + 1) == _soiBots ? UiPalette.Gold : UiPalette.PanelLight;
            }
        }

        private void CycleSoiDifficulty()
        {
            _soiDifficulty = _soiDifficulty switch
            {
                BotKind.Heuristic => BotKind.Greedy,
                BotKind.Greedy => BotKind.Strong,
                _ => BotKind.Heuristic
            };
            RefreshSoiDifficulty();
        }

        private void RefreshSoiDifficulty()
        {
            if (_soiDifficultyLabel == null) return;
            _soiDifficultyLabel.text = _soiDifficulty switch
            {
                BotKind.Heuristic => Loc.T("NORMAL"),
                BotKind.Greedy => Loc.T("HARD"),
                _ => Loc.T("MASTER (thinks ~1s)")
            };
        }

        private void StartSoiSolo()
        {
            var module = Pascension.Net.GameCatalog.Get("shards");
            var characters = module.CharactersFor(_soiDlc);

            MatchSetup.Seed = (ulong)DateTime.UtcNow.Ticks;

            // A RANDOM pick resolves here (seeded) before the bots are assigned.
            var roster = new List<string>(characters.Count);
            foreach (var c in characters) roster.Add(c.Id);
            string playerCharacter = CharacterPick.ResolveRandoms(
                new List<string> { _soiCharacter }, roster, MatchSetup.Seed)[0];

            MatchSetup.GameId = "shards";
            MatchSetup.DlcFlags = _soiDlc;
            MatchSetup.PlayerHeroId = playerCharacter;
            MatchSetup.PlayerName = "You";
            MatchSetup.Opponents = new List<OpponentSetup>();
            int next = 0;
            for (int i = 0; i < _soiBots; i++)
            {
                // Cycle the roster, skipping the player's pick first time around.
                while (characters[next % characters.Count].Id == playerCharacter && characters.Count > 1)
                    next++;
                MatchSetup.Opponents.Add(new OpponentSetup(characters[next % characters.Count].Id, _soiDifficulty));
                next++;
            }
            MatchSetup.Configured = true;
            SceneFlow.LoadGame(module.GameSceneName);
        }

        // ------------------------------------------------------------------ solo setup

        private void BuildSolo()
        {
            _soloPanel = UiFactory.CreateRect("SoloPanel", Parent);
            UiFactory.Stretch(_soloPanel);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _soloPanel);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1460f, 900f));

            var title = UiFactory.CreateText(Theme, "Title", panel.transform, Loc.T("SOLO GAME"), 40f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 8f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(600f, 48f));

            var heroLabel = UiFactory.CreateText(Theme, "HeroLabel", panel.transform, Loc.T("CHOOSE YOUR HERO"), 20f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(heroLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -74f), new Vector2(600f, 26f));

            // Hero picker cards (+ a RANDOM card at the end) — width shrinks to fit.
            int pickCount = _heroes.Count + 1;
            float spacing = 24f;
            float heroCardW = Mathf.Min(320f, (1408f - (pickCount - 1) * spacing) / pickCount);
            float startX = -((pickCount - 1) * (heroCardW + spacing)) * 0.5f;
            for (int i = 0; i < _heroes.Count; i++)
            {
                var hero = _heroes[i];
                var card = UiFactory.CreatePanel(Theme, $"Hero_{hero.Id}", panel.transform, UiPalette.PanelLight);
                UiFactory.Place(card.rectTransform, new Vector2(0.5f, 1f),
                    new Vector2(startX + i * (heroCardW + spacing), -112f), new Vector2(heroCardW, 470f));

                var outline = card.gameObject.AddComponent<Outline>();
                outline.effectColor = UiPalette.Gold;
                outline.effectDistance = new Vector2(3f, -3f);
                outline.enabled = i == _selectedHero;
                _heroOutlines.Add(outline);

                var portraitFrame = UiFactory.CreateImage("PortraitFrame", card.transform, Theme.Rounded, UiPalette.Border);
                UiFactory.Place(portraitFrame.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(160f, 160f));
                var portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.Panel);
                UiFactory.Stretch(portrait.rectTransform, 3, 3, 3, 3);
                var art = Theme.Art(hero.Id);
                if (art != null)
                {
                    portrait.sprite = art;
                    portrait.color = Color.white;
                }
                else
                {
                    var initial = UiFactory.CreateText(Theme, "Initial", portraitFrame.transform,
                        hero.Id.Substring(0, 1).ToUpperInvariant(), 64f, UiPalette.Gold,
                        TextAlignmentOptions.Center, FontStyles.Bold);
                    UiFactory.Stretch(initial.rectTransform);
                }

                var nameText = UiFactory.CreateText(Theme, "Name", card.transform, hero.Name, 21f,
                    UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(nameText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(heroCardW - 20f, 26f));
                nameText.enableAutoSizing = true;
                nameText.fontSizeMin = 13f;
                nameText.fontSizeMax = 21f;

                var archetype = UiFactory.CreateText(Theme, "Archetype", card.transform, hero.Archetype, 15f,
                    UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Italic);
                UiFactory.Place(archetype.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -208f), new Vector2(heroCardW - 20f, 20f));

                var details = UiFactory.CreateText(Theme, "Details", card.transform, HeroDetails(hero), 13f,
                    UiPalette.TextDim, TextAlignmentOptions.TopLeft);
                UiFactory.Place(details.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -234f), new Vector2(heroCardW - 28f, 224f));
                details.enableAutoSizing = true;
                details.fontSizeMin = 10f;
                details.fontSizeMax = 14f;

                var button = card.gameObject.AddComponent<Button>();
                button.targetGraphic = card;
                button.transition = Selectable.Transition.None;
                int index = i;
                button.onClick.AddListener(() => SelectHero(index));
            }

            // The RANDOM card — resolved to a hero nobody else has at game start.
            {
                int index = _heroes.Count;
                var card = UiFactory.CreatePanel(Theme, "Hero_random", panel.transform, UiPalette.PanelLight);
                UiFactory.Place(card.rectTransform, new Vector2(0.5f, 1f),
                    new Vector2(startX + index * (heroCardW + spacing), -112f), new Vector2(heroCardW, 470f));

                var outline = card.gameObject.AddComponent<Outline>();
                outline.effectColor = UiPalette.Gold;
                outline.effectDistance = new Vector2(3f, -3f);
                outline.enabled = index == _selectedHero;
                _heroOutlines.Add(outline);

                var portraitFrame = UiFactory.CreateImage("PortraitFrame", card.transform, Theme.Rounded, UiPalette.Border);
                UiFactory.Place(portraitFrame.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(160f, 160f));
                var portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.Panel);
                UiFactory.Stretch(portrait.rectTransform, 3, 3, 3, 3);
                var question = UiFactory.CreateText(Theme, "Initial", portraitFrame.transform, "?", 64f,
                    UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Stretch(question.rectTransform);

                var nameText = UiFactory.CreateText(Theme, "Name", card.transform, Loc.T("RANDOM"), 21f,
                    UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(nameText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -180f), new Vector2(heroCardW - 20f, 26f));

                var archetype = UiFactory.CreateText(Theme, "Archetype", card.transform, "???", 15f,
                    UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Italic);
                UiFactory.Place(archetype.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -208f), new Vector2(heroCardW - 20f, 20f));

                var details = UiFactory.CreateText(Theme, "Details", card.transform,
                    Loc.T("A random hero is assigned when the game starts — never one another player already has."), 13f,
                    UiPalette.TextDim, TextAlignmentOptions.TopLeft);
                UiFactory.Place(details.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -234f), new Vector2(heroCardW - 28f, 224f));
                details.enableAutoSizing = true;
                details.fontSizeMin = 10f;
                details.fontSizeMax = 14f;

                var button = card.gameObject.AddComponent<Button>();
                button.targetGraphic = card;
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => SelectHero(index));
            }

            // Opponent configuration.
            var oppLabel = UiFactory.CreateText(Theme, "OppLabel", panel.transform, Loc.T("OPPONENTS"), 20f,
                UiPalette.TextDim, TextAlignmentOptions.Left, FontStyles.Bold);
            UiFactory.Place(oppLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(-370f, 284f), new Vector2(300f, 26f));

            for (int c = 1; c <= 3; c++)
            {
                var countButton = UiFactory.CreateButton(Theme, $"Count{c}", panel.transform, c.ToString(), 22f);
                UiFactory.Place((RectTransform)countButton.transform, new Vector2(0.5f, 0f),
                    new Vector2(-380f + (c - 1) * 62f, 196f), new Vector2(52f, 52f));
                int count = c;
                countButton.onClick.AddListener(() => SetBotCount(count));
                _countButtons.Add(countButton);
            }

            for (int b = 0; b < 3; b++)
            {
                var row = UiFactory.CreateRect($"BotRow{b}", panel.transform);
                UiFactory.Place(row, new Vector2(0.5f, 0f), new Vector2(120f, 236f - b * 58f), new Vector2(760f, 52f));

                var label = UiFactory.CreateText(Theme, "Label", row, $"Bot {b + 1}", 18f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                UiFactory.Place(label.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(90f, 30f));

                int botIndex = b;
                var heroButton = UiFactory.CreateButton(Theme, "HeroCycle", row, "", 16f);
                UiFactory.Place((RectTransform)heroButton.transform, new Vector2(0f, 0.5f), new Vector2(100f, 0f), new Vector2(320f, 44f));
                heroButton.onClick.AddListener(() => CycleBotHero(botIndex));
                _botHeroLabels.Add(UiFactory.ButtonLabel(heroButton));

                var kindButton = UiFactory.CreateButton(Theme, "KindCycle", row, "", 16f);
                UiFactory.Place((RectTransform)kindButton.transform, new Vector2(0f, 0.5f), new Vector2(436f, 0f), new Vector2(220f, 44f));
                kindButton.onClick.AddListener(() => CycleBotKind(botIndex));
                _botKindLabels.Add(UiFactory.ButtonLabel(kindButton));

                _botRows.Add(row);
            }

            var start = UiFactory.CreateButton(Theme, "Start", panel.transform, Loc.T("START GAME"), 26f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)start.transform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(340f, 64f));
            start.onClick.AddListener(StartSolo);

            var back = UiFactory.CreateButton(Theme, "Back", panel.transform, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0f, 0f), new Vector2(24f, 24f), new Vector2(140f, 48f));
            back.onClick.AddListener(() => ShowPanel(_homePanel));

            RefreshSoloControls();
        }

        private string HeroDetails(HeroDefinition hero)
        {
            var lines = new List<string> { hero.Description, "" };
            foreach (var (minLevel, passive) in hero.PassiveStatics)
                lines.Add($"<b>L{minLevel} Passive:</b> {passive.Description}");
            foreach (var (minLevel, trigger) in hero.PassiveTriggers)
                lines.Add($"<b>L{minLevel} Passive:</b> {trigger.Description}");
            if (hero.Active != null)
                lines.Add($"<b>L{hero.ActiveUnlockLevel} Active:</b> {hero.Active.Description} ({hero.Active.ApCost} AP)");
            if (hero.Ultimate != null)
                lines.Add($"<b>L{hero.UltimateUnlockLevel} Ultimate:</b> {hero.Ultimate.Description} ({hero.Ultimate.ApCost} AP)");
            return string.Join("\n", lines);
        }

        private void SelectHero(int index)
        {
            _selectedHero = index;
            // No-duplicate rule: a bot holding the newly picked hero moves to RANDOM.
            if (index < _heroes.Count)
                for (int b = 0; b < _botHero.Length; b++)
                    if (_botHero[b] == index)
                        _botHero[b] = _heroes.Count;
            for (int i = 0; i < _heroOutlines.Count; i++)
                _heroOutlines[i].enabled = i == index;
            RefreshSoloControls();
        }

        private void SetBotCount(int count)
        {
            int previous = _botCount;
            _botCount = Mathf.Clamp(count, 1, 3);
            // Only newly REVEALED rows re-check — a stale hidden default must yield
            // to the visible picks, never evict one.
            for (int b = previous; b < _botCount; b++)
                if (_botHero[b] < _heroes.Count && HeroIndexTaken(_botHero[b], b))
                    _botHero[b] = _heroes.Count;
            RefreshSoloControls();
        }

        private void CycleBotHero(int bot)
        {
            int total = _heroes.Count + 1; // roster + RANDOM
            for (int step = 1; step <= total; step++)
            {
                int candidate = (_botHero[bot] + step) % total;
                if (candidate == _heroes.Count || !HeroIndexTaken(candidate, bot))
                {
                    _botHero[bot] = candidate;
                    break;
                }
            }
            RefreshSoloControls();
        }

        /// <summary>Is this roster index held by the player or another active bot row?</summary>
        private bool HeroIndexTaken(int heroIndex, int exceptBot)
        {
            if (_selectedHero == heroIndex) return true;
            for (int b = 0; b < _botCount; b++)
                if (b != exceptBot && _botHero[b] == heroIndex)
                    return true;
            return false;
        }

        private void CycleBotKind(int bot)
        {
            _botKind[bot] = _botKind[bot] == BotKind.Heuristic ? BotKind.Random : BotKind.Heuristic;
            RefreshSoloControls();
        }

        private void RefreshSoloControls()
        {
            for (int i = 0; i < _countButtons.Count; i++)
                _countButtons[i].image.color = (i + 1) == _botCount ? UiPalette.Gold : UiPalette.PanelLight;
            for (int b = 0; b < _botRows.Count; b++)
            {
                _botRows[b].gameObject.SetActive(b < _botCount);
                _botHeroLabels[b].text = "Hero: " + HeroPickName(_botHero[b]);
                _botKindLabels[b].text = "AI: " + _botKind[b];
            }
        }

        private string HeroPickName(int index) =>
            index >= _heroes.Count ? Loc.T("RANDOM") : _heroes[index].Name;

        private string HeroPickId(int index) =>
            index >= _heroes.Count ? CharacterPick.RandomId : _heroes[index].Id;

        private void StartSolo()
        {
            MatchSetup.Seed = (ulong)DateTime.UtcNow.Ticks;

            // RANDOM picks resolve here (seeded), each to a hero nobody else has.
            var roster = new List<string>(_heroes.Count);
            foreach (var hero in _heroes) roster.Add(hero.Id);
            var picks = new List<string> { HeroPickId(_selectedHero) };
            for (int b = 0; b < _botCount; b++)
                picks.Add(HeroPickId(_botHero[b]));
            var resolved = CharacterPick.ResolveRandoms(picks, roster, MatchSetup.Seed);

            MatchSetup.PlayerHeroId = resolved[0];
            MatchSetup.PlayerName = "You";
            MatchSetup.Opponents = new List<OpponentSetup>();
            for (int b = 0; b < _botCount; b++)
                MatchSetup.Opponents.Add(new OpponentSetup(resolved[b + 1], _botKind[b]));
            MatchSetup.Configured = true;
            SceneFlow.LoadGame();
        }

        // ------------------------------------------------------------------ settings

        private void BuildSettings()
        {
            _settingsPanel = UiFactory.CreateRect("SettingsPanel", Parent);
            UiFactory.Stretch(_settingsPanel);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _settingsPanel);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(620f, 480f));

            var title = UiFactory.CreateText(Theme, "Title", panel.transform, Loc.T("SETTINGS"), 34f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(400f, 40f));

            BuildVolumeRow(panel.transform, "Master volume", -110f, SceneFlow.PrefMasterVolume, value =>
            {
                AudioListener.volume = value;
            });
            BuildVolumeRow(panel.transform, "Music volume", -180f, SceneFlow.PrefMusicVolume, null);

            var toggle = UiFactory.CreateToggle(Theme, "FullControl", panel.transform,
                Loc.T("Full control (hold priority even when you can only pass)"));
            UiFactory.Place((RectTransform)toggle.transform, new Vector2(0.5f, 0.5f), new Vector2(-10f, -20f), new Vector2(520f, 32f));
            toggle.isOn = PlayerPrefs.GetInt(SceneFlow.PrefFullControl, 0) == 1;
            toggle.onValueChanged.AddListener(on =>
            {
                PlayerPrefs.SetInt(SceneFlow.PrefFullControl, on ? 1 : 0);
                PlayerPrefs.Save();
            });

            var note = UiFactory.CreateText(Theme, "Note", panel.transform,
                Loc.T("Audio hooks are stubs until sound lands."), 13f, UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(note.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 96f), new Vector2(500f, 18f));

            var back = UiFactory.CreateButton(Theme, "Back", panel.transform, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(160f, 48f));
            back.onClick.AddListener(() => ShowPanel(_homePanel));
        }

        private void BuildVolumeRow(Transform parent, string label, float y, string prefKey, Action<float> apply)
        {
            var text = UiFactory.CreateText(Theme, label, parent, Loc.T(label), 18f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(text.rectTransform, new Vector2(0.5f, 1f), new Vector2(-140f, y), new Vector2(220f, 26f));

            var slider = UiFactory.CreateSlider(Theme, label + "Slider", parent);
            UiFactory.Place((RectTransform)slider.transform, new Vector2(0.5f, 1f), new Vector2(130f, y), new Vector2(280f, 30f));
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = PlayerPrefs.GetFloat(prefKey, 1f);
            slider.onValueChanged.AddListener(value =>
            {
                PlayerPrefs.SetFloat(prefKey, value);
                PlayerPrefs.Save();
                apply?.Invoke(value);
            });
        }

        // ------------------------------------------------------------------ panels

        private void ShowPanel(RectTransform panel)
        {
            _homePanel.gameObject.SetActive(panel == _homePanel);
            _gameSelectPanel.gameObject.SetActive(panel == _gameSelectPanel);
            _soloPanel.gameObject.SetActive(panel == _soloPanel);
            _soiSoloPanel.gameObject.SetActive(panel == _soiSoloPanel);
            _settingsPanel.gameObject.SetActive(panel == _settingsPanel);
            _changelogPanel.gameObject.SetActive(panel == _changelogPanel);
        }

        // ------------------------------------------------------------------ changelog

        private void BuildChangelog()
        {
            _changelogPanel = UiFactory.CreateRect("ChangelogPanel", Parent);
            UiFactory.Stretch(_changelogPanel);

            var panel = UiFactory.CreatePanel(Theme, "Panel", _changelogPanel);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(980f, 860f));

            var title = UiFactory.CreateText(Theme, "Title", panel.transform, Loc.T("CHANGELOG"), 38f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(600f, 46f));

            // One tab per registered game (SoI absent in PUBLIC_RELEASE builds).
            var modules = Pascension.Net.GameCatalog.All;
            var tabs = new List<Button>();
            float tabX = -(modules.Count - 1) * 160f;
            foreach (var module in modules)
            {
                var tab = UiFactory.CreateButton(Theme, "Tab_" + module.GameId, panel.transform,
                    module.DisplayName.ToUpperInvariant(), 17f);
                UiFactory.Place((RectTransform)tab.transform, new Vector2(0.5f, 1f),
                    new Vector2(tabX, -76f), new Vector2(300f, 46f));
                tabX += 320f;
                string gameId = module.GameId;
                var captured = tab;
                tab.onClick.AddListener(() =>
                {
                    foreach (var other in tabs)
                        other.image.color = other == captured ? UiPalette.Gold : UiPalette.PanelLight;
                    RenderChangelog(gameId);
                });
                tabs.Add(tab);
            }

            var scroll = UiFactory.CreateScrollView(Theme, "Entries", panel.transform, out _changelogContent);
            UiFactory.Place((RectTransform)scroll.transform, new Vector2(0.5f, 1f),
                new Vector2(0f, -134f), new Vector2(920f, 630f));

            var back = UiFactory.CreateButton(Theme, "Back", panel.transform, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(180f, 50f));
            back.onClick.AddListener(() => ShowPanel(_homePanel));

            if (tabs.Count > 0)
            {
                tabs[0].image.color = UiPalette.Gold;
                RenderChangelog(modules[0].GameId);
            }
        }

        private void RenderChangelog(string gameId)
        {
            foreach (Transform child in _changelogContent)
                Destroy(child.gameObject);

            var entries = gameId == "shards" ? Changelog.Shards : Changelog.Pascension;
            const float width = 880f, pad = 14f;
            float y = -10f;
            foreach (var entry in entries)
            {
                var date = UiFactory.CreateText(Theme, "Date", _changelogContent, entry.Date, 18f,
                    UiPalette.Gold, TextAlignmentOptions.TopLeft, FontStyles.Bold);
                UiFactory.Place(date.rectTransform, new Vector2(0f, 1f), new Vector2(pad, y), new Vector2(width, 24f));
                y -= 28f;

                // Width first, then preferredHeight — it wraps against the actual rect.
                var body = UiFactory.CreateText(Theme, "Body", _changelogContent,
                    Loc.French ? entry.Fr : entry.En, 15f, UiPalette.TextMain, TextAlignmentOptions.TopLeft);
                UiFactory.Place(body.rectTransform, new Vector2(0f, 1f), new Vector2(pad + 8f, y), new Vector2(width - 16f, 10f));
                float bodyH = body.preferredHeight + 6f;
                body.rectTransform.sizeDelta = new Vector2(width - 16f, bodyH);
                y -= bodyH + 22f;
            }
            _changelogContent.sizeDelta = new Vector2(0f, -y + 10f);
        }
    }
}
