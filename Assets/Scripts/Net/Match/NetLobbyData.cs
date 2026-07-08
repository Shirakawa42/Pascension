using System.Collections.Generic;
using Pascension.Engine.Core;

namespace Pascension.Net
{
    /// <summary>
    /// Host-side handoff from the Lobby scene to the Game scene (statics survive the
    /// scene switch; the lobby's in-scene object does not). Also the authority the
    /// approval callback consults for mid-game reconnects. Unused on pure clients.
    /// </summary>
    public static class NetLobbyData
    {
        /// <summary>True from the moment the host commits to starting until shutdown/new lobby.</summary>
        public static bool MatchRunning;

        /// <summary>Built by LobbyNetBehaviour.HostStartGame via ContentRegistry.StandardConfig.</summary>
        public static GameConfig Config;

        public static List<SeatAssignment> Seats = new();

        public static void ResetForLobby()
        {
            MatchRunning = false;
            Config = null;
            Seats.Clear();
        }

        /// <summary>Player index for a persistent identity, or -1. Bots/host never match.</summary>
        public static int FindSeatByGuid(string clientGuid)
        {
            if (string.IsNullOrEmpty(clientGuid)) return -1;
            foreach (var seat in Seats)
                if (seat.Kind == LobbySlotKind.Human && !seat.IsHostHuman && seat.ClientGuid == clientGuid)
                    return seat.PlayerIndex;
            return -1;
        }
    }
}
