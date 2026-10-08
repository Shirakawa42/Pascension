using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // These checks submit the actual card/hero/market actions and inspect the
    // neural tensor, rather than accepting a correct-looking knowledge ledger.
    internal static class VisibilitySelfTest
    {
        private static void Check(bool ok,string message)
        { if(!ok)throw new InvalidOperationException("Visibility selftest: "+message); }
        private static Adapter Playing(ulong seed)
        {
            var g=new Adapter(new ShardsEngine(Program.Config(seed)));
            while(g.Decision?.Context=="soi.herodraft")g.Step(g.ExerciseChoice());
            Check(g.Actor==0,"fixture starting seat");return g;
        }
        private static ShardsCard Card(Adapter g,string id,int owner,ShardsZone zone)
        {
            var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=owner,Zone=zone};
            g.Engine.State.InvalidateCardIndex();return c;
        }
        private static float[] Observation(Adapter g)
        {
            var obs=new float[Encoder.ObsDim];
            Encoder.Encode(g,obs,new float[Encoder.MaxActions*Encoder.ActionDim],new float[Encoder.MaxActions]);
            return obs;
        }
        private static List<float[]> Facts(Adapter g,int kind)
        {
            var obs=Observation(g);int n=(int)Math.Round(obs[167]*Encoder.Capacity);
            var rows=new List<float[]>();
            for(int i=0;i<n;i++)
            {
                var row=obs.Skip(Encoder.KnowledgeOffset+i*8).Take(8).ToArray();
                if(Math.Abs(row[1]-kind/3f)<0.00001f)rows.Add(row);
            }
            return rows;
        }
        private static float[] Fact(Adapter g,int kind,ShardsCard c)
            =>Facts(g,kind).SingleOrDefault(r=>r[7]==(c.InstanceId+1)/65536f);
        private static void Exact(Adapter g,int kind,ShardsCard c,int position,string message)
        {
            var row=Fact(g,kind,c);
            Check(row!=null&&row[0]==Encoder.CardCode(c.DefId)&&row[2]==position/384f
                &&row[3]==position/384f&&row[4]==1&&row[5]==0,message);
        }
        private static void Answer(Adapter g,params int[] ids)
        {
            Check(g.Decision!=null,"fixture expected an actual decision");
            g.ApplyExternal(new SubmitDecisionAction{PlayerIndex=g.Actor,
                Answer=new DecisionAnswer{DecisionId=g.Decision.Id,ChosenOptionIds=ids.ToList()}});
        }
        private static ShardsCard Play(Adapter g,string id)
        {
            var c=Card(g,id,g.Actor,ShardsZone.Hand);g.Engine.State.Players[g.Actor].Hand.Add(c);
            g.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=g.Actor,CardInstanceId=c.InstanceId});return c;
        }
        private static ShardsCard[] TopCenter(Adapter g,int count,bool distinct=true)
        {
            var deck=g.Engine.State.CenterDeck;
            var eligible=deck.Where(c=>!c.Def.IsMonster).ToArray();
            var chosen=(distinct?eligible.GroupBy(c=>c.DefId).Select(xs=>xs.First()):eligible.AsEnumerable()).Take(count).ToArray();
            Check(chosen.Length==count,"fixture center cards");
            foreach(var c in chosen)deck.Remove(c);
            for(int i=chosen.Length-1;i>=0;i--)deck.Add(chosen[i]);
            return chosen;
        }
        private static ShardsCard[] Reorder(Adapter g,int count=3)
        {
            var top=TopCenter(g,count);Play(g,"index_of_futures");
            Check(g.Decision?.Context=="soi.reorder","Index of Futures actual reorder menu");
            Check(g.Decision.Options.Select(o=>o.CardInstanceId).SequenceEqual(top.Select(c=>c.InstanceId)),"top-down visible menu");
            Answer(g,top.Select(c=>c.InstanceId).ToArray());return top;
        }
        private static int RerollSlot(Adapter g)
        { return Array.FindIndex(g.Engine.State.CenterRow,c=>c!=null&&!c.Def.CannotBeRerolled); }
        private static void Reroll(Adapter g)
        {
            g.Engine.State.Players[g.Actor].Gems=50;
            g.ApplyExternal(new ShardsRerollRowAction{PlayerIndex=g.Actor,SlotIndex=RerollSlot(g)});
        }
        private static void End(Adapter g)
        {
            int actor=g.Actor;g.Engine.State.Players[actor].Power=0;
            g.Engine.State.PendingMonsterAttacks.Clear();
            g.ApplyExternal(new ShardsEndTurnAction{PlayerIndex=actor});
            Check(g.Actor==1-actor&&g.Decision==null,"zero-power cleanup advances actual turn");
        }
        private static float Histogram(Adapter g,int channel,string id)
        { return Observation(g)[Encoder.HistogramOffset+channel*Encoder.CardCapacity+Encoder.CardIndex(id)]*10f; }
        internal static string[] Run()
        {
            FabricatorLifecycle();ReorderLifecycle();ScryBottomLifecycle();FreeRefillLifecycle();
            PrivateScryRefinement();DuplicateIdentityRefinement();PrivatePeekRefinement();PrivateReorderRefinement();TutorInvalidation();LongshotBottoms();KnownHandIdentity();PublicHandReveals();LongshotSameDefinition();RepeatedLegionReveal();
            return new[]{"Fabricator own/opponent tops: tensor persistence, draw/cleanup and hidden-hand facts",
                "Index of Futures top three across both turns, paid refill and public bottom order",
                "Scry selected bottom sequence and remaining known prefix in actual tensors",
                "free removal and multi-card monster bypass consume remembered prefixes",
                "private opponent Scry uncertainty and public-identity refinement without hidden-answer leakage",
                "same-definition physical identities refined independently after public refill",
                "own later private peek refines remembered ranges and confirms observed identities",
                "opponent private reorder keeps known prefix membership and public pops narrow its remaining positions",
                "personal Tutor shuffle invalidates top memory while public search result stays known",
                "Longshot's two publicly revealed returns retain both ordered bottom identities",
                "publicly played same-definition copy does not erase a different identified card still in hand",
                "actual Unify, Dominion, Riposte, Shard Seer and shields retain only the publicly shown hand identities",
                "Longshot same-definition fast-play/return retires the correct temporary physical record",
                "repeated Legion reveal after intermediate shuffle retains each physical card once and conserves owned counts"};
        }
        private static void FabricatorLifecycle()
        {
            var g=Playing(99001);var top=new ShardsCard[2];
            for(int p=0;p<2;p++)
            {
                var player=g.Engine.State.Players[p];
                foreach(var c in player.Deck){c.Zone=ShardsZone.Discard;player.Discard.Add(c);}player.Deck.Clear();
                for(int i=0;i<8;i++)player.Deck.Add(Card(g,"crystal",p,ShardsZone.Deck));
                top[p]=Card(g,"li_hin",p,ShardsZone.Deck);player.Deck.Add(top[p]);
            }
            Play(g,"duplication_fabricator_duel");
            Check(g.Decision==null,"noncopyable champion tops do not need a copy menu");
            Exact(g,2,top[0],0,"Fabricator own top not available to neural input");
            Exact(g,3,top[1],0,"Fabricator opponent top not available to neural input");
            g.Engine.State.Players[0].Gems=1;
            g.ApplyExternal(new ShardsFocusAction{PlayerIndex=0});
            Exact(g,2,top[0],0,"unrelated action lost known personal top");
            End(g);Exact(g,2,top[1],0,"opponent revealed top lost across unrelated player's cleanup");
            Check(Fact(g,3,top[0])==null,"drawn personal top remains falsely on deck");
            Check(Histogram(g,17,top[0].DefId)==1,"known opponent draw was forgotten at cleanup");
            End(g);
            Check(Fact(g,3,top[1])==null,"opponent drawn top remains falsely on deck");
            Check(Histogram(g,17,top[1].DefId)==1,"opponent's known drawn top not exposed as known hand");
            Check(Histogram(g,17,"crystal")==0,"unknown private draws leaked to remembered enemy hand");
            var baseline=Observation(g);var enemy=g.Engine.State.Players[1];
            int unknown=enemy.Hand.FindIndex(c=>c.InstanceId!=top[1].InstanceId);
            var saved=enemy.Hand[unknown];enemy.Hand[unknown]=enemy.Deck[0];enemy.Deck[0]=saved;
            Check(baseline.SequenceEqual(Observation(g)),"unknown hand/deck allocation leaks after a public known draw");
        }
        private static void ReorderLifecycle()
        {
            var g=Playing(99002);var top=Reorder(g);
            for(int i=0;i<3;i++)Exact(g,1,top[i],i,"remembered Index top three before movement");
            End(g);Check(Facts(g,1).Count==0,"private Index look leaked to the other deciding seat");
            End(g);for(int i=0;i<3;i++)Exact(g,1,top[i],i,"Index top three did not survive both turns");
            var bottom=g.Engine.State.CenterRow[RerollSlot(g)];Reroll(g);
            Check(Fact(g,1,top[0])==null,"paid refill did not consume the known top");
            Exact(g,1,top[1],0,"paid refill lost known second card");Exact(g,1,top[2],1,"paid refill lost known third card");
            Exact(g,1,bottom,g.Engine.State.CenterDeck.Count-1,"paid reroll's publicly bottomed card missing");
            Reroll(g);Exact(g,1,top[2],0,"successive public refill did not shift remaining top");
        }
        private static void ScryBottomLifecycle()
        {
            var g=Playing(99003);var top=TopCenter(g,3);var p=g.Engine.State.Players[0];p.CharacterId="rez";p.Mastery=5;
            g.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=0});
            Check(g.Decision?.Context=="soi.scry","Rez actual Scry menu absent");
            Exact(g,1,top[0],0,"Scry currently visible top absent from tensor");
            Answer(g,top[0].InstanceId,top[2].InstanceId);
            Exact(g,1,top[1],0,"Scry kept card should be known new top");
            Exact(g,1,top[0],g.Engine.State.CenterDeck.Count-2,"first selected Scry bottom position wrong");
            Exact(g,1,top[2],g.Engine.State.CenterDeck.Count-1,"last selected Scry card must become actual bottom");
            End(g);End(g);Exact(g,1,top[1],0,"unchanged Scry result forgotten next turn");
            Reroll(g);Check(Fact(g,1,top[1])==null,"Scry kept top not consumed");
            Exact(g,1,top[0],g.Engine.State.CenterDeck.Count-3,"Scry bottom order did not shift after a publicly inserted bottom");
            Exact(g,1,top[2],g.Engine.State.CenterDeck.Count-2,"second Scry bottom order did not shift after refill");
        }
        private static void FreeRefillLifecycle()
        {
            var g=Playing(99004);var top=Reorder(g);var bottom=g.Engine.State.CenterRow[RerollSlot(g)];
            Play(g,"order_initiate_duel");Check(g.Decision?.Context=="soi.removeshop","free removal decision absent");
            Answer(g,Array.IndexOf(g.Engine.State.CenterRow,bottom));
            Exact(g,1,top[1],0,"free row removal did not consume just its known top");
            Exact(g,1,top[2],1,"free row removal lost remaining prefix");
            Exact(g,1,bottom,g.Engine.State.CenterDeck.Count-1,"free removal omitted known bottom");
            // Real refill skips each Ingeminex to its public space before placing
            // the first ordinary card. All consumed prefix facts must disappear.
            var monster=g.Engine.State.CenterDeck.First(c=>c.Def.IsMonster);
            g.Engine.State.CenterDeck.Remove(monster);g.Engine.State.CenterDeck.Add(monster);
            g.Knowledge.Center[0].Clear();g.Knowledge.Center[1].Clear();
            Play(g,"index_of_futures");var ids=g.Decision.Options.Select(o=>o.Id).ToArray();
            var observed=g.Decision.Options.Select(o=>g.Engine.State.CenterDeck.Single(c=>c.InstanceId==o.CardInstanceId)).ToArray();Answer(g,ids);
            Check(observed[0].Def.IsMonster,"monster prefix fixture");Reroll(g);
            Check(Fact(g,1,observed[0])==null&&Fact(g,1,observed[1])==null,"monster-bypass refill did not consume both known tops");
            Exact(g,1,observed[2],0,"monster-bypass refill did not retain third known card");
            Check(g.Engine.State.ActiveMonsters.Any(c=>c.InstanceId==observed[0].InstanceId),"monster refill must expose actual public zone");
        }
        private static void PrivateScryRefinement()
        {
            var g=Playing(99005);var top=Reorder(g);End(g);
            var enemy=g.Engine.State.Players[1];enemy.CharacterId="rez";enemy.Mastery=5;
            g.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=1});
            Answer(g,top[0].InstanceId);End(g);
            foreach(var c in top)
            {
                var row=Fact(g,1,c);Check(row!=null&&row[4]==0&&row[5]==0,"private Scry should widen known positions without inventing absence");
            }
            var before=Observation(g);g.Engine.State.CenterDeck.Reverse();
            Check(before.SequenceEqual(Observation(g)),"uncertain center facts were repaired by peeking at hidden order");
            g.Engine.State.CenterDeck.Reverse();Reroll(g);
            Check(Fact(g,1,top[1])==null,"publicly consumed known physical identity should be removed from uncertain memory");
            foreach(var c in new[]{top[0],top[2]})
            {
                var row=Fact(g,1,c);Check(row!=null&&row[5]==0,"different public refill should preserve certainty that remembered card remains in center");
            }
        }
        private static void DuplicateIdentityRefinement()
        {
            var g=Playing(99006);var deck=g.Engine.State.CenterDeck;
            var pair=deck.Where(c=>!c.Def.IsMonster).GroupBy(c=>c.DefId).First(xs=>xs.Count()>1).Take(2).ToArray();
            var other=deck.First(c=>!c.Def.IsMonster&&c.DefId!=pair[0].DefId);
            var top=new[]{pair[0],pair[1],other};foreach(var c in top)deck.Remove(c);for(int i=2;i>=0;i--)deck.Add(top[i]);
            Play(g,"index_of_futures");Answer(g,top.Select(c=>c.InstanceId).ToArray());End(g);
            var enemy=g.Engine.State.Players[1];enemy.CharacterId="rez";enemy.Mastery=5;
            g.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=1});Answer(g);End(g);Reroll(g);
            Check(Fact(g,1,pair[0])==null,"public same-definition refill failed to retire matching physical identity");
            var remaining=Fact(g,1,pair[1]);Check(remaining!=null&&remaining[5]==0,"public refill retired or made absent the wrong same-definition instance");
        }
        private static void TutorInvalidation()
        {
            var g=Playing(99007);var p=g.Engine.State.Players[0];var top=Card(g,"li_hin",0,ShardsZone.Deck);p.Deck.Add(top);
            var other=g.Engine.State.Players[1];other.Deck.Add(Card(g,"li_hin",1,ShardsZone.Deck));
            Play(g,"duplication_fabricator_duel");Exact(g,2,top,0,"pre-Tutor known top");
            Play(g,"grim_tutor");Check(g.Decision?.Context=="soi.tutor","actual Tutor menu");
            int selected=g.Decision.Options[0].Id;string id=g.Decision.Options[0].DefId;Answer(g,selected);
            Check(Facts(g,2).Count==0,"Tutor shuffle retained known personal positions");
            End(g);Check(Histogram(g,17,id)==0,"previous known hand survived owner cleanup instead of its unknown fresh redraw");
        }
        private static void PrivatePeekRefinement()
        {
            var g=Playing(99008);var top=Reorder(g);End(g);
            var enemy=g.Engine.State.Players[1];enemy.CharacterId="rez";enemy.Mastery=5;
            g.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=1});Answer(g,top[0].InstanceId);End(g);
            var own=g.Engine.State.Players[0];own.CharacterId="rez";own.Mastery=5;
            g.ApplyExternal(new ShardsHeroAbilityAction{PlayerIndex=0});
            Exact(g,1,top[1],0,"later private peek failed to confirm known new top");
            Exact(g,1,top[2],1,"later private peek failed to confirm second known top");
            var unseen=Fact(g,1,top[0]);
            Check(unseen!=null&&unseen[2]>=3/384f&&unseen[5]==0,"later complete prefix observation failed to exclude those positions for unseen remembered card");
            Answer(g);
        }
        private static void PrivateReorderRefinement()
        {
            var g=Playing(99009);var top=Reorder(g);End(g);
            Play(g,"index_of_futures");Answer(g,top.Reverse().Select(c=>c.InstanceId).ToArray());End(g);
            foreach(var c in top)
            {
                var row=Fact(g,1,c);
                Check(row!=null&&row[2]==0&&row[3]==2/384f&&row[4]==0&&row[5]==0,
                    "opponent private reorder should retain each remembered top-three card within those three positions");
            }
            Reroll(g);Check(Fact(g,1,top[2])==null,"reordered prefix actual top did not retire");
            foreach(var c in top.Take(2))
            {
                var row=Fact(g,1,c);Check(row!=null&&row[2]==0&&row[3]==1/384f&&row[5]==0,
                    "public refill should narrow the reordered remaining prefix to two positions");
            }
            Reroll(g);Exact(g,1,top[0],0,"two public refills should establish exact remaining top of privately reordered prefix");
        }
        private static void LongshotBottoms()
        {
            var g=Playing(99010);var deck=g.Engine.State.CenterDeck;
            var top=deck.Where(c=>c.Def.IsChampion).GroupBy(c=>c.DefId).Select(xs=>xs.First()).Take(2).ToArray();
            Check(top.Length==2,"Longshot nonplayable fixture");
            foreach(var c in top)deck.Remove(c);for(int i=1;i>=0;i--)deck.Add(top[i]);
            Play(g,"longshot");Check(g.Decision==null,"nonplayable Longshot tops should resolve without a menu");
            Exact(g,1,top[0],deck.Count-2,"Longshot lost the first publicly returned bottom identity");
            Exact(g,1,top[1],deck.Count-1,"Longshot lost the second publicly returned bottom identity");
            Check(g.Knowledge.Detached.Count==0,"Longshot returns must retire temporary revealed records");
            End(g);
            Exact(g,1,top[0],deck.Count-2,"other seat must know first Longshot public bottom");
            Exact(g,1,top[1],deck.Count-1,"other seat must know second Longshot public bottom");
        }
        private static void KnownHandIdentity()
        {
            var g=Playing(99011);var opponent=g.Engine.State.Players[1];
            var known=opponent.Deck.First(c=>c.DefId=="crystal");opponent.Deck.Remove(known);opponent.Deck.Add(known);
            g.Engine.State.Players[0].Deck.Add(Card(g,"li_hin",0,ShardsZone.Deck));
            Play(g,"duplication_fabricator_duel");Check(g.Decision?.Context=="soi.copy","known-hand Fabricator copy menu");
            Answer(g,known.InstanceId);End(g);End(g);
            Check(Histogram(g,17,"crystal")==1,"known-hand fixture must identify one drawn crystal");
            End(g);var different=opponent.Hand.First(c=>c.DefId=="crystal"&&c.InstanceId!=known.InstanceId);
            g.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=1,CardInstanceId=different.InstanceId});
            Check(opponent.Hand.Any(c=>c.InstanceId==known.InstanceId),"identified crystal must remain physically in enemy hand");
            // Stop at the actual defender's decision, before attacker cleanup
            // clears its hand, so the deciding AI sees the adversary's held card.
            var shield=Card(g,"cryptofist_monk",0,ShardsZone.Hand);g.Engine.State.Players[0].Hand.Add(shield);
            opponent.Power=1;g.ApplyExternal(new ShardsEndTurnAction{PlayerIndex=1});
            if(g.Decision?.Context=="soi.split")Answer(g,0);
            Check(g.Actor==0&&g.Decision?.Context=="soi.shields","known-hand actual defender decision");
            Check(Histogram(g,17,"crystal")==1,"playing a different physical copy erased the still-held identified crystal");
            var held=Fact(g,4,known);
            Check(held!=null&&held[0]==Encoder.CardCode("crystal")&&held[2]==-1/384f&&held[3]==-1/384f
                &&held[4]==0&&held[5]==0&&held[6]==1,"already-public held identity absent or falsely assigned a private hand position");
            Answer(g);
            Check(g.Actor==0&&Fact(g,4,known)==null&&Histogram(g,17,"crystal")==0,
                "attacker cleanup/unknown reshuffle retained stale public hand identities");
        }
        private static void StopAtDefense(Adapter g,int attacker)
        {
            int defender=1-attacker;
            g.Engine.State.Players[defender].Hand.Add(Card(g,"cryptofist_monk",defender,ShardsZone.Hand));
            g.Engine.State.Players[attacker].Power=1;
            g.ApplyExternal(new ShardsEndTurnAction{PlayerIndex=attacker});
            if(g.Decision?.Context=="soi.split")Answer(g,defender);
            Check(g.Actor==defender&&g.Decision?.Context=="soi.shields","actual defending observer decision");
        }
        private static void PublicHandReveals()
        {
            var unify=Playing(99012);var p=unify.Engine.State.Players[0];
            var known=Card(unify,"thorn_zealot",0,ShardsZone.Hand);var different=Card(unify,"thorn_zealot",0,ShardsZone.Hand);
            p.Hand.Add(known);p.Hand.Add(different);Play(unify,"spore_cleric_duel");
            Check(unify.Knowledge.PublicHandIds[0].ContainsKey(known.InstanceId),"Unify publicly shown hand identity was not recorded");
            unify.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=different.InstanceId});StopAtDefense(unify,0);
            Check(Fact(unify,4,known)!=null&&Histogram(unify,17,known.DefId)==1,
                "Unify revealed held copy lost after another same-definition copy was played");

            var riposte=Playing(99013);p=riposte.Engine.State.Players[0];
            known=Card(riposte,"cryptofist_monk",0,ShardsZone.Hand);p.Hand.Add(known);
            Play(riposte,"riposte_doctrine");Check(riposte.Decision?.Context=="soi.reveal","Riposte public reveal decision");
            Answer(riposte,known.InstanceId);StopAtDefense(riposte,0);
            Check(Fact(riposte,4,known)!=null,"Riposte publicly shown hand identity not available to opponent neural input");

            var seer=Playing(99014);p=seer.Engine.State.Players[0];
            known=Card(seer,"infinity_shard",0,ShardsZone.Hand);p.Hand.Add(known);
            Play(seer,"shard_seer");Check(seer.Decision?.Context=="soi.reveal","Shard Seer public reveal decision");
            Answer(seer,known.InstanceId);StopAtDefense(seer,0);
            Check(Fact(seer,4,known)!=null,"Shard Seer publicly shown hand identity absent");

            var dominion=Playing(99015);p=dominion.Engine.State.Players[0];
            var shown=new[]{Card(dominion,"drakonarius",0,ShardsZone.Hand),Card(dominion,"thorn_zealot",0,ShardsZone.Hand),Card(dominion,"wraethe_skirmisher_duel",0,ShardsZone.Hand)};
            p.Hand.AddRange(shown);Play(dominion,"omnius");Check(dominion.Decision?.Context=="soi.reveal","Dominion public reveal decision");
            Answer(dominion,shown.Select(c=>c.InstanceId).ToArray());StopAtDefense(dominion,0);
            Check(shown.All(c=>Fact(dominion,4,c)!=null),"Dominion's public multi-card hand reveal identities absent");

            var shields=Playing(99016);StopAtDefense(shields,0);
            var option=shields.Decision.Options.First();known=shields.Engine.State.Players[1].Hand.Single(c=>c.InstanceId==option.CardInstanceId);
            Answer(shields,option.Id);StopAtDefense(shields,1);
            Check(Fact(shields,4,known)!=null,"defender's publicly revealed hand shield identity absent");
            Check(shields.Knowledge.PublicHandIds[1].Count==1,"passive or unrevealed shields leaked into public known hand identities");
        }
        private static void LongshotSameDefinition()
        {
            var g=Playing(99017);var deck=g.Engine.State.CenterDeck;
            var top=deck.Where(c=>c.DefId=="wraethe_skirmisher_duel").Take(2).ToArray();
            Check(top.Length==2,"same-definition Longshot fixture");
            foreach(var c in top)deck.Remove(c);for(int i=1;i>=0;i--)deck.Add(top[i]);
            Play(g,"longshot");Check(g.Decision?.Context=="soi.warp","same-definition Longshot pick menu");
            Answer(g,top[0].InstanceId);
            Check(g.Engine.State.Players[0].PlayZone.Any(c=>c.InstanceId==top[0].InstanceId&&c.FastPlayed),"Longshot first copy must be actual fast-play");
            Check(g.Knowledge.Detached.Count==0,"Longshot retired wrong same-definition temporary reveal identity");
            Exact(g,1,top[1],deck.Count-1,"same-definition Longshot return bottom identity missing");
            float expected=deck.Count(c=>c.DefId==top[0].DefId);
            Check(Math.Abs(Histogram(g,21,top[0].DefId)-expected)<0.00001f,
                "same-definition Longshot temporary record corrupts publicly derived center composition");
        }
        private static void RepeatedLegionReveal()
        {
            foreach(var variant in new (int size,bool champion)[]{(5,false),(7,false),(7,true)})
            {
                int size=variant.size;
                var g=Playing((ulong)(99018+size));var p=g.Engine.State.Players[0];p.Mastery=20;
                foreach(var c in p.Hand.Take(size-5).ToArray()){p.Hand.Remove(c);c.Zone=ShardsZone.Deck;p.Deck.Add(c);}
                if(variant.champion)
                {
                    var old=p.Deck.Last();p.Deck.Remove(old);old.Zone=ShardsZone.Hand;p.Hand.Add(old);
                    p.Deck.Add(Card(g,"li_hin",0,ShardsZone.Deck));
                }
                var original=p.Deck.ToArray();Check(original.Length==size,"repeated Legion deck fixture");
                Play(g,"duplication_fabricator_duel");Answer(g);
                var knownTop=original.Last();Exact(g,2,knownTop,0,"pre-repeated-Legion actual Fabricator top");
                var general=Play(g,"general_decurion");
                g.ApplyExternal(new ShardsExhaustAction{PlayerIndex=0,CardInstanceId=general.InstanceId});
                while(g.Decision?.Context=="soi.copy")Answer(g);
                Check(p.CopyHomodeusAlliesThisTurn,"actual Decurion must arrange a second Legion play-effect resolution");
                Play(g,"legion_carrier_duel");Check(g.Decision?.Context=="soi.reveal","first actual Legion reveal window");
                Check(Fact(g,2,knownTop)==null,"first temporary reveal retained old deck-top fact");
                Answer(g,variant.champion?new[]{knownTop.InstanceId}:Array.Empty<int>());
                Check(g.Decision?.Context=="soi.reveal","second actual Legion reveal window after discard shuffle");
                Check(Fact(g,2,knownTop)==null,"intermediate shuffle preserved the old exact top");
                if(variant.champion)
                    Check(g.Knowledge.PublicHandIds[0].ContainsKey(knownTop.InstanceId)&&p.Hand.Any(c=>c.InstanceId==knownTop.InstanceId),
                        "public discard release erased the separately selected champion's known hand identity");
                var held=g.Decision.Options.Select(o=>original.Single(c=>c.InstanceId==o.CardInstanceId)).ToArray();
                var obs=Observation(g);
                foreach(string id in original.Select(c=>c.DefId).Distinct())
                {
                    int ordinary=p.Deck.Concat(p.Hand).Concat(p.Discard).Concat(p.PlayZone.Where(c=>!c.FastPlayed)).Concat(p.Champions).Count(c=>c.DefId==id);
                    float actual=obs[Encoder.HistogramOffset+23*Encoder.CardCapacity+Encoder.CardIndex(id)]*10;
                    Check(Math.Abs(actual-ordinary-held.Count(c=>c.DefId==id))<0.00001f,"repeated Legion reveal double-counted owned collection");
                }
                Check(g.Knowledge.Detached.Count==held.Length&&g.Knowledge.Detached.Select(c=>c.InstanceId).Distinct().Count()==held.Length,
                    "repeated public revelation duplicated or retained released temporary physical cards");
                Check(g.Knowledge.Detached.All(c=>held.Any(h=>h.InstanceId==c.InstanceId)),"partial repeated reveal exposes a hidden-deck card as still temporary");
                var enemy=g.Engine.State.Players[1];var saved=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=saved;
                Check(obs.SequenceEqual(Observation(g)),"repeated reveal fix leaks hidden enemy hand/deck allocation");
                saved=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=saved;
                Answer(g);Check(g.Knowledge.Detached.Count==0,"resolved repeated reveal retained temporary cards");
                if(variant.champion)
                {
                    StopAtDefense(g,0);
                    Check(Fact(g,4,knownTop)!=null&&Histogram(g,17,knownTop.DefId)==1,
                        "selected champion's identified hand presence did not survive release and partial repeated reveal");
                }
            }
        }
    }
}
