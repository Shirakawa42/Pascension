using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Pascension.Editor.ArtPipeline
{
    /// <summary>
    /// Blocking HTTP wrapper around the local ComfyUI server (editor tooling only —
    /// synchronous calls inside a progress-bar loop are fine here). Loads the Anima
    /// API-format workflow template, patches prompt/size/seed/output nodes, submits,
    /// polls history, downloads the PNG into Assets and imports it.
    /// See .claude/skills/art-pipeline/SKILL.md for the pipeline contract.
    /// </summary>
    public sealed class ComfyUiClient : IDisposable
    {
        public const string BaseUrl = "http://127.0.0.1:8188";
        public const string TemplatePath = "Assets/Scripts/Editor/ArtPipeline/anima_card_workflow_api.json";
        public const string LaunchHint = "ComfyUI is not running. Launch it with F:\\comfyuiauto\\Windows_Run_GPU_venv_p312_cu130.bat";

        // Prompting standards — copied EXACTLY from the art-pipeline skill.
        public const string StylePrefix =
            "masterpiece, best quality, score_7, safe, year 2025, newest, highres, " +
            "painterly, fantasy, detailed illustration, dramatic lighting, trading card game art, ";
        public const string Negative =
            "worst quality, low quality, score_1, score_2, score_3, blurry, jpeg artifacts, watermark, text, signature, artist name";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(5);

        private readonly HttpClient _http;
        private readonly string _clientId = Guid.NewGuid().ToString("N");

        public ComfyUiClient()
        {
            _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>GET /system_stats — false when the server is unreachable.</summary>
        public bool IsServerUp()
        {
            try
            {
                using var response = _http.GetAsync("/system_stats").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generate one image and import it at job.OutputPath. Blocks until done
        /// (polls every 2 s, 5 min timeout). <paramref name="keepWaiting"/> is invoked
        /// each poll; return false to cancel (best-effort /interrupt is sent).
        /// Throws on any failure — callers surface the message.
        /// </summary>
        public void Generate(ArtJob job, Func<bool> keepWaiting = null)
        {
            string promptId = SubmitPrompt(job);
            var (filename, subfolder, type) = WaitForOutput(promptId, keepWaiting);
            byte[] png = Download(filename, subfolder, type);

            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath));
            File.WriteAllBytes(job.OutputPath, png);
            AssetDatabase.ImportAsset(job.OutputPath, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>Deterministic seed: FNV-1a of the id, offset by the re-roll salt.</summary>
        public static long StableSeed(string id, int salt)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in id)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (long)hash + salt;
            }
        }

        private string SubmitPrompt(ArtJob job)
        {
            var graph = JObject.Parse(File.ReadAllText(TemplatePath));
            graph["4"]["inputs"]["text"] = StylePrefix + job.Prompt;
            graph["5"]["inputs"]["text"] = Negative;
            graph["6"]["inputs"]["width"] = job.Width;
            graph["6"]["inputs"]["height"] = job.Height;
            graph["7"]["inputs"]["seed"] = StableSeed(job.Id, job.SeedSalt);
            graph["9"]["inputs"]["filename_prefix"] = "pascension/" + job.Id;

            var body = new JObject { ["prompt"] = graph, ["client_id"] = _clientId };
            using var content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = _http.PostAsync("/prompt", content).GetAwaiter().GetResult();
            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ComfyUI rejected the prompt for '{job.Id}': {json}");

            string promptId = JObject.Parse(json).Value<string>("prompt_id");
            if (string.IsNullOrEmpty(promptId))
                throw new InvalidOperationException($"ComfyUI returned no prompt_id for '{job.Id}': {json}");
            return promptId;
        }

        private (string filename, string subfolder, string type) WaitForOutput(string promptId, Func<bool> keepWaiting)
        {
            var deadline = DateTime.UtcNow + GenerationTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (keepWaiting != null && !keepWaiting())
                {
                    TryInterrupt();
                    throw new OperationCanceledException("Generation cancelled");
                }
                Thread.Sleep(PollInterval);

                string json = _http.GetStringAsync($"/history/{promptId}").GetAwaiter().GetResult();
                var history = JObject.Parse(json);
                if (!(history[promptId] is JObject entry) || !(entry["outputs"] is JObject outputs))
                    continue;

                foreach (var node in outputs.Properties())
                {
                    if (!(node.Value["images"] is JArray images) || images.Count == 0)
                        continue;
                    var image = (JObject)images[0];
                    return (image.Value<string>("filename"),
                            image.Value<string>("subfolder") ?? "",
                            image.Value<string>("type") ?? "output");
                }
            }
            throw new TimeoutException($"ComfyUI did not finish prompt {promptId} within {GenerationTimeout.TotalMinutes:0} minutes");
        }

        private byte[] Download(string filename, string subfolder, string type)
        {
            string url = $"/view?filename={Uri.EscapeDataString(filename)}" +
                         $"&subfolder={Uri.EscapeDataString(subfolder)}" +
                         $"&type={Uri.EscapeDataString(type)}";
            return _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }

        private void TryInterrupt()
        {
            try
            {
                using var _ = _http.PostAsync("/interrupt", new StringContent("")).GetAwaiter().GetResult();
            }
            catch
            {
                // best effort only
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
