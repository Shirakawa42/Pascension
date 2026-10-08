using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
internal static class PublicIdentityAudit
{
 static IEnumerable<ShardsCard> Cards(Adapter g)=>g.Engine.State.Players.SelectMany(p=>p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone).Concat(p.Champions).Concat(p.Destinies).Concat(p.SetAside))
  .Concat(g.Engine.State.CenterDeck).Concat(g.Engine.State.CenterRow.Where(c=>c!=null)).Concat(g.Engine.State.DestinyDeck).Concat(g.Engine.State.DestinyRow).Concat(g.Engine.State.ActiveMonsters).Concat(g.Engine.State.Banished);
 internal static void Run(string output)
 {
  var rows=new List<object>();int failures=0;
  for(int seat=0;seat<2;seat++)foreach(bool compiled in new[]{false,true})foreach(string kind in new[]{"known-center","owned-center-range","owned-destiny-range","future-allocation"})
  {
   var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
   var card=kind=="known-center"?root.Engine.State.CenterDeck.Last():RezAudit.Add(root,"crystal",seat);
   card.InstanceId=kind=="known-center"?100000+root.Engine.State.CenterDeck.Count-2:kind=="owned-center-range"?100002:kind=="owned-destiny-range"?110002:200000;
   root.Engine.State.NextInstanceId=card.InstanceId+1;
   if(kind=="known-center")((List<string>)root.Knowledge.For(seat)).Add(card.DefId);
   RezAudit.Refresh(root);var original=Cards(root).ToArray();if(original.Select(c=>c.InstanceId).Distinct().Count()!=original.Length)throw new Exception("Invalid source fixture");
   ulong before=TacticalSearch.Fingerprint(root);Func<Adapter,Adapter> copy=compiled?FastCopy.Copy:TacticalSearch.Copy;
   bool unique=true,allocation=true,knowledge=true;var world=root;
   for(int n=0;n<4;n++)
   {
    int previousNext=world.Engine.State.NextInstanceId;world=TacticalSearch.PublicWorld(world,713101+n*7919,copy);var cards=Cards(world).ToArray();
    unique&=cards.Select(c=>c.InstanceId).Distinct().Count()==cards.Length;
    allocation&=world.Engine.State.NextInstanceId>cards.Max(c=>c.InstanceId)&&world.Engine.State.NextInstanceId>=previousNext;
    if(kind=="known-center")knowledge&=world.Engine.State.CenterDeck.Last().InstanceId==card.InstanceId&&world.Knowledge.For(seat).SequenceEqual(new[]{card.DefId});
   }
   bool passed=unique&&allocation&&knowledge&&before==TacticalSearch.Fingerprint(root);
   rows.Add(new{seat,compiled,kind,unique,allocation,knowledge,passed});if(!passed)failures++;
  }
  File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
  if(failures>0)throw new Exception("Public-world identity audit: "+failures+" failures");
 }
}
