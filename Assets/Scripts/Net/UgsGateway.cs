using System;
using System.Threading.Tasks;
using Unity.Networking.Transport.Relay;
using Unity.Services.Core;
using Unity.Services.Relay;
using UnityEngine;

namespace Pascension.Net
{
    /// <summary>
    /// A failure the player can be shown verbatim — UgsGateway normalizes every SDK
    /// exception into one of these so UI never displays raw service errors or hangs.
    /// </summary>
    public sealed class UgsException : Exception
    {
        public UgsException(string userMessage, Exception inner = null) : base(userMessage, inner) { }
    }

    /// <summary>
    /// Relay only: allocations and join codes (the game ID players share — no port
    /// forwarding, no matchmaking). Authentication lives in <see cref="AccountService"/>,
    /// which NetLauncher awaits before any call lands here.
    /// </summary>
    public static class UgsGateway
    {
        /// <summary>DTLS: encrypted UDP — the recommended Relay connection type for desktop.</summary>
        private const string ConnectionType = "dtls";

        /// <summary>Host: create a Relay allocation and mint its join code (the game ID).</summary>
        public static async Task<(string joinCode, RelayServerData data)> CreateAllocationAsync(int maxConnections)
        {
            try
            {
                var allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                return (joinCode.ToUpperInvariant(), new RelayServerData(allocation, ConnectionType));
            }
            catch (Exception e)
            {
                throw Normalize(e, joining: false);
            }
        }

        /// <summary>Client: join the host's allocation by game ID.</summary>
        public static async Task<RelayServerData> JoinAllocationAsync(string joinCode)
        {
            joinCode = (joinCode ?? "").Trim().ToUpperInvariant();
            if (joinCode.Length == 0)
                throw new UgsException("Enter a game ID first.");
            try
            {
                var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                return new RelayServerData(allocation, ConnectionType);
            }
            catch (Exception e)
            {
                throw Normalize(e, joining: true);
            }
        }

        private static UgsException Normalize(Exception e, bool joining)
        {
            if (e is UgsException already) return already;
            Debug.LogWarning("[Net] UGS failure: " + e); // full detail for diagnosis; UI shows the friendly text
            if (e is RelayServiceException relay)
            {
                if (relay.Reason == RelayExceptionReason.JoinCodeNotFound ||
                    relay.Reason == RelayExceptionReason.InvalidRequest)
                    return new UgsException("Invalid or expired game ID.", e);
                if (relay.Reason == RelayExceptionReason.NetworkError)
                    return new UgsException("No internet connection.", e);
                return new UgsException("Relay error: " + relay.Reason, e);
            }
            if (e is RequestFailedException failed)
            {
                if (failed.ErrorCode == CommonErrorCodes.TransportError ||
                    failed.ErrorCode == CommonErrorCodes.Timeout)
                    return new UgsException("No internet connection.", e);
                return new UgsException("Online services unavailable (is the project linked to Unity Cloud with Relay enabled?)", e);
            }
            return new UgsException(joining
                ? "Could not join the game — check the game ID and your connection."
                : "Could not reach online services — check your connection.", e);
        }
    }
}
