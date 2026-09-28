using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace Shards.AI
{

// Policy-guided same-turn rollouts over public determinizations. The caller
// supplies batched inference (GPU in the headless host, native in the game).
internal sealed class Prediction
{
    internal float[] P;
    internal float V;
}
internal sealed class Lookahead
{
    private readonly PolicySearchSettings config;
    private readonly Func<Adapter[],Prediction[]> infer;
    private readonly ParallelOptions parallel;
    private readonly Func<Adapter,Adapter> copy;
    internal long Branches, Steps, Overrides, Decisions, OpeningDecisions;
    internal Action<Adapter,object> ObserveChoices {get;set;} // Optional passive replay diagnostics.
    internal double CloneSeconds, RolloutSeconds, TerminalSeconds;
    private HybridLookahead hybrid;
    internal object HybridDiagnostics => hybrid?.Diagnostics;
    internal void TransferMacroPlan(Adapter from,Adapter to)=>hybrid?.TransferPlan(from,to);
    private sealed class Branch
    {
        internal Adapter Game;
        internal int Root, Action, Seat, Turn;
        internal double Value;
        internal bool Ended;
        internal List<string> Path=new();
    }
    internal Lookahead(PolicySearchSettings config,Func<Adapter[],Prediction[]> infer,Func<Adapter,Adapter> copy=null)
    {this.config=config.ValidatedCopy();this.infer=infer;this.copy=copy??TacticalSearch.Copy;parallel=new ParallelOptions{MaxDegreeOfParallelism=config.Workers};}
    internal int[] Choose(Adapter[] roots, Prediction[] predictions, int[] fallback, bool[] enabled)
    {
        if(config.Hybrid)
        {
            if(hybrid==null)hybrid=new HybridLookahead(config,infer,copy);
            var answer=hybrid.Choose(roots,predictions,fallback,enabled);
            Branches=hybrid.Branches;Steps=hybrid.Steps;Overrides=hybrid.Overrides;
            Decisions=hybrid.Decisions;OpeningDecisions=hybrid.OpeningDecisions;
            CloneSeconds=hybrid.CloneSeconds;RolloutSeconds=hybrid.RolloutSeconds;TerminalSeconds=hybrid.TerminalSeconds;
            return answer;
        }
        var result=(int[])fallback.Clone();
        var jobs=new List<(int root,int action,int world)>();
        var actions=new int[roots.Length][];
        for(int i=0;i<roots.Length;i++)
        {
            if(!enabled[i]||roots[i].VisibleCount<2)continue;
            Decisions++;if(roots[i].Engine.State.Round<=2)OpeningDecisions++;
            var ordered=Enumerable.Range(0,roots[i].VisibleCount).OrderByDescending(a=>predictions[i].P[a]).ToList();
            var selected=ordered.Take(config.Candidates).ToList();
            if(!selected.Contains(fallback[i]))selected.Add(fallback[i]);
            actions[i]=selected.ToArray();
            foreach(int action in selected)for(int w=0;w<config.Worlds;w++)jobs.Add((i,action,w));
        }
        if(jobs.Count==0)return result;
        var branches=new Branch[jobs.Count];
        var watch=System.Diagnostics.Stopwatch.StartNew();
        // One sanitized world per root/sample, shared as an immutable template.
        var templates=new Adapter[roots.Length*config.Worlds];
        Parallel.For(0,templates.Length,parallel,j=>
        {int i=j/config.Worlds;if(actions[i]!=null)templates[j]=TacticalSearch.PublicWorld(roots[i],713101+(j%config.Worlds)*7919,copy);});
        Parallel.For(0,jobs.Count,parallel,j=>
        {
            var job=jobs[j];var root=roots[job.root];
            var g=copy(templates[job.root*config.Worlds+job.world]);
            var branch=new Branch{Game=g,Root=job.root,Action=job.action,Seat=root.Actor,Turn=root.Engine.State.TurnPlayerIndex};
            branch.Path.Add(TacticalSearch.Key(g,job.action));g.Step(job.action);branches[j]=branch;
        });
        CloneSeconds+=watch.Elapsed.TotalSeconds;Branches+=branches.Length;Steps+=branches.Length;
        watch.Restart();
        for(int depth=1;depth<=config.Depth;depth++)
        {
            foreach(var b in branches.Where(b=>!b.Ended&&b.Game.Engine.State.GameOver))
            {b.Value=b.Game.Engine.State.WinnerIndex<0?0:b.Game.Engine.State.WinnerIndex==b.Seat?1:-1;b.Ended=true;}
            var live=branches.Where(b=>!b.Ended).ToArray();
            if(live.Length==0)break;
            var p=infer(live.Select(b=>b.Game).ToArray());
            Parallel.For(0,live.Length,parallel,j=>
            {
                var b=live[j];
                if(depth==config.Depth||b.Game.Engine.State.TurnPlayerIndex!=b.Turn)
                {b.Value=(b.Game.Actor==b.Seat?1:-1)*p[j].V;b.Ended=true;return;}
                int best=0;for(int k=1;k<b.Game.VisibleCount;k++)if(p[j].P[k]>p[j].P[best])best=k;
                b.Path.Add(TacticalSearch.Key(b.Game,best));b.Game.Step(best);
            });
            Steps+=live.Count(b=>!b.Ended);
        }
        RolloutSeconds+=watch.Elapsed.TotalSeconds;
        var proven=new bool[roots.Length];
        foreach(var group in branches.GroupBy(b=>b.Root))
        {
            int i=group.Key;
            var choices=group.GroupBy(b=>b.Action).Select(g=>new{Action=g.Key,Value=g.Average(b=>b.Value),Win=g.All(b=>b.Game.Engine.State.GameOver&&b.Game.Engine.State.WinnerIndex==b.Seat)}).ToArray();
            var initial=choices.Single(c=>c.Action==fallback[i]);
            var best=choices.OrderByDescending(c=>c.Win).ThenByDescending(c=>c.Value+config.Prior*Math.Log(Math.Max(1e-12,predictions[i].P[c.Action]))).First();
            if(best.Win)
            {
                var path=group.First(b=>b.Action==best.Action).Path;
                proven[i]=TacticalSearch.AcceptWinningLine(roots[i],path,copy);
            }
            if(best.Action!=fallback[i]&&((best.Win&&!initial.Win)||best.Value>initial.Value+config.Margin))
            {result[i]=best.Action;}
            // The accepted four-world proof belongs to this exact first move.
            if(proven[i])result[i]=best.Action;
            if(ObserveChoices!=null)ObserveChoices(roots[i],new{fallback=fallback[i],selected=result[i],best=best.Action,choices,
                leaves=group.Select(b=>new{b.Action,b.Value,terminal=b.Game.Engine.State.GameOver,actor=b.Game.Actor,turn=b.Game.Engine.State.TurnPlayerIndex,
                    ownHealth=b.Game.Engine.State.Players[b.Seat].Health,ownMastery=b.Game.Engine.State.Players[b.Seat].Mastery,
                    ownHand=b.Game.Engine.State.Players[b.Seat].Hand.Count,ownGems=b.Game.Engine.State.Players[b.Seat].Gems,ownPower=b.Game.Engine.State.Players[b.Seat].Power}).ToArray()});
        }
        if(config.TerminalNodes>0)
        {
            watch.Restart();
            Parallel.For(0,roots.Length,parallel,i=>
            {
                if(!enabled[i]||proven[i]||!TacticalSearch.IsRelevant(roots[i]))return;
                // Retain the terminal planner: policy rollouts may repeatedly miss
                // the same winning continuation. Four worlds, same concrete line.
                int win=TacticalSearch.Find(roots[i],config.TerminalNodes,12,4,copy,true);
                if(win>=0)result[i]=win;
            });
            TerminalSeconds+=watch.Elapsed.TotalSeconds;
        }
        Overrides+=Enumerable.Range(0,result.Length).Count(i=>result[i]!=fallback[i]);
        return result;
    }
}

}
