using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Engine;

internal static class HorizonAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        Prediction[] Infer(Adapter[] games)=>games.Select(g=>
        {
            var p=new float[64];
            for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsPlayCardAction?1:g.Visible(a).Action is ShardsEndTurnAction?.1f:0;
            float total=p.Sum();if(total==0)for(int a=0;a<g.VisibleCount;a++)p[a]=1f/g.VisibleCount;
            else for(int a=0;a<g.VisibleCount;a++)p[a]/=total;
            return new Prediction{P=p,V=0};
        }).ToArray();
        foreach(int seat in new[]{0,1})foreach(bool skip in new[]{false,true})foreach(int turns in new[]{1,2})
        {
            var g=RezAudit.Game(mastery:0,seat:seat);var own=g.Engine.State.Players[seat];var enemy=g.Engine.State.Players[1-seat];
            own.Health=3;
            foreach(var c in own.Hand.Concat(own.Deck).Concat(own.Discard))c.DefId="crystal";
            foreach(var c in enemy.Hand.Concat(enemy.Deck).Concat(enemy.Discard))c.DefId="blaster";
            RezAudit.Add(g,"crystal",seat);RezAudit.Refresh(g);
            ulong before=TacticalSearch.Fingerprint(g);
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=4,Depth=24,Worlds=2,TerminalNodes=0,HorizonTurns=turns,HybridSkipForced=skip},Infer,FastCopy.Copy){CaptureLeaves=true};
            var prediction=Infer(new[]{g});planner.Choose(new[]{g},prediction,new[]{0},new[]{true});
            var leaves=JArray.FromObject(planner.DebugLeaves??Array.Empty<object>());
            bool passed=leaves.Count>0&&leaves.All(l=>(bool)l["Complete"]&&Math.Abs((double)l["Value"]-(turns==1?0:-1))<1e-7)&&before==TacticalSearch.Fingerprint(g);
            rows.Add(new{name="observe-opponent-lethal-only-with-reply-horizon",seat,skip,turns,passed,leaves=leaves.Count});if(!passed)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Horizon audit failed {failures} cases");
    }
}
