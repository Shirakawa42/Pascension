using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class PublicTopAudit {
 internal static void Review(string replay,string reviews,string positions,string output){
  PositionReview.Run(replay,reviews,positions,output,(root,record)=>{
   int enemy=1-root.Actor;var known=root.Supplement.Top(enemy).ToArray();bool preserved=true;
   for(int w=0;w<16;w++){
    var g=TacticalSearch.PublicWorld(root,713101+w*7919,FastCopy.Copy);
    preserved&=g.Supplement.Top(enemy).SequenceEqual(known)&&g.Engine.State.Players[enemy].Deck.AsEnumerable().Reverse().Take(known.Length).Select(c=>c.DefId).SequenceEqual(known);
   }
   // Offline truth check only; this boolean never enters a policy or planner.
   bool consistent=root.Engine.State.Players[enemy].Deck.AsEnumerable().Reverse().Take(known.Length).Select(c=>c.DefId).SequenceEqual(known);
   if(!preserved||!consistent)throw new Exception("Public-top replay invariant failed");
   return new{knownEnemyTop=known,preserved,actualStateConsistent=consistent};
  });
 }
 internal static void Run(string output){
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(bool compiled in new[]{false,true})foreach(bool stacked in new[]{false,true}){
   var root=RezAudit.Game(seat:seat);var enemy=root.Engine.State.Players[1-seat];
   var shard=enemy.Hand.Concat(enemy.Deck).Single(c=>c.DefId=="infinity_shard");enemy.Hand.Remove(shard);enemy.Deck.Remove(shard);shard.Zone=ShardsZone.Deck;enemy.Deck.Add(shard);
   RezAudit.Add(root,"duplication_fabricator_duel",seat);RezAudit.Step(root,c=>c.Action is ShardsPlayCardAction a&&root.Engine.State.FindCard(a.CardInstanceId).DefId=="duplication_fabricator_duel");
   if(stacked){
    var card=new ShardsCard{InstanceId=root.Engine.State.NextInstanceId++,DefId="prism",Owner=1-seat,Zone=ShardsZone.Discard};enemy.Discard.Add(card);
    root.Supplement.BeforeSubmit(root.Engine.State.Players[0].Deck.Count,root.Engine.State.Players[1].Deck.Count,null,null);
    int log=root.Engine.Log.Count;enemy.Discard.Remove(card);card.Zone=ShardsZone.Deck;enemy.Deck.Add(card);
    root.Engine.Emit(new ShardsCardReturnedEvent{PlayerIndex=1-seat,InstanceId=card.InstanceId,DefId=card.DefId,ToDeckTop=true});root.Supplement.AfterSubmit(root.Engine,log);
   }
   var expected=stacked?new[]{"prism","infinity_shard"}:new[]{"infinity_shard"};
   bool learned=root.Supplement.Top(1-seat).SequenceEqual(expected);
   ((List<string>)root.Knowledge.For(1-seat)).Add("thornshell_warden");
   ulong before=TacticalSearch.Fingerprint(root);var changed=FastCopy.Copy(root);var hidden=changed.Engine.State.Players[1-seat];
   var hand=hidden.Hand[0];var deck=hidden.Deck[0];hidden.Hand[0]=deck;hidden.Deck[0]=hand;deck.Zone=ShardsZone.Hand;hand.Zone=ShardsZone.Deck;
   Func<Adapter,Adapter> copy=compiled?FastCopy.Copy:TacticalSearch.Copy;
   bool preserved=true,privateInvariant=true,privateCenterCleared=true;
   for(int n=0;n<32;n++){
    var world=TacticalSearch.PublicWorld(root,713101+n*7919,copy);var other=TacticalSearch.PublicWorld(changed,713101+n*7919,copy);var e=world.Engine.State.Players[1-seat];
    preserved&=world.Supplement.Top(1-seat).SequenceEqual(expected)&&e.Deck.AsEnumerable().Reverse().Take(expected.Length).Select(c=>c.DefId).SequenceEqual(expected)&&!e.Hand.Any(c=>expected.Contains(c.DefId))&&e.Hand.Count==enemy.Hand.Count&&e.Deck.Count==enemy.Deck.Count;
    privateInvariant&=TacticalSearch.Fingerprint(world)==TacticalSearch.Fingerprint(other);
    privateCenterCleared&=world.Knowledge.For(1-seat).Count==0;
   }
   bool passed=learned&&preserved&&privateInvariant&&privateCenterCleared&&before==TacticalSearch.Fingerprint(root)&&root.Supplement.Top(1-seat).Count==expected.Length;
   rows.Add(new{seat,compiled,stacked,learned,preserved,privateInvariant,privateCenterCleared,passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));if(failures>0)throw new Exception("Public top audit failed "+failures);
 }
}
