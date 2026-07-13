using Pascension.Game.UI;
using Pascension.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Full-screen overlay for online interruptions. Two modes:
    /// · WAITING — the match is paused for disconnected player(s); shows who, the game
    ///   ID to re-share, and (host only) a "replace with bot" kick per player.
    /// · CONNECTION LOST — we lost the host; offers REJOIN (same game ID) or LEAVE.
    /// The dimmer swallows all game input; the host-side submit gate is the backstop.
    /// </summary>
    public sealed class PauseOverlayView : MonoBehaviour
    {
        private enum Mode { Hidden, Waiting, ConnectionLost }

        private UiTheme _theme;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _subtitle;
        private RectTransform _rows;
        private TextMeshProUGUI _footer;
        private Button _copyButton;
        private Button _rejoinButton;
        private TextMeshProUGUI _rejoinLabel;
        private Button _leaveButton;
        private Mode _mode = Mode.Hidden;
        private bool _busy;

        public static PauseOverlayView Create(Transform uiRoot, UiTheme theme)
        {
            var rect = UiFactory.CreateRect("PauseOverlay", uiRoot);
            UiFactory.Stretch(rect);
            var view = rect.gameObject.AddComponent<PauseOverlayView>();
            view.Build(theme);
            view.gameObject.SetActive(false);
            return view;
        }

        private void Build(UiTheme theme)
        {
            _theme = theme;

            var dimmer = UiFactory.CreateDimmer("Dimmer", transform);
            UiFactory.Stretch(dimmer.rectTransform);

            var panel = UiFactory.CreatePanel(theme, "Panel", transform);
            UiFactory.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(680f, 460f));

            _title = UiFactory.CreateText(theme, "Title", panel.transform, Loc.T("GAME PAUSED"), 34f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            _title.characterSpacing = 4f;
            UiFactory.Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(560f, 42f));

            _subtitle = UiFactory.CreateText(theme, "Subtitle", panel.transform, "", 17f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(_subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -76f), new Vector2(600f, 48f));

            _rows = UiFactory.CreateRect("Rows", panel.transform);
            UiFactory.Place(_rows, new Vector2(0.5f, 1f), new Vector2(0f, -136f), new Vector2(600f, 190f));

            _footer = UiFactory.CreateText(theme, "Footer", panel.transform, "", 22f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_footer.rectTransform, new Vector2(0.5f, 0f), new Vector2(-56f, 84f), new Vector2(440f, 34f));

            _copyButton = UiFactory.CreateButton(theme, "CopyButton", panel.transform, Loc.T("COPY"), 15f);
            UiFactory.Place((RectTransform)_copyButton.transform, new Vector2(0.5f, 0f), new Vector2(200f, 84f), new Vector2(92f, 36f));
            _copyButton.onClick.AddListener(() =>
            {
                if (!string.IsNullOrEmpty(NetLauncher.LastJoinCode))
                    GUIUtility.systemCopyBuffer = NetLauncher.LastJoinCode;
            });

            _rejoinButton = UiFactory.CreateButton(theme, "RejoinButton", panel.transform, Loc.T("REJOIN"), 20f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_rejoinButton.transform, new Vector2(0.5f, 0f), new Vector2(-130f, 22f), new Vector2(220f, 52f));
            _rejoinButton.onClick.AddListener(OnRejoinClicked);
            _rejoinLabel = UiFactory.ButtonLabel(_rejoinButton);

            _leaveButton = UiFactory.CreateButton(theme, "LeaveButton", panel.transform, Loc.T("LEAVE"), 20f);
            UiFactory.Place((RectTransform)_leaveButton.transform, new Vector2(0.5f, 0f), new Vector2(130f, 22f), new Vector2(220f, 52f));
            _leaveButton.onClick.AddListener(() =>
            {
                NetLauncher.Shutdown();
                UI.SceneFlow.LoadMenu();
            });
        }

        /// <summary>The match is paused for the players listed in <paramref name="info"/>.</summary>
        public void ShowWaiting(PauseInfo info)
        {
            _mode = Mode.Waiting;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            _title.text = Loc.T("GAME PAUSED");
            _subtitle.text = Loc.T("The game resumes when everyone is back.") +
                             (info.CanKick ? Loc.T("\nOr replace a missing player with a bot.") : "");
            _rejoinButton.gameObject.SetActive(false);
            _leaveButton.gameObject.SetActive(false);

            bool hasCode = !string.IsNullOrEmpty(info.JoinCode);
            _footer.gameObject.SetActive(hasCode);
            _copyButton.gameObject.SetActive(hasCode);
            if (hasCode)
                _footer.text = Loc.T("GAME ID:  ") + "<color=#E8C15A>" + info.JoinCode + "</color>";

            for (int i = _rows.childCount - 1; i >= 0; i--)
                Destroy(_rows.GetChild(i).gameObject);
            for (int i = 0; i < info.Waiting.Count; i++)
            {
                var seat = info.Waiting[i];
                var label = UiFactory.CreateText(_theme, "Waiting" + i, _rows,
                    Loc.French ? "En attente de  <b>" + seat.Name + "</b>…"
                               : "Waiting for  <b>" + seat.Name + "</b>…", 20f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
                UiFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(30f, -8f - i * 56f), new Vector2(360f, 44f));

                if (info.CanKick)
                {
                    int playerIndex = seat.PlayerIndex;
                    var kick = UiFactory.CreateButton(_theme, "Kick" + i, _rows, Loc.T("REPLACE WITH BOT"), 14f);
                    UiFactory.Place((RectTransform)kick.transform, new Vector2(1f, 1f), new Vector2(-10f, -8f - i * 56f), new Vector2(190f, 44f));
                    kick.onClick.AddListener(() => ReconnectService.KickToBot(playerIndex));
                }
            }
        }

        /// <summary>We lost the host connection (only rejoin or leave can follow).</summary>
        public void ShowConnectionLost(string reason)
        {
            _mode = Mode.ConnectionLost;
            _busy = false;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            _title.text = Loc.T("CONNECTION LOST");
            _subtitle.text = string.IsNullOrEmpty(reason)
                ? Loc.T("The connection to the host was lost.")
                : reason;
            _footer.gameObject.SetActive(false);
            _copyButton.gameObject.SetActive(false);
            for (int i = _rows.childCount - 1; i >= 0; i--)
                Destroy(_rows.GetChild(i).gameObject);

            bool canRejoin = !string.IsNullOrEmpty(NetLauncher.LastJoinCode);
            _rejoinButton.gameObject.SetActive(canRejoin);
            _rejoinButton.interactable = true;
            _rejoinLabel.text = Loc.T("REJOIN");
            _leaveButton.gameObject.SetActive(true);
        }

        /// <summary>Pause ended. Never hides the connection-lost mode (that's terminal).</summary>
        public void HideWaiting()
        {
            if (_mode != Mode.Waiting) return;
            _mode = Mode.Hidden;
            gameObject.SetActive(false);
        }

        private async void OnRejoinClicked()
        {
            if (_busy) return;
            _busy = true;
            _rejoinButton.interactable = false;
            _rejoinLabel.text = Loc.T("REJOINING…");
            try
            {
                // NGO shut down on disconnect; a successful rejoin re-syncs us into the
                // host's Game scene with a fresh session — this overlay dies with the scene.
                await NetLauncher.RejoinAsync();
                _subtitle.text = Loc.T("Reconnecting to the host…");
            }
            catch (UgsException e)
            {
                _subtitle.text = e.Message + "\nThe host may have ended the game.";
                _rejoinButton.interactable = true;
                _rejoinLabel.text = Loc.T("REJOIN");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                _subtitle.text = Loc.T("Rejoin failed — the host may have ended the game.");
                _rejoinButton.interactable = true;
                _rejoinLabel.text = Loc.T("REJOIN");
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
