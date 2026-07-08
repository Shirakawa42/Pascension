using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;

namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// LLM seat agent: prompts a local Ollama model with the masked snapshot and a
    /// numbered action menu, parses the structured JSON reply, and submits the chosen
    /// legal action. Any failure (timeout, HTTP, parse, out-of-range) falls back to
    /// <see cref="SnapshotFallbackPolicy"/>. submit fires on a worker thread — wire it
    /// to GameHost.SubmitAsync.
    /// </summary>
    public sealed class OllamaBot : IAsyncAgent, IDisposable
    {
        public const int TimeoutSeconds = 25;

        private readonly OllamaClient _client;
        private readonly OllamaSettings _settings;
        private readonly bool _ownsClient;
        private readonly object _gate = new();
        private CancellationTokenSource _cts;

        public OllamaBot(OllamaSettings settings, OllamaClient client = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ownsClient = client == null;
            _client = client ?? new OllamaClient(settings.BaseUrl);
        }

        public void RequestInput(ClientSnapshot view, PendingSnap pending, Action<PlayerAction> submit)
        {
            CancellationToken cancel;
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                cancel = _cts.Token;
            }
            _ = Task.Run(() => RunAsync(view, pending, submit, cancel), CancellationToken.None);
        }

        /// <summary>Abandon the in-flight request; its submit callback will not fire.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                _cts?.Cancel();
            }
        }

        private async Task RunAsync(ClientSnapshot view, PendingSnap pending, Action<PlayerAction> submit,
            CancellationToken cancel)
        {
            PlayerAction action = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                bool decision = pending.Kind == PendingInputKind.Decision && pending.Decision != null;
                string system = PromptBuilder.SystemPrompt(pending.Kind);
                string user = PromptBuilder.BuildUserPrompt(view, pending);
                JObject schema = decision ? DecisionSchema() : PrioritySchema();

                string content = await _client
                    .ChatAsync(_settings.Model, system, user, _settings.Think, schema, timeout.Token)
                    .ConfigureAwait(false);

                action = decision ? ParseDecision(content, pending) : ParsePriority(content, pending);
            }
            catch
            {
                action = null; // every failure path funnels into the fallback below
            }

            if (cancel.IsCancellationRequested)
                return; // cancelled or superseded — a stale answer must never be submitted
            submit(action ?? SnapshotFallbackPolicy.Choose(pending));
        }

        /// <summary>{"action_index": n} → pending.LegalActions[n]; null when invalid.</summary>
        private static PlayerAction ParsePriority(string content, PendingSnap pending)
        {
            var obj = JObject.Parse(content);
            int index = obj.Value<int?>("action_index") ?? -1;
            var legal = pending.LegalActions;
            if (legal == null || index < 0 || index >= legal.Count)
                return null;
            var action = legal[index];
            action.PlayerIndex = pending.PlayerIndex;
            return action;
        }

        /// <summary>{"option_ids": [...]} → SubmitDecisionAction; null when ids are unknown,
        /// duplicated away to below Min, or outside Min..Max.</summary>
        private static PlayerAction ParseDecision(string content, PendingSnap pending)
        {
            var request = pending.Decision;
            var obj = JObject.Parse(content);
            if (!(obj["option_ids"] is JArray ids))
                return null;

            var answer = new DecisionAnswer { DecisionId = request.Id };
            foreach (var token in ids)
            {
                int id = token.Value<int>();
                if (answer.ChosenOptionIds.Contains(id))
                    continue;
                if (!request.Options.Exists(o => o.Id == id))
                    return null;
                answer.ChosenOptionIds.Add(id);
            }
            if (answer.ChosenOptionIds.Count < request.Min || answer.ChosenOptionIds.Count > request.Max)
                return null;
            return new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer };
        }

        private static JObject PrioritySchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["action_index"] = new JObject { ["type"] = "integer" },
                ["reason"] = new JObject { ["type"] = "string" }
            },
            ["required"] = new JArray("action_index")
        };

        private static JObject DecisionSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["option_ids"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" }
                },
                ["reason"] = new JObject { ["type"] = "string" }
            },
            ["required"] = new JArray("option_ids")
        };

        public void Dispose()
        {
            Cancel();
            if (_ownsClient)
                _client.Dispose();
        }
    }
}
