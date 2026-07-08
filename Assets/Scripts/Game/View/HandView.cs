using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Serialization;
using UnityEngine;

namespace Pascension.Game.View
{
    /// <summary>
    /// The local player's hand: fanned arc at the bottom center. Hover raises/zooms a
    /// card; click reports the instance id upward (GameScreen decides legality by
    /// matching a PlayCardAction). Unplayable cards are greyed.
    /// </summary>
    public sealed class HandView : MonoBehaviour
    {
        public UiTheme Theme;
        public RectTransform Container;

        /// <summary>Raised with the clicked card's instance id.</summary>
        public event Action<int> CardClicked;

        private const float CardScale = 0.82f;
        private const float HoverScale = 1.05f;
        private const float HoverRaise = 96f;

        private readonly List<CardView> _cards = new List<CardView>();
        private readonly Dictionary<CardView, Pose> _poses = new Dictionary<CardView, Pose>();
        private readonly HashSet<int> _pulseIds = new HashSet<int>();
        private Coroutine _pulse;

        private struct Pose
        {
            public Vector2 Position;
            public float RotationZ;
            public int Sibling;
        }

        public void Render(List<CardSnap> hand, HashSet<int> playableIds)
        {
            Clear();
            if (hand == null) return;

            int n = hand.Count;
            float spacing = Mathf.Min(150f, n > 1 ? 720f / (n - 1) : 0f);
            float center = (n - 1) * 0.5f;

            for (int i = 0; i < n; i++)
            {
                var card = CardViewFactory.Create(Container, Theme, CardScale);
                card.Bind(hand[i]);

                float offset = i - center;
                var pos = new Vector2(offset * spacing, -Mathf.Abs(offset) * Mathf.Abs(offset) * 7f);
                float rot = -offset * 3.5f;
                card.Rect.anchoredPosition = pos;
                card.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
                _poses[card] = new Pose { Position = pos, RotationZ = rot, Sibling = card.transform.GetSiblingIndex() };

                bool playable = playableIds != null && playableIds.Contains(card.InstanceId);
                card.SetGreyed(!playable);

                card.Clicked += OnCardClicked;
                card.Hovered += OnCardHovered;
                _cards.Add(card);
            }
            ApplyPulse();
        }

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
                card.SetGlow(_pulseIds.Contains(card.InstanceId));
            if (_pulse == null && isActiveAndEnabled)
                _pulse = StartCoroutine(PulseLoop());
        }

        private IEnumerator PulseLoop()
        {
            while (true)
            {
                float a = 0.45f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 3.5f));
                foreach (var card in _cards)
                    if (_pulseIds.Contains(card.InstanceId))
                        card.SetGlowAlpha(a);
                yield return null;
            }
        }

        private void OnCardClicked(CardView card) => CardClicked?.Invoke(card.InstanceId);

        private void OnCardHovered(CardView card, bool entered)
        {
            if (!_poses.TryGetValue(card, out var pose)) return;
            if (entered)
            {
                card.transform.SetAsLastSibling();
                card.Rect.anchoredPosition = pose.Position + new Vector2(0f, HoverRaise);
                card.transform.localRotation = Quaternion.identity;
                card.transform.localScale = Vector3.one * (CardScale * HoverScale);
            }
            else
            {
                card.transform.SetSiblingIndex(pose.Sibling);
                card.Rect.anchoredPosition = pose.Position;
                card.transform.localRotation = Quaternion.Euler(0f, 0f, pose.RotationZ);
                card.transform.localScale = Vector3.one * CardScale;
            }
        }

        private void Clear()
        {
            foreach (var card in _cards)
                if (card != null)
                    Destroy(card.gameObject);
            _cards.Clear();
            _poses.Clear();
        }
    }
}
