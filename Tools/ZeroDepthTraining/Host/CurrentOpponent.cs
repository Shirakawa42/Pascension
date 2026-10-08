using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Newtonsoft.Json;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.AI;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    /// <summary>Runs the deployed opponent through its unmodified production PolicyEngine.
    /// Its search is intentionally enabled only in evaluation, never learner rollouts.</summary>
    internal sealed class CurrentOpponent
    {
        internal sealed class Bundle
        {
            internal FrozenPolicy Policy;
            internal PolicySearchSettings Settings;
            internal string PolicySha256;
            internal string SettingsSha256;
        }

        private readonly PolicyEngine _policyEngine;
        internal ShardsEngine Engine { get; }
        internal long WrapperSteps => _productionAdapter.WrapperSteps;
        internal long Submissions => _productionAdapter.Submissions;
        internal bool Truncated => _productionAdapter.Truncated;
        private readonly Shards.AI.Adapter _productionAdapter;

        internal CurrentOpponent(ShardsConfig config, string bundleDirectory, int samplingSeed)
            : this(config, LoadBundle(bundleDirectory), samplingSeed) { }

        internal CurrentOpponent(ShardsConfig config, Bundle bundle, int samplingSeed)
        {
            if (config == null || bundle == null) throw new ArgumentNullException();
            _policyEngine = new PolicyEngine(config, bundle.Policy, samplingSeed,
                tacticalSearch: true, searchSettings: bundle.Settings);
            // The production adapter is deliberately private. Inspect it once to share
            // the same rules engine with the learner without altering shipped AI code.
            var field = typeof(PolicyEngine).GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic);
            _productionAdapter = field?.GetValue(_policyEngine) as Shards.AI.Adapter
                ?? throw new InvalidOperationException("Production PolicyEngine adapter changed; review evaluation integration");
            Engine = _productionAdapter.Engine;
        }

        internal void BindSubmit(Action<PlayerAction> submit) => _policyEngine.BindSubmit(submit);
        internal SubmitResult Submit(PlayerAction action) => _policyEngine.Submit(action);
        internal void Step() => _policyEngine.StepPolicy();

        internal static Bundle LoadBundle(string directory)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            var root = manifest.RootElement;
            if (root.GetProperty("schema").GetString() != "shards-zero-depth-incumbent-v1")
                throw new InvalidDataException("Unsupported frozen opponent manifest");
            if (!root.GetProperty("tactical_search").GetBoolean())
                throw new InvalidDataException("Frozen incumbent must retain deployed tactical search");
            var files = root.GetProperty("files");
            var verified = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in new[] { "shards-policy.bytes", "shards-search-settings.json",
                "shards-policy-metadata.json", "shards-inference.json" })
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name));
                string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (actual != files.GetProperty(name).GetProperty("sha256").GetString())
                    throw new InvalidDataException("Frozen opponent hash mismatch: " + name);
                verified.Add(name, bytes);
            }
            var settings = JsonConvert.DeserializeObject<PolicySearchSettings>(
                System.Text.Encoding.UTF8.GetString(verified["shards-search-settings.json"]))
                ?? throw new InvalidDataException("Frozen search settings are invalid");
            settings = settings.ValidatedCopy();
            return new Bundle
            {
                Policy = new FrozenPolicy(verified["shards-policy.bytes"]), Settings = settings,
                PolicySha256 = files.GetProperty("shards-policy.bytes").GetProperty("sha256").GetString(),
                SettingsSha256 = files.GetProperty("shards-search-settings.json").GetProperty("sha256").GetString()
            };
        }
    }
}
