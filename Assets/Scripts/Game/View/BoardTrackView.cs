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

        // (Path geometry lives in Evaluate — a serpentine in the right-edge strip.)

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
            new Vector2(-10f, 10f), new Vector2(10f, 10f),
            new Vector2(-10f, -10f), new Vector2(10f, -10f)
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
                _nodePos[i] = Evaluate((float)i / steps, steps);

            var innSteps = new HashSet<int>(rules.InnSteps);

            for (int i = 0; i <= steps; i++)
            {
                bool inn = innSteps.Contains(i);
                bool isBoss = i == steps;
                if (isBoss) continue; // the boss card marks the final step

                float size = inn ? 38f : i == 0 ? 32f : 24f;
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

            // Boss at the top of the right strip; the final step's pawns stand at its feet.
            var bossAnchor = new Vector2(825f, 330f);
            _bossCard = CardViewFactory.Create(Container, Theme, 0.55f);
            _bossCard.Rect.anchoredPosition = bossAnchor;
            _bossCard.Clicked += _ => BossClicked?.Invoke();

            _bossHp = UiFactory.CreateText(Theme, "BossHp", Container, "", 24f,
                UiPalette.Danger, TextAlignmentOptions.Center, FontStyles.Bold);
            UiFactory.Place(_bossHp.rectTransform, new Vector2(0.5f, 0.5f),
                new Vector2(825f, 428f), new Vector2(160f, 30f));
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
                _bossHp.color = remaining < snap.BossHp ? UiPalette.WoundedRed : Color.white;
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

        /// <summary>Anchor rect of the boss card (for stack-target arrows). Null before Init.</summary>
        public RectTransform BossRect => _built && _bossCard != null ? _bossCard.Rect : null;

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
        /// Vertical serpentine in the right-edge strip: steps snake bottom→top in rows of
        /// five (even rows left→right, odd rows right→left); the final step sits above
        /// the last row, under the boss card.
        /// </summary>
        private static Vector2 Evaluate(float t, int steps)
        {
            int i = Mathf.RoundToInt(Mathf.Clamp01(t) * steps);
            if (i >= steps)
                return new Vector2(825f, 240f); // final step: pawns stand at the boss's feet

            int row = i / 5;
            int col = i % 5;
            float x = row % 2 == 0 ? 745f + col * 40f : 905f - col * 40f;
            float y = -480f + row * 78f;
            return new Vector2(x, y);
        }
    }
}
