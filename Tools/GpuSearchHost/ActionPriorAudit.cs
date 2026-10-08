using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class ActionPriorAudit
{
 internal static void Run(string output)
 {
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(double cap in new[]{0.0,6.0})foreach(int verification in new[]{0,8})
  {
   var root=RezAudit.Game(seat:seat,mastery:6);var own=root.Engine.State.Players[seat];own.CharacterId="kosynwu";own.HeroAbilityUsedThisTurn=true;
   var card=RezAudit.Add(root,"anomaly_cleric",seat);RezAudit.Refresh(root);
   Prediction Predict(Adapter g)
   {
    // Persistent public mastery survives turn cleanup and simulation copies.
    float value=g.Engine.State.Players[seat].Mastery>6?-.64f:-.78f;var p=new float[64];
    for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsEndTurnAction?1:g.Visible(a).Action is ShardsPlayCardAction?1e-12f:.00001f;
    return new Prediction{P=p,V=g.Actor==seat?value:-value};
   }
   int end=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
   var config=new PolicySearchSettings{Candidates=4,Depth=8,Worlds=2,Workers=1,TerminalNodes=0,ActionPriorCap=cap,PriorVerificationWorlds=verification};
   var planner=new HybridLookahead(config,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};ulong before=TacticalSearch.Fingerprint(root);
   int chosen=planner.Choose(new[]{root},new[]{Predict(root)},new[]{end},new[]{true})[0];
   bool verified=planner.VerifiedRoots==(cap>0&&verification>0?1:0);
   bool passed=verified&&before==TacticalSearch.Fingerprint(root)&&(cap==0?chosen==end:root.Visible(chosen).Action is ShardsPlayCardAction);
   rows.Add(new{seat,cap,verification,planner.VerifiedRoots,planner.VerificationChanges,selected=ReviewRecorder.Name(root,root.Visible(chosen)),leaves=planner.DebugLeaves,passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
  if(failures>0)throw new Exception("Action prior audit: "+failures+" failures");
 }
}
