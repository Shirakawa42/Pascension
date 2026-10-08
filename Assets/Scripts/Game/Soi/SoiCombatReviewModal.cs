using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Game.UI;
using Pascension.Game.View;
using Shards.Engine;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pascension.Game.Soi
{
    /// <summary>
    /// End-turn reminder for Duel's immediate champion attacks. The host supplies
    /// legal attacks and their costs; the window never allocates damage or infers
    /// targeting rules. It stays open while snapshots replace destroyed champions.
    /// </summary>
    public sealed class SoiCombatReviewModal : MonoBehaviour
    {
        private const float CardScale = 0.76f;
        private const int Columns = 7;
        private const float CellWidth = 188f;
        private const float RowHeight = CardView.Height * CardScale + 62f;

        private UiTheme _theme;
        private RectTransform _root;
        private RectTransform _rows;
        private RectTransform _animationLayer;
        private ScrollRect _scroll;
        private TextMeshProUGUI _power;
        private Button _finish;
        private Button _cancel;
        private int _viewer;
        private int _maxHealth;
        private bool _waiting;
        private Action<int> _onAttack;
        private Action _onFinish;
        private Action _onCancel;

        private readonly Dictionary<int, ShardsAttackChampionAction> _attacks = new();
        private readonly List<(CardView card, Image veil)> _champions = new();
        private readonly HashSet<int> _destroyedChampions = new();

        public bool Visible => _root != null && _root.gameObject.activeSelf;

        public static SoiCombatReviewModal Create(Transform parent, UiTheme theme)
        {
            var root = UiFactory.CreateRect("SoiCombatReviewModal", parent);
            UiFactory.Stretch(root);
            var modal = root.gameObject.AddComponent<SoiCombatReviewModal>();
            modal.Build(theme, root);
            return modal;
        }

        private void Build(UiTheme theme, RectTransform root)
        {
            _theme = theme;
            _root = root;
            var dimmer = UiFactory.CreateDimmer("Dimmer", root);
            dimmer.color = new Color(0f, 0f, 0f, 0.66f);

            var panel = UiFactory.CreatePanel(theme, "Panel", root, UiPalette.Panel).rectTransform;
            UiFactory.Place(panel, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1500f, 720f));

            var title = UiFactory.CreateText(theme, "Title", panel,
                Loc.T("COMBAT"), 38f, UiPalette.Gold,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(title.rectTransform, new Vector2(0.5f, 1f),
                new Vector2(0f, -24f), new Vector2(1400f, 52f));
            title.enableAutoSizing = true;
            title.fontSizeMin = 24f;
            title.fontSizeMax = 38f;

            _power = UiFactory.CreateText(theme, "RemainingPower", panel, "", 30f,
                UiPalette.Gold, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_power.rectTransform, new Vector2(0.5f, 1f),
                new Vector2(0f, -82f), new Vector2(1000f, 40f));

            var body = UiFactory.CreateRect("Body", panel);
            UiFactory.Stretch(body, 36f, 120f, 36f, 140f);
            _scroll = UiFactory.CreateScrollView(theme, "Opponents", body, out _rows);
            UiFactory.Stretch((RectTransform)_scroll.transform);
            var backdrop = _scroll.GetComponent<Image>();
            backdrop.color = Color.clear;
            backdrop.sprite = null;
            var layout = _rows.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = _rows.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _cancel = UiFactory.CreateButton(theme, "BackToTurn", panel,
                Loc.T("BACK"), 26f);
            UiFactory.Place((RectTransform)_cancel.transform, new Vector2(0.5f, 0f),
                new Vector2(-180f, 32f), new Vector2(340f, 68f));
            _cancel.onClick.AddListener(() =>
            {
                if (!Visible || _waiting) return;
                _onCancel?.Invoke();
            });

            _finish = UiFactory.CreateButton(theme, "FinishTurn", panel,
                Loc.T("END TURN"), 26f, UiPalette.Gold, UiPalette.Background);
            UiFactory.Place((RectTransform)_finish.transform, new Vector2(0.5f, 0f),
                new Vector2(180f, 32f), new Vector2(340f, 68f));
            _finish.onClick.AddListener(() =>
            {
                if (!Visible || _waiting) return;
                SetWaiting(true);
                _onFinish?.Invoke();
            });

            // Keep keyboard/controller navigation inside the open window.
            _finish.navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnLeft = _cancel, selectOnRight = _cancel };
            _cancel.navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnLeft = _finish, selectOnRight = _finish };

            // Destruction proxies stay above the panel, outside the scrolling
            // rows which are rebuilt immediately from authoritative snapshots.
            _animationLayer = UiFactory.CreateRect("ChampionDeaths", panel);
            UiFactory.Stretch(_animationLayer);
            var animationGroup = _animationLayer.gameObject.AddComponent<CanvasGroup>();
            animationGroup.interactable = false;
            animationGroup.blocksRaycasts = false;

            root.gameObject.SetActive(false);
        }

        public void Show(ShardsSnapshot snapshot, int viewer, int maxHealth,
            IReadOnlyList<ShardsAttackChampionAction> attacks, Action<int> onAttack,
            Action onFinish, Action onCancel)
        {
            ClearDeathAnimations();
            _viewer = viewer;
            _maxHealth = maxHealth;
            _onAttack = onAttack;
            _onFinish = onFinish;
            _onCancel = onCancel;
            _waiting = false;
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Refresh(snapshot, attacks);
            _scroll.verticalNormalizedPosition = 1f;
            EventSystem.current?.SetSelectedGameObject(_finish.gameObject);
        }

        /// <summary>Refresh is independent of the input acknowledgement: the screen
        /// releases SetWaiting only when the host has supplied fresh legal actions.</summary>
        public void Refresh(ShardsSnapshot snapshot, IReadOnlyList<ShardsAttackChampionAction> attacks)
        {
            if (!Visible || snapshot == null) return;
            float scrollPosition = _scroll.verticalNormalizedPosition;
            _attacks.Clear();
            if (attacks != null)
                foreach (var attack in attacks)
                    if (attack != null) _attacks[attack.CardInstanceId] = attack;

            _champions.Clear();
            // Destroy is deferred until frame end. Deactivate first so a previous
            // snapshot cannot remain a clickable target during this frame.
            foreach (Transform child in _rows)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            int power = 0;
            foreach (var player in snapshot.Players)
            {
                if (player.Index == _viewer) power = player.Power;
                else if (!player.Eliminated) BuildOpponent(player);
            }
            _power.text = string.Format(Loc.T("Remaining power: {0}"), power);
            RefreshAvailability();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rows);
            _scroll.verticalNormalizedPosition = scrollPosition;
        }

        public void SetWaiting(bool waiting)
        {
            _waiting = waiting;
            RefreshAvailability();
        }

        /// <summary>Call only for a confirmed champion-destroyed event, before
        /// refreshing its snapshot. Rejected clicks never reach this path.</summary>
        public void MarkDestroyed(int instanceId)
        {
            if (!Visible || _destroyedChampions.Contains(instanceId)) return;
            for (int i = 0; i < _champions.Count; i++)
            {
                var view = _champions[i];
                if (view.card == null || view.card.InstanceId != instanceId) continue;

                _destroyedChampions.Add(instanceId);
                _champions.RemoveAt(i);
                _attacks.Remove(instanceId);
                var card = view.card;
                card.SetGlow(false);
                card.SetOuterGlow(false);
                card.SetHoverGlow(false);
                card.SetSparkle(false);
                card.SetTapped(false);
                card.SetMarkedDamage(0);
                card.HpText.text = "0";
                card.HpText.color = UiPalette.WoundedRed;
                if (view.veil != null) view.veil.gameObject.SetActive(false);
                // Preserve the on-screen pose while escaping the next row rebuild.
                card.Rect.SetParent(_animationLayer, true);
                card.Group.blocksRaycasts = false;
                card.Group.interactable = false;
                foreach (var graphic in card.GetComponentsInChildren<Graphic>(true))
                    graphic.raycastTarget = false;
                card.enabled = false;
                StartCoroutine(AnimateChampionDeath(card));
                return;
            }
        }

        private IEnumerator AnimateChampionDeath(CardView card)
        {
            var rect = card.Rect;
            var startPosition = rect.anchoredPosition;
            var startScale = rect.localScale;
            var startRotation = rect.localRotation;
            var flash = UiFactory.CreateImage("DeathFlash", rect, _theme.Rounded,
                new Color(1f, 0.22f, 0.12f, 0f), raycast: false);
            UiFactory.Stretch(flash.rectTransform);

            const float impactDuration = 0.12f;
            const float dissolveDuration = 0.34f;
            float elapsed = 0f;
            while (card != null && elapsed < impactDuration + dissolveDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float impact = Mathf.Clamp01(elapsed / impactDuration);
                float dissolve = Mathf.Clamp01((elapsed - impactDuration) / dissolveDuration);
                float punch = Mathf.Sin(impact * Mathf.PI) * 0.09f;
                rect.localScale = startScale * (1f + punch - 0.35f * dissolve);
                rect.anchoredPosition = startPosition + new Vector2(
                    Mathf.Sin(impact * Mathf.PI * 4f) * 7f * (1f - impact),
                    -52f * dissolve * dissolve);
                rect.localRotation = startRotation * Quaternion.Euler(0f, 0f, -9f * dissolve);
                card.Group.alpha = 1f - dissolve;
                flash.color = new Color(1f, 0.22f, 0.12f,
                    0.48f * Mathf.Sin(impact * Mathf.PI));
                yield return null;
            }
            if (card != null) Destroy(card.gameObject);
        }

        private void ClearDeathAnimations()
        {
            StopAllCoroutines();
            _destroyedChampions.Clear();
            if (_animationLayer == null) return;
            foreach (Transform child in _animationLayer)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
        }

        private void OnDisable() => ClearDeathAnimations();

        public void Hide()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(_root))
                EventSystem.current.SetSelectedGameObject(null);
            _root.gameObject.SetActive(false);
            _attacks.Clear();
            _onAttack = null;
            _onFinish = null;
            _onCancel = null;
            _waiting = false;
        }

        private void BuildOpponent(ShardsPlayerSnap player)
        {
            var header = UiFactory.CreateRect("Player_" + player.Index, _rows);
            header.sizeDelta = new Vector2(0f, 34f);
            var name = UiFactory.CreateText(_theme, "Name", header,
                player.Name, 24f, UiPalette.TextMain,
                TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Stretch(name.rectTransform);

            int count = 1 + player.Champions.Count;
            for (int first = 0; first < count; first += Columns)
            {
                int inRow = Mathf.Min(Columns, count - first);
                var row = UiFactory.CreateRect("Targets_" + player.Index + "_" + first, _rows);
                row.sizeDelta = new Vector2(0f, RowHeight);
                float startX = -(inRow - 1) * CellWidth / 2f;
                for (int column = 0; column < inRow; column++)
                {
                    int index = first + column;
                    float x = startX + column * CellWidth;
                    if (index == 0) BuildHero(row, player, x);
                    else BuildChampion(row, player.Champions[index - 1], x);
                }
            }
        }

        private CardView CreateCard(RectTransform row, float x)
        {
            var card = CardViewFactory.Create(row, _theme, CardScale);
            card.Rect.anchorMin = card.Rect.anchorMax = new Vector2(0.5f, 1f);
            card.Rect.pivot = new Vector2(0.5f, 1f);
            card.Rect.anchoredPosition = new Vector2(x, -6f);
            card.RotateWhenTapped = false;
            return card;
        }

        private TextMeshProUGUI Caption(RectTransform row, float x)
        {
            var text = UiFactory.CreateText(_theme, "Caption", row, "", 20f,
                UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(text.rectTransform, new Vector2(0.5f, 1f),
                new Vector2(x, -(CardView.Height * CardScale + 12f)),
                new Vector2(CellWidth - 12f, 44f));
            text.enableAutoSizing = true;
            text.fontSizeMin = 15f;
            text.fontSizeMax = 20f;
            return text;
        }

        private void BuildHero(RectTransform row, ShardsPlayerSnap player, float x)
        {
            var portrait = CreateCard(row, x);
            portrait.BindDef(SoiCardFaces.CharacterPrefix + player.CharacterId);
            // Portraits remain hoverable, but have no damage-allocation action.
            var health = Caption(row, x);
            health.text = player.Health + " / " + _maxHealth;
            health.color = UiPalette.HealthColor(player.Health);
        }

        private void BuildChampion(RectTransform row, ShardsCardSnap snapshot, float x)
        {
            var card = CreateCard(row, x);
            card.BindDef(snapshot.DefId, snapshot.InstanceId);
            card.SetTapped(snapshot.Exhausted);
            SoiCardFaces.ApplyChampionState(card, snapshot);
            card.Clicked += clicked =>
            {
                if (!Visible || _waiting || !clicked.gameObject.activeInHierarchy ||
                    !_attacks.ContainsKey(clicked.InstanceId)) return;
                SetWaiting(true);
                _onAttack?.Invoke(clicked.InstanceId);
            };
            var veil = UiFactory.CreateImage("Unavailable", card.Rect, _theme.Rounded,
                new Color(0.02f, 0.02f, 0.03f, 0.58f), raycast: false);
            // Keep live defense and the name readable even when this target is
            // protected or unaffordable; dim only the art and rules below them.
            UiFactory.Stretch(veil.rectTransform, 0f, 0f, 0f, 42f);
            _champions.Add((card, veil));
        }

        private void RefreshAvailability()
        {
            _finish.interactable = !_waiting;
            _cancel.interactable = !_waiting;
            foreach (var view in _champions)
            {
                bool legal = _attacks.ContainsKey(view.card.InstanceId);
                view.card.SetGlow(legal && !_waiting, UiPalette.WoundedRed);
                view.veil.gameObject.SetActive(!legal);
            }
        }
    }
}
