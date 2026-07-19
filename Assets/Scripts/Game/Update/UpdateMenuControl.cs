using System;
using System.Threading;
using Pascension.Core;
using Pascension.Game.UI;
using Pascension.Game.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Update
{
    /// <summary>
    /// Persistent corner widgets on the main menu, Root-parented like the language
    /// toggle so MainMenu.ShowPanel never hides them: a version label bottom-left and
    /// a self-update button bottom-right (stacked above the language toggle). The
    /// button relabels itself through the whole pipeline (DOWNLOADING 42% → VERIFYING…
    /// → INSTALLING… → RESTARTING…); errors show on a small status line and clicking
    /// retries. When self-install isn't possible the button opens the releases page.
    /// </summary>
    public sealed class UpdateMenuControl : MonoBehaviour
    {
        private UiTheme _theme;
        private Transform _parent;
        private Button _button;               // created lazily, only when an update exists
        private TextMeshProUGUI _buttonLabel;
        private TextMeshProUGUI _status;      // "" = hidden
        private bool _manualMode;             // true → button opens the releases page
        private bool _busy;
        private CancellationTokenSource _cts;
        private UpdateInstaller _installer;

        public void Init(UiTheme theme, Transform parent)
        {
            _theme = theme;
            _parent = parent;

            UpdateInstaller.CleanupStale();

            var version = UiFactory.CreateText(_theme, "VersionLabel", _parent,
                "v" + Application.version, 13f, UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(version.rectTransform, new Vector2(0f, 0f), new Vector2(16f, 12f), new Vector2(300f, 20f));

            if (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable)
                CreateButton();
            else if (UpdateChecker.Current == UpdateChecker.State.Unchecked)
                RunCheck();
        }

        private async void RunCheck()
        {
            await UpdateChecker.CheckAsync();
            if (this == null) return; // scene changed during the fetch
            if (UpdateChecker.Current == UpdateChecker.State.UpdateAvailable)
                CreateButton();
        }

        private void CreateButton()
        {
            _manualMode = UpdateChecker.PackageForThisPlatform(UpdateChecker.Manifest) == null
                          || !UpdateInstaller.CanSelfInstall(out _);

            _button = UiFactory.CreateButton(_theme, "UpdateButton", _parent,
                AvailableLabel(), 15f, UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_button.transform,
                new Vector2(1f, 0f), new Vector2(-24f, 80f), new Vector2(320f, 48f));
            _buttonLabel = UiFactory.ButtonLabel(_button);
            _button.onClick.AddListener(OnUpdateClicked);

            _status = UiFactory.CreateText(_theme, "UpdateStatus", _parent, "", 13f,
                UiPalette.Danger, TextAlignmentOptions.MidlineRight);
            UiFactory.Place(_status.rectTransform, new Vector2(1f, 0f), new Vector2(-24f, 134f), new Vector2(320f, 34f));
        }

        private string AvailableLabel() => _manualMode
            ? Loc.T("OPEN DOWNLOAD PAGE")
            : Loc.T("UPDATE AVAILABLE") + " (v" + UpdateChecker.Manifest.Version + ")";

        private async void OnUpdateClicked()
        {
            if (_busy) return;
            if (_manualMode)
            {
                Application.OpenURL(UpdateChecker.ReleasesPageUrl);
                return;
            }

            _busy = true;
            _button.interactable = false;
            _status.text = "";
            _cts = new CancellationTokenSource();
            _installer = new UpdateInstaller();
            try
            {
                // Outside the editor this only returns on failure (success = quit).
                await _installer.RunAsync(_cts.Token);
                if (this == null) return;
                _buttonLabel.text = AvailableLabel(); // editor dry-run completed
            }
            catch (OperationCanceledException)
            {
                // scene reload / quit mid-download; partial file already cleaned
            }
            catch (UpdateFailedException e)
            {
                Debug.LogWarning("[Update] failed: " + e.Message + " — " + e.InnerException?.Message);
                if (this == null) return;
                _status.text = Loc.T(e.Message);
                // The platform vanished from a newer manifest, or the install turned
                // out to be un-swappable — degrade to the manual-download button.
                if (e.Message.StartsWith("Automatic update unavailable"))
                {
                    _manualMode = true;
                    Application.OpenURL(UpdateChecker.ReleasesPageUrl);
                }
                _buttonLabel.text = AvailableLabel(); // click = retry (or opens the page)
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this == null) return;
                _status.text = Loc.T("Update failed — see the log.");
                _buttonLabel.text = AvailableLabel();
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    _installer = null;
                    if (_button != null) _button.interactable = true;
                }
            }
        }

        private void Update()
        {
            if (!_busy || _installer == null || _buttonLabel == null) return;
            switch (_installer.CurrentPhase)
            {
                case UpdateInstaller.Phase.Downloading:
                    float p = _installer.DownloadProgress;
                    _buttonLabel.text = p >= 0f
                        ? Loc.T("DOWNLOADING") + " " + Mathf.RoundToInt(Mathf.Clamp01(p) * 100f) + "%"
                        : Loc.T("DOWNLOADING") + "…";
                    break;
                case UpdateInstaller.Phase.Verifying:
                    _buttonLabel.text = Loc.T("VERIFYING…");
                    break;
                case UpdateInstaller.Phase.Extracting:
                    _buttonLabel.text = Loc.T("INSTALLING…");
                    break;
                case UpdateInstaller.Phase.Launching:
                    _buttonLabel.text = Loc.T("RESTARTING…");
                    break;
            }
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
        }
    }
}
