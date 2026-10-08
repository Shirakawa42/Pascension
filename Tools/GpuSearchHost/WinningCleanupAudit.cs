using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

internal static class WinningCleanupAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        for(int seat=0;seat<2;seat++)
        {
            var g=RezAudit.Game(mastery:5,power:4,seat:seat);
            g.Engine.State.Players[seat].CharacterId="kosynwu";
            g.Engine.State.Players[1-seat].Health=5;
            foreach(var c in g.Engine.State.Players[1-seat].Hand.Concat(g.Engine.State.Players[1-seat].Deck))c.DefId="crystal";
            RezAudit.Add(g,"brute",seat);RezAudit.Add(g,"blaster",seat);RezAudit.Refresh(g);
            var simulated=FastCopy.Copy(g);var path=new List<string>();
            foreach(string name in new[]{"hero:kosynwu","brute","play:blaster","ShardsEndTurnAction"})
            {
                int action=Enumerable.Range(0,simulated.VisibleCount).Single(a=>ReviewRecorder.Name(simulated,simulated.Visible(a))==name);
                path.Add(TacticalSearch.Key(simulated,action));simulated.Step(action);
            }
            if(!TacticalSearch.AcceptWinningLine(g,path,FastCopy.Copy))throw new Exception("Paid-prefix fixture not winning");
            int first=Enumerable.Range(0,g.VisibleCount).Single(a=>TacticalSearch.Key(g,a)==path[0]);
            ulong before=TacticalSearch.Fingerprint(g);
            int chosen=TacticalSearch.SimplifyPaidWinningPrefix(g,first,FastCopy.Copy);
            bool passed=ReviewRecorder.Name(g,g.Visible(chosen))=="play:blaster"&&before==TacticalSearch.Fingerprint(g);
            rows.Add(new{seat,scenario="real-banish-prefix-removed-from-validated-win",passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)foreach(bool enabled in new[]{false,true})
        foreach(string scenario in new[]{"end-already-wins","avoid-paid-winning-prefix","hidden-shield-blocks-end"})
        {
            var g=RezAudit.Game(mastery:5,power:scenario=="avoid-paid-winning-prefix"?4:5,seat:seat);
            var p=g.Engine.State.Players[seat];p.CharacterId="kosynwu";p.Health=35;
            var enemy=g.Engine.State.Players[1-seat];enemy.Health=5;
            foreach(var c in enemy.Hand.Concat(enemy.Deck))c.DefId="crystal";
            if(scenario=="hidden-shield-blocks-end")enemy.Hand[0].DefId="brute";
            RezAudit.Add(g,"crystal",seat);RezAudit.Add(g,"blaster",seat);RezAudit.Refresh(g);
            Prediction Predict(Adapter a)
            {
                var probabilities=new float[64];var owner=a.Engine.State.Players[a.Actor];
                for(int k=0;k<a.VisibleCount;k++)
                {
                    var c=a.Visible(k);float weight=.00001f;
                    if(c.Action is ShardsHeroAbilityAction)weight=1;
                    if(c.Action is ShardsPlayCardAction play)weight=a.Engine.State.FindCard(play.CardInstanceId).DefId=="blaster"?.1f:.01f;
                    if(c.Action is ShardsEndTurnAction)weight=owner.Power>=5&&!(!owner.HeroAbilityUsedThisTurn&&a.WrapperSteps==g.WrapperSteps)?10:.0001f;
                    if(c.Kind==12)weight=c.Option?.DefId=="crystal"?1:.01f;
                    probabilities[k]=weight;
                }
                float total=probabilities.Sum();for(int k=0;k<64;k++)probabilities[k]/=total;
                return new Prediction{P=probabilities,V=0};
            }
            Prediction[] Infer(Adapter[] games)=>games.Select(Predict).ToArray();
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=64,Depth=8,Worlds=2,Workers=1,TerminalNodes=0,SimplifyWins=enabled},Infer,FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(g);
            int chosen=planner.Choose(new[]{g},Infer(new[]{g}),new[]{0},new[]{true})[0];
            string selected=ReviewRecorder.Name(g,g.Visible(chosen));
            bool correct=(!enabled||scenario switch{
                "end-already-wins"=>g.Visible(chosen).Action is ShardsEndTurnAction,
                "avoid-paid-winning-prefix"=>selected=="play:blaster",
                _=>!(g.Visible(chosen).Action is ShardsEndTurnAction)
            })&&before==TacticalSearch.Fingerprint(g);
            rows.Add(new{seat,enabled,scenario,selected,passed=correct});if(!correct)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Winning cleanup failed {failures} cases");
    }
}
