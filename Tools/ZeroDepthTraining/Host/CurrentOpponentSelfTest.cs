using System;
using System.Collections.Generic;
using System.Reflection;
using Pascension.Core;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Runs a few real production searches; does not train or estimate strength.
    internal static class CurrentOpponentSelfTest
    {
        internal static object Run(string bundleDirectory)
        {
            ShardsContentRegistry.EnsureRegistered();
            Encoder.Initialize();
            var bundle = CurrentOpponent.LoadBundle(bundleDirectory);
            var config = ShardsContentRegistry.StandardConfig(9223372036854775821,
                new List<PlayerSpec> { new() { Name = "P0", CharacterId = "decima" },
                    new() { Name = "P1", CharacterId = "tetra" } }, ShardsDlc.Duel);
            var helper = new CurrentOpponent(config, bundle, 71831);
            var learnerView = new Adapter(helper.Engine, helper.Submit, automaticSingletons: false);
            var expected = new PolicyEngine(config, bundle.Policy, 71831,
                tacticalSearch: true, searchSettings: bundle.Settings);
            helper.BindSubmit(action => learnerView.ApplyExternal(action));
            expected.BindSubmit(action => expected.Submit(action));
            var expectedAdapter = (Shards.AI.Adapter)typeof(PolicyEngine)
                .GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(expected);
            const int decisions = 6;
            for (int i = 0; i < decisions; i++)
            {
                helper.Step(); expected.StepPolicy();
                if (helper.Engine.State.ComputeHash() != expectedAdapter.Engine.State.ComputeHash() ||
                    helper.Submissions != expectedAdapter.Submissions ||
                    learnerView.Submissions != helper.Submissions ||
                    helper.WrapperSteps != expectedAdapter.WrapperSteps)
                    throw new InvalidOperationException("Evaluation incumbent behavior differs from production PolicyEngine");
            }
            var search = (Lookahead)typeof(PolicyEngine)
                .GetField("_lookahead", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(expected);
            if (search.Decisions < 1) throw new InvalidOperationException("Opponent self-test did not execute deployed search");
            // A learner submission also flows through the canonical incumbent tracker.
            // End Turn exercises production forced splits and any nested callbacks.
            int endTurn = -1;
            for (int i = 0; i < learnerView.VisibleCount; i++)
                if (learnerView.Visible(i).Kind == 10) { endTurn = i; break; }
            if (endTurn < 0) throw new InvalidOperationException("Parity fixture cannot end its opening turn");
            var endAction = learnerView.Visible(endTurn).Action;
            learnerView.Step(endTurn);
            if (!expected.Submit(endAction).Accepted)
                throw new InvalidOperationException("Production reference rejected the learner End Turn");
            if (helper.Engine.State.ComputeHash() != expectedAdapter.Engine.State.ComputeHash() ||
                helper.Submissions != expectedAdapter.Submissions || learnerView.Submissions != helper.Submissions)
                throw new InvalidOperationException("Shared evaluation trackers changed the game or observed submissions twice");
            var obs = new float[Encoder.ObsDim];
            var candidates = new float[Encoder.MaxActions * Encoder.ActionDim];
            var mask = new float[Encoder.MaxActions];
            Encoder.Encode(learnerView, obs, candidates, mask);
            if (learnerView.VisibleCount < 1) throw new InvalidOperationException("Shared learner observation has no legal action");
            return new
            {
                passed = true, decisions, deployedSearchDecisions = search.Decisions,
                deployedSearchBranches = search.Branches, bundle.PolicySha256, bundle.SettingsSha256,
                sharedEngineSubmissions = learnerView.Submissions, learnerObservationEncoded = true,
                strengthEvidence = false
            };
        }
    }
}
