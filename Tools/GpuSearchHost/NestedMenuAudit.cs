using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Engine;

internal static class NestedMenuAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();
        for(int seat=0;seat<2;seat++)foreach(bool conflicting in new[]{false,true})for(int depth=1;depth<=2;depth++)
        {
            var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="volos";own.Health=30;own.Gems=1;own.HeroAbilityUsedThisTurn=false;RezAudit.Refresh(root);
            string Hand(ShardsPlayer p)=>string.Join(",",p.Hand.Select(c=>c.DefId).OrderBy(x=>x,StringComparer.Ordinal));
            var labels=Enumerable.Range(0,4).Select(w=>Hand(TacticalSearch.PublicWorld(root,713101+w*7919,FastCopy.Copy).Engine.State.Players[1-seat])).Distinct().OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(labels.Length<2)throw new Exception("Adversarial menu fixture lacks hidden-hand variation");
            Prediction Predict(Adapter g)
            {
                var p=new float[64];
                for(int a=0;a<g.VisibleCount;a++)
                    p[a]=g.Visible(a).Action is ShardsEndTurnAction||g.Visible(a).Option?.DefId==VolosAbilityChoice.FacePrefix+"1"?100:.001f;
                float total=p.Sum();for(int a=0;a<64;a++)p[a]/=total;
                float value;
                if(!conflicting)value=(g.Actor==seat?1:-1)*g.Engine.State.Players[seat].Health*.01f;
                else if(g.Actor==seat)value=0;
                else
                {
                    // At this leaf the opponent is the observing actor, so their
                    // own hand is a legitimate model input. Root Volos cannot see
                    // it. Favor opposing menu choices for different hidden hands.
                    bool healingFavored=Hand(g.Engine.State.Players[g.Actor])==labels[0];
                    bool healed=g.Engine.State.Players[seat].Health>30;
                    bool attacked=g.Engine.State.Players[g.Actor].Health<50;
                    float rootValue=healed?(healingFavored?.8f:-.8f):attacked?(healingFavored?-.6f:.6f):-.9f;
                    value=-rootValue;
                }
                return new Prediction{P=p,V=value};
            }
            var prediction=Predict(root);int fallback=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
            var settings=new PolicySearchSettings{Candidates=64,Worlds=4,Depth=24,Workers=1,TerminalNodes=0,Prior=0,MenuDepth=depth};
            var planner=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            ulong before=TacticalSearch.Fingerprint(root);
            int chosen=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(!(root.Visible(chosen).Action is ShardsHeroAbilityAction))throw new Exception("Menu improvement failed to discover the useful activation");
            var leaves=JArray.FromObject(planner.DebugLeaves).Where(l=>l["path"][0].Value<string>().Contains("ShardsHeroAbilityAction")).ToArray();
            var choices=leaves.Select(l=>l["path"][1].Value<string>()).Distinct().ToArray();
            if(leaves.Length!=4||choices.Length!=1)throw new Exception("Hidden worlds chose incompatible actions at the same visible menu");
            if(!conflicting&&!choices[0].EndsWith(VolosAbilityChoice.FacePrefix+"0",StringComparison.Ordinal))throw new Exception("Failed to choose free healing over the bad greedy power mode");
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Nested menu search mutated source");
            var changed=FastCopy.Copy(root);var enemy=changed.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {
                var card=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=card;
                enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;
            }
            var second=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            int other=second.Choose(new[]{changed},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(chosen!=other||JsonConvert.SerializeObject(planner.DebugLeaves)!=JsonConvert.SerializeObject(second.DebugLeaves))throw new Exception("Nested menu planning depends on the actual enemy hand allocation");
            var concurrentSettings=settings.ValidatedCopy();concurrentSettings.Workers=8;
            var concurrent=new HybridLookahead(concurrentSettings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            int parallelChoice=concurrent.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(chosen!=parallelChoice||JsonConvert.SerializeObject(planner.DebugLeaves)!=JsonConvert.SerializeObject(concurrent.DebugLeaves))throw new Exception("Parallel menu simulation differs from the serial result");
            rows.Add(new{seat,depth,conflicting,hiddenHands=labels.Length,choice=choices[0],passed=true});
        }
        for(int seat=0;seat<2;seat++)for(int depth=1;depth<=2;depth++)
        {
            var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="decima";own.Mastery=10;own.RelicRecruited=true;own.Deck.Clear();own.Discard.Clear();
            for(int i=0;i<5;i++)own.Deck.Add(new ShardsCard{InstanceId=root.Engine.State.NextInstanceId++,DefId="crystal",Owner=seat,Zone=ShardsZone.Deck});
            RezAudit.Add(root,"cinder_scars_duel",seat);RezAudit.Refresh(root);
            Prediction Predict(Adapter g)
            {
                var p=new float[64];
                for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsEndTurnAction||g.Visible(a).Kind==13?100:.001f;
                float total=p.Sum();for(int a=0;a<64;a++)p[a]/=total;
                var player=g.Engine.State.Players[seat];
                float count=player.Hand.Count+player.Deck.Count+player.Discard.Count+player.PlayZone.Count;
                return new Prediction{P=p,V=(g.Actor==seat?1:-1)*(1-count*.1f)};
            }
            int fallback=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
            var planner=new HybridLookahead(new PolicySearchSettings{Candidates=64,Worlds=4,Depth=24,Workers=1,TerminalNodes=0,Prior=0,MenuDepth=depth},gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            ulong before=TacticalSearch.Fingerprint(root);
            int chosen=planner.Choose(new[]{root},new[]{Predict(root)},new[]{fallback},new[]{true})[0];
            if(ReviewRecorder.Name(root,root.Visible(chosen))!="play:cinder_scars_duel")throw new Exception("Did not discover draw-then-banish sequence");
            var leaves=JArray.FromObject(planner.DebugLeaves).Where(l=>l["path"][0].Value<string>().Contains("ShardsPlayCardAction")).ToArray();
            var targets=leaves.Select(l=>l["path"][1].Value<string>()).ToArray();
            if(targets.Length!=4||targets.Distinct().Count()<2||targets.Any(key=>!key.EndsWith(":crystal",StringComparison.Ordinal)))throw new Exception("Equivalent drawn cards were not matched using their semantic menu positions");
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Draw-menu search mutated source");
            rows.Add(new{seat,depth,caseId="drawn-identical-cards-different-instance-ids",distinctTargets=targets.Distinct().Count(),passed=true});
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,rows},Formatting.Indented));
    }
}
