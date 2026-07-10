namespace Pascension.Engine.Events
{
    /// <summary>
    /// An observable fact about the game, emitted by every state mutation. Events are
    /// sequence-numbered and immutable once appended. Hidden information is stripped
    /// per-viewer via <see cref="RedactFor"/> — the ONLY sanctioned redaction point,
    /// used by EventLog.FilterFor and the snapshot builder.
    /// </summary>
    public abstract class GameEvent
    {
        public int Seq;

        /// <summary>Return this event as the given player may see it (default: fully public).</summary>
        public virtual GameEvent RedactFor(int viewerIndex) => this;
    }
}
