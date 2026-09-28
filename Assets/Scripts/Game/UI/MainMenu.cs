using System;
using System.Collections.Generic;
using Pascension.Content;
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
        private RectTransform _settingsPanel;
        private RectTransform _changelogPanel;
        private RectTransform _changelogContent;
        private RectTransform _accountPanel;
        private AccountPanel _accountPanelComponent;
        private RectTransform _statsPanel;
        private Soi.SoiStatsScreen _statsScreen;

        private void Start()
        {
            // No legitimate flow reaches the menu with networking alive (host/join go
            // straight to the Lobby scene) — a listening NetworkManager here is always
            // a leak from a finished online match. Clear it before starting another.
            if (Unity.Netcode.NetworkManager.Singleton != null &&
                Unity.Netcode.NetworkManager.Singleton.IsListening)
                Pascension.Net.NetLauncher.Shutdown();

            ContentRegistry.RegisterAll();

            AudioListener.volume = PlayerPrefs.GetFloat(SceneFlow.PrefMasterVolume, 1f);

            BuildHome();
            BuildSettings();
            BuildChangelog();
            _accountPanelComponent = AccountPanel.Create(Parent, Theme, () => ShowPanel(_homePanel));
            _accountPanel = _accountPanelComponent.Root;

            // SoI stats screen — only when this build ships SoI (Get falls back to
            // Pascension for unknown ids, so compare the id, not null).
            if (Pascension.Net.GameCatalog.Get("shards").GameId == "shards")
            {
                _statsScreen = Soi.SoiStatsScreen.Create(Parent, Theme,
                    () => ShowPanel(_homePanel), OpenMultiplayer);
                _statsPanel = _statsScreen.Root;
            }

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

            // Account corner widget (top-right); its Init is the single boot trigger.
            gameObject.AddComponent<AccountMenuControl>().Init(Theme, Parent, () => ShowPanel(_accountPanel));

            // A session that expires while the player sits on the home screen bounces
            // them to the account panel (SignedOut + a made choice == account mode).
            Pascension.Net.AccountService.Changed += OnAccountChanged;

            // Initial panel — AFTER every panel is built (ShowPanel touches them all).
            // First run: the account-or-guest choice comes before anything else.
            ShowPanel(Pascension.Net.AccountService.FirstRunChoicePending ? _accountPanel : _homePanel);
        }

        private void OnDestroy()
        {
            Pascension.Net.AccountService.Changed -= OnAccountChanged;
        }

        private void OnAccountChanged()
        {
            if (Pascension.Net.AccountService.State == Pascension.Net.AccountState.SignedOut &&
                !Pascension.Net.AccountService.FirstRunChoicePending &&
                _homePanel != null && _homePanel.gameObject.activeSelf)
                ShowPanel(_accountPanel);
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
            // Straight to the lobby: the game is picked THERE (host game-cycle button,
            // replicated) — a pre-lobby choice was ignored and just confused people.
            // Online play needs an account (guests pass in the editor dev fallback).
            var multi = MenuButton(_homePanel, "MULTIPLAYER", ref y);
            multi.onClick.AddListener(OpenMultiplayer);

            if (Pascension.Net.GameCatalog.Get("shards").GameId == "shards")
            {
                var solo = MenuButton(_homePanel, "PLAY SHARDS VS AI", ref y);
                solo.onClick.AddListener(Soi.SoiSoloMatch.Launch);
            }

            var settings = MenuButton(_homePanel, "SETTINGS", ref y);
            settings.onClick.AddListener(() => ShowPanel(_settingsPanel));

            if (Pascension.Net.GameCatalog.Get("shards").GameId == "shards")
            {
                var stats = MenuButton(_homePanel, "STATS", ref y);
                stats.onClick.AddListener(() => { _statsScreen.Open(); ShowPanel(_statsPanel); });
            }

            var changelog = MenuButton(_homePanel, "CHANGELOG", ref y);
            changelog.onClick.AddListener(() => ShowPanel(_changelogPanel));

            var quit = MenuButton(_homePanel, "QUIT", ref y);
            quit.onClick.AddListener(Application.Quit);
        }

        private void OpenMultiplayer()
        {
            if (Pascension.Net.AccountService.CanPlayOnline)
                UnityEngine.SceneManagement.SceneManager.LoadScene(Pascension.Net.NetLauncher.LobbySceneName);
            else
            {
                _accountPanelComponent.ShowOnlineNotice();
                ShowPanel(_accountPanel);
            }
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
            _settingsPanel.gameObject.SetActive(panel == _settingsPanel);
            _changelogPanel.gameObject.SetActive(panel == _changelogPanel);
            _accountPanel.gameObject.SetActive(panel == _accountPanel);
            if (_statsPanel != null) _statsPanel.gameObject.SetActive(panel == _statsPanel);
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
                if (entry.Cards != null)
                    foreach (var change in entry.Cards)
                        y = RenderBalanceChange(change, y, width);
            }
            _changelogContent.sizeDelta = new Vector2(0f, -y + 10f);
            _changelogContent.GetComponentInParent<ScrollRect>().StopMovement();
            _changelogContent.anchoredPosition = Vector2.zero;
        }
        private float RenderBalanceChange(Changelog.CardChange change, float y, float width)
        {
            const float scale = 1.15f;
            float cardWidth = CardView.Width * scale;
            float cardHeight = CardView.Height * scale;
            var row = UiFactory.CreateRect("CardChange", _changelogContent);
            UiFactory.Place(row, new Vector2(0f, 1f), new Vector2(14f, y), new Vector2(width, cardHeight + 74f));
            var title = UiFactory.CreateText(Theme, "CardName", row, change.After.Face.Name, 20f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -2f), new Vector2(width, 28f));
            for (int side = 0; side < 2; side++)
            {
                float x = (side == 0 ? -1 : 1) * (cardWidth / 2f + 54f);
                var label = UiFactory.CreateText(Theme, side == 0 ? "Before" : "After", row,
                    Loc.T(side == 0 ? "BEFORE" : "AFTER"), 14f,
                    side == 0 ? UiPalette.TextDim : UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(label.rectTransform, new Vector2(0.5f, 1f), new Vector2(x, -34f), new Vector2(cardWidth, 24f));
                var card = CardViewFactory.Create(row, Theme, scale);
                card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0.5f, 1f);
                card.Rect.pivot = new Vector2(0.5f, 1f);
                card.Rect.anchoredPosition = new Vector2(x, -64f);
                card.BindFace(side == 0 ? change.Before.Face : change.After.Face);
            }
            var arrow = UiFactory.CreateRect("Arrow", row);
            UiFactory.Place(arrow, new Vector2(0.5f, 1f), new Vector2(0f, -64f - cardHeight / 2f), new Vector2(60f, 36f));
            var shaft = UiFactory.CreateImage("Shaft", arrow, null, UiPalette.Gold, raycast: false);
            UiFactory.Place(shaft.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(46f, 4f));
            for (int side = -1; side <= 1; side += 2)
            {
                var head = UiFactory.CreateImage("Head", arrow, null, UiPalette.Gold, raycast: false);
                UiFactory.Place(head.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(16f, side * 7f), new Vector2(22f, 4f));
                head.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -side * 40f);
            }
            return y - cardHeight - 100f;
        }
    }
}
