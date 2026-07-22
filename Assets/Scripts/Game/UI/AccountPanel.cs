using System;
using Pascension.Game.View;
using Pascension.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.UI
{
    /// <summary>
    /// The account screen: log in / create account / switch between the accounts known
    /// on this device / play as guest, plus the signed-in view with log out. The root
    /// rect is a full-screen panel MainMenu registers in ShowPanel; content re-renders
    /// from AccountService.State on every Changed. Zero rules logic; every failure is
    /// a user-readable UgsException shown on the status line.
    /// </summary>
    public sealed class AccountPanel : MonoBehaviour
    {
        private UiTheme _theme;
        private Action _onClose;
        private RectTransform _content;
        private TMP_InputField _usernameInput;
        private TMP_InputField _passwordInput;
        private TextMeshProUGUI _statusText;
        private string _status = "";
        private bool _statusIsError;
        private string _prefillUsername;
        private bool _busy;
        private bool _addingAccount;

        /// <summary>The full-screen rect MainMenu shows/hides via ShowPanel.</summary>
        public RectTransform Root { get; private set; }

        public static AccountPanel Create(Transform parent, UiTheme theme, Action onClose)
        {
            var root = UiFactory.CreateRect("AccountPanel", parent);
            UiFactory.Stretch(root);
            var panel = root.gameObject.AddComponent<AccountPanel>();
            panel.Root = root;
            panel._theme = theme;
            panel._onClose = onClose;

            var frame = UiFactory.CreatePanel(theme, "Panel", root);
            UiFactory.Place(frame.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(680f, 660f));
            panel._content = UiFactory.CreateRect("Content", frame.transform);
            UiFactory.Stretch(panel._content);

            AccountService.Changed += panel.OnAccountChanged;
            panel.Render();
            return panel;
        }

        /// <summary>Show "an account is required" on the status line (MainMenu calls
        /// this right before opening the panel from the MULTIPLAYER button).</summary>
        public void ShowOnlineNotice()
        {
            _status = Loc.T("An account is required to play online.");
            _statusIsError = true;
            ApplyStatus();
        }

        private void OnEnable()
        {
            if (_content != null)
                Render();
        }

        private void OnDisable()
        {
            // Each open starts clean (ShowOnlineNotice arrives while hidden, AFTER this).
            _status = "";
            _statusIsError = false;
            _prefillUsername = null;
            _addingAccount = false;
        }

        private void OnDestroy()
        {
            AccountService.Changed -= OnAccountChanged;
        }

        private void OnAccountChanged()
        {
            _addingAccount = false;
            Render();
        }

        // ------------------------------------------------------------------ rendering

        private void Render()
        {
            string notice = AccountService.PendingNotice; // one-shot (boot session expiry)
            if (!string.IsNullOrEmpty(notice))
            {
                _status = Loc.T(notice);
                _statusIsError = true;
            }

            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);
            _usernameInput = null;
            _passwordInput = null;
            _statusText = null;

            var state = AccountService.State;
            bool signedIn = state == AccountState.SignedIn || state == AccountState.SignedInOffline;
            if (signedIn && !_addingAccount)
                RenderSignedIn(state);
            else
                RenderSignedOut(state);
            ApplyStatus();
        }

        private void RenderSignedOut(AccountState state)
        {
            bool busy = _busy || state == AccountState.SigningIn;
            if (busy && string.IsNullOrEmpty(_status))
            {
                // Boot restore in flight (not one of our own handlers).
                _status = Loc.T("Signing in…");
                _statusIsError = false;
            }

            var title = UiFactory.CreateText(_theme, "Title", _content, Loc.T("ACCOUNT"), 36f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 4f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(400f, 44f));

            var userLabel = UiFactory.CreateText(_theme, "UsernameLabel", _content, Loc.T("USERNAME"), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(userLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-90f, -80f), new Vector2(260f, 20f));

            _usernameInput = UiFactory.CreateInputField(_theme, "UsernameInput", _content, "", 18f);
            UiFactory.Place((RectTransform)_usernameInput.transform, new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(440f, 42f));
            _usernameInput.characterLimit = 20;
            if (!string.IsNullOrEmpty(_prefillUsername))
                _usernameInput.text = _prefillUsername;

            var hint = UiFactory.CreateText(_theme, "UsernameHint", _content,
                Loc.T("3-20 characters: letters, digits, . - @ _"), 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(hint.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(440f, 18f));

            var passLabel = UiFactory.CreateText(_theme, "PasswordLabel", _content, Loc.T("PASSWORD"), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(passLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(-90f, -180f), new Vector2(260f, 20f));

            _passwordInput = UiFactory.CreateInputField(_theme, "PasswordInput", _content, "", 18f);
            UiFactory.Place((RectTransform)_passwordInput.transform, new Vector2(0.5f, 1f), new Vector2(0f, -204f), new Vector2(440f, 42f));
            _passwordInput.characterLimit = 64;
            _passwordInput.contentType = TMP_InputField.ContentType.Password;

            var login = UiFactory.CreateButton(_theme, "LogInButton", _content, Loc.T("LOG IN"), 18f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)login.transform, new Vector2(0.5f, 1f), new Vector2(-115f, -266f), new Vector2(210f, 50f));
            login.interactable = !busy;
            login.onClick.AddListener(() => OnSubmit(signUp: false));

            var create = UiFactory.CreateButton(_theme, "CreateAccountButton", _content, Loc.T("CREATE ACCOUNT"), 16f);
            UiFactory.Place((RectTransform)create.transform, new Vector2(0.5f, 1f), new Vector2(115f, -266f), new Vector2(210f, 50f));
            create.interactable = !busy;
            create.onClick.AddListener(() => OnSubmit(signUp: true));

            _statusText = UiFactory.CreateText(_theme, "Status", _content, "", 15f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(_statusText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -324f), new Vector2(620f, 40f));

            var known = AccountService.KnownAccounts;
            if (known.Count > 0)
            {
                var listLabel = UiFactory.CreateText(_theme, "KnownLabel", _content,
                    Loc.T("ACCOUNTS ON THIS DEVICE"), 14f,
                    UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(listLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -372f), new Vector2(440f, 20f));

                float y = -398f;
                for (int i = 0; i < known.Count && i < 3; i++)
                {
                    string account = known[i];
                    var button = UiFactory.CreateButton(_theme, "Account_" + i, _content, account, 16f);
                    UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(440f, 38f));
                    button.interactable = !busy;
                    button.onClick.AddListener(() => OnSwitch(account));
                    y -= 44f;
                }
            }

            if (_addingAccount)
            {
                // A signed-in player adding an account: BACK cancels back to the
                // signed-in view; the guest row makes no sense here.
                var back = UiFactory.CreateButton(_theme, "Back", _content, Loc.T("BACK"), 18f);
                UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(160f, 44f));
                back.onClick.AddListener(() =>
                {
                    _addingAccount = false;
                    Render();
                });
                return;
            }

            var guest = UiFactory.CreateButton(_theme, "GuestButton", _content, Loc.T("PLAY AS GUEST"), 17f,
                UiPalette.PanelLight);
            UiFactory.Place((RectTransform)guest.transform, new Vector2(0.5f, 0f), new Vector2(0f, 104f), new Vector2(280f, 46f));
            guest.interactable = !busy;
            guest.onClick.AddListener(() =>
            {
                AccountService.ChooseGuest();
                _onClose?.Invoke();
            });

            var caption = UiFactory.CreateText(_theme, "GuestCaption", _content,
                Loc.T("Playing as guest — multiplayer is disabled."), 13f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Italic);
            UiFactory.Place(caption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 78f), new Vector2(560f, 18f));

            if (!AccountService.FirstRunChoicePending)
            {
                var back = UiFactory.CreateButton(_theme, "Back", _content, Loc.T("BACK"), 18f);
                UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(160f, 44f));
                back.onClick.AddListener(() => _onClose?.Invoke());
            }
        }

        private void RenderSignedIn(AccountState state)
        {
            var title = UiFactory.CreateText(_theme, "Title", _content, Loc.T("ACCOUNT"), 36f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 4f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(400f, 44f));

            var asLabel = UiFactory.CreateText(_theme, "SignedInAsLabel", _content, Loc.T("SIGNED IN AS"), 15f,
                UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(asLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(440f, 20f));

            string display = AccountService.CurrentUsername +
                (state == AccountState.SignedInOffline ? " · " + Loc.T("offline") : "");
            var name = UiFactory.CreateText(_theme, "Username", _content, display, 30f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -130f), new Vector2(600f, 40f));

            var logout = UiFactory.CreateButton(_theme, "LogOutButton", _content, Loc.T("LOG OUT"), 18f,
                UiPalette.Danger);
            UiFactory.Place((RectTransform)logout.transform, new Vector2(0.5f, 1f), new Vector2(0f, -200f), new Vector2(220f, 50f));
            logout.interactable = !_busy;
            logout.onClick.AddListener(AccountService.Logout);

            float y = -290f;
            var known = AccountService.KnownAccounts;
            bool anyOther = false;
            for (int i = 0; i < known.Count; i++)
                if (!string.Equals(known[i], AccountService.CurrentUsername, StringComparison.OrdinalIgnoreCase))
                    anyOther = true;
            if (anyOther)
            {
                var listLabel = UiFactory.CreateText(_theme, "KnownLabel", _content,
                    Loc.T("ACCOUNTS ON THIS DEVICE"), 14f,
                    UiPalette.TextDim, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Place(listLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(440f, 20f));
                y -= 28f;

                int shown = 0;
                for (int i = 0; i < known.Count && shown < 3; i++)
                {
                    string account = known[i];
                    if (string.Equals(account, AccountService.CurrentUsername, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var button = UiFactory.CreateButton(_theme, "Account_" + i, _content, account, 16f);
                    UiFactory.Place((RectTransform)button.transform, new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(440f, 38f));
                    button.interactable = !_busy;
                    button.onClick.AddListener(() => OnSwitch(account));
                    y -= 44f;
                    shown++;
                }
            }

            var add = UiFactory.CreateButton(_theme, "AddAccountButton", _content, Loc.T("ADD ACCOUNT"), 16f);
            UiFactory.Place((RectTransform)add.transform, new Vector2(0.5f, 1f), new Vector2(0f, y - 8f), new Vector2(260f, 44f));
            add.interactable = !_busy;
            add.onClick.AddListener(() =>
            {
                _addingAccount = true;
                Render();
            });

            _statusText = UiFactory.CreateText(_theme, "Status", _content, "", 15f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(_statusText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 84f), new Vector2(620f, 36f));

            var back = UiFactory.CreateButton(_theme, "Back", _content, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(160f, 44f));
            back.onClick.AddListener(() => _onClose?.Invoke());
        }

        private void ApplyStatus()
        {
            if (_statusText == null) return;
            _statusText.text = _status;
            _statusText.color = _statusIsError ? UiPalette.Danger : UiPalette.TextDim;
        }

        // ------------------------------------------------------------------ handlers

        private async void OnSubmit(bool signUp)
        {
            if (_busy) return;
            string username = _usernameInput != null ? _usernameInput.text : "";
            string password = _passwordInput != null ? _passwordInput.text : "";
            _prefillUsername = username;
            _busy = true;
            _status = Loc.T("Signing in…");
            _statusIsError = false;
            Render();
            try
            {
                if (signUp)
                    await AccountService.CreateAccountAsync(username, password);
                else
                    await AccountService.LoginAsync(username, password);
                _status = "";
                _prefillUsername = null;
            }
            catch (UgsException e)
            {
                _status = Loc.T(e.Message);
                _statusIsError = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = Loc.T("Unexpected error — see the log.");
                _statusIsError = true;
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    Render();
                }
            }
        }

        private async void OnSwitch(string username)
        {
            if (_busy) return;
            _busy = true;
            _status = Loc.T("Signing in…");
            _statusIsError = false;
            Render();
            try
            {
                await AccountService.SwitchToAsync(username);
                _status = "";
            }
            catch (UgsException e)
            {
                _status = Loc.T(e.Message);
                _statusIsError = true;
                if (e.Message == "Session expired — enter the password for this account.")
                    _prefillUsername = username; // the form re-renders — hand them the name
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = Loc.T("Unexpected error — see the log.");
                _statusIsError = true;
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    Render();
                }
            }
        }
    }
}
