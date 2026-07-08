using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Pascension.Game.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Pascension.Game.View
{
    /// <summary>
    /// The 50-step race track, laid procedurally along a rounded-rectangle path hugging
    /// the screen edge (bottom-center start → right side → boss at top-center). Inns are
    /// larger nodes; player pawns animate node-to-node; reachable nodes highlight and
    /// submit a move on click.
    /// </summary>
    public sealed class BoardTrackView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Raised with the clicked node's step number (1..50).</summary>
        public event Action<int> NodeClicked;

        /// <summary>Raised when the boss card is clicked.</summary>
        public event Action BossClicked;

        private const float HalfW = 912f;
        private const float HalfH = 494f;
        private const float CornerRadius = 72f;

        private GameRules _rules;
        private Vector2[] _nodePos;
        private Image[] _nodeImages;
        private Button[] _nodeButtons;
        private readonly List<RectTransform> _pawns = new List<RectTransform>();
        private readonly List<bool> _pawnAnimating = new List<bool>();
        private CardView _bossCard;
        private TextMeshProUGUI _bossHp;
        private HashSet<int> _reachable = new HashSet<int>();
        private bool _built;

        private static readonly Vector2[] PawnOffsets =
        {
            new Vector2(-12f, 12f), new Vector2(12f, 12f),
            new Vector2(-12f, -12f), new Vector2(12f, -12f)
        };

        public void Init(UiTheme theme, GameRules rules)
        {
            Theme = theme;
            _rules = rules;
            if (_built) return;
            _built = true;

            int steps = rules.BoardSteps;
            _nodePos = new Vector2[steps + 1];
            _nodeImages = new Image[steps + 1];
            _nodeButtons = new Button[steps + 1];

            for (int i = 0; i <= steps; i++)
                _nodePos[i] = Evaluate((float)i / steps);

            var innSteps = new HashSet<int>(rules.InnSteps);

            for (int i = 0; i <= steps; i++)
            {
                bool inn = innSteps.Contains(i);
                bool isBoss = i == steps;
                if (isBoss) continue; // the boss card marks the final step

                float size = inn ? 46f : i == 0 ? 38f : 26f;
                var node = UiFactory.CreateImage($"Node{i}", Container, Theme.Circle,
                    inn ? UiPalette.GoldDim : UiPalette.PanelLight, raycast: true);
                UiFactory.Place(node.rectTransform, new Vector2(0.5f, 0.5f), _nodePos[i], new Vector2(size, size));
                var outline = node.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
                outline.effectDistance = new Vector2(1f, -1f);
                _nodeImages[i] = node;

                var button = node.gameObject.AddComponent<Button>();
                button.targetGraphic = node;
                int step = i;
                button.onClick.AddListener(() => NodeClicked?.Invoke(step));
                _nodeButtons[i] = button;

                if (inn)
                {
                    var label = UiFactory.CreateText(Theme, "InnLabel", node.transform, "INN", 12f,
                        UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
                    UiFactory.Stretch(label.rectTransform);
                }
                else if (i > 0 && i % 10 == 5)
                {
                    var label = UiFactory.CreateText(Theme, "StepLabel", node.transform, i.ToString(), 12f,
                        UiPalette.TextDim, TextAlignmentOptions.Center);
                    UiFactory.Stretch(label.rectTransform);
                }
                else if (i == 0)
                {
                    var label = UiFactory.CreateText(Theme, "StartLabel", node.transform, "0", 13f,
                        UiPalette.TextMain, TextAlignmentOptions.Center, FontStyles.Bold);
                    UiFactory.Stretch(label.rectTransform);
                }
            }

            // Boss at the final step, top-center.
            var bossAnchor = _nodePos[steps] + new Vector2(0f, -86f);
            _bossCard = CardViewFactory.Create(Container, Theme, 0.52f);
            _bossCard.Rect.anchoredPosition = bossAnchor;
            _bossCard.Clicked += _ => BossClicked?.Invoke();

            _bossHp = UiFactory.CreateText(Theme, "BossHp", Container, "", 24f,
                UiPalette.Danger, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_bossHp.rectTransform, new Vector2(0.5f, 0.5f),
                bossAnchor + new Vector2(96f, 40f), new Vector2(120f, 30f));
        }

        // ------------------------------------------------------------------ rendering

        public void Render(ClientSnapshot snap, HashSet<int> reachableSteps)
        {
            if (!_built || snap == null) return;

            EnsurePawns(snap);
            for (int i = 0; i < snap.Players.Count && i < _pawns.Count; i++)
            {
                if (_pawnAnimating[i]) continue;
                int pos = Mathf.Clamp(snap.Players[i].Position, 0, _rules.BoardSteps);
                _pawns[i].anchoredPosition = _nodePos[pos] + PawnOffsets[i % PawnOffsets.Length];
            }

            _reachable = reachableSteps ?? new HashSet<int>();
            var innSteps = new HashSet<int>(_rules.InnSteps);
            for (int i = 0; i < _nodeImages.Length; i++)
            {
                var img = _nodeImages[i];
                if (img == null) continue;
                bool reachable = _reachable.Contains(i);
                img.color = reachable ? UiPalette.Gold
                    : innSteps.Contains(i) ? UiPalette.GoldDim
                    : UiPalette.PanelLight;
                if (_nodeButtons[i] != null)
                    _nodeButtons[i].interactable = reachable;
            }

            if (snap.Boss != null)
            {
                _bossCard.gameObject.SetActive(true);
                _bossCard.Bind(snap.Boss);
                int remaining = Mathf.Max(0, snap.BossHp - snap.Boss.MarkedDamage);
                _bossHp.text = $"{remaining} HP";
            }
            else
            {
                _bossCard.gameObject.SetActive(false);
                _bossHp.text = "";
            }
        }

        private void EnsurePawns(ClientSnapshot snap)
        {
            while (_pawns.Count < snap.Players.Count)
            {
                int i = _pawns.Count;
                var player = snap.Players[i];
                var pawn = UiFactory.CreateImage($"Pawn{i}", Container, Theme.Circle, UiPalette.PlayerColor(i));
                UiFactory.Place(pawn.rectTransform, new Vector2(0.5f, 0.5f), _nodePos[0], new Vector2(30f, 30f));
                var outline = pawn.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(1.5f, -1.5f);

                string initial = !string.IsNullOrEmpty(player.HeroId)
                    ? player.HeroId.Substring(0, 1).ToUpperInvariant()
                    : (i + 1).ToString();
                var label = UiFactory.CreateText(Theme, "Initial", pawn.transform, initial, 17f,
                    UiPalette.Background, TextAlignmentOptions.Center, FontStyles.Bold);
                UiFactory.Stretch(label.rectTransform);

                _pawns.Add(pawn.rectTransform);
                _pawnAnimating.Add(false);
            }
        }

        // ------------------------------------------------------------------ animation

        /// <summary>Steps a pawn node-to-node; pacing goes through the queue so clicks fast-forward.</summary>
        public IEnumerator AnimatePawn(int playerIndex, int from, int to, PresentationQueue queue)
        {
            if (!_built || playerIndex < 0 || playerIndex >= _pawns.Count) yield break;
            from = Mathf.Clamp(from, 0, _rules.BoardSteps);
            to = Mathf.Clamp(to, 0, _rules.BoardSteps);
            if (from == to) yield break;
            var pawn = _pawns[playerIndex];
            var offset = PawnOffsets[playerIndex % PawnOffsets.Length];

            _pawnAnimating[playerIndex] = true;
            int direction = to >= from ? 1 : -1;
            for (int step = from + direction; ; step += direction)
            {
                pawn.anchoredPosition = _nodePos[step] + offset;
                if (step == to) break;
                yield return queue.Wait(0.09f);
            }
            _pawnAnimating[playerIndex] = false;
        }

        public void SetBossGlow(bool on, Color color)
        {
            if (_built && _bossCard != null)
                _bossCard.SetGlow(on, color);
        }

        public void FlashBoss()
        {
            if (_built && _bossCard != null && _bossCard.gameObject.activeSelf && isActiveAndEnabled)
                StartCoroutine(Tween.Flash(_bossCard.Frame, UiPalette.Danger));
        }

        // ------------------------------------------------------------------ path

        /// <summary>
        /// Rounded-rect path: t=0 bottom-center → right along the bottom edge → up the
        /// right side → left along the top edge → t=1 top-center (boss).
        /// </summary>
        private static Vector2 Evaluate(float t)
        {
            float r = CornerRadius;
            float bottomRun = HalfW - r;
            float arc = r * Mathf.PI * 0.5f;
            float rightRun = 2f * HalfH - 2f * r;
            float topRun = HalfW - r;
            float total = bottomRun + arc + rightRun + arc + topRun;

            float d = Mathf.Clamp01(t) * total;

            if (d <= bottomRun)
                return new Vector2(d, -HalfH);
            d -= bottomRun;

            if (d <= arc)
            {
                float a = d / arc * Mathf.PI * 0.5f; // 0..90°
                var center = new Vector2(HalfW - r, -HalfH + r);
                return center + new Vector2(Mathf.Sin(a) * r, -Mathf.Cos(a) * r);
            }
            d -= arc;

            if (d <= rightRun)
                return new Vector2(HalfW, -HalfH + r + d);
            d -= rightRun;

            if (d <= arc)
            {
                float a = d / arc * Mathf.PI * 0.5f;
                var center = new Vector2(HalfW - r, HalfH - r);
                return center + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }
            d -= arc;

            return new Vector2(HalfW - r - d, HalfH);
        }
    }
}
