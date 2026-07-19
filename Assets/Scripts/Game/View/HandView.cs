using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Serialization;
using Pascension.Game.Presentation;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// The local player's hand — Slay-the-Spire interaction model:
    /// · drag a card UP past the play line and release to play it;
    /// · drag left/right to reorder — the fan opens a gap in realtime;
    /// · renders instantly from every snapshot (drawn cards start hidden and are
    ///   revealed one by one as their draw-flight lands);
    /// · playing removes the card immediately, even while animations continue.
    /// Visual order persists across renders (client-side only — order has no rules meaning).
    /// </summary>
    public sealed class HandView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Raised when a card is dragged past the play line and released.</summary>
        public event Action<int> PlayRequested;

        /// <summary>Optional steady per-card glow (SoI condition glow): id → color,
        /// null = none. Re-applied on every render; the response-window pulse and the
        /// drag play-intent glow take precedence while active.</summary>
        public Func<int, Color?> GlowResolver;

        private const float CardScale = 0.82f;
        private const float HoverScale = 1.05f;
        private const float HoverRaise = 96f;
        /// <summary>Container-local Y above which a released drag means "play it".
        /// Kept above the fan/hover/reorder band (~250 + hover raise) so horizontal
        /// reordering can never accidentally cross into a play.</summary>
        private const float PlayLineY = 390f;

        private readonly List<CardView> _cards = new List<CardView>();
        private readonly Dictionary<int, Pose> _posesById = new Dictionary<int, Pose>();
        // Invisible strip below each card, raycast-enabled ONLY while that card is
        // hovered: the hover lift vacates the card's bottom edge, and without the pad
        // the pointer there flip-flops enter/exit every frame (card bounces up/down).
        private readonly Dictionary<int, UnityEngine.UI.Image> _padsById = new Dictionary<int, UnityEngine.UI.Image>();
        private readonly List<int> _order = new List<int>();
        private readonly HashSet<int> _pulseIds = new HashSet<int>();
        private HashSet<int> _playable = new HashSet<int>();
        private Coroutine _pulse;
        private int _draggingId = -1;

        private struct Pose
        {
            public Vector2 Position;
            public float RotationZ;
        }

        public bool IsDragging => _draggingId >= 0;

        // ------------------------------------------------------------------ rendering

        /// <summary>Diff-update from a snapshot: existing views — including an active
        /// drag — stay alive; removed cards are destroyed; new cards are created (hidden
        /// if their draw-flight hasn't landed, revealed by RevealCard). Snapshots stream
        /// in constantly during play chains, so a rebuild here must NEVER reset a drag
        /// or the fan poses.</summary>
        public void Render(List<CardSnap> hand, HashSet<int> playableIds, HashSet<int> hiddenIds = null)
        {
            if (hand == null) return;
            _playable = playableIds ?? new HashSet<int>();

            var byId = new Dictionary<int, CardSnap>();
            foreach (var snap in hand) byId[snap.InstanceId] = snap;

            // Persistent visual order: keep known ids in their user-arranged order,
            // append newly drawn ids on the right.
            _order.RemoveAll(id => !byId.ContainsKey(id));
            foreach (var snap in hand)
                if (!_order.Contains(snap.InstanceId))
                    _order.Add(snap.InstanceId);

            bool wasEmpty = _cards.Count == 0;
            bool changed = false;

            // Destroy views for cards that left the hand.
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                var view = _cards[i];
                if (view != null && byId.ContainsKey(view.InstanceId)) continue;
                if (view != null)
                {
                    if (view.InstanceId == _draggingId)
                    {
                        UiLog.Log("Hand", $"dragged card #{_draggingId} left the hand — drag dropped");
                        _draggingId = -1;
                    }
                    _padsById.Remove(view.InstanceId);
                    Destroy(view.gameObject);
                }
                _cards.RemoveAt(i);
                changed = true;
            }

            // Create views for newly arrived cards.
            var have = new HashSet<int>();
            foreach (var view in _cards) have.Add(view.InstanceId);
            foreach (int id in _order)
            {
                if (have.Contains(id)) continue;
                var card = CardViewFactory.Create(Container, Theme, CardScale);
                card.Bind(byId[id]);
                if (hiddenIds != null && hiddenIds.Contains(id) && card.Group != null)
                {
                    card.Group.alpha = 0f;
                    card.Group.blocksRaycasts = false;
                }
                var drag = card.gameObject.AddComponent<HandCardDrag>();
                drag.Bind(this, card);
                card.Hovered += OnCardHovered;
                // Hover pad: full card width, extending below the bottom edge far
                // enough to cover the strip the hover lift vacates (96px ≈ 117 local).
                var pad = UiFactory.CreateImage("HoverPad", card.transform, null,
                    new Color(0f, 0f, 0f, 0.001f), raycast: false);
                pad.rectTransform.anchorMin = new Vector2(0f, 0f);
                pad.rectTransform.anchorMax = new Vector2(1f, 0f);
                pad.rectTransform.pivot = new Vector2(0.5f, 1f);
                pad.rectTransform.anchoredPosition = Vector2.zero;
                pad.rectTransform.sizeDelta = new Vector2(0f, 150f);
                _padsById[id] = pad;
                _cards.Add(card);
                changed = true;
            }

            _cards.Sort((a, b) => _order.IndexOf(a.InstanceId).CompareTo(_order.IndexOf(b.InstanceId)));

            foreach (var view in _cards)
            {
                if (view == null || view.InstanceId == _draggingId) continue;
                // Draw-flight pending: keep alpha 0 — SetGreyed writes the SAME CanvasGroup
                // alpha, so touching it here would un-hide the card before its flight lands.
                if (hiddenIds != null && hiddenIds.Contains(view.InstanceId)) continue;
                // Reveal safety: only draw-hidden views have blocksRaycasts off; once no
                // longer marked hidden the card must show (RefreshAll clears pending
                // reveals; a still-running RevealCard pop converges to the same state).
                if (view.Group != null && !view.Group.blocksRaycasts)
                    view.Group.blocksRaycasts = true;
                view.SetGreyed(!_playable.Contains(view.InstanceId));
            }

            // Untouched hands keep their poses (and any hover lift / active drag).
            if (changed)
                ApplyPoses(instant: wasEmpty || !isActiveAndEnabled);
            ApplyPulse();
            UiLog.Log("Hand", $"render: {_cards.Count} cards, playable={_playable.Count}, hidden={(hiddenIds?.Count ?? 0)}, changed={changed}");
        }

        /// <summary>Fade+pop a card in as its draw-flight lands.</summary>
        public void RevealCard(int instanceId)
        {
            var card = Find(instanceId);
            if (card == null || card.Group == null) return;
            card.Group.blocksRaycasts = true;
            if (isActiveAndEnabled)
                StartCoroutine(RevealPop(card));
            else
                card.Group.alpha = 1f;
            UiLog.Log("Hand", $"reveal card #{instanceId} ({card.DefId})");
        }

        private IEnumerator RevealPop(CardView card)
        {
            float t = 0f;
            const float duration = 0.14f;
            var baseScale = Vector3.one * CardScale;
            // Converge to the greyed-aware alpha (SetGreyed's 0.45/1), not a hard 1 —
            // an unplayable card must land dimmed.
            float target = _playable.Contains(card.InstanceId) ? 1f : 0.45f;
            while (t < duration && card != null && card.Group != null)
            {
                t += Time.deltaTime;
                float x = Mathf.Clamp01(t / duration);
                card.Group.alpha = x * target;
                card.transform.localScale = baseScale * (1f + 0.12f * Mathf.Sin(x * Mathf.PI));
                yield return null;
            }
            if (card != null && card.Group != null)
            {
                card.Group.alpha = target;
                card.transform.localScale = baseScale;
            }
        }

        /// <summary>Remove a played card immediately and close the fan around the gap —
        /// called optimistically the moment the play is submitted.</summary>
        public void RemoveCardOptimistic(int instanceId)
        {
            var card = Find(instanceId);
            if (card == null) return;
            _order.Remove(instanceId);
            _cards.Remove(card);
            _padsById.Remove(instanceId);
            Destroy(card.gameObject);
            if (_draggingId == instanceId) _draggingId = -1;
            ApplyPoses(instant: false);
            UiLog.Log("Hand", $"optimistic remove #{instanceId}, {_cards.Count} left");
        }

        /// <summary>Recompute the fan and move cards there (tweened unless instant).</summary>
        private void ApplyPoses(bool instant)
        {
            _posesById.Clear();
            int n = _cards.Count;
            float spacing = Mathf.Min(150f, n > 1 ? 720f / (n - 1) : 0f);
            float center = (n - 1) * 0.5f;

            for (int i = 0; i < n; i++)
            {
                var card = _cards[i];
                float offset = i - center;
                var pose = new Pose
                {
                    Position = new Vector2(offset * spacing, -Mathf.Abs(offset) * Mathf.Abs(offset) * 7f),
                    RotationZ = -offset * 3.5f
                };
                _posesById[card.InstanceId] = pose;

                if (card.InstanceId == _draggingId)
                    continue; // the dragged card follows the pointer and stays on top

                card.transform.SetSiblingIndex(i);

                if (instant || !isActiveAndEnabled)
                {
                    card.Rect.anchoredPosition = pose.Position;
                    card.transform.localRotation = Quaternion.Euler(0f, 0f, pose.RotationZ);
                }
                else
                {
                    StartCoroutine(Tween.Move(card.Rect, pose.Position, 0.14f));
                    StartCoroutine(Tween.RotateZ(card.transform, pose.RotationZ, 0.14f));
                }
            }

            // SetSiblingIndex on later cards displaces the dragged card downward —
            // re-assert its top z-order for every caller (Render, optimistic remove, reorder).
            if (_draggingId >= 0)
            {
                var dragged = Find(_draggingId);
                if (dragged != null) dragged.transform.SetAsLastSibling();
            }
        }

        // ------------------------------------------------------------------ dragging (called by HandCardDrag)

        internal void BeginDrag(CardView card)
        {
            _draggingId = card.InstanceId;
            if (_padsById.TryGetValue(card.InstanceId, out var pad) && pad != null)
                pad.raycastTarget = false;
            card.transform.SetAsLastSibling();
            card.transform.localRotation = Quaternion.identity;
            card.SetGlow(false);
            UiLog.Log("Drag", $"begin #{card.InstanceId} ({card.DefId})");
        }

        internal void Drag(CardView card, Vector2 containerLocal)
        {
            card.Rect.anchoredPosition = containerLocal;

            bool playIntent = containerLocal.y > PlayLineY && _playable.Contains(card.InstanceId);
            card.SetGlow(playIntent, UiPalette.Gold);

            if (containerLocal.y <= PlayLineY)
            {
                // Reorder: find the insertion index this X maps to among the other cards.
                int current = _order.IndexOf(card.InstanceId);
                int target = 0;
                foreach (int otherId in _order)
                {
                    if (otherId == card.InstanceId) continue;
                    if (_posesById.TryGetValue(otherId, out var pose) && pose.Position.x < containerLocal.x)
                        target++;
                }
                if (target != current)
                {
                    _order.Remove(card.InstanceId);
                    _order.Insert(target, card.InstanceId);
                    _cards.Sort((a, b) => _order.IndexOf(a.InstanceId).CompareTo(_order.IndexOf(b.InstanceId)));
                    ApplyPoses(instant: false);
                    card.transform.SetAsLastSibling();
                    UiLog.Log("Drag", $"reorder #{card.InstanceId} -> slot {target}");
                }
            }
        }

        internal void EndDrag(CardView card, Vector2 containerLocal)
        {
            int id = card.InstanceId;
            _draggingId = -1;
            card.SetGlow(false);

            if (containerLocal.y > PlayLineY && _playable.Contains(id))
            {
                UiLog.Log("Drag", $"release-to-PLAY #{id} ({card.DefId}) at y={containerLocal.y:0}");
                PlayRequested?.Invoke(id);
                return; // the play handler removes the card optimistically on accept
            }

            UiLog.Log("Drag", $"release #{id} at y={containerLocal.y:0} (return to fan)");
            ApplyPoses(instant: false);
            if (_posesById.TryGetValue(id, out var pose) && isActiveAndEnabled)
            {
                StartCoroutine(Tween.Move(card.Rect, pose.Position, 0.14f));
                StartCoroutine(Tween.RotateZ(card.transform, pose.RotationZ, 0.14f));
            }
            ApplyPulse(); // restore the steady/pulse glow the drag cleared
        }

        internal bool IsPlayable(int instanceId) => _playable.Contains(instanceId);

        /// <summary>Double-clicking a playable card plays it (drag remains the primary path).</summary>
        internal void RequestDoubleClickPlay(CardView card)
        {
            if (card == null || !_playable.Contains(card.InstanceId)) return;
            UiLog.Log("Play", $"double-click play #{card.InstanceId} ({card.DefId})");
            PlayRequested?.Invoke(card.InstanceId);
        }

        // ------------------------------------------------------------------ misc

        /// <summary>Pulse-glow a set of cards (response window: playable instants).</summary>
        public void SetPulse(HashSet<int> instanceIds)
        {
            _pulseIds.Clear();
            if (instanceIds != null)
                foreach (int id in instanceIds)
                    _pulseIds.Add(id);
            ApplyPulse();
        }

        private void ApplyPulse()
        {
            foreach (var card in _cards)
            {
                if (card == null || card.InstanceId == _draggingId) continue;
                if (_pulseIds.Contains(card.InstanceId))
                {
                    card.SetGlow(true);
                    continue;
                }
                // No pulse: fall back to the steady condition glow, if any.
                var steady = GlowResolver?.Invoke(card.InstanceId);
                if (steady.HasValue) card.SetGlow(true, steady.Value);
                else card.SetGlow(false);
            }
            if (_pulse == null && _pulseIds.Count > 0 && isActiveAndEnabled)
                _pulse = StartCoroutine(PulseLoop());
        }

        private IEnumerator PulseLoop()
        {
            while (_pulseIds.Count > 0)
            {
                float alpha = 0.45f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 3.5f));
                foreach (var card in _cards)
                    if (card != null && _pulseIds.Contains(card.InstanceId))
                        card.SetGlowAlpha(alpha);
                yield return null;
            }
            _pulse = null;
        }

        /// <summary>Hide the real card while an animation proxy represents it.</summary>
        public RectTransform HideCard(int instanceId)
        {
            var card = Find(instanceId);
            if (card == null) return null;
            if (card.Group != null) card.Group.alpha = 0f;
            return card.Rect;
        }

        public RectTransform CardRect(int instanceId) => Find(instanceId)?.Rect;

        private CardView Find(int instanceId)
        {
            foreach (var card in _cards)
                if (card != null && card.InstanceId == instanceId)
                    return card;
            return null;
        }

        private void OnCardHovered(CardView card, bool entered)
        {
            if (IsDragging || card == null) return;
            if (!_posesById.TryGetValue(card.InstanceId, out var pose)) return;
            // The pad keeps the pointer inside the card's hierarchy while lifted, so
            // resting the mouse where the (unlifted) bottom edge was doesn't oscillate.
            if (_padsById.TryGetValue(card.InstanceId, out var pad) && pad != null)
                pad.raycastTarget = entered;
            if (entered)
            {
                card.transform.SetAsLastSibling();
                card.Rect.anchoredPosition = pose.Position + new Vector2(0f, HoverRaise);
                card.transform.localRotation = Quaternion.identity;
                card.transform.localScale = Vector3.one * (CardScale * HoverScale);
            }
            else
            {
                card.Rect.anchoredPosition = pose.Position;
                card.transform.localRotation = Quaternion.Euler(0f, 0f, pose.RotationZ);
                card.transform.localScale = Vector3.one * CardScale;
                card.transform.SetSiblingIndex(Mathf.Clamp(_order.IndexOf(card.InstanceId), 0, Container.childCount - 1));
            }
        }

    }
}
