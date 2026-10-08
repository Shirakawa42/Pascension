using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Shards.Nyou
{
internal sealed partial class HybridLookahead
{
    long futureScryBranches,futureScryInformationSets;

    // Resolve the Scry immediately after a proposed root action. Compare ordered bottom
    // subsets, sharing the decision across indistinguishable sampled histories.
    // The newly revealed cards are observed at this future decision, not at root.
    void ImproveFutureScry(Branch[] parents,int limit,int menuDepth)
    {
        var features=new float[Encoder.ObsDim+Encoder.MaxActions*(Encoder.ActionDim+1)];
        var bytes=new byte[features.Length*sizeof(float)];
        var sets=new Dictionary<string,List<Branch>>();var ordered=new List<List<Branch>>();
        using(var sha=SHA256.Create())foreach(var b in parents)
        {
            Encoder.Encode(b.Game,features.AsSpan(0,Encoder.ObsDim),features.AsSpan(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),features.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim,Encoder.MaxActions));
            Buffer.BlockCopy(features,0,bytes,0,bytes.Length);
            string key=b.Root+":"+b.Option+":"+b.Style+":"+b.MenuHistory+":"+Convert.ToBase64String(sha.ComputeHash(bytes))+":"+
                HybridKnowledge.ObserverSignature(b.Game.Knowledge,b.Seat);
            b.MenuInformation=key;
            if(!sets.TryGetValue(key,out var set)){set=new List<Branch>();sets.Add(key,set);ordered.Add(set);}
            set.Add(b);
        }
        futureScryInformationSets+=ordered.Count;
        var batch=new List<List<Branch>>();int size=0;
        foreach(var set in ordered)
        {
            int needed=set.Count*16;
            if(size>0&&size+needed>2048){Evaluate(batch);batch.Clear();size=0;}
            batch.Add(set);size+=needed;
        }
        if(batch.Count>0)Evaluate(batch);

        void Evaluate(List<List<Branch>> groups)
        {
            var representatives=groups.Select(xs=>xs[0].Game).ToArray();
            var options=groups.Select(_=>new List<Option>()).ToArray();
            ScryPlanning.Expand(representatives,options,copy,infer);
            var children=new List<Branch>();var jobs=new List<(Branch parent,Branch child,int[] actions)>();
            var choices=new List<(List<Branch> parents,Branch[][] alternatives,Option[] options)>();
            for(int i=0;i<groups.Count;i++)
            {
                var group=groups[i];var plans=options[i].ToArray();var alternatives=new Branch[plans.Length][];
                if(plans.Length==0)throw new InvalidOperationException("Future Scry has no complete choices");
                for(int a=0;a<plans.Length;a++)
                {
                    // Physical instance IDs can differ between equivalent worlds.
                    // Resolve the representative plan into semantic menu indices.
                    var probe=copy(representatives[i]);var indices=new List<int>();
                    foreach(string key in plans[a].Keys)
                    {
                        int action=Find(probe,key);if(action<0)throw new InvalidOperationException("Future Scry plan is illegal");
                        indices.Add(action);probe.Step(action);
                    }
                    alternatives[a]=new Branch[group.Count];
                    for(int w=0;w<group.Count;w++)
                    {
                        var parent=group[w];
                        var child=new Branch{Root=parent.Root,Option=parent.Option,Style=parent.Style,Seat=parent.Seat,
                            Turn=parent.Turn,Round=parent.Round,Used=parent.Used,Depth=parent.Depth,
                            Path=new List<string>(parent.Path),FutureScryUsed=true,
                            TurnChanged=parent.TurnChanged,TurnChanges=parent.TurnChanges,TurnStartDepth=parent.TurnStartDepth,
                            FirstTurnPathLength=parent.FirstTurnPathLength,HealthSpent=parent.HealthSpent,
                            MenuHistory=parent.MenuInformation+":scry:"+a,SetupKeys=parent.SetupKeys,SetupNext=parent.SetupNext};
                        alternatives[a][w]=child;children.Add(child);jobs.Add((parent,child,indices.ToArray()));
                    }
                }
                choices.Add((group,alternatives,plans));
            }
            Parallel.For(0,jobs.Count,parallel,j=>
            {
                var job=jobs[j];var child=job.child;child.Game=copy(job.parent.Game);int request=child.Game.Decision.Id;
                foreach(int action in job.actions)
                {
                    if(child.Game.Decision?.Id!=request||action>=child.Game.VisibleCount||
                        child.Game.Visible(action).Kind is not 12 and not 13)
                        throw new InvalidOperationException("Equivalent future Scry menu changed its action layout");
                    child.Advance(action);
                }
                if(child.Game.Decision?.Id==request)throw new InvalidOperationException("Future Scry plan is incomplete");
                // A complete menu is one decision for horizon accounting. Used
                // and Steps still count every actual wrapper action.
                child.Depth++;
            });
            Branches+=children.Count;futureScryBranches+=children.Count;
            Steps+=jobs.Sum(job=>job.child.Used-job.parent.Used);
            FinishBranches(children.ToArray(),limit,menuDepth);
            foreach(var choice in choices)
            {
                int best=Enumerable.Range(0,choice.options.Length).OrderByDescending(a=>choice.alternatives[a].Average(b=>b.Value))
                    .ThenByDescending(a=>choice.options[a].Probability).First();
                for(int w=0;w<choice.parents.Count;w++)
                {
                    var parent=choice.parents[w];var child=choice.alternatives[best][w];
                    parent.Game=child.Game;parent.Used=child.Used;parent.Depth=child.Depth;parent.Path=child.Path;
                    parent.Value=child.Value;parent.Done=child.Done;parent.Complete=child.Complete;parent.TurnChanged=child.TurnChanged;
                    parent.TurnChanges=child.TurnChanges;parent.TurnStartDepth=child.TurnStartDepth;parent.FirstTurnPathLength=child.FirstTurnPathLength;
                    parent.HealthSpent=child.HealthSpent;parent.MenuHistory=child.MenuHistory;parent.FutureScryUsed=true;
                    parent.SetupKeys=child.SetupKeys;parent.SetupNext=child.SetupNext;
                }
            }
        }
    }
}
}
