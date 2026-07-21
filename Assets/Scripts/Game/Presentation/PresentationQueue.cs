using System;
using System.Collections;
using System.Collections.Generic;
using Pascension.Engine.Events;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// FIFO of GameEvent batches played sequentially by a coroutine. The GameScreen
    /// assigns <see cref="EventPlayer"/> (per-event animation + log line) and listens to
    /// <see cref="Drained"/> to refresh views from the latest snapshot afterwards
    /// (snapshot-after-animation model). Consecutive CardDrawn events by the same player
    /// coalesce into one CoalescedDrawEvent. Any mouse click fast-forwards the current batch.
    /// </summary>
    public sealed class PresentationQueue : MonoBehaviour
    {
        /// <summary>Plays one event; may return null for "nothing to show".</summary>
        public Func<GameEvent, IEnumerator> EventPlayer;

        /// <summary>Raised whenever the queue transitions to empty.</summary>
        public event Action Drained;

        private readonly Queue<List<GameEvent>> _batches = new Queue<List<GameEvent>>();
        private bool _playing;
        private bool _fastForward;

        /// <summary>When more than this many events are waiting (alt-tab catch-up:
        /// runInBackground keeps the host resolving bot turns while unfocused), the
        /// queue fast-forwards continuously until the backlog is back under control —
        /// seconds of snap-through instead of minutes of animated replay.</summary>
        private const int BacklogFastForwardThreshold = 20;

        private int PendingEventCount()
        {
            int count = 0;
            foreach (var batch in _batches)
                count += batch.Count;
            return count;
        }

        public bool IsIdle => !_playing && _batches.Count == 0;

        /// <summary>True while the current batch is being fast-forwarded (per-frame
        /// animations should snap to their end state when this flips on).</summary>
        public bool FastForwarding => _fastForward;

        public void Enqueue(List<GameEvent> batch)
        {
            if (batch == null || batch.Count == 0) return;
            _batches.Enqueue(batch);
            if (!_playing && isActiveAndEnabled)
                StartCoroutine(PlayLoop());
        }

        private void Update()
        {
            if (!_playing) return;
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                _fastForward = true;
            if (!_fastForward && PendingEventCount() > BacklogFastForwardThreshold)
                _fastForward = true;
        }

        /// <summary>
        /// Pacing helper for event players: waits scaled seconds, returning immediately
        /// while the current batch is being fast-forwarded.
        /// </summary>
        public IEnumerator Wait(float seconds)
        {
            float t = 0f;
            while (t < seconds && !_fastForward)
            {
                t += Time.deltaTime;
                yield return null;
            }
        }

        private IEnumerator PlayLoop()
        {
            _playing = true;
            while (_batches.Count > 0)
            {
                var batch = Coalesce(_batches.Dequeue());
                for (int i = 0; i < batch.Count; i++)
                {
                    var player = EventPlayer;
                    if (player == null) continue;
                    IEnumerator anim = null;
                    try
                    {
                        anim = player(batch[i]);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                    if (anim != null)
                        yield return anim;
                }
                _fastForward = false;
            }
            _playing = false;
            Drained?.Invoke();
        }

        private static List<GameEvent> Coalesce(List<GameEvent> batch)
        {
            var result = new List<GameEvent>(batch.Count);
            int i = 0;
            while (i < batch.Count)
            {
                if (batch[i] is CardDrawnEvent first)
                {
                    int count = 1;
                    while (i + count < batch.Count &&
                           batch[i + count] is CardDrawnEvent next &&
                           next.PlayerIndex == first.PlayerIndex)
                        count++;
                    if (count > 1)
                    {
                        var coalesced = new CoalescedDrawEvent { PlayerIndex = first.PlayerIndex, Count = count };
                        for (int k = 0; k < count; k++)
                            coalesced.InstanceIds.Add(((CardDrawnEvent)batch[i + k]).InstanceId);
                        result.Add(coalesced);
                        i += count;
                        continue;
                    }
                }

                // Shards of Infinity draws coalesce the same way (redraw-5 at cleanup).
                if (batch[i] is Shards.Engine.ShardsCardDrawnEvent sfirst)
                {
                    int count = 1;
                    while (i + count < batch.Count &&
                           batch[i + count] is Shards.Engine.ShardsCardDrawnEvent snext &&
                           snext.PlayerIndex == sfirst.PlayerIndex)
                        count++;
                    if (count > 1)
                    {
                        var coalesced = new CoalescedDrawEvent { PlayerIndex = sfirst.PlayerIndex, Count = count };
                        for (int k = 0; k < count; k++)
                            coalesced.InstanceIds.Add(((Shards.Engine.ShardsCardDrawnEvent)batch[i + k]).InstanceId);
                        result.Add(coalesced);
                        i += count;
                        continue;
                    }
                }

                result.Add(batch[i]);
                i++;
            }
            return result;
        }
    }
}
