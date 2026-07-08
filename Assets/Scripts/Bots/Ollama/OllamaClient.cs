using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// Minimal HTTP client for a local Ollama server: model listing (/api/tags) and
    /// non-streaming structured chat (/api/chat). Pure C# — usable from headless tools
    /// and the Unity player alike. Callers own timeouts via CancellationTokens.
    /// </summary>
    public sealed class OllamaClient : IDisposable
    {
        public const string DefaultBaseUrl = "http://127.0.0.1:11434";

        private readonly HttpClient _http;

        public OllamaClient(string baseUrl = DefaultBaseUrl)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        /// <summary>Names of all locally available models (GET /api/tags).</summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken cancel = default)
        {
            using var response = await _http.GetAsync("/api/tags", cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var obj = JObject.Parse(json);
            var names = new List<string>();
            if (obj["models"] is JArray models)
                foreach (var model in models)
                {
                    string name = model.Value<string>("name");
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            return names;
        }

        /// <summary>
        /// One-shot chat completion (POST /api/chat, stream:false). <paramref name="format"/>
        /// is a JSON-schema object constraining the reply; returns message.content verbatim.
        /// </summary>
        public async Task<string> ChatAsync(string model, string systemPrompt, string userPrompt,
            bool think, object format, CancellationToken cancel)
        {
            var body = new JObject
            {
                ["model"] = model,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                },
                ["stream"] = false,
                ["think"] = think,
                ["options"] = new JObject { ["temperature"] = 0.2 }
            };
            if (format != null)
                body["format"] = format as JToken ?? JToken.FromObject(format);

            using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("/api/chat", content, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var obj = JObject.Parse(json);
            string reply = obj["message"]?["content"]?.ToString();
            if (reply == null)
                throw new InvalidOperationException("Ollama chat response had no message.content");
            return reply;
        }

        public void Dispose() => _http.Dispose();
    }
}
