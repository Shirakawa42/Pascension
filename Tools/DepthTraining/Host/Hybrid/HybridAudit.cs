using System;
using System.Linq;
using System.Collections.Generic;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class HybridAudit
    {
        internal static object Run()
        {
            var settings=new Shards.AI.PolicySearchSettings{Candidates=2,Depth=2,Worlds=1,Workers=2,
                Hybrid=true,TerminalNodes=0,RolloutStyles=1,PriorVerificationWorlds=0,
                TacticalGuards=true,MixedResourcePlans=true,MenuPlans=true,SetupPlans=true,
                OptionalChoices=true,ScryPlans=true,SequenceRepairs=true,PruneNoEffectPlans=true,SimplifyWins=true};
            Prediction[] Infer(Adapter[] games)=>games.Select(g=>new Prediction{
                P=Enumerable.Range(0,64).Select(a=>a<g.VisibleCount&&g.Visible(a).Kind!=11?1f/Math.Max(1,g.VisibleCount-1):0).ToArray(),V=0}).ToArray();
            var search=new HybridLookahead(settings,Infer,DepthCopy.Copy);
            var rng=new Pascension.Engine.Core.DeterministicRng(731961);int checkedRoots=0,hiddenChecks=0;
            foreach(ulong seed in new ulong[]{731960,731964,731971,731978})
            {
                var g=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(g,seed);
                for(int step=0;step<240&&!g.Engine.State.GameOver&&!g.Truncated;step++)
                {
                    var legal=Enumerable.Range(0,g.VisibleCount).Where(a=>g.Visible(a).Kind!=11).ToArray();
                    int random=legal[rng.Next(legal.Length)];
                    if(step%8==0)
                    {
                        ulong before=g.Engine.State.ComputeHash();int log=g.Engine.Log.Count;
                        long submissions=g.Submissions,steps=g.WrapperSteps;
                        var choices=search.Choose(new[]{g},Infer(new[]{g}),new[]{random},new[]{true});
                        if(!legal.Contains(choices[0]))throw new Exception("Hybrid proposed an illegal action");
                        if(before!=g.Engine.State.ComputeHash()||log!=g.Engine.Log.Count||submissions!=g.Submissions||steps!=g.WrapperSteps)
                            throw new Exception("Hybrid mutated its real root");
                        var defense=HybridKnowledge.DefensiveWorld(g,731991);
                        if(before!=g.Engine.State.ComputeHash())throw new Exception("Defensive sample mutated its root");
                        if(step%40==0)
                        {
                            var scrambled=DepthCopy.Copy(g);var state=scrambled.Engine.State;var enemy=state.Players[1-g.Actor];
                            var hidden=enemy.Hand.Concat(enemy.Deck).Reverse().ToArray();int count=enemy.Hand.Count;
                            enemy.Hand.Clear();enemy.Hand.AddRange(hidden.Take(count));enemy.Deck.Clear();enemy.Deck.AddRange(hidden.Skip(count));
                            state.CenterDeck.Reverse();state.DestinyDeck.Reverse();state.Players[g.Actor].Deck.Reverse();
                            var left=new HybridLookahead(settings,Infer,DepthCopy.Copy).Choose(new[]{g},Infer(new[]{g}),new[]{random},new[]{true});
                            var right=new HybridLookahead(settings,Infer,DepthCopy.Copy).Choose(new[]{scrambled},Infer(new[]{scrambled}),new[]{random},new[]{true});
                            if(left[0]!=right[0])throw new Exception("Hybrid choice depends on actual hidden arrangement");
                            if(TacticalSearch.Fingerprint(HybridKnowledge.DefensiveWorld(g,731991))!=
                                TacticalSearch.Fingerprint(HybridKnowledge.DefensiveWorld(scrambled,731991)))
                                throw new Exception("Defensive sample depends on actual hidden arrangement");
                            hiddenChecks++;
                        }
                        checkedRoots++;
                    }
                    g.Step(random);
                }
            }
            int scryPlans=ScryCoverage(search);
            return new{passed=true,checkedRoots,hiddenChecks,scryPlans,diagnostics=search.Diagnostics};
        }

        static int ScryCoverage(HybridLookahead search)
        {
            ulong seed=731960;while(HeroAssignments.ForSeed(seed).Seat0!="rez")seed++;
            var game=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(game,seed);
            game.Engine.State.Players[0].Mastery=5;
            typeof(ShardsEngine).GetMethod("RoutePriority",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(game.Engine,null);
            game.Rebuild();
            int ability=Enumerable.Range(0,game.VisibleCount).Single(a=>game.Visible(a).Action is ShardsHeroAbilityAction);
            game.Step(ability);
            if(!ScryPlanning.Applies(game)||game.Decision.Options.Count!=3)throw new Exception("Rez did not expose Scry 3");
            var prediction=new Prediction{P=Enumerable.Repeat(.25f,64).ToArray(),V=0};
            ulong before=game.Engine.State.ComputeHash();
            var options=search.BuildOptions(game,prediction,0);
            if(game.Engine.State.ComputeHash()!=before)throw new Exception("Scry expansion changed real state");
            if(options.Count!=16)throw new Exception($"Scry 3 must cover all 16 ordered subsets, found {options.Count}");
            var selected=new HashSet<string>();
            foreach(var option in options)
            {
                var copy=DepthCopy.Copy(game);var sequence=new List<int>();int request=copy.Decision.Id;
                foreach(string key in option.Keys)
                {
                    int a=SequenceRepairs.Find(copy,key);if(a<0)throw new Exception("Scry path is illegal");
                    if(copy.Visible(a).Kind==12)sequence.Add(copy.Visible(a).Option.CardInstanceId);
                    copy.Step(a);
                }
                if(copy.Decision?.Id==request)throw new Exception("Scry path did not submit the complete choice");
                if(!selected.Add(string.Join(",",sequence)))throw new Exception("Duplicate Scry sequence");
            }
            foreach(int length in Enumerable.Range(0,4))
            {
                int count=selected.Count(s=>s.Length==0?length==0:s.Split(',').Length==length);
                int expected=new[]{1,3,6,6}[length];if(count!=expected)throw new Exception("Missing Scry subset length");
            }
            return options.Count;
        }
    }
}
