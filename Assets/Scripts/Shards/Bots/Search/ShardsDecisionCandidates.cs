using System;
using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Generates a SMALL set of candidate answers for a decision so the search
    /// can branch over them (full answer spaces are combinatorial — soi.split alone is
    /// power^targets). Every candidate is built from the CURRENT request's options and
    /// respects Min/Max, so submits can never be rejected.</summary>
    public static class ShardsDecisionCandidates
    {
        public static List<List<int>> Generate(ShardsEngine engine, DecisionRequest request, ShardsValueModel model)
        {
            var candidates = new List<List<int>>();
            void Add(List<int> ids)
            {
                if (ids == null) return;
                if (ids.Count < request.Min || ids.Count > request.Max) return;
                foreach (var existing in candidates)
                    if (SameMultiset(existing, ids))
                        return;
                candidates.Add(ids);
            }

            // The tuned model's answer is always a candidate (and the fallback).
            Add(model.ChooseAnswer(engine, request).ChosenOptionIds);

            switch (request.Context)
            {
                case "soi.split":
                    foreach (var alt in SplitAlternatives(engine, request))
                        Add(alt);
                    break;

                case "soi.shields":
                    break; // reveal-everything is strictly best — single candidate

                case "soi.warp":
                case "soi.recruit":
                case "soi.copy":
                case "soi.destroy":
                case "soi.return":
                case "soi.destiny":
                case "soi.relic":
                case "soi.target":
                case "soi.reveal":
                case "soi.confirm":
                case "soi.maglev":
                case "soi.keepfast":
                {
                    // Small pick-one style spaces: branch over every single option, and
                    // over declining when legal. This is exactly where search beats a
                    // static "most valuable" rule.
                    if (request.Max == 1)
                    {
                        foreach (var option in request.Options)
                            Add(new List<int> { option.Id });
                        if (request.Min == 0)
                            Add(new List<int>());
                    }
                    else if (request.Min == 0)
                    {
                        Add(new List<int>()); // decline everything as the one alternative
                    }
                    break;
                }

                case "soi.banish":
                    if (request.Min == 0)
                        Add(new List<int>()); // banishing can be wrong — let search decide
                    break;

                case "soi.discard":
                    break; // forced discards: the model's lowest-value pick suffices

                default:
                {
                    // Unknown/future contexts: model answer + the engine default.
                    var fallback = new List<int>(request.DefaultOptionIds);
                    if (fallback.Count == 0 && request.Min > 0)
                        for (int i = 0; i < request.Min && i < request.Options.Count; i++)
                            fallback.Add(request.Options[i].Id);
                    Add(fallback);
                    break;
                }
            }

            if (candidates.Count == 0)
                Add(model.ChooseAnswer(engine, request).ChosenOptionIds);
            return candidates;
        }

        /// <summary>Split alternatives beyond the model's greedy pick: all-on-one-face
        /// per opponent, exact-lethal on each killable champion (rest to the weakest
        /// face), and a kill-everything-affordable sweep.</summary>
        private static IEnumerable<List<int>> SplitAlternatives(ShardsEngine engine, DecisionRequest request)
        {
            int budget = request.Max;

            DecisionOption taunt = null;
            foreach (var option in request.Options)
                if (option.Required)
                    taunt = option;

            var faces = new List<int>();
            foreach (var option in request.Options)
                if (option.Id < ShardsEngine.ChampionSplitBase &&
                    (taunt == null || option.Id != taunt.OwnerIndex))
                    faces.Add(option.Id);

            var champions = new List<(int Id, int Need, int OwnerFace)>();
            foreach (var option in request.Options)
            {
                if (option.Id < ShardsEngine.ChampionSplitBase || option.Required) continue;
                var card = engine.State.FindCard(option.CardInstanceId);
                if (card == null) continue;
                int need = option.Amount > 0 ? option.Amount
                    : Math.Max(1, card.Def.Defense - card.DamageThisTurn);
                if (need <= budget)
                    champions.Add((option.Id, need, option.OwnerIndex));
            }

            List<int> WithTaunt(Action<List<int>, int> fill)
            {
                var ids = new List<int>();
                int remaining = budget;
                if (taunt != null)
                {
                    if (taunt.Amount > remaining) return null;
                    for (int i = 0; i < taunt.Amount; i++) ids.Add(taunt.Id);
                    remaining -= taunt.Amount;
                }
                fill(ids, remaining);
                return ids.Count == budget ? ids : null;
            }

            int DefaultFace(int preferred) =>
                faces.Count > 0 ? (faces.Contains(preferred) ? preferred : faces[0])
                : taunt?.OwnerIndex ?? -1;

            // All-on-one-face per living opponent.
            foreach (int face in faces)
                yield return WithTaunt((ids, remaining) =>
                {
                    for (int i = 0; i < remaining; i++) ids.Add(face);
                });

            // Exact-lethal each champion, remainder to its owner's face (or any face).
            foreach (var (id, need, ownerFace) in champions)
                yield return WithTaunt((ids, remaining) =>
                {
                    if (need > remaining) return;
                    for (int i = 0; i < need; i++) ids.Add(id);
                    remaining -= need;
                    int face = DefaultFace(ownerFace);
                    if (face < 0) return;
                    for (int i = 0; i < remaining; i++) ids.Add(face);
                });

            // Kill everything affordable, cheapest need first, rest to the weakest face.
            if (champions.Count > 1)
                yield return WithTaunt((ids, remaining) =>
                {
                    var sorted = new List<(int Id, int Need, int OwnerFace)>(champions);
                    sorted.Sort((a, b) => a.Need.CompareTo(b.Need));
                    foreach (var (id, need, _) in sorted)
                    {
                        if (need > remaining) continue;
                        for (int i = 0; i < need; i++) ids.Add(id);
                        remaining -= need;
                    }
                    int face = DefaultFace(-1);
                    if (face < 0) return;
                    for (int i = 0; i < remaining; i++) ids.Add(face);
                });
        }

        private static bool SameMultiset(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            var counts = new Dictionary<int, int>();
            foreach (int id in a)
                counts[id] = counts.TryGetValue(id, out int v) ? v + 1 : 1;
            foreach (int id in b)
            {
                if (!counts.TryGetValue(id, out int v) || v == 0) return false;
                counts[id] = v - 1;
            }
            return true;
        }
    }
}
