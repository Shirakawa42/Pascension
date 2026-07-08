using System.Collections.Generic;

namespace Pascension.Engine.Events
{
    /// <summary>
    /// Ordered, append-only record of everything that happened. Per-player filtered
    /// views (with hidden info redacted) are produced here and ONLY here.
    /// </summary>
    public sealed class EventLog
    {
        private readonly List<GameEvent> _events = new();

        public int Count => _events.Count;

        public GameEvent this[int index] => _events[index];

        public void Append(GameEvent e)
        {
            e.Seq = _events.Count;
            _events.Add(e);
        }

        /// <summary>Events from seq (inclusive), redacted for the given viewer. viewerIndex -1 = omniscient (host log/tests).</summary>
        public List<GameEvent> FilterFor(int viewerIndex, int fromSeq = 0)
        {
            var result = new List<GameEvent>();
            for (int i = fromSeq; i < _events.Count; i++)
                result.Add(viewerIndex < 0 ? _events[i] : _events[i].RedactFor(viewerIndex));
            return result;
        }
    }
}
