namespace Pascension.Net
{
    /// <summary>
    /// JSON blob carried in NetworkConfig.ConnectionData and parsed during connection
    /// approval. Keep it tiny — NGO limits connection-request payload size.
    /// </summary>
    public sealed class ConnectionPayload
    {
        public string ClientGuid;
        public string PlayerName;

        /// <summary>Application.version of the connecting build — checked at approval
        /// (wire format is only guaranteed between identical builds). Null from
        /// pre-update clients, which the gate treats as "0" and rejects.</summary>
        public string GameVersion;
    }
}
