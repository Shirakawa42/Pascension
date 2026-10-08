// Diagnostic port of the deployed planner to the full-information adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
internal sealed partial class HybridLookahead
{
    long menuBranches,menuInformationSets;

    void FinishBranches(Branch[] branches,int limit,int menuDepth)
    {
        // A cutoff inside split/shields/cleanup compares unsettled damage with
        // completed turns. Spend extra steps only once the public end phase has
        // started. An opt-in reply horizon gets a fresh budget at TurnStarted;
        // the default still stops at the first boundary, exactly as before.
        int Limit(Branch b)=>limit+(config.HorizonTurns>1?b.TurnStartDepth:0)+(b.Game.Engine.IsResolvingEndTurn?config.EndTurnExtension:0);
        bool Boundary(Branch b)=>b.TurnChanges>=config.HorizonTurns||config.HorizonTurns==1&&
            (b.TurnChanged||b.Game.Engine.State.TurnPlayerIndex!=b.Turn||b.Game.Engine.State.Round!=b.Round);
        for(int wave=0;wave<config.HorizonTurns*(limit+config.EndTurnExtension);wave++)
        {
            if(config.HybridSkipForced)
            {
                long before=branches.Sum(b=>(long)b.Used);
                Parallel.ForEach(branches,parallel,b=>
                {
                    while(!b.Done&&!b.Game.Engine.State.GameOver&&!Boundary(b)&&b.Depth<Limit(b)-1)
                    {
                        if(b.SetupKeys!=null&&b.Game.Decision==null&&b.Game.Actor==b.Seat)break;
                        int sole=SoleAction(b.Game);if(sole<0)break;
                        b.Advance(sole,true);
                    }
                });
                Steps+=branches.Sum(b=>(long)b.Used)-before;
            }
            foreach(var b in branches.Where(b=>!b.Done&&b.Game.Engine.State.GameOver))
            {b.Value=b.Game.Engine.State.WinnerIndex<0?0:b.Game.Engine.State.WinnerIndex==b.Seat?1:-1;b.Done=b.Complete=true;}
            if(FutureScry)
            {
                // Expand a Scry produced directly by the proposed root action.
                // Expanding every later speculative activation multiplied work
                // in the full-game probe; these immediate menus are the ones
                // needed to fairly value using Rez's power at this real root.
                var scries=branches.Where(b=>!b.Done&&!b.TurnChanged&&!b.FutureScryUsed&&b.Depth==0&&b.Depth<limit-1&&
                    b.Game.Actor==b.Seat&&ScryPlanning.Applies(b.Game)).ToArray();
                if(scries.Length>0)ImproveFutureScry(scries,limit,menuDepth);
            }
            if(menuDepth>0)
            {
                var menus=branches.Where(b=>!b.Done&&!b.TurnChanged&&b.Depth<limit-1&&b.Game.Actor==b.Seat&&
                    MenuPlanning.VisibleMenu(b.Game)&&b.Game.VisibleCount<=8).ToArray();
                if(menus.Length>0)ImproveMenus(menus,limit,menuDepth);
            }
            var live=branches.Where(b=>!b.Done).ToArray();if(live.Length==0)break;
            var p=infer(live.Select(b=>b.Game).ToArray());
            Parallel.For(0,live.Length,parallel,j=>
            {
                var b=live[j];var g=b.Game;
                bool boundary=Boundary(b);
                if(boundary||b.Depth>=Limit(b)-1)
                {b.Value=(g.Actor==b.Seat?1:-1)*p[j].V;b.Done=true;b.Complete=boundary;return;}
                int action=-1;
                if(b.SetupKeys!=null&&g.Decision==null&&g.Actor==b.Seat)
                {
                    action=Find(g,b.SetupKeys[b.SetupNext++]);
                    // A reveal may make a known target unavailable. Abandon that
                    // hypothetical prefix rather than substitute a sampled card.
                    if(action<0||b.SetupNext>=b.SetupKeys.Length)b.SetupKeys=null;
                }
                if(action<0)action=Greedy(g,p[j],g.Actor==b.Seat?b.Style:0);
                b.Advance(action,true);
            });
            Steps+=live.Count(b=>!b.Done);
        }
    }

