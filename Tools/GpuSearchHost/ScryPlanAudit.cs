using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class ScryPlanAudit
{
 internal static Prediction Predict(Adapter g)
 {
  var p=new float[64];int chosen=-1;
  for(int a=0;a<g.VisibleCount;a++)
   if(g.Decision?.Context=="soi.scry"?(g.Selected.Count<2?g.Visible(a).Kind==12:g.Visible(a).Kind==13):g.Visible(a).Action is ShardsEndTurnAction){chosen=a;break;}
  if(chosen<0)chosen=0;p[chosen]=1;return new Prediction{P=p,V=0};
 }
 internal static void Run(string output)
 {
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(bool partial in new[]{false,true})foreach(bool compiled in new[]{false,true})
  {
   Func<Adapter,Adapter> copy=compiled?FastCopy.Copy:TacticalSearch.Copy;
   var root=RezAudit.Game(seat:seat,top:new[]{"cache_warden","the_rotten_duel","j_chord_duel"});
   RezAudit.Step(root,c=>c.Action is ShardsHeroAbilityAction);
   if(partial)RezAudit.Step(root,c=>c.Kind==12);
   var settings=new PolicySearchSettings{ScryPlans=true,OptionalChoices=true,ChoicePriorScale=2,EndTurnExtension=64,Candidates=4,Depth=4,Worlds=2,Workers=1,TerminalNodes=0};
   var planner=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),copy);
   var prediction=Predict(root);int fallback=Array.IndexOf(prediction.P,1f);ulong original=TacticalSearch.Fingerprint(root);
   var options=planner.BuildOptions(root,prediction,fallback);int expected=partial?5:16;
   bool complete=true;var finishes=new HashSet<string>();
   foreach(var option in options)
   {
    var g=TacticalSearch.PublicWorld(root,713101,copy);int request=g.Decision.Id;
    foreach(string key in option.Keys)
    {
     int action=Enumerable.Range(0,g.VisibleCount).FirstOrDefault(a=>TacticalSearch.Key(g,a)==key,-1);
     if(action<0){complete=false;break;}g.Step(action);
    }
    complete&=g.Decision?.Id!=request;finishes.Add(string.Join("|",option.Keys));
   }
   var altered=copy(root);var enemy=altered.Engine.State.Players[1-seat];altered.Engine.State.Players[seat].Deck.Reverse();
   if(enemy.Hand.Count>0&&enemy.Deck.Count>0){var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
   int known=root.Knowledge.For(seat).Count;
   for(int i=0;i<altered.Engine.State.CenterDeck.Count-known;i++)altered.Engine.State.CenterDeck[i].DefId=i%2==0?"crystal":"infinity_shard";
   var other=planner.BuildOptions(altered,Predict(altered),fallback);
   bool invariant=options.Select(o=>string.Join("|",o.Keys)).SequenceEqual(other.Select(o=>string.Join("|",o.Keys)));
   int chosen=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
   bool unchanged=original==TacticalSearch.Fingerprint(root);long submissions=root.Submissions;var path=new List<string>();
   for(int n=0;n<4&&root.Decision?.Context=="soi.scry";n++)
   {
    path.Add(ReviewRecorder.Name(root,root.Visible(chosen)));root.Step(chosen);
    if(root.Decision?.Context!="soi.scry")break;
    // Contradict the original plan after each partial selection. A cached
    // complete choice must retain its evaluated sequence until submission.
    int stop=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Kind==13);
    var hostile=new Prediction{P=new float[64],V=0};hostile.P[stop]=1;
    chosen=planner.Choose(new[]{root},new[]{hostile},new[]{stop},new[]{true})[0];
   }
   bool followed=root.Decision?.Context!="soi.scry"&&root.Knowledge.For(seat).SequenceEqual(new[]{"j_chord_duel"})&&root.Submissions-submissions==1;
   bool passed=options.Count==expected&&finishes.Count==expected&&complete&&invariant&&unchanged&&followed;
   rows.Add(new{seat,partial,compiled,expected,options=options.Count,complete,invariant,unchanged,followed,path,passed});if(!passed)failures++;
  }
  for(int seat=0;seat<2;seat++)
  {
   var root=RezAudit.Game(seat:seat,top:new[]{"cache_warden","the_rotten_duel","j_chord_duel"});
   int hero=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsHeroAbilityAction);
   var prediction=new Prediction{P=new float[64],V=0};prediction.P[hero]=1;
   var config=new PolicySearchSettings{MenuPlans=true,ScryPlans=true,Candidates=4,Workers=1};
   var planner=new HybridLookahead(config,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);ulong before=TacticalSearch.Fingerprint(root);
   var options=planner.BuildOptions(root,prediction,hero);var altered=FastCopy.Copy(root);altered.Engine.State.CenterDeck.Reverse();
   var other=planner.BuildOptions(altered,prediction,hero);
   bool passed=options.All(o=>!o.ScryPlan)&&options.Select(o=>string.Join("|",o.Keys)).SequenceEqual(other.Select(o=>string.Join("|",o.Keys)))&&before==TacticalSearch.Fingerprint(root)&&root.Knowledge.For(seat).Count==0;
   rows.Add(new{kind="no-plan-before-private-reveal",seat,passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
  if(failures>0)throw new Exception("Scry plan audit: "+failures+" failures");
 }
}
