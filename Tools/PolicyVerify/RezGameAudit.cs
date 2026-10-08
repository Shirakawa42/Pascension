using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Independent diagnostic games, not a replay of the published stochastic cohort.
// Hidden center order is used ONLY by assertions auditing remembered facts; it is
// never passed to the policy or used to choose an action.
internal static class RezGameAudit
{
    internal static void Run(FrozenPolicy policy,string output,int games)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var records=new ConcurrentBag<object>();int done=0;
        Parallel.For(0,games,new ParallelOptions{MaxDegreeOfParallelism=8},index=>
        {
            ulong seed=0x7b00000000000000UL+(ulong)index;
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(config));g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
            int rez=index%2;string opponent=new[]{"decima","tetra","volos","kosynwu"}[(index/2)%4];
            foreach(string h in new[]{rez==1?"rez":opponent,rez==0?"rez":opponent})RezAudit.Step(g,c=>c.Option?.DefId==h);
            var rng=new Random(271000+index);var counts=new Dictionary<string,double>();var examples=new List<object>();bool firstRelic=true;
            Action<string,double> add=(k,v)=>{counts.TryGetValue(k,out double old);counts[k]=old+v;};
            var obs=new float[3328];var candidates=new float[2048];var mask=new float[64];
            while(!g.Engine.State.GameOver&&!g.Truncated)
            {
                Encoder.Encode(g,obs,candidates,mask);var probabilities=policy.Probabilities(obs,candidates,mask,out _);
                double random=rng.NextDouble();int chosen=-1;for(int i=0;i<g.VisibleCount;i++){chosen=i;random-=probabilities[i];if(random<0)break;}
                var selected=g.Visible(chosen);int actor=g.Actor;var p=g.Engine.State.Players[actor];
                var memory=g.Knowledge.For(rez).ToArray();var top=g.Engine.State.CenterDeck.AsEnumerable().Reverse().Take(4).Select(c=>c.DefId).ToArray();
                if(!memory.SequenceEqual(top.Take(memory.Length)))throw new Exception("False center memory seed="+seed);
                int log=g.Engine.Log.Count;string played=null;
                if(selected.Action is ShardsPlayCardAction play)played=p.Hand.Single(c=>c.InstanceId==play.CardInstanceId).DefId;
                if(actor==rez)
                {
                    if(firstRelic&&g.Candidates.Any(c=>c.Action is ShardsRecruitRelicAction))
                    {
                        firstRelic=false;float all=0,warp=0;int choices=0;
                        for(int i=0;i<g.VisibleCount;i++)if(g.Visible(i).Action is ShardsRecruitRelicAction a)
                        {choices++;all+=probabilities[i];if(p.SetAside.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId=="warpquartz_duel"))warp=probabilities[i];}
                        add("first_relic_opportunities",1);add("first_relic_choice_count_sum",choices);add("first_relic_conditional_warp_probability_sum",warp/all);
                    }
                    int hero=-1,longshot=-1;
                    for(int i=0;i<g.VisibleCount;i++)
                    {
                        var c=g.Visible(i);
                        if(c.Action is ShardsHeroAbilityAction)hero=i;
                        if(c.Action is ShardsPlayCardAction a&&p.Hand.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId=="longshot"))longshot=i;
                    }
                    if(hero>=0&&longshot>=0)
                    {
                        add("longshot_scry_opportunities",1);add("longshot_probability_sum",probabilities[longshot]);add("scry_probability_sum",probabilities[hero]);
                        if(chosen==longshot){add("longshot_chosen_before_scry",1);if(memory.Length<2)add("unknown_longshot_before_scry",1);}
                        if(chosen==hero)add("scry_chosen_with_longshot",1);
                    }
                    if(longshot>=0&&memory.Length>=2)
                    {
                        var blank=(float[])obs.Clone();for(int i=0;i<12;i++)blank[HeroFeatures.Slots[i]]=0;
                        var ablated=policy.Probabilities(blank,candidates,mask,out _);
                        string tag=memory.Take(2).All(id=>{var d=ShardsCardDatabase.Get(id);return !d.IsChampion&&!d.IsMonster&&!d.CannotBeFastPlayed&&d.Cost<=(p.Mastery>=15?5:3);})?"known_good_pair":"known_incompatible_pair";
                        add(tag+"_states",1);add(tag+"_play_probability",probabilities[longshot]);add(tag+"_blank_play_probability",ablated[longshot]);
                    }
                    if(hero>=0&&selected.Action is ShardsRerollRowAction&&ShardsEngine.RerollCost(p)==0){add("free_reroll_before_scry",1);if(memory.Length==0)add("unknown_free_reroll_before_scry",1);}
                    if(selected.Action is ShardsHeroAbilityAction)add("hero_scry_uses",1);
                    if(played=="longshot")add("longshot_hand_plays",1);
                    var decision=g.Decision;
                    if(decision?.Context=="soi.banish"&&g.Selected.Count==0&&SupplementKnowledge.AuthorizedSource(g)?.DefId=="warpquartz_duel")
                    {
                        add("warpquartz_banish_decisions",1);
                        bool crystal=decision.Options.Any(o=>o.DefId=="crystal");
                        if(crystal)
                        {
                            add("warpquartz_crystal_available",1);
                            for(int i=0;i<g.VisibleCount;i++)if(g.Visible(i).Kind==13)add("warpquartz_decline_probability_with_crystal",probabilities[i]);
                            if(selected.Kind==13)add("warpquartz_declined_with_crystal",1);
                        }
                    }
                    bool commits=decision?.Context=="soi.scry"&&(selected.Kind==13||selected.Kind==12&&g.Selected.Count+1==decision.Max);
                    if(commits)
                    {
                        var buried=new HashSet<int>(g.Selected);if(selected.Kind==12)buried.Add(selected.Option.Id);
                        bool gate=g.Engine.State.Players[1-rez].Champions.Any(c=>c.DefId=="doom_gate");
                        foreach(var option in decision.Options.Where(o=>ShardsCardDatabase.Get(o.DefId).IsMonster))
                        {
                            string tag=gate?"scry_monster_enemy_gate":"scry_monster_no_enemy_gate";add(tag+"_seen",1);if(buried.Contains(option.Id))add(tag+"_buried",1);
                            if(gate&&p.Power<7&&p.Hand.Count==0&&!p.Champions.Any(c=>!c.Exhausted))
                            {add("gate_low_power_empty_hand_monster_seen",1);if(buried.Contains(option.Id))add("gate_low_power_empty_hand_monster_buried",1);}
                        }
                    }
                }
                g.Step(chosen);
                if(actor==rez&&memory.Length>0&&g.Knowledge.For(rez).Count==0)
                {
                    bool reveal=false;for(int i=log;i<g.Engine.Log.Count;i++)if(g.Engine.Log[i] is ShardsCardsRevealedEvent)reveal=true;
                    var after=g.Engine.State.CenterDeck.AsEnumerable().Reverse().Take(memory.Length).Select(c=>c.DefId).ToArray();
                    if(reveal&&top.Take(memory.Length).SequenceEqual(after))
                    {
                        add("unrelated_reveal_memory_losses",1);
                        if(examples.Count<4)examples.Add(new{seed=seed.ToString("x16"),round=g.Engine.State.Round,played,before=memory,after=g.Knowledge.For(rez).ToArray()});
                    }
                    if(played=="longshot"&&memory.Length>2)add("longshot_lost_known_tail",1);
                }
            }
            if(g.Truncated)throw new Exception("Diagnostic game truncated");
            records.Add(new{seed=seed.ToString("x16"),rez_seat=rez,opponent,winner=g.Engine.State.WinnerIndex,round=g.Engine.State.Round,counts,examples});
            int n=System.Threading.Interlocked.Increment(ref done);if(n%100==0)Console.WriteLine("Rez diagnostic games: "+n+"/"+games);
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{games,training=false,published=false,records},Formatting.Indented));
        Console.WriteLine("Saved "+output);
    }
}
