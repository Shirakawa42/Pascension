using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

internal static class NaturalGainAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        void Check(string name,bool expected,Func<int,Adapter> factory)
        {
            for(int seat=0;seat<2;seat++)
            {
                var g=factory(seat);RezAudit.Refresh(g);ulong before=TacticalSearch.Fingerprint(g);
                bool actual=SafeTurnGains.HasAlternative(g);
                var changed=FastCopy.Copy(g);var enemy=changed.Engine.State.Players[1-seat];
                changed.Engine.State.Players[seat].Deck.Reverse();
                if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
                {
                    var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;
                    enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;
                }
                bool privateInvariant=actual==SafeTurnGains.HasAlternative(changed);
                bool unchanged=before==TacticalSearch.Fingerprint(g);
                bool passed=actual==expected&&privateInvariant&&unchanged;
                if(!passed)failures++;
                rows.Add(new{name,seat,expected,actual,privateInvariant,unchanged,passed});
            }
        }
        Adapter Game(int seat)=>RezAudit.Game(seat:seat);
        void Card(Adapter g,string id,ShardsZone zone)
        {
            int seat=g.Actor;var p=g.Engine.State.Players[seat];
            var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=seat,Zone=zone};
            if(zone==ShardsZone.Champions)p.Champions.Add(c);
            else if(zone==ShardsZone.Discard)p.Discard.Add(c);
            else if(zone==ShardsZone.DestinyRow)p.Destinies.Add(c);
            else if(zone==ShardsZone.PlayZone){p.PlayZone.Add(c);p.PlayedThisTurn.Add(c);p.CountFactionPlay(c.Def.Faction,!c.Def.IsChampion);}
            else p.Hand.Add(c);
        }
        Check("thornshell-full-health-power",true,s=>{var g=Game(s);g.Engine.State.Players[s].Health=50;Card(g,"thornshell_warden",ShardsZone.Champions);return g;});
        Check("medicine-active",true,s=>{var g=Game(s);g.Engine.State.Players[s].Health=22;Card(g,"advanced_medicine",ShardsZone.DestinyRow);Card(g,"mining_drones",ShardsZone.PlayZone);Card(g,"shard_seer",ShardsZone.PlayZone);return g;});
        Check("medicine-inactive",false,s=>{var g=Game(s);g.Engine.State.Players[s].Health=22;Card(g,"advanced_medicine",ShardsZone.DestinyRow);return g;});
        Check("datic-active",true,s=>{var g=Game(s);Card(g,"datic_secrets_duel",ShardsZone.DestinyRow);Card(g,"cloud_oracles_sos",ShardsZone.PlayZone);Card(g,"shard_seer",ShardsZone.PlayZone);return g;});
        Check("datic-inactive",false,s=>{var g=Game(s);Card(g,"datic_secrets_duel",ShardsZone.DestinyRow);return g;});
        Check("ferrata-public-counter",true,s=>{var g=Game(s);Card(g,"ferrata_guard_duel",ShardsZone.Champions);return g;});
        Check("kiln-gems",true,s=>{var g=Game(s);Card(g,"kiln_drone",ShardsZone.Hand);return g;});
        Check("bleak-paid-draw-optional",false,s=>{var g=Game(s);Card(g,"bleak_communion",ShardsZone.Hand);return g;});
        Check("synthesis-under-threshold",false,s=>{var g=Game(s);Card(g,"synthesis",ShardsZone.DestinyRow);return g;});
        Check("leshai-unify-free-power",true,s=>{var g=Game(s);Card(g,"leshai_knight",ShardsZone.Hand);Card(g,"thornshell_warden",ShardsZone.PlayZone);return g;});
        Check("brute-power-optional-warp",true,s=>{var g=Game(s);Card(g,"brute",ShardsZone.Hand);return g;});
        Check("numeri-gem-and-recruit-routing",true,s=>{var g=Game(s);Card(g,"numeri_drones",ShardsZone.Champions);return g;});
        Check("korvus-power-empty-discard",true,s=>{var g=Game(s);Card(g,"korvus_legionnaire_duel",ShardsZone.Hand);return g;});
        Check("korvus-power-public-return",true,s=>{var g=Game(s);Card(g,"korvus_legionnaire_duel",ShardsZone.Hand);Card(g,"numeri_drones",ShardsZone.Discard);return g;});
        Check("aegis-guaranteed-gems-before-dominion",true,s=>{var g=Game(s);Card(g,"aegis_archivist",ShardsZone.Champions);return g;});
        Check("aegis-active-dominion-gems",true,s=>{var g=Game(s);Card(g,"aegis_archivist",ShardsZone.Champions);foreach(string id in new[]{"mining_drones","leshai_knight","nil_assassin_duel"})Card(g,id,ShardsZone.PlayZone);return g;});
        Check("bulwark-possible-dominion-draw-not-certified",false,s=>{var g=Game(s);Card(g,"bulwark_chanter",ShardsZone.Hand);return g;});
        var unsafeDominion=new SafeTurnGains.Amounts();bool rejectsDominionCost=!SafeTurnGains.Read(E.Seq(E.Gems(2),new Dominion(E.Health(-1))),0,ref unsafeDominion);
        rows.Add(new{name="dominion-cost-not-certified",passed=rejectsDominionCost});if(!rejectsDominionCost)failures++;
        var returned=new SafeTurnGains.Amounts();
        bool returnSafe=SafeTurnGains.Read(E.Seq(E.Power(3),new ReturnFromDiscard(_=>throw new Exception("Return filter was evaluated"),"champion")),0,ref returned)&&returned.Power==3;
        rows.Add(new{name="return-filter-never-probed",passed=returnSafe});if(!returnSafe)failures++;
        for(int seat=0;seat<2;seat++)
        {
            var g=Game(seat);Card(g,"korvus_legionnaire_duel",ShardsZone.Hand);Card(g,"numeri_drones",ShardsZone.Discard);RezAudit.Refresh(g);
            var player=g.Engine.State.Players[seat];int power=player.Power;
            int action=Enumerable.Range(0,g.VisibleCount).Single(a=>ReviewRecorder.Name(g,g.Visible(a))=="play:korvus_legionnaire_duel");
            g.Step(action);
            bool passed=player.Power==power+3&&player.Hand.Any(c=>c.DefId=="numeri_drones")&&player.Discard.Count==0&&g.Decision==null;
            rows.Add(new{name="korvus-real-return-and-power",seat,passed});if(!passed)failures++;
        }
        var gain=new SafeTurnGains.Amounts();
        bool read=SafeTurnGains.Read(E.Seq(E.Mastery(1),E.Seq(E.Mastery(1),E.At(3,E.Power(99)))),0,ref gain);
        bool nested=read&&gain.Mastery==2&&gain.Power==0;
        rows.Add(new{name="nested-sequence-threshold",passed=nested,gain.Mastery,gain.Power});if(!nested)failures++;
        var opaque=new SafeTurnGains.Amounts();
        var context=new ShardsContext{Engine=Game(0).Engine,ControllerIndex=0};
        bool unknown=SafeTurnGains.Read(new If(_=>throw new Exception("Opaque condition was evaluated"),E.Power(5)),0,ref opaque,context:context)&&opaque.Power==0;
        rows.Add(new{name="opaque-condition-never-probed",passed=unknown});if(!unknown)failures++;
        var opaqueCount=new SafeTurnGains.Amounts();
        bool unknownCount=SafeTurnGains.Read(new PerCount(_=>throw new Exception("Opaque counter was evaluated"),power:5),0,ref opaqueCount,context:context)&&opaqueCount.Power==0;
        rows.Add(new{name="opaque-counter-never-probed",passed=unknownCount});if(!unknownCount)failures++;
        for(int seat=0;seat<2;seat++)
        {
            var root=RezAudit.Game(mastery:30,seat:seat);var p=root.Engine.State.Players[seat];
            p.CharacterId="decima";p.Gems=1;p.FocusedThisTurn=false;p.CharacterExhausted=false;
            RezAudit.Refresh(root);
            Prediction Predict(Adapter g)
            {
                var probabilities=new float[64];
                for(int a=0;a<g.VisibleCount;a++)probabilities[a]=g.Visible(a).Action is ShardsFocusAction?100:g.Visible(a).Action is ShardsEndTurnAction?1:.001f;
                float total=probabilities.Sum();for(int a=0;a<64;a++)probabilities[a]/=total;
                return new Prediction{P=probabilities,V=0};
            }
            var prediction=Predict(root);int fallback=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsFocusAction);
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=64,Worlds=1,Depth=24,Workers=1,TerminalNodes=0,TacticalGuards=true},gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(root);
            int chosen=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            bool passed=!(root.Visible(chosen).Action is ShardsFocusAction)&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{name="reject-focus-at-mastery-cap",seat,passed,selected=ReviewRecorder.Name(root,root.Visible(chosen))});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)foreach(string card in new[]{"slipstream_shard_duel","cinder_scars_duel"})
        {
            var g=RezAudit.Game(seat:seat);g.Engine.State.Players[seat].Mastery=18;
            RezAudit.Add(g,card,seat);RezAudit.Refresh(g);ulong before=TacticalSearch.Fingerprint(g);
            int preferred=SafeTurnGains.Preferred(g,2);
            bool passed=preferred>=0&&ReviewRecorder.Name(g,g.Visible(preferred))=="play:"+card&&before==TacticalSearch.Fingerprint(g);
            rows.Add(new{name="prefer-gain-prefix-before-opaque-tail",seat,card,preferred,passed});if(!passed)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Natural gain audit: {failures} failures");
    }
}
