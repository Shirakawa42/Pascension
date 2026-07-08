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
    }
}
