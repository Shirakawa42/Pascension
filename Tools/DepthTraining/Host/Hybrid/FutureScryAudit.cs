using System;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
internal sealed partial class HybridLookahead
{
    internal static object AuditFutureScry()
    {
        ulong seed=731960;while(HeroAssignments.ForSeed(seed).Seat0!="rez")seed++;
        var root=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(root,seed);
        root.Engine.State.Players[0].Mastery=5;
        typeof(ShardsEngine).GetMethod("RoutePriority",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(root.Engine,null);
        root.Rebuild();
        var beforeAbility=DepthCopy.Copy(root);
        root.Step(Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsHeroAbilityAction));
        ulong before=root.Engine.State.ComputeHash();
        var settings=new Shards.AI.PolicySearchSettings{Depth=3,Worlds=1,Workers=1,Hybrid=true,HybridSkipForced=true,HorizonTurns=1,EndTurnExtension=0};
        Prediction[] Infer(Adapter[] games)=>games.Select(g=>
        {
            var p=new float[64];p[0]=1;
            // A deliberately collapsed prior tries to bottom everything. The
            // synthetic critic rewards keeping the publicly revealed prefix.
            int kept=g.Knowledge.Center[0].Count(f=>f.Min==f.Max&&f.Min<3&&!f.UncertainPresence);
            return new Prediction{P=p,V=(g.Actor==0?1:-1)*(kept==3?.8f:-.8f)};
        }).ToArray();
        var search=new HybridLookahead(settings,Infer,DepthCopy.Copy);
        var branch=new Branch{Game=DepthCopy.Copy(root),Seat=0,Turn=0,Round=root.Engine.State.Round};
        search.FinishBranches(new[]{branch},2,0);
        if(root.Engine.State.ComputeHash()!=before)throw new Exception("Future Scry planning mutated root");
        if(branch.Game.Decision?.Context=="soi.scry")throw new Exception("Future Scry stopped with an unresolved partial choice");
        if(branch.Game.Knowledge.Center[0].Count(f=>f.Min==f.Max&&f.Min<3&&!f.UncertainPresence)!=3)
            throw new Exception("Collapsed prior overrode the better complete keep-all Scry choice");
        // Two worlds have identical public observations but opposite synthetic
        // critic preferences. Optimizing each world separately would score .8
        // by cheating; one shared choice must instead average to zero.
        var left=DepthCopy.Copy(root);var right=DepthCopy.Copy(root);
        right.Engine.State.Players[1].Hand.Reverse();
        int marker=left.Engine.State.Players[1].Hand[0].InstanceId;
        if(marker==right.Engine.State.Players[1].Hand[0].InstanceId)throw new Exception("Audit worlds did not differ");
        Prediction[] Conflicting(Adapter[] games)=>games.Select(g=>
        {
            var p=new float[64];p[0]=1;
            bool keep=g.Knowledge.Center[0].Count(f=>f.Min==f.Max&&f.Min<3&&!f.UncertainPresence)==3;
            bool hidden=g.Engine.State.Players[1].Hand[0].InstanceId==marker;
            return new Prediction{P=p,V=(g.Actor==0?1:-1)*(keep==hidden?.8f:-.8f)};
        }).ToArray();
        var grouped=new HybridLookahead(settings,Conflicting,DepthCopy.Copy);
        var worlds=new[]{left,right}.Select(g=>new Branch{Game=g,Seat=0,Turn=0,Round=g.Engine.State.Round}).ToArray();
        grouped.FinishBranches(worlds,2,0);
        if(Math.Abs(worlds.Average(b=>b.Value))>1e-7||!worlds[0].Path.SequenceEqual(worlds[1].Path))
            throw new Exception("Future Scry used hidden information to pick different choices in equivalent worlds");
        if(grouped.futureScryInformationSets!=1||grouped.futureScryBranches!=32)
            throw new Exception("Equivalent Scry worlds were not evaluated as one information set");
        var scrambled=DepthCopy.Copy(beforeAbility);var state=scrambled.Engine.State;var enemy=state.Players[1];
        var hidden=enemy.Hand.Concat(enemy.Deck).Reverse().ToArray();int handCount=enemy.Hand.Count;
        enemy.Hand.Clear();enemy.Hand.AddRange(hidden.Take(handCount));enemy.Deck.Clear();enemy.Deck.AddRange(hidden.Skip(handCount));
        state.CenterDeck.Reverse();state.Players[0].Deck.Reverse();state.DestinyDeck.Reverse();
        settings.Depth=4;settings.Worlds=2;settings.Candidates=2;settings.TerminalNodes=0;settings.RolloutStyles=1;
        settings.PriorVerificationWorlds=0;settings.ScryPlans=true;
        int fallback=Enumerable.Range(0,beforeAbility.VisibleCount).Single(a=>beforeAbility.Visible(a).Action is ShardsHeroAbilityAction);
        var leftSearch=new HybridLookahead(settings,Infer,DepthCopy.Copy){CaptureLeaves=true};
        var rightSearch=new HybridLookahead(settings,Infer,DepthCopy.Copy){CaptureLeaves=true};
        int selectedLeft=leftSearch.Choose(new[]{beforeAbility},Infer(new[]{beforeAbility}),new[]{fallback},new[]{true})[0];
        int selectedRight=rightSearch.Choose(new[]{scrambled},Infer(new[]{scrambled}),new[]{fallback},new[]{true})[0];
        if(selectedLeft!=selectedRight||System.Text.Json.JsonSerializer.Serialize(leftSearch.DebugLeaves)!=System.Text.Json.JsonSerializer.Serialize(rightSearch.DebugLeaves))
            throw new Exception("Planning a future reveal depends on the real hidden arrangement");
        if(leftSearch.futureScryBranches==0)throw new Exception("Future reveal invariance test did not exercise future Scry");
        return new{passed=true,value=branch.Value,diagnostics=search.Diagnostics,sharedChoiceDiagnostics=grouped.Diagnostics,
            hiddenArrangementDiagnostics=leftSearch.Diagnostics};
    }
}
}
