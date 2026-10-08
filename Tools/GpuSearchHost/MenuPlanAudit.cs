using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

internal static class MenuPlanAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();
        foreach(string hero in new[]{"volos","kosynwu"})for(int seat=0;seat<2;seat++)
        {
            var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId=hero;own.Health=30;own.HeroAbilityUsedThisTurn=false;
            if(hero=="volos")own.Gems=1;
            if(hero=="kosynwu")RezAudit.Add(root,"crystal",seat);
            RezAudit.Refresh(root);bool denyInference=false;
            Prediction Predict(Adapter g)
            {
                var p=new float[64];
                for(int a=0;a<g.VisibleCount;a++)
                {
                    var c=g.Visible(a);p[a]=.001f;
                    if(c.Action is ShardsEndTurnAction||c.Kind==13)p[a]=100;
                    if(c.Option?.DefId==VolosAbilityChoice.FacePrefix+"1")p[a]=100;
                }
                float total=p.Sum();for(int i=0;i<p.Length;i++)p[i]/=total;
                var self=g.Engine.State.Players[g.Actor];var enemy=g.Engine.State.Players[1-g.Actor];
                float health=(hero=="volos"?(g.Actor==seat?self.Health*2-enemy.Health:self.Health-enemy.Health*2):self.Health-enemy.Health)*.01f;
                int Size(ShardsPlayer player)=>player.Hand.Count+player.Deck.Count+player.Discard.Count+player.PlayZone.Count+player.Champions.Count;
                double thinning=hero=="kosynwu"?.2*(Size(enemy)-Size(self)):0;
                return new Prediction{P=p,V=(float)Math.Tanh((self.Mastery-enemy.Mastery)*.1+health+thinning)};
            }
            Prediction[] Infer(Adapter[] games)
            {
                if(denyInference)throw new Exception("Committed menu unexpectedly reran search");
                return games.Select(Predict).ToArray();
            }
            var config=new PolicySearchSettings{Candidates=64,Worlds=2,Depth=24,Workers=1,TerminalNodes=0,Prior=0,MenuPlans=true,RolloutStyles=1};
            var planner=new HybridLookahead(config,Infer,FastCopy.Copy);var prediction=Predict(root);
            int fallback=Enumerable.Range(0,root.VisibleCount).First(a=>root.Visible(a).Action is ShardsEndTurnAction);
            ulong before=TacticalSearch.Fingerprint(root);var options=planner.BuildOptions(root,prediction,fallback);
            if(!options.Any(o=>o.MenuPlan))throw new Exception("Missing visible menu plans");
            var changed=FastCopy.Copy(root);changed.Engine.State.Players[seat].Deck.Reverse();
            var opponent=changed.Engine.State.Players[1-seat];
            if(opponent.Hand.Count>0&&opponent.Deck.Count>0)
            {
                var c=opponent.Hand[0];opponent.Hand[0]=opponent.Deck[0];opponent.Deck[0]=c;
                opponent.Hand[0].Zone=ShardsZone.Hand;opponent.Deck[0].Zone=ShardsZone.Deck;
            }
            string Signature(System.Collections.Generic.List<HybridLookahead.Option> os)=>JsonConvert.SerializeObject(os.Select(o=>new{o.First,o.Keys,o.Probability,o.ChoiceProbability,o.MenuPlan,o.FirstSubmissions}));
            if(Signature(options)!=Signature(planner.BuildOptions(changed,prediction,fallback)))throw new Exception("Menu plans depend on hidden allocation");
            int first=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(!(root.Visible(first).Action is ShardsHeroAbilityAction))throw new Exception("Did not select useful ability: "+hero);
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Menu planning mutated source");
            long submissions=root.Submissions;root.Step(first);long firstSubmissions=root.Submissions-submissions;
            if(firstSubmissions!=(hero=="kosynwu"?0:1))throw new Exception("Unexpected preview submission count");
            // The saved choice must survive PolicyEngine's scratch/live transfer.
            var next=FastCopy.Copy(root);planner.TransferPlan(root,next);denyInference=true;
            int action=planner.Choose(new[]{next},new[]{Predict(next)},new[]{0},new[]{true})[0];
            string definition=next.Visible(action).Option?.DefId;
            string expected=hero=="volos"?VolosAbilityChoice.FacePrefix+"0":"crystal";
            if(definition!=expected)throw new Exception($"Wrong committed choice for {hero}: {definition}");
            next.Step(action);
            rows.Add(new{hero,seat,firstSubmissions,definition,passed=true});
        }
        for(int seat=0;seat<2;seat++)
        {
            var root=RezAudit.Game(seat:seat,top:new[]{"pall_shades","aetherbreaker"});RezAudit.Add(root,"longshot",seat);
            var p=new Prediction{P=Enumerable.Repeat(1f/root.VisibleCount,64).ToArray()};
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=64,MenuPlans=true},_=>throw new Exception("Probe should not infer"),FastCopy.Copy);
            int play=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsPlayCardAction);
            var options=planner.BuildOptions(root,p,play);
            if(options.Any(o=>o.First==play&&o.MenuPlan))throw new Exception("Committed a hidden Longshot reveal");
            rows.Add(new{hero="rez",seat,hiddenRevealExcluded=true,passed=true});
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,rows},Formatting.Indented));
    }
}
