using System.Collections.Generic;

namespace Pascension.Net
{
    /// <summary>A seat the game is waiting on (its human disconnected mid-game).</summary>
    public sealed class PausedSeat
    {
        public int PlayerIndex;
        public string Name;
    }

    /// <summary>
    /// Pause state pushed to every session while a match is frozen: who we are waiting
    /// for, the game ID to re-share with them, and whether the receiver may kick
    /// (host only). Not hidden information — broadcast identically to all players.
    /// </summary>
    public sealed class PauseInfo
    {
        public bool Paused;
        public List<PausedSeat> Waiting = new();
        /// <summary>The game ID a rejoiner needs (shown on the pause overlay).</summary>
        public string JoinCode;
        /// <summary>True only on the host's copy — enables "replace with bot".</summary>
        public bool CanKick;
    }
}
