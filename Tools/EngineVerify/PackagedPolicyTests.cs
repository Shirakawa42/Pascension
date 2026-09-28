using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pascension.Core;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

[TestFixture]
public sealed class PackagedPolicyTests
{
    private static string Resource(string name)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ProjectSettings", "ProjectVersion.txt"))) root = root.Parent;
        Assert.That(root, Is.Not.Null, "Cannot find repository root");
        return Path.Combine(root.FullName, "Assets", "Resources", "AI", name);
    }

    [Test]
    public void ReleaseResourcesMatchAcceptedSnapshotAndEnableSearch()
    {
        Assert.That(File.Exists(Resource("shards-policy.json")), Is.False, "Duplicate Unity Resources basename shadows model bytes");
        var bytes = File.ReadAllBytes(Resource("shards-policy.bytes"));
        var metadata = JObject.Parse(File.ReadAllText(Resource("shards-policy-metadata.json")));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo("2e9d7dc1b3c65d9ac57629abfdbd7e7d0c9cd34467dddfb4e4e0fca795ff286d"));
        Assert.That(hash, Is.EqualTo((string)metadata["policy_sha256"]));
        var settings = JsonConvert.DeserializeObject<PolicySearchSettings>(File.ReadAllText(Resource("shards-search-settings.json"))).ValidatedCopy();
        Assert.That(settings.Hybrid && settings.TacticalGuards && settings.SetupPlans && settings.MenuPlans && settings.ScryPlans && settings.SequenceRepairs && settings.OptionalChoices && settings.MixedResourcePlans && settings.PruneNoEffectPlans && settings.SimplifyWins, Is.True);
        Assert.That(new[] { settings.Candidates, settings.Depth, settings.Worlds, settings.Workers, settings.TerminalNodes, settings.EndTurnExtension, settings.RolloutStyles, settings.PriorVerificationWorlds }, Is.EqualTo(new[] { 4, 24, 2, 8, 512, 64, 4, 8 }));
        var policy = new FrozenPolicy(bytes);
        var config = ShardsContentRegistry.StandardConfig(827492, new List<PlayerSpec> { new PlayerSpec { Name = "P0", CharacterId = "decima" }, new PlayerSpec { Name = "P1", CharacterId = "tetra" } }, ShardsDlc.Duel);
        var game = new PolicyEngine(config, policy, 8372, true, settings);
        game.BindSubmit(action => game.Submit(action));
        // Exercise the actual packaged model through drafting and opening actions.
        for (int i = 0; i < 12 && !game.GameOver; i++) game.StepPolicy();
        Assert.That(game.PendingInput, Is.Not.Null);
    }
}