    // Every indistinguishable sampled history shares a choice. Never take the
    // best action separately in each hidden world and average that oracle value.
    void ImproveMenus(Branch[] parents,int limit,int depth)
    {
        var features=new float[Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim+Encoder.MaxActions];var bytes=new byte[features.Length*sizeof(float)];
        var sets=new Dictionary<string,List<Branch>>();var orderedSets=new List<List<Branch>>();
        using(var sha=SHA256.Create())foreach(var b in parents)
        {
            Encoder.Encode(b.Game,features.AsSpan(0,Encoder.ObsDim),features.AsSpan(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),features.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim,Encoder.MaxActions));
            Buffer.BlockCopy(features,0,bytes,0,bytes.Length);
            // Physical instance IDs are not information: two sampled draws may
            // expose equivalent Crystals with different IDs. Current semantic
            // observations, legal candidate vectors and known-card memory define
            // this conservative policy information set.
            string key=b.Root+":"+b.Option+":"+b.Style+":"+b.MenuHistory+":"+Convert.ToBase64String(sha.ComputeHash(bytes))+":"+
                HybridKnowledge.ObserverSignature(b.Game.Knowledge,b.Seat);
            b.MenuInformation=key;
            if(!sets.TryGetValue(key,out var set))
            {set=new List<Branch>();sets.Add(key,set);orderedSets.Add(set);}
            set.Add(b);
        }
        menuInformationSets+=sets.Count;
        var batch=new List<List<Branch>>();int size=0;
        foreach(var set in orderedSets)
        {
            int needed=set.Count*set[0].Game.VisibleCount;
            if(size>0&&size+needed>4096){Evaluate(batch);batch.Clear();size=0;}
            batch.Add(set);size+=needed;
        }
        if(batch.Count>0)Evaluate(batch);

        void Evaluate(List<List<Branch>> groups)
        {
            var children=new List<Branch>();var jobs=new List<(Branch parent,Branch child,int action)>();
            var choices=new List<(List<Branch> parents,int[] actions,Branch[][] children,Prediction[] priors)>();
            var priorGames=groups.SelectMany(xs=>xs).Select(b=>b.Game).ToArray();
            var priors=infer(priorGames);int offset=0;
            foreach(var group in groups)
            {
                var first=group[0].Game;
                var actions=Enumerable.Range(0,first.VisibleCount).Where(a=>first.Visible(a).Kind==12||first.Visible(a).Kind==13).ToArray();
                var alternatives=new Branch[actions.Length][];
                for(int a=0;a<actions.Length;a++)
                {
                    alternatives[a]=new Branch[group.Count];
                    for(int w=0;w<group.Count;w++)
                    {
                        var parent=group[w];int action=actions[a];
                        // Identical encoded menus have identical semantic action
                        // indices, while each world retains its own physical IDs.
                        if(action>=parent.Game.VisibleCount||parent.Game.Visible(action).Kind!=first.Visible(action).Kind)throw new InvalidOperationException("Equivalent menu has a different semantic action index");
                        var child=new Branch{Root=parent.Root,Option=parent.Option,Style=parent.Style,Seat=parent.Seat,
                            Turn=parent.Turn,Round=parent.Round,Used=parent.Used,Depth=parent.Depth,Path=new List<string>(parent.Path),
                            TurnChanged=parent.TurnChanged,TurnChanges=parent.TurnChanges,TurnStartDepth=parent.TurnStartDepth,FirstTurnPathLength=parent.FirstTurnPathLength,HealthSpent=parent.HealthSpent,
                            FutureScryUsed=parent.FutureScryUsed,
                            MenuHistory=parent.MenuInformation+":"+action,SetupKeys=parent.SetupKeys,SetupNext=parent.SetupNext};
                        alternatives[a][w]=child;children.Add(child);jobs.Add((parent,child,action));
                    }
                }
                if(actions.Length==0)throw new InvalidOperationException("Visible effect menu contains no choices");
                choices.Add((group,actions,alternatives,priors.Skip(offset).Take(group.Count).ToArray()));offset+=group.Count;
            }
            Parallel.For(0,jobs.Count,parallel,j=>
            {var job=jobs[j];job.child.Game=copy(job.parent.Game);job.child.Advance(job.action,true);});
            Branches+=children.Count;menuBranches+=children.Count;Steps+=children.Count;
            FinishBranches(children.ToArray(),limit,depth-1);
            foreach(var choice in choices)
            {
                int best=Enumerable.Range(0,choice.actions.Length).OrderByDescending(a=>choice.children[a].Average(b=>b.Value))
                    .ThenByDescending(a=>choice.priors.Average(p=>(double)p.P[choice.actions[a]])).First();
                for(int w=0;w<choice.parents.Count;w++)
                {
                    var parent=choice.parents[w];var child=choice.children[best][w];
                    parent.Game=child.Game;parent.Used=child.Used;parent.Depth=child.Depth;parent.Path=child.Path;
                    parent.Value=child.Value;parent.Done=child.Done;parent.Complete=child.Complete;parent.TurnChanged=child.TurnChanged;
                    parent.TurnChanges=child.TurnChanges;parent.TurnStartDepth=child.TurnStartDepth;parent.FirstTurnPathLength=child.FirstTurnPathLength;parent.HealthSpent=child.HealthSpent;
                    parent.MenuHistory=child.MenuHistory;
                    parent.FutureScryUsed=child.FutureScryUsed;
                    parent.SetupKeys=child.SetupKeys;parent.SetupNext=child.SetupNext;
                }
            }
        }
    }
}
}
