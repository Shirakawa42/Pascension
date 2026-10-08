using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class DefensiveWorldAudit
{
 internal static void Run(string output)
 {
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(bool ignore in new[]{false,true})foreach(bool knownTop in new[]{false,true})foreach(bool compiled in new[]{false,true})
  {
   var root=RezAudit.Game(seat:seat,mastery:6,power:20);var own=root.Engine.State.Players[seat];own.HeroAbilityUsedThisTurn=true;own.IgnoreShieldsThisTurn=ignore;
   var enemy=root.Engine.State.Players[1-seat];enemy.Hand.Clear();enemy.Deck.Clear();enemy.Discard.Clear();enemy.Champions.Clear();enemy.Mastery=20;enemy.Health=20;
   RezAudit.Add(root,"crystal",1-seat);
   for(int n=0;n<64;n++)enemy.Deck.Add(new ShardsCard{InstanceId=root.Engine.State.NextInstanceId++,DefId="crystal",Owner=1-seat,Zone=ShardsZone.Deck});
   var shield=RezAudit.Add(root,"datic_robes_duel",1-seat);enemy.Hand.Remove(shield);shield.Zone=ShardsZone.Deck;enemy.Deck.Add(shield);RezAudit.Refresh(root);
   if(knownTop)((List<string>)root.Supplement.Top(1-seat)).Add("datic_robes_duel");
   int end=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsEndTurnAction);
   int sampled=Enumerable.Range(0,4).Count(w=>TacticalSearch.PublicWorld(root,713101+w*7919,FastCopy.Copy).Engine.State.Players[1-seat].Hand.Any(c=>c.DefId=="datic_robes_duel"));
   if(sampled!=0)throw new Exception("Rare defensive fixture must evade the four ordinary samples");
   Func<Adapter,Adapter> copy=compiled?FastCopy.Copy:TacticalSearch.Copy;
   ulong before=TacticalSearch.Fingerprint(root);var stress=TacticalSearch.DefensiveWorld(root,713101,copy);
   var hidden=copy(root);var foe=hidden.Engine.State.Players[1-seat];int swap=knownTop?0:foe.Deck.FindIndex(c=>c.DefId=="datic_robes_duel");
   (foe.Hand[0],foe.Deck[swap])=(foe.Deck[swap],foe.Hand[0]);foe.Hand[0].Zone=ShardsZone.Hand;foe.Deck[swap].Zone=ShardsZone.Deck;hidden.Engine.State.InvalidateCardIndex();
   bool invariant=TacticalSearch.Fingerprint(stress)==TacticalSearch.Fingerprint(TacticalSearch.DefensiveWorld(hidden,713101,copy));
   int shields=stress.Engine.State.Players[1-seat].Hand.Sum(c=>stress.Engine.ShieldValue(stress.Engine.State.Players[1-seat],c));
   bool accepted=TacticalSearch.AcceptWinningLine(root,new List<string>{TacticalSearch.Key(root,end)},copy);
   bool expected=ignore||knownTop;bool passed=invariant&&shields==(knownTop?0:20)&&accepted==expected&&before==TacticalSearch.Fingerprint(root);
   rows.Add(new{seat,ignore,knownTop,compiled,invariant,shields,sampled,accepted,expected,passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
  if(failures>0)throw new Exception("Defensive validation audit: "+failures+" failures");
 }
}
