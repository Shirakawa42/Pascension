using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Engine;

internal static class EndResolutionAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        for(int seat=0;seat<2;seat++)foreach(int extension in new[]{0,64})foreach(bool defenseRoot in new[]{false,true})
        {
            var root=RezAudit.Game(seat:seat,mastery:6,power:5);var own=root.Engine.State.Players[seat];
            own.HeroAbilityUsedThisTurn=true;
            var enemy=root.Engine.State.Players[1-seat];enemy.Health=30;
            // Every public-world allocation retains a shield response. Nothing
            // about the actual hidden hand is passed to the search policy.
            foreach(var card in enemy.Hand.Concat(enemy.Deck))card.DefId="prism";
            RezAudit.Refresh(root);
            int end=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
            if(defenseRoot)root.Step(end);
            Prediction Predict(Adapter g)
            {
                var probs=new float[64];
                for(int a=0;a<g.VisibleCount;a++)probs[a]=g.Visible(a).Action is ShardsEndTurnAction||g.Visible(a).Kind==12?1:.001f;
                return new Prediction{P=probs,V=g.Engine.IsResolvingEndTurn?.9f:-.4f};
            }
            var prediction=Predict(root);int fallback=Array.IndexOf(prediction.P,prediction.P.Max());
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=1,Depth=1,Worlds=2,Workers=1,TerminalNodes=0,EndTurnExtension=extension},gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            ulong before=TacticalSearch.Fingerprint(root);
            planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true});
            var leaves=JArray.Parse(JsonConvert.SerializeObject(planner.DebugLeaves));
            bool complete=leaves.All(l=>(bool)l["Complete"]);
            bool bounded=leaves.All(l=>(int)l["Used"]<=65);
            bool passed=leaves.Count>0&&complete==(extension>0)&&bounded&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{seat,extension,defenseRoot,complete,bounded,leaves,passed});if(!passed)failures++;
        }
        // Ordinary hand play must retain its original cutoff. The extension is
        // not an unconditional increase to every branch's search depth.
        for(int seat=0;seat<2;seat++)foreach(int extension in new[]{0,64})
        {
            var root=RezAudit.Game(seat:seat,mastery:6);RezAudit.Add(root,"kiln_drone",seat);
            int play=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsPlayCardAction);
            Prediction Predict(Adapter g)
            {
                var p=new float[64];for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsPlayCardAction?1:.001f;
                return new Prediction{P=p,V=0};
            }
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=1,Depth=1,Worlds=2,Workers=1,TerminalNodes=0,EndTurnExtension=extension},gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            ulong before=TacticalSearch.Fingerprint(root);planner.Choose(new[]{root},new[]{Predict(root)},new[]{play},new[]{true});
            var leaves=JArray.Parse(JsonConvert.SerializeObject(planner.DebugLeaves));
            bool passed=leaves.Count==2&&leaves.All(l=>!(bool)l["Complete"]&&(int)l["Used"]==1)&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{kind="ordinary-cutoff-unchanged",seat,extension,leaves,passed});if(!passed)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception("End resolution audit: "+failures+" failures");
    }
}
