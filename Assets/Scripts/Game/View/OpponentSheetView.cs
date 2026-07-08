using Pascension.Engine.Serialization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// Compact opponent strip: portrait, name, level, board position, hand count,
    /// AP/damage, equipment icons and relic count. One instance per opponent, created
    /// at runtime by the GameScreen.
    /// </summary>
    public sealed class OpponentSheetView : MonoBehaviour
    {
        public UiTheme Theme;

        private Image _panel;
        private Image _portrait;
        private TextMeshProUGUI _initial;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _statusLine;
        private TextMeshProUGUI _apDmg;
        private readonly Image[] _equipIcons = new Image[3];
        private TextMeshProUGUI _relics;
        private Outline _turnOutline;

        public static OpponentSheetView Create(Transform parent, UiTheme theme, int playerIndex)
        {
            var root = UiFactory.CreateRect($"Opponent{playerIndex}", parent);
            root.sizeDelta = new Vector2(316f, 84f);
            var view = root.gameObject.AddComponent<OpponentSheetView>();
            view.Theme = theme;

            view._panel = UiFactory.CreatePanel(theme, "Panel", root, UiPalette.WithAlpha(UiPalette.Panel, 0.95f));
            UiFactory.Stretch(view._panel.rectTransform);
            view._turnOutline = view._panel.gameObject.AddComponent<Outline>();
            view._turnOutline.effectColor = UiPalette.Gold;
            view._turnOutline.effectDistance = new Vector2(2f, -2f);
            view._turnOutline.enabled = false;

            var portraitFrame = UiFactory.CreateImage("PortraitFrame", view._panel.transform, theme.Rounded,
                UiPalette.PlayerColor(playerIndex));
            UiFactory.Place(portraitFrame.rectTransform, new Vector2(0f, 0.5f), new Vector2(6f, 0f), new Vector2(60f, 60f));
            view._portrait = UiFactory.CreateImage("Portrait", portraitFrame.transform, null, UiPalette.PanelLight);
            UiFactory.Stretch(view._portrait.rectTransform, 3, 3, 3, 3);
            view._initial = UiFactory.CreateText(theme, "Initial", portraitFrame.transform, "?", 26f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(view._initial.rectTransform);

            view._name = UiFactory.CreateText(theme, "Name", view._panel.transform, "Opponent", 17f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UiFactory.Place(view._name.rectTransform, new Vector2(0f, 1f), new Vector2(74f, -6f), new Vector2(160f, 22f));

            view._statusLine = UiFactory.CreateText(theme, "Status", view._panel.transform, "", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            UiFactory.Place(view._statusLine.rectTransform, new Vector2(0f, 1f), new Vector2(74f, -28f), new Vector2(180f, 18f));

            view._apDmg = UiFactory.CreateText(theme, "ApDmg", view._panel.transform, "", 15f,
                UiPalette.Gold, TextAlignmentOptions.MidlineRight, FontStyles.Bold);
            UiFactory.Place(view._apDmg.rectTransform, new Vector2(1f, 1f), new Vector2(-10f, -6f), new Vector2(110f, 22f));

            for (int i = 0; i < 3; i++)
            {
                var icon = UiFactory.CreateImage($"Equip{i}", view._panel.transform, theme.Rounded,
                    UiPalette.WithAlpha(UiPalette.Background, 0.9f));
                UiFactory.Place(icon.rectTransform, new Vector2(0f, 0f), new Vector2(74f + i * 26f, 6f), new Vector2(22f, 22f));
                view._equipIcons[i] = icon;
            }

            view._relics = UiFactory.CreateText(theme, "Relics", view._panel.transform, "", 13f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineRight);
            UiFactory.Place(view._relics.rectTransform, new Vector2(1f, 0f), new Vector2(-10f, 6f), new Vector2(120f, 20f));

            return view;
        }

        public void Render(PlayerSnap p, bool isTheirTurn)
        {
            if (p == null) return;

            _name.text = p.Name + (p.Conceded ? " (out)" : "");
            _statusLine.text = $"Lv {p.Level} · Step {p.Position} · Hand {p.HandCount}";
            _apDmg.text = $"{p.Ap} AP · {p.DamagePool} DMG";
            _turnOutline.enabled = isTheirTurn;

            var portrait = Theme.Art(p.HeroId);
            if (portrait != null)
            {
                _portrait.sprite = portrait;
                _portrait.color = Color.white;
                _initial.text = "";
            }
            else
            {
                _portrait.sprite = null;
                _portrait.color = UiPalette.PanelLight;
                _initial.text = !string.IsNullOrEmpty(p.HeroId) ? p.HeroId.Substring(0, 1).ToUpperInvariant() : "?";
            }

            for (int i = 0; i < 3; i++)
            {
                var snap = p.Equipment != null && i < p.Equipment.Length ? p.Equipment[i] : null;
                var icon = _equipIcons[i];
                if (snap == null)
                {
                    icon.sprite = Theme.Rounded;
                    icon.color = UiPalette.WithAlpha(UiPalette.Background, 0.9f);
                    continue;
                }
                var art = Theme.Art(snap.DefId);
                if (art != null)
                {
                    icon.sprite = art;
                    icon.type = Image.Type.Simple;
                    icon.color = snap.Tapped ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
                }
                else
                {
                    icon.sprite = Theme.Rounded;
                    icon.color = snap.Tapped ? UiPalette.Border : UiPalette.TierColor(GetTier(snap.DefId));
                }
            }

            _relics.text = p.Relics.Count > 0 ? $"Relics ×{p.Relics.Count}" : "";

            var group = UiFactory.AddGroup(gameObject);
            group.alpha = p.Conceded ? 0.4f : 1f;
        }

        private static Engine.Core.CardTier GetTier(string defId)
        {
            if (!string.IsNullOrEmpty(defId) && Engine.Cards.CardDatabase.TryGet(defId, out var def))
                return def.Tier;
            return Engine.Core.CardTier.Default;
        }
    }
}
