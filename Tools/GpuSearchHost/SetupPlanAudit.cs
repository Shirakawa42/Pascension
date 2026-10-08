using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Engine;

internal static class SetupPlanAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        foreach(int seat in new[]{0,1})foreach(string kind in new[]{"focus-reactor","mastery-infinity","deploy-evokatus","numeri-recruit","mastery-draw-warp","mastery-dominion-infinity"})
        {
            var g=RezAudit.Game(mastery:kind=="focus-reactor"?4:kind=="mastery-draw-warp"?17:9,seat:seat);var p=g.Engine.State.Players[seat];
            p.CharacterId="decima";p.RelicRecruited=true;string payoff;
            if(kind=="focus-reactor")
            {p.Gems=1;p.FocusedThisTurn=false;p.CharacterExhausted=false;RezAudit.Add(g,"shard_reactor",seat);payoff="play:shard_reactor";}
            else if(kind=="mastery-infinity")
            {RezAudit.Add(g,"systema_ai",seat,ShardsZone.Champions);RezAudit.Add(g,"infinity_shard",seat);payoff="play:infinity_shard";}
            else if(kind=="deploy-evokatus")
            {RezAudit.Add(g,"evokatus_duel",seat,ShardsZone.Champions);foreach(string id in new[]{"systema_ai","testudo_vanguard","primus_pilus_duel"})RezAudit.Add(g,id,seat);payoff="exhaust:evokatus_duel";}
            else if(kind=="numeri-recruit")
            {
                p.Gems=3;RezAudit.Add(g,"numeri_drones",seat,ShardsZone.Champions);
                g.Engine.State.CenterRow[0]=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId="primus_pilus_duel",Owner=-1,Zone=ShardsZone.CenterRow};payoff="buy:primus_pilus_duel";
            }
            else if(kind=="mastery-dominion-infinity")
            {foreach(string id in new[]{"infinity_shard","bulwark_chanter","cloud_oracles_sos","undergrowth_aspirant_duel","nil_assassin_duel"})RezAudit.Add(g,id,seat);payoff="play:infinity_shard";}
            else
            {p.CharacterId="rez";RezAudit.Add(g,"star_seeker",seat,ShardsZone.Champions);RezAudit.Add(g,"systema_ai",seat,ShardsZone.Champions);RezAudit.Add(g,"slipstream_shard_duel",seat);payoff="exhaust:star_seeker";}
            RezAudit.Refresh(g);int first=Enumerable.Range(0,g.VisibleCount).Single(a=>ReviewRecorder.Name(g,g.Visible(a))==payoff);
            var prediction=new Prediction{P=new float[64]};prediction.P[first]=1;
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=1,SetupPlans=true,Workers=1},gs=>throw new Exception("Proposal must not invoke inference"),FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(g);var options=planner.BuildOptions(g,prediction,first);
            var plans=options.Where(o=>o.SetupPlan).Select(o=>o.Keys).ToArray();
            string expected=kind=="focus-reactor"?"ShardsFocusAction":kind=="mastery-infinity"?"ShardsExhaustAction":kind=="deploy-evokatus"||kind=="mastery-dominion-infinity"?"ShardsPlayCardAction":"ShardsExhaustAction";
            bool passed=plans.Any(xs=>xs.Length>1&&xs[0].Contains(expected)&&xs.Last()==TacticalSearch.Key(g,first))&&before==TacticalSearch.Fingerprint(g);
            if(kind=="deploy-evokatus")passed&=plans.Any(xs=>xs.Length==4);
            if(kind=="mastery-draw-warp")passed&=plans.Any(xs=>xs.Length==3&&xs[1].Contains("slipstream_shard_duel"));
            var changed=FastCopy.Copy(g);changed.Engine.State.Players[seat].Deck.Reverse();var enemy=changed.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
            var other=planner.BuildOptions(changed,prediction,first).Where(o=>o.SetupPlan).Select(o=>string.Join("\n",o.Keys));
            passed&=plans.Select(xs=>string.Join("\n",xs)).SequenceEqual(other);
            if(kind=="mastery-dominion-infinity")
            {
                var missing=FastCopy.Copy(g);missing.Engine.State.Players[seat].Hand.RemoveAll(c=>c.DefId=="nil_assassin_duel");RezAudit.Refresh(missing);
                passed&=!planner.BuildOptions(missing,prediction,first).Any(o=>o.SetupPlan&&o.Keys[0].Contains("bulwark_chanter"));
                var resolved=FastCopy.Copy(g);RezAudit.Step(resolved,c=>c.Action is ShardsPlayCardAction a&&resolved.Engine.State.FindCard(a.CardInstanceId).DefId=="bulwark_chanter");
                foreach(string id in new[]{"cloud_oracles_sos","undergrowth_aspirant_duel","nil_assassin_duel"})RezAudit.Step(resolved,c=>c.Option?.DefId==id);
                passed&=resolved.Decision==null&&resolved.Engine.State.Players[seat].Mastery==11;
                RezAudit.Step(resolved,c=>c.Action is ShardsPlayCardAction a&&resolved.Engine.State.FindCard(a.CardInstanceId).DefId=="infinity_shard");
                passed&=resolved.Engine.State.Players[seat].Power==3;
                Prediction[] StopAfterMenu(Adapter[] games)=>games.Select(x=>
                {
                    var probabilities=new float[64];
                    for(int a=0;a<x.VisibleCount;a++)probabilities[a]=x.Visible(a).Action is ShardsEndTurnAction||x.Visible(a).Kind==12?1:.00001f;
                    return new Prediction{P=probabilities,V=0};
                }).ToArray();
                var continuation=new HybridLookahead(new PolicySearchSettings{Candidates=1,SetupPlans=true,Worlds=1,Workers=1,Depth=24,TerminalNodes=0},StopAfterMenu,FastCopy.Copy){CaptureLeaves=true};
                var planned=continuation.BuildOptions(g,prediction,first);
                continuation.Choose(new[]{g},new[]{prediction},new[]{first},new[]{true});
                var leaves=JArray.FromObject(continuation.DebugLeaves).Where(x=>planned[(int)x["Option"]].SetupPlan&&planned[(int)x["Option"]].Keys[0].Contains("bulwark_chanter")).ToArray();
                bool resumed=leaves.Length>0&&leaves.All(x=>x["path"].Values<string>().Contains(TacticalSearch.Key(g,first)))&&before==TacticalSearch.Fingerprint(g);
                rows.Add(new{kind="resume-setup-after-visible-menu",seat,passed=resumed});if(!resumed)failures++;
            }
            rows.Add(new{kind,seat,plans,passed});if(!passed)failures++;
            if(kind=="focus-reactor"||kind=="mastery-infinity")
            {
                var setup=options.First(o=>o.SetupPlan);var path=new[]{TacticalSearch.Key(g,first),setup.Keys[0]};
                int repaired=SetupPlanning.ImproveResourceOrder(g,path,FastCopy.Copy);
                bool repairedPass=repaired==setup.First&&before==TacticalSearch.Fingerprint(g);
                repairedPass&=SetupPlanning.ImproveResourceOrder(changed,path,FastCopy.Copy)==repaired;
                repairedPass&=SetupPlanning.ImproveResourceOrder(g,new[]{path[0]},FastCopy.Copy)==-1;
                rows.Add(new{kind="repair-selected-"+kind,seat,repaired,passed=repairedPass});if(!repairedPass)failures++;
            }
        }
        for(int seat=0;seat<2;seat++)foreach(string id in new[]{"shard_reactor","infinity_shard"})
        {
            var root=RezAudit.Game(mastery:id=="shard_reactor"?4:9,seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="decima";own.Gems=0;own.FocusedThisTurn=false;own.CharacterExhausted=false;
            RezAudit.Add(root,id,seat);RezAudit.Add(root,"crystal",seat);RezAudit.Add(root,"crystal",seat);RezAudit.Refresh(root);
            var prefix=new List<string>();var probe=FastCopy.Copy(root);
            foreach(string name in new[]{"play:"+id,"play:crystal","play:crystal","ShardsFocusAction"})
            {
                int a=Enumerable.Range(0,probe.VisibleCount).First(i=>ReviewRecorder.Name(probe,probe.Visible(i))==name);
                prefix.Add(TacticalSearch.Key(probe,a));probe.Step(a);
            }
            ulong before=TacticalSearch.Fingerprint(root);int repaired=SetupPlanning.ImproveResourceOrder(root,prefix,FastCopy.Copy);
            bool passed=repaired>=0&&ReviewRecorder.Name(root,root.Visible(repaired))=="play:crystal"&&before==TacticalSearch.Fingerprint(root);
            passed&=SetupPlanning.ImproveResourceOrder(root,prefix.Take(3).ToArray(),FastCopy.Copy)==-1;
            var changed=FastCopy.Copy(root);changed.Engine.State.Players[seat].Deck.Reverse();var enemy=changed.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
            passed&=SetupPlanning.ImproveResourceOrder(changed,prefix,FastCopy.Copy)==repaired;
            rows.Add(new{kind="fund-before-threshold-"+id,seat,repaired,passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)foreach(bool refundable in new[]{true,false})
        {
            var root=RezAudit.Game(mastery:refundable?4:9,seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="decima";own.Gems=4;own.FocusedThisTurn=false;own.CharacterExhausted=false;
            string resourceId=refundable?"shard_reactor":"infinity_shard";RezAudit.Add(root,resourceId,seat);
            root.Engine.State.CenterRow[0]=new ShardsCard{InstanceId=root.Engine.State.NextInstanceId++,DefId="systema_ai",Owner=-1,Zone=ShardsZone.CenterRow};RezAudit.Refresh(root);
            var probe=FastCopy.Copy(root);var path=new List<string>();
            foreach(string name in new[]{"play:"+resourceId,"buy:systema_ai","ShardsFocusAction"})
            {int a=Enumerable.Range(0,probe.VisibleCount).First(i=>ReviewRecorder.Name(probe,probe.Visible(i))==name);path.Add(TacticalSearch.Key(probe,a));probe.Step(a);}
            ulong before=TacticalSearch.Fingerprint(root);int repaired=SetupPlanning.ImproveResourceOrder(root,path,FastCopy.Copy);
            bool passed=(refundable?repaired>=0&&root.Visible(repaired).Action is ShardsFocusAction:repaired==-1)&&before==TacticalSearch.Fingerprint(root);
            passed&=SetupPlanning.ImproveResourceOrder(root,path.Take(2).ToArray(),FastCopy.Copy)==-1;
            rows.Add(new{kind="refunded-focus-before-purchase",seat,refundable,repaired,passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)
        {
            var root=RezAudit.Game(mastery:4,seat:seat);
            RezAudit.Add(root,"shard_reactor",seat);RezAudit.Add(root,"anomaly_cleric",seat);RezAudit.Refresh(root);
            string Key(string name)=>TacticalSearch.Key(root,Enumerable.Range(0,root.VisibleCount).Single(a=>ReviewRecorder.Name(root,root.Visible(a))==name));
            var path=new[]{Key("play:shard_reactor"),Key("play:anomaly_cleric")};
            ulong before=TacticalSearch.Fingerprint(root);
            int repaired=SetupPlanning.ImproveResourceOrder(root,path,FastCopy.Copy);
            bool passed=repaired>=0&&ReviewRecorder.Name(root,root.Visible(repaired))=="play:anomaly_cleric"&&before==TacticalSearch.Fingerprint(root);
            passed&=SetupPlanning.ImproveResourceOrder(root,path.Take(1).ToArray(),FastCopy.Copy)==-1;
            var changed=FastCopy.Copy(root);changed.Engine.State.Players[seat].Deck.Reverse();
            passed&=SetupPlanning.ImproveResourceOrder(changed,path,FastCopy.Copy)==repaired;
            var original=FastCopy.Copy(root);var better=FastCopy.Copy(root);
            foreach(string name in new[]{"play:shard_reactor","play:anomaly_cleric"})RezAudit.Step(original,c=>c.Action is ShardsPlayCardAction a&&"play:"+original.Engine.State.FindCard(a.CardInstanceId).DefId==name);
            foreach(string name in new[]{"play:anomaly_cleric","play:shard_reactor"})RezAudit.Step(better,c=>c.Action is ShardsPlayCardAction a&&"play:"+better.Engine.State.FindCard(a.CardInstanceId).DefId==name);
            passed&=better.Engine.State.Players[seat].Gems==original.Engine.State.Players[seat].Gems+1;
            rows.Add(new{kind="mastery-card-before-reactor",seat,repaired,passed});if(!passed)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Setup plan audit: {failures} failures");
    }
}
