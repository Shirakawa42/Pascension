namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// Configuration for the Ollama bot. Plain data, constructor-injected into
    /// <see cref="OllamaBot"/>; persistence (player prefs / lobby UI) lives in the UI layer.
    /// </summary>
    public sealed class OllamaSettings
    {
        /// <summary>Ollama model name as listed by /api/tags (e.g. "llama3.1:8b").</summary>
        public string Model { get; }

        /// <summary>Enable the model's thinking mode (slower, usually stronger play).</summary>
        public bool Think { get; }

        /// <summary>Base URL of the local Ollama server.</summary>
        public string BaseUrl { get; }

        public OllamaSettings(string model, bool think = false, string baseUrl = OllamaClient.DefaultBaseUrl)
        {
            Model = model;
            Think = think;
            BaseUrl = baseUrl;
        }
    }
}
