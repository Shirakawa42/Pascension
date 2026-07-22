using System;
using Pascension.Game.View;
using Pascension.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.UI
{
    /// <summary>
    /// Persistent top-right corner widget on the main menu (Root-parented like the
    /// language toggle, so ShowPanel never hides it): shows the account state and opens
    /// the account panel on click. Init is also the app's single AccountService boot
    /// trigger (fire-and-forget, mirroring UpdateMenuControl.RunCheck).
    /// </summary>
    public sealed class AccountMenuControl : MonoBehaviour
    {
        private Button _button;
        private TextMeshProUGUI _label;
        private Action _openPanel;

        public void Init(UiTheme theme, Transform parent, Action openPanel)
        {
            _openPanel = openPanel;

            _button = UiFactory.CreateButton(theme, "AccountButton", parent, "", 15f);
            UiFactory.Place((RectTransform)_button.transform, new Vector2(1f, 1f),
                new Vector2(-24f, -24f), new Vector2(300f, 44f));
            _label = UiFactory.ButtonLabel(_button);
            _button.onClick.AddListener(() => _openPanel?.Invoke());

            AccountService.Changed += Refresh;
            Refresh();
            RunBoot();
        }

        private async void RunBoot()
        {
            try
            {
                await AccountService.BootAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e); // BootAsync should not throw — belt and braces
            }
        }

        private void OnDestroy()
        {
            AccountService.Changed -= Refresh;
        }

        private void Refresh()
        {
            if (_button == null || _label == null) return;
            bool interactable = true;
            string label;
            Color fill, text;
            switch (AccountService.State)
            {
                case AccountState.SignedIn:
                    label = AccountService.CurrentUsername;
                    fill = UiPalette.Gold;
                    text = UiPalette.Background;
                    break;
                case AccountState.SignedInOffline:
                    label = AccountService.CurrentUsername + " · " + Loc.T("offline");
                    fill = UiPalette.PanelLight;
                    text = UiPalette.TextMain;
                    break;
                case AccountState.SigningIn:
                    label = Loc.T("SIGNING IN…");
                    fill = UiPalette.PanelLight;
                    text = UiPalette.TextDim;
                    interactable = false;
                    break;
                case AccountState.Guest:
                    label = Loc.T("GUEST");
                    fill = UiPalette.PanelLight;
                    text = UiPalette.TextDim;
                    break;
                default:
                    label = Loc.T("SIGN IN");
                    fill = UiPalette.Gold;
                    text = UiPalette.Background;
                    break;
            }
            _label.text = label;
            _label.color = text;
            if (_button.image != null)
                _button.image.color = fill;
            _button.interactable = interactable;
        }
    }
}
