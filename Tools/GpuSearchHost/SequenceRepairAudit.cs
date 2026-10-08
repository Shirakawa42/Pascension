using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

internal static class SequenceRepairAudit
{
    static List<string> Path(Adapter root,params string[] names)
    {
        var g=FastCopy.Copy(root);var path=new List<string>();
        foreach(string name in names)
        {
            int action=Enumerable.Range(0,g.VisibleCount).First(a=>ReviewRecorder.Name(g,g.Visible(a))==name);
            path.Add(TacticalSearch.Key(g,action));g.Step(action);
        }
        return path;
    }
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        void Check(Adapter root,List<string> path,string expected,string scenario,int seat)
        {
            ulong before=TacticalSearch.Fingerprint(root);
            int action=SequenceRepairs.Improve(root,path,FastCopy.Copy);
            string actual=action<0?null:ReviewRecorder.Name(root,root.Visible(action));
            var hidden=FastCopy.Copy(root);hidden.Engine.State.Players[seat].Deck.Reverse();
            var enemy=hidden.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {var card=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=card;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
            int other=SequenceRepairs.Improve(hidden,path,FastCopy.Copy);
            bool passed=actual==expected&&other==action&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{scenario,seat,expected,actual,passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)foreach(bool discard in new[]{false,true})
        {
            var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="kosynwu";own.Gems=2;own.FocusedThisTurn=false;
            var crystal=RezAudit.Add(root,"crystal",seat);
            if(discard){own.Hand.Remove(crystal);crystal.Zone=ShardsZone.Discard;own.Discard.Add(crystal);}
            RezAudit.Add(root,"shadow_apostle",seat);RezAudit.Refresh(root);
            RezAudit.Step(root,c=>c.Action is ShardsPlayCardAction a&&root.Engine.State.FindCard(a.CardInstanceId).DefId=="shadow_apostle");
            var path=Path(root,"kind:13","ShardsFocusAction","hero:kosynwu","crystal");
            Check(root,path,"crystal","free-before-paid-"+(discard?"discard":"hand"),seat);
            Check(root,path.Take(2).ToList(),null,"no-later-payment",seat);
            var corrupt=path.ToList();corrupt[corrupt.Count-1]="12:0:0:0:999999:crystal";
            Check(root,corrupt,null,"exact-instance-required",seat);
            var paid=FastCopy.Copy(root);paid.Step(Enumerable.Range(0,paid.VisibleCount).Single(a=>paid.Visible(a).Kind==13));
            RezAudit.Step(paid,c=>c.Action is ShardsHeroAbilityAction);
            Check(paid,Path(paid,"kind:13","ShardsFocusAction"),null,"paid-preview-is-not-free",seat);
        }
        for(int seat=0;seat<2;seat++)foreach(int mastery in new[]{18,19,20})
        {
            var root=RezAudit.Game(mastery:mastery,seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="volos";
            foreach(string id in new[]{"longshot","cryptofist_monk","carnivorous_vine"})
            {
                var card=RezAudit.Add(root,id,seat);own.Hand.Remove(card);card.Zone=ShardsZone.PlayZone;
                own.PlayZone.Add(card);own.PlayedThisTurn.Add(card);own.CountFactionPlay(card.Def.Faction,true);
            }
            RezAudit.Add(root,"infinity_shard",seat);RezAudit.Add(root,"order_initiate_duel",seat);RezAudit.Refresh(root);
            var path=Path(root,"play:infinity_shard","play:order_initiate_duel","kind:13");
            Check(root,path,mastery<20?"play:order_initiate_duel":null,"optional-setup-mastery-"+mastery,seat);
            Check(root,path.Take(2).ToList(),null,"unresolved-menu-rejected",seat);
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Sequence repair failed {failures} checks");
    }
}
