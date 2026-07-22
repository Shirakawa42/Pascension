using System;
using System.Collections.Generic;
using System.Globalization;
using Pascension.Engine.Serialization;
using Pascension.Game.Stats;
using Pascension.Game.UI;
using Pascension.Game.View;
using Shards.Content;
using Shards.Engine;
using Shards.Stats;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// Main-menu STATS screen for Shards of Infinity: mode/opponent filter row, six
    /// sub-tabs rendered into one scroll view, everything rebuilt from cached
    /// SoiStatsAggregator results. Rendering only — every number comes out of
    /// Shards.Stats; this class never touches a record beyond handing the list to the
    /// aggregator.
    /// </summary>
    public sealed class SoiStatsScreen : MonoBehaviour
    {
        /// <summary>The full-screen rect MainMenu shows/hides via ShowPanel.</summary>
        public RectTransform Root { get; private set; }

        private static readonly string[] TabNames =
            { "OVERVIEW", "HEROES", "CARDS", "SYNERGIES", "OPPONENTS", "HISTORY" };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private UiTheme _theme;
        private Action _onBack;
        private Action _onPlayNow;

        private RectTransform _panel;
        private TextMeshProUGUI _accountLine;
        private TextMeshProUGUI _warningLine;
        private Toggle _toggleAi, _toggleMp2, _toggleMp3;
        private Button _opponentButton;
        private TextMeshProUGUI _matchingText;
        private readonly List<Button> _tabButtons = new List<Button>();
        private RectTransform _content;
        private CardListModal _cardList;
        private SoiStatsOpponentPicker _picker;

        private bool _incAi = true, _incMp2 = true, _incMp3 = true;
        private string _opponentKey;
        private int _tab; // index into TabNames
        private bool _playedExpanded, _boughtExpanded;
        private bool _subscribed;

        private readonly Dictionary<string, SoiStatsAggregates> _aggCache = new Dictionary<string, SoiStatsAggregates>();
        private readonly Dictionary<string, List<PairAgg>> _pairCache = new Dictionary<string, List<PairAgg>>();

        public static SoiStatsScreen Create(Transform parent, UiTheme theme, Action onBack, Action onPlayNow)
        {
            var root = UiFactory.CreateRect("StatsPanel", parent);
            UiFactory.Stretch(root);
            var screen = root.gameObject.AddComponent<SoiStatsScreen>();
            screen.Root = root;
            screen._theme = theme;
            screen._onBack = onBack;
            screen._onPlayNow = onPlayNow;
            screen.Build();
            root.gameObject.SetActive(false); // MainMenu.ShowPanel activates it
            return screen;
        }

        /// <summary>Called by MainMenu right before showing the panel: refreshes the
        /// account line, consumes a pending load warning and re-renders.</summary>
        public void Open()
        {
            SoiCardFaces.Install();
            if (!_subscribed)
            {
                SoiStatsService.RecordsChanged += OnRecordsChanged;
                _subscribed = true;
            }
            string user = Pascension.Net.AccountService.CurrentUsername;
            _accountLine.text = user ?? Loc.T("Playing as guest — stats are stored on this device only.");
            if (SoiStatsService.LastLoadWarning != null)
            {
                _warningLine.text = Loc.T(SoiStatsService.LastLoadWarning);
                SoiStatsService.LastLoadWarning = null;
            }
            InvalidateCaches();
            RefreshAll();
        }

        private void OnDestroy()
        {
            if (_subscribed)
                SoiStatsService.RecordsChanged -= OnRecordsChanged;
        }

        private void OnRecordsChanged()
        {
            InvalidateCaches();
            if (Root != null && Root.gameObject.activeInHierarchy)
                RefreshAll();
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            var panelImg = UiFactory.CreatePanel(_theme, "Panel", Root);
            _panel = panelImg.rectTransform;
            UiFactory.Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1760f, 980f));

            var title = UiFactory.CreateText(_theme, "Title", _panel, Loc.T("STATISTICS"), 40f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            title.characterSpacing = 6f;
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(700f, 48f));

            _accountLine = UiFactory.CreateText(_theme, "AccountLine", _panel, "", 14f,
                UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(_accountLine.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -58f), new Vector2(1000f, 20f));

            _warningLine = UiFactory.CreateText(_theme, "Warning", _panel, "", 13f,
                UiPalette.Danger, TextAlignmentOptions.MidlineRight);
            UiFactory.Place(_warningLine.rectTransform, new Vector2(1f, 1f), new Vector2(-24f, -16f), new Vector2(520f, 40f));

            // Filter row: mode toggles left, opponent picker + matching count right.
            _toggleAi = FilterToggle("FilterAi", "VS AI", 40f, on => { _incAi = on; RefreshAll(); });
            _toggleMp2 = FilterToggle("FilterMp2", "ONLINE 1V1", 280f, on => { _incMp2 = on; RefreshAll(); });
            _toggleMp3 = FilterToggle("FilterMp3", "ONLINE 3+", 560f, on => { _incMp3 = on; RefreshAll(); });

            _opponentButton = UiFactory.CreateButton(_theme, "OpponentFilter", _panel, "", 16f);
            UiFactory.Place((RectTransform)_opponentButton.transform, new Vector2(1f, 1f),
                new Vector2(-360f, -78f), new Vector2(330f, 44f));
            var oppLabel = UiFactory.ButtonLabel(_opponentButton);
            oppLabel.enableAutoSizing = true;
            oppLabel.fontSizeMin = 10f;
            oppLabel.fontSizeMax = 16f;
            _opponentButton.onClick.AddListener(() =>
                _picker.Show(Agg().Opponents, _opponentKey, key => { _opponentKey = key; RefreshAll(); }));

            _matchingText = UiFactory.CreateText(_theme, "Matching", _panel, "", 14f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineRight);
            UiFactory.Place(_matchingText.rectTransform, new Vector2(1f, 1f), new Vector2(-28f, -84f), new Vector2(300f, 30f));

            // Sub-tabs (Changelog tab idiom: selected gold, rest panel-light).
            for (int i = 0; i < TabNames.Length; i++)
            {
                var tab = UiFactory.CreateButton(_theme, "Tab_" + TabNames[i], _panel, Loc.T(TabNames[i]), 17f);
                UiFactory.Place((RectTransform)tab.transform, new Vector2(0.5f, 1f),
                    new Vector2(-675f + i * 270f, -128f), new Vector2(250f, 46f));
                int index = i;
                tab.onClick.AddListener(() => SelectTab(index));
                _tabButtons.Add(tab);
            }

            var scroll = UiFactory.CreateScrollView(_theme, "Content", _panel, out _content);
            UiFactory.Place((RectTransform)scroll.transform, new Vector2(0.5f, 1f),
                new Vector2(0f, -190f), new Vector2(1700f, 700f));

            var back = UiFactory.CreateButton(_theme, "Back", _panel, Loc.T("BACK"), 18f);
            UiFactory.Place((RectTransform)back.transform, new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(200f, 52f));
            back.onClick.AddListener(() => _onBack?.Invoke());

            // Own full-stretch overlay rects, direct children of Root: the opponent
            // picker, and a dedicated CardListModal container (Init deactivates its
            // Container — it must NEVER share a rect with the rest of the screen).
            _picker = SoiStatsOpponentPicker.Create(Root, _theme);
            var cardListRect = UiFactory.CreateRect("CardList", Root);
            UiFactory.Stretch(cardListRect);
            _cardList = cardListRect.gameObject.AddComponent<CardListModal>();
            _cardList.Container = cardListRect;
            _cardList.Init(_theme);

            RefreshTabColors();
        }

        private Toggle FilterToggle(string name, string label, float x, UnityEngine.Events.UnityAction<bool> onChanged)
        {
            var toggle = UiFactory.CreateToggle(_theme, name, _panel, Loc.T(label));
            UiFactory.Place((RectTransform)toggle.transform, new Vector2(0f, 1f), new Vector2(x, -84f), new Vector2(230f, 34f));
            toggle.isOn = true;
            toggle.onValueChanged.AddListener(onChanged);
            return toggle;
        }

        private void SelectTab(int index)
        {
            _tab = index;
            RefreshTabColors();
            Render();
        }

        private void RefreshTabColors()
        {
            for (int i = 0; i < _tabButtons.Count; i++)
                _tabButtons[i].image.color = i == _tab ? UiPalette.Gold : UiPalette.PanelLight;
        }

        // ------------------------------------------------------------------ state

        private string FilterKey =>
            (_incAi ? "1" : "0") + (_incMp2 ? "1" : "0") + (_incMp3 ? "1" : "0") + "|" + (_opponentKey ?? "");

        private SoiStatsFilter BuildFilter() => new SoiStatsFilter
        {
            IncludeAi = _incAi,
            IncludeMp2 = _incMp2,
            IncludeMp3Plus = _incMp3,
            OpponentKey = _opponentKey
        };

        private SoiStatsAggregates Agg()
        {
            string key = FilterKey;
            if (!_aggCache.TryGetValue(key, out var agg))
                _aggCache[key] = agg = SoiStatsAggregator.Compute(
                    SoiStatsService.Records, SoiStatsService.Stubs, BuildFilter());
            return agg;
        }

        /// <summary>Pair aggregation is heavier, so it only runs on the first
        /// SYNERGIES render per filter key.</summary>
        private List<PairAgg> Pairs()
        {
            string key = FilterKey;
            if (!_pairCache.TryGetValue(key, out var pairs))
                _pairCache[key] = pairs = SoiStatsAggregator.ComputePairs(SoiStatsService.Records, BuildFilter());
            return pairs;
        }

        private void InvalidateCaches()
        {
            _aggCache.Clear();
            _pairCache.Clear();
        }

        private void RefreshAll()
        {
            var agg = Agg();
            _matchingText.text = Loc.T("Matching games: ") + agg.Games;
            UiFactory.ButtonLabel(_opponentButton).text = _opponentKey == null
                ? Loc.T("OPPONENT: ALL")
                : Loc.T("OPPONENT: ") + CurrentOpponentLabel(agg);
            Render();
        }

        private void ResetFilters()
        {
            _incAi = _incMp2 = _incMp3 = true;
            _opponentKey = null;
            _toggleAi.SetIsOnWithoutNotify(true);
            _toggleMp2.SetIsOnWithoutNotify(true);
            _toggleMp3.SetIsOnWithoutNotify(true);
            RefreshAll();
        }

        private string CurrentOpponentLabel(SoiStatsAggregates agg)
        {
            if (_opponentKey == null) return Loc.T("ALL");
            foreach (var opp in agg.Opponents)
                if (opp.IdentityKey == _opponentKey)
                    return OpponentDisplayName(opp);
            return DeriveOpponentName(_opponentKey);
        }

        // Bots always use the kind-based label — their seat DisplayName is whatever
        // character they played last ("Tetra (Bot)"), misleading for an identity
        // that spans games with different characters.
        internal static string OpponentDisplayName(OpponentAgg opp) =>
            opp.IsBot || string.IsNullOrEmpty(opp.DisplayName)
                ? DeriveOpponentName(opp.IdentityKey) : opp.DisplayName;

        /// <summary>Bots record as "bot:&lt;kind&gt;" — derive a readable fallback; the
        /// kind is already a rank word and stays untranslated.</summary>
        private static string DeriveOpponentName(string identityKey)
        {
            if (string.IsNullOrEmpty(identityKey)) return "?";
            return identityKey.StartsWith("bot:") ? "Bot (" + identityKey.Substring(4) + ")" : identityKey;
        }

        internal static string DateOnly(string utc) =>
            !string.IsNullOrEmpty(utc) && utc.Length >= 10 ? utc.Substring(0, 10) : (utc ?? "");

        /// <summary>Picker/list order: most games first, then latest, then key.</summary>
        internal static List<OpponentAgg> SortOpponents(List<OpponentAgg> opponents)
        {
            var sorted = opponents != null ? new List<OpponentAgg>(opponents) : new List<OpponentAgg>();
            sorted.Sort((a, b) =>
            {
                int byGames = b.Games.CompareTo(a.Games);
                if (byGames != 0) return byGames;
                int byLast = string.CompareOrdinal(b.LastPlayedUtc ?? "", a.LastPlayedUtc ?? "");
                return byLast != 0 ? byLast : string.CompareOrdinal(a.IdentityKey ?? "", b.IdentityKey ?? "");
            });
            return sorted;
        }

        // ------------------------------------------------------------------ display math (winrate = wins / (games - ties))

        private static int PctOf(int wins, int decisive) =>
            decisive > 0 ? Mathf.RoundToInt(100f * wins / decisive) : 0;

        private static string Pct(int wins, int decisive) =>
            decisive > 0 ? PctOf(wins, decisive) + "%" : "—";

        private static float Frac(int wins, int decisive) =>
            decisive > 0 ? Mathf.Clamp01((float)wins / decisive) : 0f;

        private static string Wlt(int wins, int losses, int ties) => wins + "-" + losses + "-" + ties;

        private static string ModeTag(string mode) => mode switch
        {
            "ai" => Loc.T("AI"),
            "mp2" => "1V1",
            "mp3plus" => "3+",
            _ => mode
        };

        // ------------------------------------------------------------------ render

        private void Render()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);

            float y;
            if (SoiStatsService.Records.Count == 0 && SoiStatsService.Stubs.Count == 0)
                y = RenderNoGames();
            else
            {
                var agg = Agg();
                if (agg.Games == 0)
                    y = RenderNoMatches();
                else
                    y = _tab switch
                    {
                        0 => RenderOverview(agg),
                        1 => RenderHeroes(agg),
                        2 => RenderCards(agg),
                        3 => RenderSynergies(agg),
                        4 => RenderOpponents(agg),
                        _ => RenderRecentRows(agg.Recent, 50, -16f)
                    };
            }
            _content.sizeDelta = new Vector2(0f, Mathf.Max(660f, -y + 24f));
        }

        private float RenderNoGames()
        {
            var text = UiFactory.CreateText(_theme, "Empty", _content,
                Loc.T("No games recorded yet — your Shards of Infinity story starts with your first finished game."),
                20f, UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(text.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -240f), new Vector2(1100f, 64f));
            var play = UiFactory.CreateButton(_theme, "PlayNow", _content, Loc.T("PLAY NOW"), 22f,
                UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)play.transform, new Vector2(0.5f, 1f), new Vector2(0f, -340f), new Vector2(260f, 60f));
            play.onClick.AddListener(() => _onPlayNow?.Invoke());
            return -440f;
        }

        private float RenderNoMatches()
        {
            var text = UiFactory.CreateText(_theme, "Empty", _content,
                Loc.T("No games match these filters."), 20f, UiPalette.TextDim, TextAlignmentOptions.Center);
            UiFactory.Place(text.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -240f), new Vector2(900f, 32f));
            var reset = UiFactory.CreateButton(_theme, "ResetFilters", _content, Loc.T("RESET FILTERS"), 20f);
            UiFactory.Place((RectTransform)reset.transform, new Vector2(0.5f, 1f), new Vector2(0f, -300f), new Vector2(320f, 56f));
            reset.onClick.AddListener(ResetFilters);
            return -400f;
        }

        // ------------------------------------------------------------------ OVERVIEW

        private float RenderOverview(SoiStatsAggregates agg)
        {
            float y = -20f;
            int decisive = agg.Games - agg.Ties;

            string streak = agg.CurrentWinStreak > 0 ? Loc.T("W") + agg.CurrentWinStreak
                : agg.CurrentLossStreak > 0 ? Loc.T("L") + agg.CurrentLossStreak : "—";
            int minutes = Mathf.RoundToInt(agg.AvgDurationSeconds / 60f);
            SoiStatsWidgets.StatTile(_theme, _content, 24f, y, Loc.T("GAMES"), agg.Games.ToString());
            SoiStatsWidgets.StatTile(_theme, _content, 304f, y, Loc.T("WINRATE"),
                Pct(agg.Wins, decisive), Wlt(agg.Wins, agg.Losses, agg.Ties));
            SoiStatsWidgets.StatTile(_theme, _content, 584f, y, Loc.T("CURRENT STREAK"), streak);
            SoiStatsWidgets.StatTile(_theme, _content, 864f, y, Loc.T("BEST WIN STREAK"),
                agg.BestWinStreak > 0 ? Loc.T("W") + agg.BestWinStreak : "—");
            SoiStatsWidgets.StatTile(_theme, _content, 1144f, y, Loc.T("AVG LENGTH"),
                agg.AvgRounds.ToString("0.0", Inv) + " " + Loc.T("rounds"), minutes + " " + Loc.T("min"));
            SoiStatsWidgets.StatTile(_theme, _content, 1424f, y, Loc.T("BIGGEST HIT"),
                agg.MaxSingleHit + " <sprite name=\"soi_power\">");
            y -= 150f;

            SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("WINRATE BY MODE"));
            y -= 40f;
            y = ModeRow(y, "VS AI", agg.Ai);
            y = ModeRow(y, "ONLINE 1V1", agg.Mp2);
            y = ModeRow(y, "ONLINE 3+", agg.Mp3Plus);
            y -= 16f;

            SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("HOW YOU WIN"));
            y -= 40f;
            y = ShareRow(y, "Health to zero", agg.WinsByKill, agg.Wins, UiPalette.Good);
            y = ShareRow(y, "Mastery 30", agg.WinsByOverwhelm, agg.Wins, UiPalette.Good);
            y = ShareRow(y, "Concede", agg.WinsByConcede, agg.Wins, UiPalette.Good);
            y -= 16f;

            SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("HOW YOU LOSE"));
            y -= 40f;
            y = ShareRow(y, "Health to zero", agg.LossesByKill, agg.Losses, UiPalette.Danger);
            y = ShareRow(y, "Mastery 30", agg.LossesByOverwhelm, agg.Losses, UiPalette.Danger);
            y = ShareRow(y, "Concede", agg.LossesByConcede, agg.Losses, UiPalette.Danger);
            y -= 20f;

            // Hero + card spotlight.
            string heroId = agg.BestHeroCharacterId;
            var heroAgg = FindHero(agg, heroId);
            var fav = FavoriteCard(agg);
            if (heroId != null || fav != null)
            {
                if (heroId != null)
                {
                    var heroLabel = UiFactory.CreateText(_theme, "HeroLabel", _content,
                        Loc.T(agg.BestHeroQualified ? "BEST HERO" : "FAVORITE HERO"), 15f,
                        UiPalette.Gold, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                    SoiStatsWidgets.Place(heroLabel.rectTransform, 40f, y, 320f, 20f);
                }
                if (fav != null)
                {
                    var cardLabel = UiFactory.CreateText(_theme, "CardLabel", _content,
                        Loc.T("FAVORITE CARD"), 15f, UiPalette.Gold,
                        TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                    SoiStatsWidgets.Place(cardLabel.rectTransform, 420f, y, 320f, 20f);
                }
                float rowTop = y - 26f;
                if (heroId != null)
                {
                    string c1 = null, c2 = null;
                    if (heroAgg != null)
                    {
                        int d = heroAgg.Games - heroAgg.Ties;
                        c1 = heroAgg.Games + " " + Loc.T("games") + " · " +
                             Wlt(heroAgg.Wins, heroAgg.Games - heroAgg.Wins - heroAgg.Ties, heroAgg.Ties);
                        c2 = Pct(heroAgg.Wins, d) + " " + Loc.T("winrate");
                    }
                    SoiStatsWidgets.CardWithCaption(_theme, _content, 40f, rowTop,
                        SoiCardFaces.CharacterPrefix + heroId, 0.72f, c1, c2);
                }
                if (fav != null)
                {
                    int d = fav.GamesBought - fav.TiesWhenBought;
                    SoiStatsWidgets.CardWithCaption(_theme, _content, 420f, rowTop, fav.DefId, 0.72f,
                        "×" + fav.TimesBought + " " + Loc.T("buys"),
                        Pct(fav.WinsWhenBought, d) + " " + Loc.T("wins"));
                }
                y = rowTop - (CardView.Height * 0.72f + 44f) - 28f;
            }

            SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("MASTERY PACE"));
            y -= 38f;
            string pace = "<sprite name=\"soi_mastery\">  M10 " + PaceText(agg.AvgRoundToM10) +
                " · M20 " + PaceText(agg.AvgRoundToM20) +
                " · M30 " + PaceText(agg.AvgRoundToM30) +
                " · " + Loc.T("reached: ") + Mathf.RoundToInt(agg.M30ReachRate * 100f) + "%";
            var paceText = UiFactory.CreateText(_theme, "Pace", _content, pace, 18f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
            if (_theme.Icons != null) paceText.spriteAsset = _theme.Icons;
            SoiStatsWidgets.Place(paceText.rectTransform, 40f, y, 1600f, 28f);
            return y - 44f;
        }

        private static string PaceText(float avgRound) =>
            avgRound < 0f ? Loc.T("never") : Loc.T("round ") + avgRound.ToString("0.0", Inv);

        private float ModeRow(float y, string label, ModeAgg mode)
        {
            int decisive = mode.Games - mode.Ties;
            int losses = mode.Games - mode.Wins - mode.Ties;
            SoiStatsWidgets.BarRow(_theme, _content, y, Loc.T(label), Frac(mode.Wins, decisive),
                UiPalette.Good, Pct(mode.Wins, decisive) + " · " + Wlt(mode.Wins, losses, mode.Ties));
            return y - 34f;
        }

        private float ShareRow(float y, string label, int count, int total, Color fill)
        {
            SoiStatsWidgets.BarRow(_theme, _content, y, Loc.T(label), Frac(count, total), fill,
                count.ToString());
            return y - 34f;
        }

        private static HeroAgg FindHero(SoiStatsAggregates agg, string characterId)
        {
            if (characterId == null) return null;
            foreach (var h in agg.Heroes)
                if (h.CharacterId == characterId)
                    return h;
            return null;
        }

        private static CardAgg FavoriteCard(SoiStatsAggregates agg)
        {
            CardAgg best = null;
            foreach (var c in agg.Cards)
                if (c.TimesBought > 0 && (best == null || c.TimesBought > best.TimesBought))
                    best = c;
            return best;
        }

        // ------------------------------------------------------------------ HEROES

        private float RenderHeroes(SoiStatsAggregates agg)
        {
            float y = -16f;
            var byId = new Dictionary<string, HeroAgg>();
            foreach (var h in agg.Heroes)
                byId[h.CharacterId] = h;
            // Full roster in registry order; zero-game heroes dimmed at the bottom.
            var roster = ShardsContentRegistry.CharactersFor(ShardsDlc.ShadowOfSalvation);
            foreach (var id in roster)
                if (byId.TryGetValue(id, out var h))
                    y = HeroRow(y, id, h, agg.BestHeroQualified && id == agg.BestHeroCharacterId);
            foreach (var id in roster)
                if (!byId.ContainsKey(id))
                    y = HeroRow(y, id, null, false);
            return y;
        }

        private float HeroRow(float y, string characterId, HeroAgg h, bool best)
        {
            var row = UiFactory.CreateRect("HeroRow", _content);
            SoiStatsWidgets.Place(row, 0f, y, 1688f, 150f);
            if (h == null)
                UiFactory.AddGroup(row.gameObject).alpha = 0.45f;

            var view = SoiStatsWidgets.CardWithCaption(_theme, row, 24f, -4f,
                SoiCardFaces.CharacterPrefix + characterId, 0.45f, null, null);
            if (best)
            {
                var outline = view.Frame.gameObject.AddComponent<Outline>();
                outline.effectColor = UiPalette.Gold;
                outline.effectDistance = new Vector2(3f, -3f);
            }

            string name = ShardsContentRegistry.CharacterDisplayName(characterId) +
                (best ? "  <color=#D4AF37>" + Loc.T("BEST") + "</color>" : "");
            var nameText = UiFactory.CreateText(_theme, "Name", row, name, 20f,
                UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            SoiStatsWidgets.Place(nameText.rectTransform, 160f, -6f, 700f, 26f);

            if (h == null)
            {
                var none = UiFactory.CreateText(_theme, "None", row, Loc.T("no games yet"), 15f,
                    UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
                SoiStatsWidgets.Place(none.rectTransform, 160f, -38f, 500f, 22f);
                return y - 158f;
            }

            int decisive = h.Games - h.Ties;
            int losses = h.Games - h.Wins - h.Ties;
            var info = UiFactory.CreateText(_theme, "Info", row,
                h.Games + " " + Loc.T("games") + " · " + Pct(h.Wins, decisive) + " " + Loc.T("winrate"),
                15f, UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            SoiStatsWidgets.Place(info.rectTransform, 160f, -36f, 700f, 22f);

            SoiStatsWidgets.Bar(_theme, row, 160f, -66f, 900f, 18f, Frac(h.Wins, decisive), UiPalette.Good);
            var wlt = UiFactory.CreateText(_theme, "Wlt", row, Wlt(h.Wins, losses, h.Ties), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            SoiStatsWidgets.Place(wlt.rectTransform, 1080f, -64f, 300f, 22f);

            var detail = UiFactory.CreateText(_theme, "Detail", row,
                Loc.T("avg ") + h.AvgRounds.ToString("0.0", Inv) + " " + Loc.T("rounds") +
                " · " + Loc.T("M30 avg round ") +
                (h.AvgRoundToM30 < 0f ? Loc.T("never") : h.AvgRoundToM30.ToString("0.0", Inv)) +
                " · " + Loc.T("biggest hit ") + h.MaxSingleHit, 14f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            SoiStatsWidgets.Place(detail.rectTransform, 160f, -96f, 900f, 22f);
            return y - 158f;
        }

        // ------------------------------------------------------------------ CARDS

        private float RenderCards(SoiStatsAggregates agg)
        {
            float y = -16f;
            y = CardSection(y, agg, "MOST PLAYED", byPlays: true, _playedExpanded,
                () => { _playedExpanded = !_playedExpanded; Render(); });
            y -= 12f;
            y = CardSection(y, agg, "MOST BOUGHT", byPlays: false, _boughtExpanded,
                () => { _boughtExpanded = !_boughtExpanded; Render(); });
            return y;
        }

        private float CardSection(float y, SoiStatsAggregates agg, string header, bool byPlays,
            bool expanded, Action toggle)
        {
            var list = new List<CardAgg>();
            foreach (var c in agg.Cards)
                if ((byPlays ? c.TimesPlayed : c.TimesBought) > 0)
                    list.Add(c);
            list.Sort((a, b) =>
            {
                int byMetric = (byPlays ? b.TimesPlayed : b.TimesBought)
                    .CompareTo(byPlays ? a.TimesPlayed : a.TimesBought);
                return byMetric != 0 ? byMetric : string.CompareOrdinal(a.DefId, b.DefId);
            });

            SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T(header));
            if (list.Count > 10)
            {
                var see = UiFactory.CreateButton(_theme, "SeeAll", _content,
                    Loc.T(expanded ? "SEE LESS" : "SEE ALL"), 14f);
                SoiStatsWidgets.Place((RectTransform)see.transform, 1470f, y + 4f, 190f, 32f);
                see.onClick.AddListener(() => toggle());
            }
            y -= 44f;

            int count = expanded ? list.Count : Mathf.Min(10, list.Count);
            const float scale = 0.62f;
            float rowH = CardView.Height * scale + 44f + 16f;
            for (int i = 0; i < count; i++)
            {
                var c = list[i];
                float x = 40f + i % 10 * 165f;
                float cy = y - i / 10 * rowH;
                int wins = byPlays ? c.WinsWhenPlayed : c.WinsWhenBought;
                int decisive = byPlays ? c.GamesPlayed - c.TiesWhenPlayed : c.GamesBought - c.TiesWhenBought;
                SoiStatsWidgets.CardWithCaption(_theme, _content, x, cy, c.DefId, scale,
                    "×" + (byPlays ? c.TimesPlayed : c.TimesBought) + " " + Loc.T(byPlays ? "plays" : "buys"),
                    Pct(wins, decisive) + " " + Loc.T("wins"));
            }
            int rows = (count + 9) / 10;
            return y - Mathf.Max(1, rows) * rowH;
        }

        // ------------------------------------------------------------------ SYNERGIES

        private float RenderSynergies(SoiStatsAggregates agg)
        {
            float y = -16f;
            var intro = UiFactory.CreateText(_theme, "Intro", _content,
                Loc.T("Card pairs you bought in the same game — ranked by how much your winrate climbs when both are in your deck (minimum 5 games together)."),
                15f, UiPalette.TextDim, TextAlignmentOptions.TopLeft);
            SoiStatsWidgets.Place(intro.rectTransform, 24f, y, 1640f, 10f);
            float introH = intro.preferredHeight + 6f;
            intro.rectTransform.sizeDelta = new Vector2(1640f, introH);
            y -= introH + 18f;

            int decisiveAll = agg.Games - agg.Ties;
            float overall = Frac(agg.Wins, decisiveAll);
            var ranked = new List<(PairAgg Pair, float Lift, float Rate)>();
            foreach (var p in Pairs())
            {
                int decisive = p.GamesTogether - p.TiesTogether;
                if (decisive < 5) continue;
                float rate = (float)p.WinsTogether / decisive;
                ranked.Add((p, rate - overall, rate));
            }
            ranked.Sort((a, b) =>
            {
                int byLift = b.Lift.CompareTo(a.Lift);
                if (byLift != 0) return byLift;
                int byGames = b.Pair.GamesTogether.CompareTo(a.Pair.GamesTogether);
                if (byGames != 0) return byGames;
                int byA = string.CompareOrdinal(a.Pair.DefA, b.Pair.DefA);
                return byA != 0 ? byA : string.CompareOrdinal(a.Pair.DefB, b.Pair.DefB);
            });

            if (ranked.Count == 0)
            {
                var none = UiFactory.CreateText(_theme, "None", _content,
                    Loc.T("Not enough data yet — pairs need at least 5 games together."), 18f,
                    UiPalette.TextDim, TextAlignmentOptions.Center);
                UiFactory.Place(none.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, y - 60f), new Vector2(1000f, 30f));
                return y - 140f;
            }

            const float scale = 0.5f;
            float rowH = CardView.Height * scale + 24f;
            for (int i = 0; i < ranked.Count && i < 8; i++)
            {
                var (pair, _, rate) = ranked[i];
                SoiStatsWidgets.CardWithCaption(_theme, _content, 40f, y, pair.DefA, scale, null, null);
                SoiStatsWidgets.CardWithCaption(_theme, _content, 162f, y, pair.DefB, scale, null, null);
                int pct = Mathf.RoundToInt(rate * 100f);
                int overallPct = Mathf.RoundToInt(overall * 100f);
                int delta = pct - overallPct;
                string deltaText = delta >= 0
                    ? "<color=#6FDF8F>(+" + delta + "%)</color>"
                    : "<color=#E84545>(" + delta + "%)</color>";
                var line = UiFactory.CreateText(_theme, "PairLine", _content,
                    Loc.T("TOGETHER: ") + pair.GamesTogether + " · " + pct + "% — " +
                    Loc.T("overall ") + overallPct + "%  " + deltaText,
                    19f, UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
                SoiStatsWidgets.Place(line.rectTransform, 310f, y - CardView.Height * scale * 0.5f + 14f, 1200f, 28f);
                y -= rowH;
            }
            return y;
        }

        // ------------------------------------------------------------------ OPPONENTS

        private float RenderOpponents(SoiStatsAggregates agg)
        {
            float y = -16f;
            var intro = UiFactory.CreateText(_theme, "Intro", _content,
                Loc.T("Click an opponent to focus every tab on games against them."), 15f,
                UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
            SoiStatsWidgets.Place(intro.rectTransform, 24f, y, 1400f, 22f);
            y -= 40f;
            return _opponentKey == null ? RenderOpponentList(agg, y) : RenderHeadToHead(agg, y);
        }

        private float RenderOpponentList(SoiStatsAggregates agg, float y)
        {
            foreach (var opp in SortOpponents(agg.Opponents))
            {
                var row = UiFactory.CreateRect("OpponentRow", _content);
                SoiStatsWidgets.Place(row, 0f, y, 1688f, 64f);
                var hit = UiFactory.CreateImage("Hit", row, _theme.Rounded,
                    UiPalette.WithAlpha(UiPalette.PanelLight, 0.25f), raycast: true);
                UiFactory.Stretch(hit.rectTransform, 8, 4, 8, 4);
                var button = hit.gameObject.AddComponent<Button>();
                button.targetGraphic = hit;
                string key = opp.IdentityKey;
                button.onClick.AddListener(() => { _opponentKey = key; RefreshAll(); });

                var name = UiFactory.CreateText(_theme, "Name", row,
                    OpponentDisplayName(opp), 18f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                SoiStatsWidgets.Place(name.rectTransform, 28f, -6f, 430f, 26f);

                int decisive = opp.Games - opp.Ties;
                var counts = UiFactory.CreateText(_theme, "Counts", row,
                    opp.Games + " · " + Wlt(opp.MyWins, opp.MyLosses, opp.Ties), 14f,
                    UiPalette.TextDim, TextAlignmentOptions.MidlineLeft);
                SoiStatsWidgets.Place(counts.rectTransform, 28f, -34f, 430f, 20f);

                SoiStatsWidgets.Bar(_theme, row, 480f, -22f, 640f, 18f,
                    Frac(opp.MyWins, decisive), UiPalette.Good);
                var pctText = UiFactory.CreateText(_theme, "Pct", row, Pct(opp.MyWins, decisive), 15f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
                SoiStatsWidgets.Place(pctText.rectTransform, 1140f, -20f, 120f, 22f);

                var date = UiFactory.CreateText(_theme, "Date", row, DateOnly(opp.LastPlayedUtc), 14f,
                    UiPalette.TextDim, TextAlignmentOptions.MidlineRight);
                SoiStatsWidgets.Place(date.rectTransform, 1300f, -20f, 360f, 22f);

                y -= 70f;
            }
            return y;
        }

        private float RenderHeadToHead(SoiStatsAggregates agg, float y)
        {
            SoiStatsWidgets.SectionHeader(_theme, _content, y,
                Loc.T("HEAD-TO-HEAD") + " — " + CurrentOpponentLabel(agg));
            y -= 44f;

            int decisive = agg.Games - agg.Ties;
            SoiStatsWidgets.StatTile(_theme, _content, 24f, y, Loc.T("GAMES"), agg.Games.ToString());
            SoiStatsWidgets.StatTile(_theme, _content, 304f, y, Loc.T("WINRATE"), Pct(agg.Wins, decisive));
            SoiStatsWidgets.StatTile(_theme, _content, 584f, y, Loc.T("W-L-T"),
                Wlt(agg.Wins, agg.Losses, agg.Ties));
            y -= 150f;

            var h2h = agg.H2H;
            if (h2h != null && h2h.TheirHeroes.Count > 0)
            {
                SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("THEIR HEROES"));
                y -= 44f;
                var theirs = new List<HeroAgg>(h2h.TheirHeroes);
                theirs.Sort((a, b) =>
                {
                    int byGames = b.Games.CompareTo(a.Games);
                    return byGames != 0 ? byGames : string.CompareOrdinal(a.CharacterId, b.CharacterId);
                });
                float x = 40f;
                foreach (var h in theirs)
                {
                    SoiStatsWidgets.CardWithCaption(_theme, _content, x, y,
                        SoiCardFaces.CharacterPrefix + h.CharacterId, 0.5f,
                        h.Games + " " + Loc.T("games"),
                        Pct(h.Wins, h.Games - h.Ties) + " " + Loc.T("wins"));
                    x += 140f;
                }
                y -= CardView.Height * 0.5f + 44f + 24f;
            }

            if (h2h != null && h2h.TheirCards.Count > 0)
            {
                SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("THEIR FAVORITE CARDS"));
                y -= 44f;
                var cards = new List<CardAgg>();
                foreach (var c in h2h.TheirCards)
                    if (c.TimesBought > 0)
                        cards.Add(c);
                cards.Sort((a, b) =>
                {
                    int byBought = b.TimesBought.CompareTo(a.TimesBought);
                    return byBought != 0 ? byBought : string.CompareOrdinal(a.DefId, b.DefId);
                });
                float x = 40f;
                for (int i = 0; i < cards.Count && i < 10; i++)
                {
                    var c = cards[i];
                    SoiStatsWidgets.CardWithCaption(_theme, _content, x, y, c.DefId, 0.5f,
                        "×" + c.TimesBought + " " + Loc.T("buys"),
                        Pct(c.WinsWhenBought, c.GamesBought - c.TiesWhenBought) + " " + Loc.T("wins"));
                    x += 160f;
                }
                y -= CardView.Height * 0.5f + 44f + 24f;
            }

            if (agg.Heroes.Count > 0)
            {
                SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("YOUR HEROES VS THEM"));
                y -= 40f;
                foreach (var h in agg.Heroes)
                {
                    int d = h.Games - h.Ties;
                    int losses = h.Games - h.Wins - h.Ties;
                    SoiStatsWidgets.BarRow(_theme, _content, y,
                        ShardsContentRegistry.CharacterDisplayName(h.CharacterId),
                        Frac(h.Wins, d), UiPalette.Good,
                        Pct(h.Wins, d) + " · " + Wlt(h.Wins, losses, h.Ties));
                    y -= 34f;
                }
                y -= 16f;
            }

            if (agg.Recent.Count > 0)
            {
                SoiStatsWidgets.SectionHeader(_theme, _content, y, Loc.T("HISTORY"));
                y -= 40f;
                y = RenderRecentRows(agg.Recent, 10, y);
            }
            return y;
        }

        // ------------------------------------------------------------------ HISTORY

        private float RenderRecentRows(List<RecentGame> recent, int limit, float y)
        {
            for (int i = 0; i < recent.Count && i < limit; i++)
            {
                var r = recent[i];
                string pillText = r.Tie ? Loc.T("TIE") : r.Won ? Loc.T("WIN") : Loc.T("LOSS");
                Color pillFill = r.Tie ? UiPalette.PanelLight : r.Won ? UiPalette.Gold : UiPalette.Danger;
                Color pillTextColor = r.Tie ? UiPalette.TextMain : r.Won ? UiPalette.Background : Color.white;
                SoiStatsWidgets.ResultPill(_theme, _content, 24f, y - 6f, pillText, pillFill, pillTextColor);

                string term = r.Tie ? Loc.T("Tie")
                    : r.Termination == "overwhelm" ? Loc.T("Mastery 30") : Loc.T("Health to zero");
                string hero = r.MyCharacterId != null
                    ? ShardsContentRegistry.CharacterDisplayName(r.MyCharacterId) : "?";
                string line = DateOnly(r.EndedAtUtc) + " · " + hero + " · " + Loc.T("vs ") +
                    string.Join(", ", r.OpponentNames) + " · " + ModeTag(r.Mode) + " · " + term +
                    " · " + r.Rounds + " " + Loc.T("rounds") + " · " +
                    Mathf.RoundToInt(r.DurationSeconds / 60f) + " " + Loc.T("min");
                var text = UiFactory.CreateText(_theme, "Line", _content, line, 15f,
                    UiPalette.TextMain, TextAlignmentOptions.MidlineLeft);
                SoiStatsWidgets.Place(text.rectTransform, 104f, y - 8f, 1330f, 24f);

                if (r.MyBuys.Count > 0)
                {
                    var bought = UiFactory.CreateButton(_theme, "Bought", _content, Loc.T("BOUGHT"), 13f);
                    SoiStatsWidgets.Place((RectTransform)bought.transform, 1500f, y - 3f, 150f, 32f);
                    var captured = r;
                    bought.onClick.AddListener(() => ShowBoughtCards(captured));
                }
                y -= 44f;
            }
            return y;
        }

        /// <summary>Buys expanded into synthetic CardSnaps (SoiGameScreen.Synth trick)
        /// so the shared CardListModal renders real SoI faces.</summary>
        private void ShowBoughtCards(RecentGame game)
        {
            var defs = new List<string>(game.MyBuys.Keys);
            defs.Sort(StringComparer.Ordinal);
            var snaps = new List<CardSnap>();
            int next = 1;
            foreach (var defId in defs)
                for (int i = 0; i < game.MyBuys[defId]; i++)
                    snaps.Add(new CardSnap { DefId = defId, InstanceId = next++, EffectiveCost = -1 });
            _cardList.Show(DateOnly(game.EndedAtUtc) + Loc.T(" — cards bought"), snaps);
        }
    }
}
