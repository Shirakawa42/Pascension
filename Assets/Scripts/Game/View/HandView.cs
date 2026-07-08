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

        private const float CardScale = 0.82f;
        private const float HoverScale = 1.05f;
        private const float HoverRaise = 96f;
        /// <summary>Container-local Y above which a released drag means "play it".
        /// Kept well above the fan/hover/reorder band (~250) so horizontal reordering
        /// can never accidentally cross into a play.</summary>
        private const float PlayLineY = 450f;

        private readonly List<CardView> _cards = new List<CardView>();
        private readonly Dictionary<int, Pose> _posesById = new Dictionary<int, Pose>();
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

        /// <summary>Rebuild from a snapshot. hiddenIds = cards whose draw-flight hasn't
        /// landed yet (rendered invisible, revealed by RevealCard).</summary>
        public void Render(List<CardSnap> hand, HashSet<int> playableIds, HashSet<int> hiddenIds = null)
        {
            if (_draggingId >= 0)
                UiLog.Log("Hand", $"render interrupted an active drag of #{_draggingId} — drag dropped");
            Clear(); // resets _draggingId: the dragged view is destroyed with the rest
            if (hand == null) return;

            _playable = playableIds ?? new HashSet<int>();

            // Persistent visual order: keep known ids in their user-arranged order,
            // append newly drawn ids on the right.
            var inHand = new HashSet<int>();
            foreach (var snap in hand) inHand.Add(snap.InstanceId);
            _order.RemoveAll(id => !inHand.Contains(id));
            foreach (var snap in hand)
                if (!_order.Contains(snap.InstanceId))
                    _order.Add(snap.InstanceId);

            var byId = new Dictionary<int, CardSnap>();
            foreach (var snap in hand) byId[snap.InstanceId] = snap;

            foreach (int id in _order)
            {
                var card = CardViewFactory.Create(Container, Theme, CardScale);
                card.Bind(byId[id]);

                bool playable = _playable.Contains(id);
                card.SetGreyed(!playable);

                if (hiddenIds != null && hiddenIds.Contains(id) && card.Group != null)
                {
                    card.Group.alpha = 0f;
                    card.Group.blocksRaycasts = false;
                }

                var drag = card.gameObject.AddComponent<HandCardDrag>();
                drag.Bind(this, card);

                card.Hovered += OnCardHovered;
                _cards.Add(card);
            }

            ApplyPoses(instant: true);
            ApplyPulse();
            UiLog.Log("Hand", $"render: {_cards.Count} cards, playable={_playable.Count}, hidden={(hiddenIds?.Count ?? 0)}");
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
            while (t < duration && card != null && card.Group != null)
            {
                t += Time.deltaTime;
                float x = Mathf.Clamp01(t / duration);
                card.Group.alpha = x;
                card.transform.localScale = baseScale * (1f + 0.12f * Mathf.Sin(x * Mathf.PI));
                yield return null;
            }
            if (card != null && card.Group != null)
            {
                card.Group.alpha = 1f;
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
                card.transform.SetSiblingIndex(i);

                if (card.InstanceId == _draggingId)
                    continue; // the dragged card follows the pointer

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
        }

        // ------------------------------------------------------------------ dragging (called by HandCardDrag)

        internal void BeginDrag(CardView card)
        {
            _draggingId = card.InstanceId;
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
        }

        internal bool IsPlayable(int instanceId) => _playable.Contains(instanceId);

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
                if (card.InstanceId != _draggingId)
                    card.SetGlow(_pulseIds.Contains(card.InstanceId));
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

        private void Clear()
        {
            foreach (var card in _cards)
                if (card != null)
                    Destroy(card.gameObject);
            _cards.Clear();
            _posesById.Clear();
            _draggingId = -1;
        }
    }
}
