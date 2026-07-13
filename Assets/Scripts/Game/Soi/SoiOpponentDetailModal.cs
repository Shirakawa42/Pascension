using System;
using System.Collections.Generic;
using Pascension.Engine.Serialization;
using Pascension.Game.UI;
using Pascension.Game.View;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Full opponent sheet for Shards of Infinity — the "bigger description card":
    /// character portrait, the same four stats as the local sheet, champion and destiny
    /// rows at readable sizes, and browse buttons for the public piles. Opened by
    /// clicking an opponent's compact top-strip panel. Mirrors Pascension's
    /// OpponentDetailModal pattern (dimmer + panel + BrowseRequested-style callback).
    /// </summary>
    public sealed class SoiOpponentDetailModal : MonoBehaviour
    {
        private UiTheme _theme;
        private RectTransform _root;
        private RectTransform _panel;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _stats;
        private TextMeshProUGUI _counts;
        private CardView _portrait;
        private RectTransform _championRow;
        private RectTransform _destinyRow;
        private TextMeshProUGUI _championLabel;
        private TextMeshProUGUI _destinyLabel;
        private Button _browseDiscard, _browsePlayed;

        private ShardsPlayerSnap _shown;
        private Action<string, List<CardSnap>> _browse;

        public bool Visible => _root != null && _root.gameObject.activeSelf;
        /// <summary>Seat currently displayed (-1 when hidden) — lets the host screen
        /// re-bind the sheet on every snapshot so it never goes stale while open.</summary>
        public int ShownIndex { get; private set; } = -1;

        public static SoiOpponentDetailModal Create(Transform parent, UiTheme theme)
        {
            var rect = UiFactory.CreateRect("SoiOpponentDetail", parent);
            UiFactory.Stretch(rect);
            var modal = rect.gameObject.AddComponent<SoiOpponentDetailModal>();
            modal.Build(theme, rect);
            return modal;
        }

        private void Build(UiTheme theme, RectTransform rect)
        {
            _theme = theme;
            _root = rect;

            var dimmer = UiFactory.CreateDimmer("Dimmer", rect);
            var closeCatcher = dimmer.gameObject.AddComponent<Button>();
            closeCatcher.targetGraphic = dimmer;
            closeCatcher.transition = Selectable.Transition.None;
            closeCatcher.onClick.AddListener(Hide);

            var panelImg = UiFactory.CreatePanel(theme, "Panel", rect, UiPalette.Panel);
            _panel = (RectTransform)panelImg.transform;
            UiFactory.Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(940f, 620f));

            // Portrait (top-left), name + stats to its right.
            _portrait = CardViewFactory.Create(_panel, theme, 0.72f);
            _portrait.Rect.anchorMin = _portrait.Rect.anchorMax = _portrait.Rect.pivot = new Vector2(0f, 1f);
            _portrait.Rect.anchoredPosition = new Vector2(28f, -28f);
            _portrait.SetRaycastable(false);
            if (_portrait.Group != null) _portrait.Group.blocksRaycasts = false;

            _name = UiFactory.CreateText(theme, "Name", _panel, "", 28f, UiPalette.Gold,
                TextAlignmentOptions.TopLeft, FontStyles.Bold);
            UiFactory.Place(_name.rectTransform, new Vector2(0f, 1f), new Vector2(214f, -34f), new Vector2(560f, 36f));

            _stats = UiFactory.CreateText(theme, "Stats", _panel, "", 22f, UiPalette.TextMain,
                TextAlignmentOptions.TopLeft);
            if (theme.Icons != null) _stats.spriteAsset = theme.Icons;
            UiFactory.Place(_stats.rectTransform, new Vector2(0f, 1f), new Vector2(214f, -84f), new Vector2(640f, 34f));

            _counts = UiFactory.CreateText(theme, "Counts", _panel, "", 17f, UiPalette.TextDim,
                TextAlignmentOptions.TopLeft);
            UiFactory.Place(_counts.rectTransform, new Vector2(0f, 1f), new Vector2(214f, -126f), new Vector2(640f, 26f));

            _browseDiscard = UiFactory.CreateButton(theme, "BrowseDiscard", _panel, Loc.T("DISCARD"), 15f);
            UiFactory.Place((RectTransform)_browseDiscard.transform, new Vector2(0f, 1f), new Vector2(214f, -170f), new Vector2(160f, 42f));
            _browseDiscard.onClick.AddListener(() =>
            {
                if (_shown != null) _browse?.Invoke(_shown.Name + Loc.T(" — discard"), Snaps(_shown.Discard));
            });

            _browsePlayed = UiFactory.CreateButton(theme, "BrowsePlayed", _panel, Loc.T("PLAYED THIS TURN"), 15f);
            UiFactory.Place((RectTransform)_browsePlayed.transform, new Vector2(0f, 1f), new Vector2(388f, -170f), new Vector2(210f, 42f));
            _browsePlayed.onClick.AddListener(() =>
            {
                if (_shown != null) _browse?.Invoke(_shown.Name + Loc.T(" — played this turn"), Snaps(_shown.PlayZone));
            });

            _championLabel = UiFactory.CreateText(theme, "ChampLabel", _panel, Loc.T("CHAMPIONS"), 13f,
                UiPalette.TextDim, TextAlignmentOptions.Left, FontStyles.Bold);
            UiFactory.Place(_championLabel.rectTransform, new Vector2(0f, 1f), new Vector2(32f, -318f), new Vector2(400f, 18f));
            _championRow = UiFactory.CreateRect("Champions", _panel);
            UiFactory.Place(_championRow, new Vector2(0f, 1f), new Vector2(28f, -338f), new Vector2(884f, 140f));

            _destinyLabel = UiFactory.CreateText(theme, "DestinyLabel", _panel, Loc.T("DESTINIES"), 13f,
                UiPalette.TextDim, TextAlignmentOptions.Left, FontStyles.Bold);
            UiFactory.Place(_destinyLabel.rectTransform, new Vector2(0f, 1f), new Vector2(32f, -486f), new Vector2(400f, 18f));
            _destinyRow = UiFactory.CreateRect("Destinies", _panel);
            UiFactory.Place(_destinyRow, new Vector2(0f, 1f), new Vector2(28f, -506f), new Vector2(884f, 105f));

            var close = UiFactory.CreateButton(theme, "Close", _panel, Loc.T("CLOSE"), 16f);
            UiFactory.Place((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(120f, 42f));
            close.onClick.AddListener(Hide);

            rect.gameObject.SetActive(false);
        }

        public void Show(ShardsPlayerSnap player, int maxHealth, Action<string, List<CardSnap>> browse)
        {
            _shown = player;
            _browse = browse;
            ShownIndex = player.Index;

            _portrait.BindDef(SoiCardFaces.CharacterPrefix + player.CharacterId);
            _portrait.SetTapped(player.CharacterExhausted);

            _name.text = player.Name + (player.Eliminated ? Loc.T("  · eliminated") : "");
            _stats.text =
                $"<color=#6FDF8F>{player.Health}/{maxHealth}</color><sprite name=\"soi_health\">   " +
                $"<color=#D4AF37>{player.Mastery}/30</color><sprite name=\"soi_mastery\">   " +
                $"<color=#73AEF2>{player.Gems}</color><sprite name=\"soi_gem\">   " +
                $"<color=#E06C55>{player.Power}</color><sprite name=\"soi_power\">";
            _counts.text = $"{Loc.T("hand")} {player.HandCount} · {Loc.T("deck")} {player.DeckCount} · " +
                           $"{Loc.T("discard")} {player.Discard.Count} · {Loc.T("played")} {player.PlayZone.Count}" +
                           (player.RelicRecruited ? Loc.T(" · relic recruited") : "");

            foreach (Transform child in _championRow) Destroy(child.gameObject);
            foreach (Transform child in _destinyRow) Destroy(child.gameObject);

            FillRow(_championRow, player.Champions, 0.44f, 106f, showState: true);
            FillRow(_destinyRow, player.Destinies, 0.33f, 80f, showState: true);
            _championLabel.text = Loc.T(player.Champions.Count == 0 ? "CHAMPIONS — none" : "CHAMPIONS");
            _destinyLabel.text = Loc.T(player.Destinies.Count == 0 ? "DESTINIES — none" : "DESTINIES");

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
            _shown = null;
            ShownIndex = -1;
        }

        private void FillRow(RectTransform row, List<ShardsCardSnap> cards, float scale, float step, bool showState)
        {
            if (cards == null) return;
            float width = row.sizeDelta.x;
            float cardWidth = 220f * scale;
            float actualStep = cards.Count > 1 && (cards.Count - 1) * step + cardWidth > width
                ? (width - cardWidth) / (cards.Count - 1) : step;
            float x = 0f;
            foreach (var card in cards)
            {
                var view = CardViewFactory.Create(row, _theme, scale);
                view.RotateWhenTapped = false;
                view.Rect.anchorMin = view.Rect.anchorMax = new Vector2(0f, 0.5f);
                view.Rect.pivot = new Vector2(0f, 0.5f);
                view.Rect.anchoredPosition = new Vector2(x, 0f);
                x += actualStep;
                view.BindDef(card.DefId, card.InstanceId);
                if (showState)
                {
                    view.SetTapped(card.Exhausted);
                    view.SetMarkedDamage(card.DamageThisTurn);
                }
            }
        }

        private static List<CardSnap> Snaps(List<ShardsCardSnap> zone)
        {
            var result = new List<CardSnap>();
            if (zone == null) return result;
            foreach (var card in zone)
                result.Add(new CardSnap { DefId = card.DefId, InstanceId = card.InstanceId, EffectiveCost = -1 });
            return result;
        }
    }
}
