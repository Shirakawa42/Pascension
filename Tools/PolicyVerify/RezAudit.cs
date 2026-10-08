using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Diagnostic fixtures are deliberately constructed; all subsequent decisions go
// through the real adapter/engine and the shipped frozen policy. No training.
internal static class RezAudit
{
    private static FrozenPolicy Policy;
    private static readonly List<object> Rows=new();
    private static readonly List<object> Checks=new();
    internal static Adapter Game(int mastery=5,bool gate=false,int power=0,string[] top=null,ulong seed=270927,int seat=0,string relic="slipstream_shard_duel")
    {
        ShardsContentRegistry.EnsureRegistered(); Encoder.Initialize();
        var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="rez"},new(){Name="P1",CharacterId="kosynwu"}},ShardsDlc.Duel);
        var g=new Adapter(new ShardsEngine(config));
        g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
        foreach(string hero in seat==0?new[]{"kosynwu","rez"}:new[]{"rez","kosynwu"})Step(g,c=>c.Option?.DefId==hero);
        g.Engine.State.TurnPlayerIndex=seat;
        var p=g.Engine.State.Players[seat];p.Mastery=mastery;p.Hand.Clear();p.Gems=0;p.Power=power;p.FocusedThisTurn=true;
        g.Engine.State.DestinyRow.Clear();
        if(mastery>=10)
        {
            Refresh(g);
            Step(g,c=>c.Action is ShardsRecruitRelicAction a&&p.SetAside.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==relic));
        }
        if(gate)Add(g,"doom_gate",1-seat,ShardsZone.Champions);
        if(top!=null)Top(g,top);
        Refresh(g);return g;
    }
    internal static void Refresh(Adapter g)
    {
        g.Engine.State.InvalidateCardIndex();
        typeof(ShardsEngine).GetMethod("RoutePriority",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(g.Engine,null);
        g.Rebuild();
    }
    internal static ShardsCard Add(Adapter g,string id,int seat=0,ShardsZone zone=ShardsZone.Hand)
    {
        var p=g.Engine.State.Players[seat];
        // Move a hero's physical relic instead of duplicating it in SetAside.
        var c=p.SetAside.FirstOrDefault(x=>x.DefId==id);
        if(c!=null)p.SetAside.Remove(c);
        else if(ShardsCardDatabase.Get(id).Type==ShardsCardType.Relic&&(c=p.Discard.FirstOrDefault(x=>x.DefId==id))!=null)p.Discard.Remove(c);
        else c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=seat,Zone=zone};
        c.Owner=seat;c.Zone=zone;
        if(zone==ShardsZone.Hand)p.Hand.Add(c);else if(zone==ShardsZone.Champions)p.Champions.Add(c);else throw new Exception("Fixture zone");
        Refresh(g);return c;
    }
    internal static void Top(Adapter g,params string[] ids)
    {
        var deck=g.Engine.State.CenterDeck;var cards=new List<ShardsCard>();
        foreach(string id in ids)
        {
            var c=deck.FirstOrDefault(x=>x.DefId==id);
            if(c!=null)deck.Remove(c);else c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=-1,Zone=ShardsZone.CenterDeck};
            cards.Add(c);
        }
        cards.Reverse();deck.AddRange(cards);Refresh(g);
    }
    internal static void Step(Adapter g,Func<Candidate,bool> predicate)
    {
        for(int i=0;i<g.VisibleCount;i++)if(predicate(g.Visible(i))){g.Step(i);return;}
        throw new Exception("Missing fixture action; context="+g.Decision?.Context);
    }
    private static float[] Prob(Adapter g)
    {
        var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
        return Policy.Probabilities(o,c,m,out _);
    }
    internal static string Name(Adapter g,Candidate c)
    {
        if(c.Option!=null)return c.Option.DefId??c.Option.Label;
        if(c.Action is ShardsPlayCardAction a)return "play:"+g.Engine.State.Players[g.Actor].Hand.Single(x=>x.InstanceId==a.CardInstanceId).DefId;
        if(c.Action is ShardsHeroAbilityAction)return "hero_scry";
        if(c.Action is ShardsRerollRowAction r)return "reroll:"+r.SlotIndex;
        return c.Action?.GetType().Name??"kind:"+c.Kind;
    }
    private static void Record(string label,Adapter g)
    {
        var p=Prob(g);Rows.Add(new{label,mastery=g.Engine.State.Players[g.Actor].Mastery,context=g.Decision?.Context,
            known=g.Knowledge.For(g.Actor).ToArray(),actions=Enumerable.Range(0,g.VisibleCount).Select(i=>new{name=Name(g,g.Visible(i)),kind=g.Visible(i).Kind,probability=p[i]}).OrderByDescending(x=>x.probability).ToArray()});
    }
    private static void Check(string name,bool passed,object evidence)=>Checks.Add(new{name,passed,evidence});
    private static void Scry(Adapter g)=>Step(g,c=>c.Action is ShardsHeroAbilityAction);
    private static void Finish(Adapter g)=>Step(g,c=>c.Kind==13);
    private static void Play(Adapter g,string id)=>Step(g,c=>c.Action is ShardsPlayCardAction a&&g.Engine.State.Players[g.Actor].Hand.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==id));
    private static object ScryDistribution(Func<Adapter> factory)
    {
        var outcomes=new Dictionary<int,double>();var root=factory();var ids=root.Decision.Options.Select(o=>o.DefId).ToArray();
        void Visit(List<int> prefix,double weight)
        {
            var g=factory();foreach(int option in prefix)Step(g,c=>c.Option?.Id==option);
            int subset=0;foreach(int id in prefix)subset|=1<<root.Decision.Options.FindIndex(o=>o.Id==id);
            if(g.Decision?.Context!="soi.scry"){outcomes.TryGetValue(subset,out double old);outcomes[subset]=old+weight;return;}
            var p=Prob(g);
            for(int i=0;i<g.VisibleCount;i++)
            {
                var c=g.Visible(i);double chance=weight*p[i];
                if(c.Kind==13){outcomes.TryGetValue(subset,out double old);outcomes[subset]=old+chance;}
                else{var next=new List<int>(prefix){c.Option.Id};Visit(next,chance);}
            }
        }
        Visit(new List<int>(),1);
        return new{cards=ids,total=outcomes.Values.Sum(),bury_probability=Enumerable.Range(0,ids.Length).Select(i=>new{id=ids[i],probability=outcomes.Where(x=>(x.Key&(1<<i))!=0).Sum(x=>x.Value)}),subsets=outcomes.OrderByDescending(x=>x.Value).Select(x=>new{buried=Enumerable.Range(0,ids.Length).Where(i=>(x.Key&(1<<i))!=0).Select(i=>ids[i]),probability=x.Value})};
    }
    internal static void Run(FrozenPolicy policy,string output)
    {
        Policy=policy;
        foreach(int mastery in new[]{5,14,15,20})
        {
            var g=Game(mastery,top:new[]{"additri_gaiamancer","testudo_vanguard","wraethe_skirmisher_duel","korvus_legionnaire_duel"});Add(g,"longshot");
            Record("longshot_before_scry_M"+mastery,g);
            var p=Prob(g);float hp=0,lp=0;for(int i=0;i<g.VisibleCount;i++){if(g.Visible(i).Action is ShardsHeroAbilityAction)hp=p[i];if(Name(g,g.Visible(i))=="play:longshot")lp=p[i];}
            Check("Prefer free Scry before unknown Longshot M"+mastery,hp>lp,new{hero=hp,longshot=lp});
            Scry(g);Record("longshot_scry_choices_M"+mastery,g);
            Rows.Add(new{label="longshot_complete_scry_M"+mastery,distribution=ScryDistribution(()=>{var x=Game(mastery,top:new[]{"additri_gaiamancer","testudo_vanguard","wraethe_skirmisher_duel","korvus_legionnaire_duel"});Add(x,"longshot");Scry(x);return x;})});
        }
        foreach(bool gate in new[]{false,true})foreach(int power in new[]{0,20})
        {
            var g=Game(10,gate,power,new[]{"ingeminex_agony","wraethe_skirmisher_duel","korvus_legionnaire_duel"});Scry(g);
            Record("monster_scry_gate_"+gate+"_power_"+power,g);
            Rows.Add(new{label="monster_complete_scry_gate_"+gate+"_power_"+power,distribution=ScryDistribution(()=>{var x=Game(10,gate,power,new[]{"ingeminex_agony","wraethe_skirmisher_duel","korvus_legionnaire_duel"});Scry(x);return x;})});
        }
        foreach(int mastery in new[]{4,14})
        {
            var g=Game(mastery);Add(g,"longshot");g.Engine.State.Players[0].Gems=1;g.Engine.State.Players[0].FocusedThisTurn=false;Refresh(g);
            Record("focus_unlock_before_longshot_M"+mastery,g);
        }
        foreach(int mastery in new[]{10,19,20,30})
        {
            var g=Game(mastery,relic:"warpquartz_duel");Add(g,"warpquartz_duel");Add(g,"crystal");Record("warpquartz_before_crystal_M"+mastery,g);
            Play(g,"warpquartz_duel");Record("warpquartz_banish_crystal_M"+mastery,g);
        }
        foreach(int round in new[]{1,11})
        {
            var g=Game(20,relic:"warpquartz_duel");g.Engine.State.Round=round;g.Engine.State.Players[1].Health=3;
            var enemy=g.Engine.State.Players[1];
            foreach(var c in enemy.Hand.Concat(enemy.Deck).Concat(enemy.Discard))c.DefId="crystal";
            Add(g,"warpquartz_duel");Add(g,"crystal");Play(g,"warpquartz_duel");Record("warpquartz_crystal_banish_immediate_lethal_round_"+round,g);
            Step(g,c=>c.Option?.DefId=="crystal");if(g.Decision?.Context=="soi.banish")Finish(g);
            Step(g,c=>c.Action is ShardsEndTurnAction);
            for(int i=0;i<20&&!g.Engine.State.GameOver;i++){if(g.Candidates.Any(c=>c.Kind==13))Finish(g);else g.Step(0);}
            Check("Warpquartz crystal banish can deliver immediate lethal round "+round,g.Engine.State.GameOver&&g.Engine.State.WinnerIndex==0,new{winner=g.Engine.State.WinnerIndex});
        }
        {
            var g=Game(10,true,0,new[]{"ingeminex_agony","wraethe_skirmisher_duel","korvus_legionnaire_duel"});Scry(g);
            var probs=Prob(g);var before=Enumerable.Range(0,g.VisibleCount).ToDictionary(i=>Name(g,g.Visible(i)),i=>probs[i]);
            g.Candidates.Reverse();probs=Prob(g);
            double error=Enumerable.Range(0,g.VisibleCount).Max(i=>Math.Abs(before[Name(g,g.Visible(i))]-probs[i]));
            Check("Scry candidate permutation preserves card identity scores",error<0.0001,new{max_probability_error=error});
        }
        {
            var g=Game(top:new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"});Add(g,"longshot");
            var obs=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,obs,c,m);
            var deck=g.Engine.State.CenterDeck;var tmp=deck[deck.Count-1];deck[deck.Count-1]=deck[deck.Count-2];deck[deck.Count-2]=tmp;
            var changed=new float[3328];var cc=new float[2048];var mm=new float[64];Encoder.Encode(g,changed,cc,mm);
            Check("Unknown center order cannot leak into observation or actions",obs.SequenceEqual(changed)&&c.SequenceEqual(cc)&&m.SequenceEqual(mm),new{equal=true});
            Scry(g);Finish(g);Encoder.Encode(g,obs,c,m);
            tmp=deck[0];deck[0]=deck[1];deck[1]=tmp;
            var enemy=g.Engine.State.Players[1];tmp=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0]=tmp;tmp.Zone=ShardsZone.Deck;
            Encoder.Encode(g,changed,cc,mm);
            Check("Unrevealed tail and opponent hidden-zone assignment do not leak",obs.SequenceEqual(changed)&&c.SequenceEqual(cc)&&m.SequenceEqual(mm),new{equal=true});
        }
        {
            var g=WinningSetup();Record("known_bad_pair_scry_enables_immediate_mastery_win",g);
            Scry(g);Record("scry_to_find_known_mastery_win",g);
            foreach(string id in new[]{"additri_gaiamancer","testudo_vanguard"})Step(g,c=>c.Option?.DefId==id);
            Finish(g);Play(g,"longshot");Step(g,c=>c.Option?.DefId=="shard_abstractor");if(g.Decision?.Context=="soi.warp")Finish(g);
            Play(g,"infinity_shard");Step(g,c=>c.Action is ShardsEndTurnAction);
            for(int i=0;i<20&&!g.Engine.State.GameOver;i++){if(g.Candidates.Any(c=>c.Kind==13))Finish(g);else g.Step(0);}
            Check("Constructed Scry-Longshot-Infinity line wins immediately",g.Engine.State.GameOver&&g.Engine.State.WinnerIndex==0,new{winner=g.Engine.State.WinnerIndex,mastery=g.Engine.State.Players[0].Mastery});
        }
        foreach(bool good in new[]{false,true})
        {
            var g=Game(top:good?new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"}:new[]{"additri_gaiamancer","testudo_vanguard","wraethe_skirmisher_duel"});
            Add(g,"longshot");Scry(g);Finish(g);Record("longshot_known_pair_good_"+good,g);
            g.Knowledge.Clear();Record("longshot_same_state_erased_memory_good_"+good,g);
        }
        {
            var g=Game(top:new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"});Add(g,"crystal");Scry(g);Finish(g);
            var expected=g.Knowledge.For(0).ToArray();Check("Scry keeps all three revealed cards",expected.Length==3,expected);
            Check("Opponent does not receive private Scry",g.Knowledge.For(1).Count==0,g.Knowledge.For(1).ToArray());
            Play(g,"crystal");Check("Ordinary card preserves memory",expected.SequenceEqual(g.Knowledge.For(0)),g.Knowledge.For(0).ToArray());
            Add(g,"undergrowth_aspirant_duel");Add(g,"shardwood_guardian_duel");Play(g,"undergrowth_aspirant_duel");
            Check("Unrelated hand Unify reveal preserves center memory",expected.SequenceEqual(g.Knowledge.For(0)),g.Knowledge.For(0).ToArray());
        }
        {
            var g=Game(top:new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"});Add(g,"longshot");Scry(g);Finish(g);Play(g,"longshot");
            Check("Longshot retains known third center card",g.Knowledge.For(0).SequenceEqual(new[]{"command_seer_duel"}),g.Knowledge.For(0).ToArray());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{checks=Checks,scenarios=Rows},Formatting.Indented));
        Console.WriteLine(JsonConvert.SerializeObject(Checks));Console.WriteLine("Scenario details: "+output);
    }
    private static Adapter WinningSetup()
    {
        var g=Game(28,top:new[]{"additri_gaiamancer","testudo_vanguard","shard_abstractor","wraethe_skirmisher_duel"});
        var p=g.Engine.State.Players[0];p.Deck.Clear();p.Discard.Clear();p.RerollsThisTurn=1;
        Add(g,"index_of_futures");Add(g,"longshot");Add(g,"infinity_shard");Play(g,"index_of_futures");
        foreach(string id in new[]{"additri_gaiamancer","testudo_vanguard","shard_abstractor"})Step(g,c=>c.Option?.DefId==id);
        return g;
    }
    internal static void MemoryRegression()
    {
        var failures=new List<string>();
        var g=Game(top:new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"});
        Add(g,"undergrowth_aspirant_duel");Add(g,"shardwood_guardian_duel");Scry(g);Finish(g);
        var expected=g.Knowledge.For(0).ToArray();
        if(expected.Length!=3)throw new Exception("Invalid regression setup: missing Scry knowledge");
        Play(g,"undergrowth_aspirant_duel");
        if(!expected.SequenceEqual(g.Knowledge.For(0)))failures.Add("Unify erased unrelated center knowledge");
        g=Game(top:new[]{"wraethe_skirmisher_duel","korvus_legionnaire_duel","command_seer_duel"});
        Add(g,"longshot");Scry(g);Finish(g);Play(g,"longshot");
        if(!g.Knowledge.For(0).SequenceEqual(new[]{"command_seer_duel"}))failures.Add("Longshot erased the still-known third card");
        if(failures.Count>0)throw new Exception(string.Join("; ",failures));
        Console.WriteLine("Rez memory regression: both checks passed");
    }
}
