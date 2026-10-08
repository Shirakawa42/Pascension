using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

internal static class NoEffectPlanAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        Prediction[] Infer(Adapter[] games)=>games.Select(g=>new Prediction{P=Enumerable.Repeat(1f/g.VisibleCount,64).ToArray(),V=0}).ToArray();
        void Check(string name,Adapter root,bool enabled,Func<List<HybridLookahead.Option>,bool> valid)
        {
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=64,MenuPlans=true,PruneNoEffectPlans=enabled},Infer,FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(root);
            var options=planner.BuildOptions(root,Infer(new[]{root})[0],0);
            bool passed=valid(options)&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{name,seat=root.Actor,enabled,passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)
        {
            ShardsContentRegistry.EnsureRegistered();
            foreach(var def in ShardsCardDatabase.All.Where(d=>d.Type==ShardsCardType.Destiny&&d.ExhaustEffect is If condition&&condition.StableOnExhaust).ToArray())
            {
                var inactive=RezAudit.Game(seat:seat);var p=inactive.Engine.State.Players[seat];p.Health=35;
                var card=new ShardsCard{InstanceId=inactive.Engine.State.NextInstanceId++,DefId=def.Id,Owner=seat,Zone=ShardsZone.DestinyRow};
                p.Destinies.Add(card);RezAudit.Refresh(inactive);
                int action=Enumerable.Range(0,inactive.VisibleCount).Single(a=>inactive.Visible(a).Action is ShardsExhaustAction e&&e.CardInstanceId==card.InstanceId);
                bool certified=NoEffectPlans.InactiveDestiny(inactive,action);
                ulong before=TacticalSearch.Fingerprint(inactive);
                inactive.Step(action);card.Exhausted=false;
                bool passed=certified&&inactive.Decision==null&&before==TacticalSearch.Fingerprint(inactive);
                rows.Add(new{name="registered-inactive-condition-agrees-with-engine",seat,definition=def.Id,passed});if(!passed)failures++;
            }
            var conditional=RezAudit.Game(seat:seat);
            var owner=conditional.Engine.State.Players[seat];
            var destiny=new ShardsCard{InstanceId=conditional.Engine.State.NextInstanceId++,DefId="unconditional_conscription",Owner=seat,Zone=ShardsZone.DestinyRow};
            owner.Destinies.Add(destiny);
            RezAudit.Add(conditional,"the_dispossessed",seat);
            RezAudit.Add(conditional,"wraethe_skirmisher_duel",seat);
            RezAudit.Refresh(conditional);
            bool HasConscription(List<HybridLookahead.Option> os)=>os.Any(o=>conditional.Visible(o.First).Action is ShardsExhaustAction e&&e.CardInstanceId==destiny.InstanceId);
            Check("conscription-waits-for-two-allies",conditional,true,os=>!HasConscription(os));
            Check("conditional-disabled-keeps-legal-action",conditional,false,HasConscription);
            conditional.Step(Enumerable.Range(0,conditional.VisibleCount).Single(a=>ReviewRecorder.Name(conditional,conditional.Visible(a))=="play:the_dispossessed"));
            Check("conscription-still-waits-after-one-ally",conditional,true,os=>!HasConscription(os));
            conditional.Step(Enumerable.Range(0,conditional.VisibleCount).Single(a=>ReviewRecorder.Name(conditional,conditional.Visible(a))=="play:wraethe_skirmisher_duel"));
            Check("conscription-available-after-second-ally",conditional,true,HasConscription);
            int powerBefore=owner.Power;
            conditional.Step(Enumerable.Range(0,conditional.VisibleCount).Single(a=>conditional.Visible(a).Action is ShardsExhaustAction e&&e.CardInstanceId==destiny.InstanceId));
            bool gained=owner.Power==powerBefore+4;
            rows.Add(new{name="delayed-conscription-gains-four-power",seat,passed=gained});if(!gained)failures++;
            var winning=RezAudit.Game(mastery:20,power:40,seat:seat);
            winning.Engine.State.Players[seat].CharacterId="kosynwu";
            winning.Engine.State.Players[1-seat].Health=5;
            foreach(var c in winning.Engine.State.Players[1-seat].Hand.Concat(winning.Engine.State.Players[1-seat].Deck))c.DefId="crystal";
            RezAudit.Add(winning,"crystal",seat);RezAudit.Refresh(winning);
            int hero=Enumerable.Range(0,winning.VisibleCount).Single(a=>winning.Visible(a).Action is ShardsHeroAbilityAction);
            var simulated=FastCopy.Copy(winning);simulated.Step(hero);
            int cancel=Enumerable.Range(0,simulated.VisibleCount).Single(a=>simulated.Visible(a).Kind==13);
            var line=new List<string>{TacticalSearch.Key(winning,hero),TacticalSearch.Key(simulated,cancel)};
            simulated.Step(cancel);
            int end=Enumerable.Range(0,simulated.VisibleCount).Single(a=>simulated.Visible(a).Action is ShardsEndTurnAction);
            line.Add(TacticalSearch.Key(simulated,end));
            if(!TacticalSearch.AcceptWinningLine(winning,line,FastCopy.Copy))throw new Exception("Winning-prefix fixture is not winning");
            ulong winningBefore=TacticalSearch.Fingerprint(winning);
            int simple=TacticalSearch.SimplifyWinningPrefix(winning,hero,FastCopy.Copy);
            bool simplified=winning.Visible(simple).Action is ShardsEndTurnAction&&winningBefore==TacticalSearch.Fingerprint(winning);
            rows.Add(new{name="terminal-cancel-prefix-removed",seat,passed=simplified});if(!simplified)failures++;
            var visible=RezAudit.Game(seat:seat);var context=new ShardsContext{Engine=visible.Engine,ControllerIndex=seat};
            foreach(var item in new[]{
                (name:"opaque-predicate-never-called",effect:(IShardsEffect)new If(_=>throw new Exception("Private predicate executed"),E.Power(1)),empty:false),
                (name:"visible-predicate-never-called",effect:(IShardsEffect)If.Visible(_=>throw new Exception("Pre-activation predicate executed"),E.Draw(1)),empty:false),
                (name:"visible-active-draw",effect:(IShardsEffect)If.Visible(_=>true,E.Draw(1)),empty:false),
                (name:"partial-sequence-gain-retained",effect:(IShardsEffect)E.Seq(E.Power(1),E.At(30,E.Draw(1))),empty:false)})
            {
                bool passed=NoEffectPlans.Empty(item.effect,context)==item.empty;
                rows.Add(new{name=item.name,seat,passed});if(!passed)failures++;
            }
            foreach(bool enabled in new[]{false,true})foreach(int mastery in new[]{14,15})
            {
                var g=RezAudit.Game(mastery:mastery,seat:seat);var p=g.Engine.State.Players[seat];
                p.Destinies.Add(new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId="synthesis",Owner=seat,Zone=ShardsZone.DestinyRow});
                RezAudit.Refresh(g);
                bool Expected(List<HybridLookahead.Option> os)=>os.Any(o=>g.Visible(o.First).Action is ShardsExhaustAction)==(!enabled||mastery>=15);
                Check("synthesis-current-threshold",g,enabled,Expected);
                p.Mastery=15;RezAudit.Refresh(g);Check("synthesis-becomes-active",g,enabled,os=>os.Any(o=>g.Visible(o.First).Action is ShardsExhaustAction));
            }
            foreach(bool enabled in new[]{false,true})
            {
                var g=RezAudit.Game(seat:seat);g.Engine.State.Players[seat].CharacterId="kosynwu";
                RezAudit.Add(g,"crystal",seat);RezAudit.Add(g,"blaster",seat);RezAudit.Refresh(g);
                Check("sacrifice-preserves-all-targets",g,enabled,os=>
                {
                    var hero=os.Where(o=>g.Visible(o.First).Action is ShardsHeroAbilityAction).ToArray();
                    int targets=hero.Count(o=>o.MenuPlan&&o.Keys.Length==2&&o.Keys[1].StartsWith("12:"));
                    bool hasPrimitive=hero.Any(o=>o.Keys.Length==1);
                    bool hasCancel=hero.Any(o=>o.Keys.Length==2&&o.Keys[1].StartsWith("13:"));
                    return targets==2&&hasPrimitive==!enabled&&hasCancel==!enabled&&os.Any(o=>g.Visible(o.First).Action is ShardsPlayCardAction);
                });
                // More than the target expansion budget must retain primitive
                // activation, otherwise legal targets would silently disappear.
                for(int n=0;n<35;n++)RezAudit.Add(g,"crystal",seat);
                RezAudit.Refresh(g);
                Check("sacrifice-partial-coverage-retains-primitive",g,enabled,
                    os=>os.Any(o=>g.Visible(o.First).Action is ShardsHeroAbilityAction&&o.Keys.Length==1));
            }
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"No-effect plan audit failed {failures} cases");
    }
}
