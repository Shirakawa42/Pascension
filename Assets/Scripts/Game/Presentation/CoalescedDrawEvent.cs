using System.Collections.Generic;
using Pascension.Engine.Events;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Presentation-only stand-in for a run of consecutive CardDrawnEvents by the same
    /// player. Never serialized, never enters the engine — created by the
    /// PresentationQueue's coalescing pass so "draw 5" animates as one staggered volley.
    /// InstanceIds drive the per-card reveal as each flight lands (viewer's own draws;
    /// redacted opponent draws still carry their instance ids).
    /// </summary>
    public sealed class CoalescedDrawEvent : GameEvent
    {
        public int PlayerIndex;
        public int Count;
        public readonly List<int> InstanceIds = new List<int>();
    }
}
