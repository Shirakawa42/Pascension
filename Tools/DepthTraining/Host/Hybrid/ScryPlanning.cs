// Diagnostic port of the deployed planner to the full-information adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
// Complete choices for an already revealed Scry, not guesses about the cards a
// future Scry will reveal. Enumerates every ordered subset for at most 3 cards.
internal static class ScryPlanning
{
    internal static bool Applies(Adapter g)=>g.Actor==g.Engine.State.TurnPlayerIndex&&
        g.Decision?.Context=="soi.scry"&&g.Decision.Min==0&&g.Decision.Max<=3&&g.Decision.Options.Count<=3;
    sealed class Node
    {
        internal Adapter Game;
        internal int Root,Request,Seat,First=-1;
        internal string[] Keys=Array.Empty<string>();
        internal long[] Submissions=Array.Empty<long>();
        internal double LogProbability;
    }
    internal static void Expand(Adapter[] roots,List<HybridLookahead.Option>[] options,Func<Adapter,Adapter> copy,Func<Adapter[],Prediction[]> infer)
    {
        var frontier=new List<Node>();
        for(int i=0;i<roots.Length;i++)if(options[i]!=null&&Applies(roots[i]))
        {
            options[i]=new List<HybridLookahead.Option>();
            frontier.Add(new Node{Root=i,Request=roots[i].Decision.Id,Seat=roots[i].Actor,Game=TacticalSearch.PublicWorld(roots[i],713101,copy)});
        }
        // Batch each selection depth across every independent live game. No
        // separate GPU request for each subset or each individual game.
        while(frontier.Count>0)
        {
            var predictions=infer(frontier.Select(n=>n.Game).ToArray());var next=new List<Node>();
            for(int i=0;i<frontier.Count;i++)
            {
                var parent=frontier[i];var g=parent.Game;
                for(int a=0;a<g.VisibleCount;a++)
                {
                    if(g.Visible(a).Kind!=12&&g.Visible(a).Kind!=13)continue;
                    var child=new Node{Root=parent.Root,Request=parent.Request,Seat=parent.Seat,First=parent.First<0?a:parent.First,
                        Keys=parent.Keys.Concat(new[]{TacticalSearch.Key(g,a)}).ToArray(),Game=copy(g),
                        LogProbability=parent.LogProbability+Math.Log(Math.Max(1e-30,predictions[i].P[a]))};
                    long before=child.Game.Submissions;child.Game.Step(a);
                    child.Submissions=parent.Submissions.Concat(new[]{child.Game.Submissions-before}).ToArray();
                    if(!child.Game.Engine.State.GameOver&&child.Game.Actor==child.Seat&&
                        child.Game.Decision?.Context=="soi.scry"&&child.Game.Decision.Id==child.Request)
                    {next.Add(child);continue;}
                    options[child.Root].Add(new HybridLookahead.Option{First=child.First,Keys=child.Keys,
                        Probability=Math.Exp(child.LogProbability),MenuPlan=true,ScryPlan=true,MenuId=child.Request,
                        FirstSubmissions=child.Submissions[0],StepSubmissions=child.Submissions});
                }
            }
            frontier=next;
        }
    }
}
}
