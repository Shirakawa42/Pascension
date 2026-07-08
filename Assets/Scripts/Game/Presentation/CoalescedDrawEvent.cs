using Pascension.Engine.Events;

namespace Pascension.Game.Presentation
{
    /// <summary>
    /// Presentation-only stand-in for a run of consecutive CardDrawnEvents by the same
    /// player. Never serialized, never enters the engine — created by the
    /// PresentationQueue's coalescing pass so "draw 5" animates once.
    /// </summary>
    public sealed class CoalescedDrawEvent : GameEvent
    {
        public int PlayerIndex;
        public int Count;
    }
}
