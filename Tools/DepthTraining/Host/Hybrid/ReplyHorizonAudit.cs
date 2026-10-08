using System;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
internal sealed partial class HybridLookahead
{
    internal static object AuditReplyHorizon()
    {
        int checks=0;
        foreach(ulong seed in Enumerable.Range(0,20).Select(i=>736180UL+(ulong)i))
        {
            var root=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(root,seed);
            ulong hash=root.Engine.State.ComputeHash();
            Prediction[] Infer(Adapter[] games)=>games.Select(g=>
            {
                var p=new float[64];
                int action=Enumerable.Range(0,g.VisibleCount).FirstOrDefault(a=>g.Visible(a).Action is ShardsEndTurnAction,-1);
                if(action<0)action=Enumerable.Range(0,g.VisibleCount).First(a=>g.Visible(a).Kind!=11);
                p[action]=1;return new Prediction{P=p,V=.25f};
            }).ToArray();
            foreach(int horizon in new[]{1,2})
            {
                var settings=new Shards.AI.PolicySearchSettings{HorizonTurns=horizon,Depth=16,EndTurnExtension=16,Workers=1};
                var search=new HybridLookahead(settings,Infer,DepthCopy.Copy);
                var branch=new Branch{Game=DepthCopy.Copy(root),Seat=0,Turn=0,Round=root.Engine.State.Round};
                int end=Enumerable.Range(0,branch.Game.VisibleCount).Single(a=>branch.Game.Visible(a).Action is ShardsEndTurnAction);
                branch.Advance(end);search.FinishBranches(new[]{branch},16,0);
                if(!branch.Complete||branch.TurnChanges!=horizon||branch.Game.Engine.State.TurnPlayerIndex!=horizon%2)
                    throw new Exception("Reply horizon stopped at the wrong settled turn boundary");
                if(branch.Value!=(horizon==1?-.25:.25))throw new Exception("Reply horizon inverted the critic perspective");
                if(root.Engine.State.ComputeHash()!=hash)throw new Exception("Reply horizon changed the live root");
                checks++;
            }
        }
        return new{passed=true,checks};
    }
}
}
