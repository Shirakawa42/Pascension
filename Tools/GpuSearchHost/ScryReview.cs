using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class ScryReview
{
 internal static void Run(string replay,string reviews,string positions,string output,string policy,bool scryPlans=true)
 {
  var model=new FrozenPolicy(File.ReadAllBytes(policy));
  Prediction[] Infer(Adapter[] games)=>games.Select(g=>{var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);var p=model.Probabilities(o,c,m,out float v);return new Prediction{P=p,V=v};}).ToArray();
  PositionReview.Run(replay,reviews,positions,output,(root,record)=>{
   var results=new List<object>();
   foreach(string variant in new[]{"default","no-prior","depth64"})
   {
    var config=new PolicySearchSettings{Hybrid=true,TacticalGuards=true,MixedResourcePlans=true,MenuPlans=true,SetupPlans=true,OptionalChoices=true,ScryPlans=scryPlans,ChoicePriorScale=2,EndTurnExtension=64,RolloutStyles=4,Candidates=4,Depth=variant=="depth64"?64:24,Worlds=2,Workers=1,TerminalNodes=512,Prior=variant=="no-prior"?0:.015};
    var planner=new HybridLookahead(config,Infer,FastCopy.Copy){CaptureLeaves=true};var prediction=Infer(new[]{root})[0];int fallback=Array.IndexOf(prediction.P,prediction.P.Max());
    var options=planner.BuildOptions(root,prediction,fallback);
    int selected=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];var leaves=planner.DebugLeaves;
    var played=FastCopy.Copy(root);planner.TransferPlan(root,played);int action=selected;var path=new List<object>();
    for(int n=0;n<8;n++)
    {
     path.Add(new{action=ReviewRecorder.Name(played,played.Visible(action)),context=played.Decision?.Context});played.Step(action);
     if(played.Engine.State.GameOver||played.Decision?.Context!="soi.scry")break;
     var next=Infer(new[]{played})[0];int greedy=Array.IndexOf(next.P,next.P.Max());action=planner.Choose(new[]{played},new[]{next},new[]{greedy},new[]{true})[0];
    }
    var known=played.Knowledge.For(root.Actor).ToArray();object turn=null;
    if(variant=="default")
    {
     var continuation=new List<object>();bool ended=false;object first=null;
     for(int n=0;n<128&&!ended&&!played.Engine.State.GameOver;n++)
     {
      var next=Infer(new[]{played})[0];int greedy=Array.IndexOf(next.P,next.P.Max());var opts=n==0?planner.BuildOptions(played,next,greedy):null;int choice=planner.Choose(new[]{played},new[]{next},new[]{greedy},new[]{true})[0];
      if(n==0)first=new{options=opts.Select((o,i)=>new{option=i,o.Keys,o.Probability}).ToArray(),leaves=planner.DebugLeaves,priors=Enumerable.Range(0,played.VisibleCount).Select(a=>new{name=ReviewRecorder.Name(played,played.Visible(a)),p=next.P[a]}).ToArray()};
      var own=played.Engine.State.Players[root.Actor];continuation.Add(new{action=ReviewRecorder.Name(played,played.Visible(choice)),own.Health,own.Mastery,own.Gems,own.Power});
      int log=played.Engine.Log.Count;played.Step(choice);ended=played.Engine.State.GameOver;
      for(int e=log;e<played.Engine.Log.Count;e++)if(played.Engine.Log[e]is ShardsTurnStartedEvent)ended=true;
     }
     turn=new{first,path=continuation,ended,played.Engine.State.GameOver,played.Engine.State.WinnerIndex};
    }
    results.Add(new{variant,scryPlans,prediction.V,selected=ReviewRecorder.Name(root,root.Visible(selected)),priors=Enumerable.Range(0,root.VisibleCount).Select(a=>new{name=ReviewRecorder.Name(root,root.Visible(a)),p=prediction.P[a]}).ToArray(),options=options.Select((o,i)=>new{option=i,o.Keys,o.Probability}).ToArray(),leaves,path,known,turn});
   }
   return results;
  });
 }
}
