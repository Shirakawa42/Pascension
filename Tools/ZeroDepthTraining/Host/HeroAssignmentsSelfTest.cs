using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class HeroAssignmentsSelfTest
    {
        private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Hero assignment selftest: "+message);}
        private static Adapter Assigned(ulong seed,bool automatic=false)
        {
            var game=new Adapter(new ShardsEngine(Program.Config(seed)),automaticSingletons:automatic);
            HeroAssignments.Apply(game,seed);return game;
        }
        private static void Submit(ShardsEngine engine,string hero)
        {
            var request=engine.PendingInput.Decision;var option=request.Options.Single(o=>o.DefId==hero&&!o.Disabled);
            var result=engine.Submit(new SubmitDecisionAction{PlayerIndex=request.PlayerIndex,
                Answer=new DecisionAnswer{DecisionId=request.Id,ChosenOptionIds=new List<int>{option.Id}}});
            Check(result.Accepted,"manual actual draft Submit rejected");
        }
        private static string TensorHash(Adapter game)
        {
            var values=new float[Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim+Encoder.MaxActions];
            Encoder.Encode(game,values.AsSpan(0,Encoder.ObsDim),values.AsSpan(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),values.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim));
            return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
        }
        private static void RealDraft(ulong seed)
        {
            var expected=HeroAssignments.ForSeed(seed);var game=Assigned(seed,automatic:true);
            var manual=new ShardsEngine(Program.Config(seed));ulong rngState=manual.State.Rng.State,rngInc=manual.State.Rng.Inc;
            Check(manual.PendingInput.Decision.PlayerIndex==1,"draft must start at seat1");Submit(manual,expected.Seat1);
            Check(manual.PendingInput.Decision.PlayerIndex==0,"draft must then reach seat0");Submit(manual,expected.Seat0);
            Check(game.Engine.State.ComputeHash()==manual.State.ComputeHash(),"forced/manual actual draft state parity");
            Check(game.Engine.State.Rng.State==manual.State.Rng.State&&game.Engine.State.Rng.Inc==manual.State.Rng.Inc,"forced/manual engine PCG parity");
            Check(rngState==game.Engine.State.Rng.State&&rngInc==game.Engine.State.Rng.Inc,"hero sampler/draft consumed engine PCG");
            Check(game.PolicyChoices==0&&game.WrapperSteps==0&&game.Submissions==2&&game.AutomaticallyApplied==0,"draft ownership/counter provenance");
            Check(game.Decision==null&&game.Actor==0&&game.Engine.State.TurnPlayerIndex==0,"initial draft did not finish into normal first turn");
            Check(TensorHash(game)==TensorHash(new Adapter(manual)),"forced/manual adapter knowledge/candidates/observation parity");
            var obs=new float[Encoder.ObsDim];Encoder.Encode(game,obs,new float[Encoder.MaxActions*Encoder.ActionDim],new float[Encoder.MaxActions]);
            for(int seat=0;seat<2;seat++)
            {
                var player=game.Engine.State.Players[seat];var ids=ShardsEngine.RelicIdsFor(player.CharacterId,game.Engine.State.Dlc);
                Check(player.SetAside.Select(c=>c.DefId).SequenceEqual(ids),"actual draft relic membership/order");
                Check(player.SetAside.All(c=>c.Owner==seat&&c.Zone==ShardsZone.SetAside),"actual draft relic owner/zone");
                Check(obs[16+seat*64+6]==(Shards.AI.Encoder.HeroIndex(player.CharacterId)+1)/5f,"assigned hero observation");
                foreach(string id in ids)
                    Check(obs[Encoder.HistogramOffset+(seat==0?6:12)*Encoder.CardCapacity+Encoder.CardIndex(id)]==0.1f,"assigned own/inferred enemy relic observation");
            }
            var events=Enumerable.Range(0,game.Engine.Log.Count).Select(i=>game.Engine.Log[i]).OfType<ShardsHeroDraftedEvent>().ToArray();
            Check(events.Length==2&&events[0].PlayerIndex==1&&events[0].CharacterId==expected.Seat1&&events[1].PlayerIndex==0&&events[1].CharacterId==expected.Seat0,"public drafted-event provenance/order");
        }
        private static string[] Batch(ulong seed,int count,int workers)
        {
            var result=new string[count];using var team=new LaneWorkers(workers);
            team.Run(count,lane=>
            {
                ulong gameSeed=unchecked(seed+(ulong)lane);var game=Assigned(gameSeed);
                result[lane]=$"{game.Engine.State.Players[0].CharacterId}/{game.Engine.State.Players[1].CharacterId}:{game.Engine.State.ComputeHash():x16}:{game.Engine.State.Rng.State:x16}:{game.Engine.State.Rng.Inc:x16}:{TensorHash(game)}";
            });return result;
        }
        private static void Abilities()
        {
            foreach(string hero in ShardsEngine.DraftableCharacters)
            {
                ulong seed=0;while(HeroAssignments.ForSeed(seed).Seat0!=hero)seed++;
                var game=Assigned(seed);var player=game.Engine.State.Players[0];var spec=ShardsEngine.HeroAbilityInfo(hero);
                int passiveBase=game.Engine.EffectiveCost(player,game.Engine.State.CenterRow.First(c=>c.Def.Cost>=2).Def);
                player.Mastery=5;player.Gems=10;player.Health=40;
                // This test raises resources directly; refresh the engine's own
                // priority action list before checking the ordinary wrapper.
                typeof(ShardsEngine).GetMethod("RoutePriority",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(game.Engine,null);
                game.Rebuild();
                Check(game.Candidates.Any(c=>c.Action is ShardsHeroAbilityAction)==spec.Active,"assigned hero activation legality");
                if(!spec.Active)
                {
                    Check(hero=="decima","unknown passive hero fixture");
                    Check(game.Engine.EffectiveCost(player,game.Engine.State.CenterRow.First(c=>c.Def.Cost>=2).Def)==Math.Max(0,passiveBase-ShardsEngine.DecimaFirstBuyDiscount),"assigned Decima passive");continue;
                }
                int hand=player.Hand.Count,gems=player.Gems,health=player.Health;
                game.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=0});
                Check(player.HeroAbilityUsedThisTurn&&player.Gems==gems-spec.Gems&&player.Health==health-spec.Health,"assigned hero actual activation/cost");
                if(hero=="tetra")Check(player.Hand.Count==hand+2,"assigned Tetra draws");
                else
                {
                    string context=hero=="volos"?"soi.volos":hero=="rez"?"soi.scry":"soi.banish";
                    Check(game.Decision?.Context==context,"assigned hero modal power");
                    var request=game.Decision;var revealed=request.Options.Select(o=>o.CardInstanceId).ToArray();
                    if(hero=="rez")Check(request.Options.Count==3,"assigned Rez Scry3 menu");
                    var chosen=hero=="rez"?new List<int>():new List<int>{request.Options.First(o=>!o.Disabled).Id};
                    game.ApplyExternal(new SubmitDecisionAction{PlayerIndex=0,Answer=new DecisionAnswer{DecisionId=request.Id,ChosenOptionIds=chosen}});
                    if(hero=="volos")Check(player.Health==health+3,"assigned Volos heal mode");
                    if(hero=="kosynwu")Check(game.Engine.State.Banished.Count==1&&player.Hand.Count==hand-1,"assigned KoSynWu banish mode");
                    if(hero=="rez")
                    {
                        Check(game.Decision==null&&game.Engine.State.CenterDeck.TakeLast(3).Reverse().Select(c=>c.InstanceId).SequenceEqual(revealed),"assigned Rez keep-all Scry3 order");
                        Check(game.Knowledge.Center[0].Count>=3&&game.Knowledge.Center[0].Count(f=>f.Min==f.Max&&!f.UncertainPresence)>=3,"assigned Rez remembered Scry3");
                    }
                }
                Check(ObservationFinite(game),"assigned hero power observation");
            }
        }
        private static bool ObservationFinite(Adapter game)
        {
            var obs=new float[Encoder.ObsDim];Encoder.Encode(game,obs,new float[Encoder.MaxActions*Encoder.ActionDim],new float[Encoder.MaxActions]);return obs.All(float.IsFinite);
        }
        internal static object Run()
        {
            Check(HeroAssignments.ParseMode(null)==HeroAssignments.Policy&&HeroAssignments.ParseMode("policy")==HeroAssignments.Policy,"legacy default policy mode");
            bool invalid=false;try{HeroAssignments.ParseMode("fixed");}catch(ArgumentException){invalid=true;}Check(invalid,"invalid mode accepted");
            var untouched=new Adapter(new ShardsEngine(Program.Config(314)));Check(untouched.Decision?.Context=="soi.herodraft"&&untouched.Submissions==0&&untouched.PolicyChoices==0,"legacy policy draft unavailable");
            var stalled=new Adapter(new ShardsEngine(Program.Config(315)),submit:action=>Pascension.Engine.Core.SubmitResult.Ok());
            bool bounded=false;try{HeroAssignments.Apply(stalled,315);}catch(InvalidOperationException e){bounded=e.Message.Contains("exceed two submissions");}
            Check(bounded&&stalled.Submissions==2,"stalled draft submission guard");
            Check(ShardsEngine.DraftableCharacters.SequenceEqual(new[]{"decima","tetra","volos","kosynwu","rez"}),"versioned sampler hero catalog/order changed");
            int[] golden={13,11,9,19,15,1,17,4,10,16,5,14,8,7,12,6,0,3,2,18,
                1,11,16,17,18,0,4,7,13,10,9,8,12,19,14,15,5,6,2,3};
            for(int seed=0;seed<golden.Length;seed++)Check(HeroAssignments.ForSeed((ulong)seed).Matchup==golden[seed],"versioned sampler golden seed vector");
            int scheduleChecks=0;
            foreach(ulong start in new[]{0UL,17UL,0x2000000000000000UL,ulong.MaxValue-5139UL})
            {
                var counts=new int[20];
                for(ulong index=0;index<5120;index++)
                {
                    var pair=HeroAssignments.ForSeed(start+index);Check(pair.Seat0!=pair.Seat1,"mirror matchup generated");
                    Check(ShardsEngine.DraftableCharacters.Contains(pair.Seat0)&&ShardsEngine.DraftableCharacters.Contains(pair.Seat1),"unknown hero generated");counts[pair.Matchup]++;scheduleChecks++;
                }
                Check(counts.All(n=>Math.Abs(n-256)<=1),"5120-game boundary window matchup imbalance");
            }
            var permutationStrings=new HashSet<string>();
            foreach(ulong cycle in new[]{0UL,1UL,2UL,1000UL,0x2000000000000000UL/20,ulong.MaxValue/20-1})
            {
                var seen=new HashSet<int>();var order=new List<int>();
                for(ulong slot=0;slot<20;slot++){var pair=HeroAssignments.ForSeed(cycle*20+slot);seen.Add(pair.Matchup);order.Add(pair.Matchup);RealDraft(cycle*20+slot);}
                Check(seen.Count==20,"cycle does not contain every ordered distinct matchup");permutationStrings.Add(string.Join(",",order));
            }
            Check(permutationStrings.Count>1,"cycles are not randomly reshuffled");
            int workerCases=0;
            foreach(ulong start in new[]{0UL,17UL,0x200000000000007fUL,ulong.MaxValue-127UL})
            {
                var single=Batch(start,128,1);Check(single.SequenceEqual(Batch(start,128,8)),"workers1vs8 raw/state/PCG equality");
                Check(single.SequenceEqual(Batch(start,128,8)),"repeat reset raw/state/PCG equality");
                var split=Batch(start,64,4).Concat(Batch(unchecked(start+64),64,4));Check(single.SequenceEqual(split),"batch128vs2x64 equality");
                var four=Enumerable.Range(0,4).SelectMany(i=>Batch(unchecked(start+(ulong)(32*i)),32,2));Check(single.SequenceEqual(four),"batch128vs4x32 equality");workerCases+=128;
            }
            Abilities();
            return new{passed=true,sampler_version=HeroAssignments.Version,schedule_window_seeds=scheduleChecks,real_draft_pairs=120,worker_reset_batch_cases=workerCases,
                distinct_test_cycle_permutations=permutationStrings.Count,heroes=ShardsEngine.DraftableCharacters,ordered_distinct_matchups=20,forced_submissions_per_game=2,forced_policy_choices_per_game=0,
                initial_seed_vectors=Enumerable.Range(0,80).Select(i=>{var pair=HeroAssignments.ForSeed((ulong)i);return new{seed=i,seat0=pair.Seat0,seat1=pair.Seat1,matchup=pair.Matchup};}).ToArray(),
                checks=new[]{"20 shuffled ordered distinct hero matchups per aligned cycle","boundary5120-game windows remain within one of each matchup's ideal count","actual reverse-seat draft submits match manual engine state and PCG exactly","real relics, public events, hero observations and live abilities","zero initial hero policy/owned choices","workers1vs8/reset/batch128vs2x64vs4x32 raw bit equality","legacy policy draft remains pending"}};
        }
    }
}
