using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class TerminalGuardAudit {
 internal static void Run(string output){
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(bool guarded in new[]{false,true}){
   var root=RezAudit.Game(mastery:25,power:20,seat:seat);var own=root.Engine.State.Players[seat];own.Hand.Clear();own.HeroAbilityUsedThisTurn=true;
   var enemy=root.Engine.State.Players[1-seat];enemy.Health=20;foreach(var card in enemy.Hand.Concat(enemy.Deck))card.DefId="crystal";
   RezAudit.Add(root,"kiln_drone",seat);RezAudit.Refresh(root);
   Prediction Predict(Adapter g){var p=new float[64];for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsEndTurnAction?1:0;return new Prediction{P=p,V=0};}
   int end=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
   ulong before=TacticalSearch.Fingerprint(root);int raw=TacticalSearch.Find(root,512,12,4,FastCopy.Copy,true);
   var planner=new HybridLookahead(new PolicySearchSettings{TacticalGuards=guarded,Candidates=4,Depth=1,Worlds=2,Workers=1,TerminalNodes=512},gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
   int chosen=planner.Choose(new[]{root},new[]{Predict(root)},new[]{end},new[]{true})[0];
   bool passed=SafeTurnGains.HasAlternative(root)&&raw==end&&(guarded?root.Visible(chosen).Action is ShardsPlayCardAction:chosen==end)&&before==TacticalSearch.Fingerprint(root);
   rows.Add(new{seat,guarded,raw=ReviewRecorder.Name(root,root.Visible(raw)),selected=ReviewRecorder.Name(root,root.Visible(chosen)),passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));if(failures>0)throw new Exception("Terminal guard audit failed "+failures);
 }
 internal static void Review(string replay,string reviews,string positions,string output,string policy){
  var model=new FrozenPolicy(File.ReadAllBytes(policy));
  Prediction[] Infer(Adapter[] games)=>games.Select(g=>{var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);var p=model.Probabilities(o,c,m,out float v);return new Prediction{P=p,V=v};}).ToArray();
  PositionReview.Run(replay,reviews,positions,output,(root,record)=>{
   var config=new PolicySearchSettings{Hybrid=true,TacticalGuards=true,MixedResourcePlans=true,MenuPlans=true,SetupPlans=true,OptionalChoices=true,RolloutStyles=4,Candidates=4,Depth=24,Worlds=2,Workers=1,TerminalNodes=512};
   var planner=new HybridLookahead(config,Infer,FastCopy.Copy);var p=Infer(new[]{root});int fallback=Array.IndexOf(p[0].P,p[0].P.Max());
   int selected=planner.Choose(new[]{root},p,new[]{fallback},new[]{true})[0];
   var played=FastCopy.Copy(root);planner.TransferPlan(root,played);int action=selected,seat=played.Actor;bool ended=false;var path=new List<object>();
   for(int n=0;n<128&&!ended;n++){
    var own=played.Engine.State.Players[seat];path.Add(new{action=ReviewRecorder.Name(played,played.Visible(action)),own.Health,own.Mastery,own.Gems,own.Power});
    int log=played.Engine.Log.Count;played.Step(action);ended=played.Engine.State.GameOver;
    for(int e=log;e<played.Engine.Log.Count;e++)if(played.Engine.Log[e] is ShardsTurnStartedEvent)ended=true;
    if(ended)break;var next=Infer(new[]{played});int greedy=Array.IndexOf(next[0].P,next[0].P.Max());action=planner.Choose(new[]{played},next,new[]{greedy},new[]{true})[0];
   }
   return new{gain=SafeTurnGains.HasAlternative(root),rootValue=p[0].V,selected=ReviewRecorder.Name(root,root.Visible(selected)),path,ended,played.Engine.State.GameOver,played.Engine.State.WinnerIndex};
  });
 }
}
