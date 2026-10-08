using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Fixtures control the starting state; every tested transition goes through
    // the production Adapter and ShardsEngine.Submit. No policy filtering.
    internal static class ActionSelfTest
    {
        private static readonly MethodInfo Route=typeof(ShardsEngine).GetMethod("RoutePriority",BindingFlags.NonPublic|BindingFlags.Instance);
        private static readonly MethodInfo Queue=typeof(ShardsEngine).GetMethod("QueueEffect",BindingFlags.NonPublic|BindingFlags.Instance);
        private static int _priority,_answers,_splits,_catalog,_menus;
        private static readonly HashSet<string> Contexts=new(StringComparer.Ordinal);
        private static readonly HashSet<string> Definitions=new(StringComparer.Ordinal);
        private static void Check(bool value,string label)
        {if(!value)throw new InvalidOperationException("Action audit: "+label);}
        private static Adapter Playing(string hero="decima",ShardsDlc dlc=ShardsDlc.Duel,bool automatic=false)
        {
            string other=hero=="tetra"?"volos":"tetra";
            var g=new Adapter(new ShardsEngine(ShardsContentRegistry.StandardConfig(772301,
                new List<PlayerSpec>{new(){Name="P0",CharacterId=hero},new(){Name="P1",CharacterId=other}},dlc)),automaticSingletons:automatic);
            while(g.Decision?.Context=="soi.herodraft")Pick(g,g.Decision.Options.Single(o=>o.DefId==(g.Actor==0?hero:other)).Id);
            var s=g.Engine.State;
            foreach(var monster in s.ActiveMonsters){monster.Zone=ShardsZone.CenterDeck;s.CenterDeck.Insert(0,monster);}
            s.ActiveMonsters.Clear();s.PendingMonsterAttacks.Clear();
            foreach(var p in s.Players){p.Power=0;p.Gems=30;p.Mastery=20;p.Health=40;}
            Refresh(g);return g;
        }
        private static void Refresh(Adapter g)
        {g.Engine.State.InvalidateCardIndex();Route.Invoke(g.Engine,null);g.Rebuild();}
        private static ShardsCard Add(Adapter g,string id,int owner,ShardsZone zone,List<ShardsCard> list)
        {
            var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=owner,Zone=zone};list.Add(c);
            Check(zone!=ShardsZone.Champions||c.Def.IsChampion,"champion fixture uses a non-champion definition: "+id);
            g.Engine.State.InvalidateCardIndex();return c;
        }
        private static int Find(Adapter g,Func<Candidate,bool> predicate)
        {
            if(g.Decision!=null)Contexts.Add(g.Decision.Context);
            int pages=(g.Candidates.Count+62)/63;
            for(int page=0;page<=pages;page++)
            {
                for(int i=0;i<g.VisibleCount;i++)if(predicate(g.Visible(i)))return i;
                int next=Enumerable.Range(0,g.VisibleCount).FirstOrDefault(i=>g.Visible(i).Kind==14,-1);
                if(next<0)break;g.Step(next);
            }
            throw new InvalidOperationException("Action audit: requested legal choice is unreachable");
        }
        private static void Pick(Adapter g,int id)=>g.Step(Find(g,c=>c.Kind==12&&c.Option.Id==id));
        private static void Commit(Adapter g)=>g.Step(Find(g,c=>c.Kind==13));
        private static void Choose(Adapter g,params int[] ids)
        {
            var request=g.Decision;Check(request!=null,"choice requires pending decision");
            foreach(int id in ids)Pick(g,id);
            if(ReferenceEquals(g.Decision,request))Commit(g);
        }
        private static ShardsCard Play(Adapter g,string id)
        {
            var p=g.Engine.State.Players[g.Actor];var c=Add(g,id,p.Index,ShardsZone.Hand,p.Hand);Refresh(g);
            g.Step(Find(g,x=>x.Action is ShardsPlayCardAction a&&a.CardInstanceId==c.InstanceId));return c;
        }
        private static void Encode(Adapter g)
        {
            var obs=new float[Encoder.ObsDim];var actions=new float[Encoder.MaxActions*Encoder.ActionDim];var mask=new float[Encoder.MaxActions];
            Encoder.Encode(g,obs,actions,mask);
            Check(obs.All(float.IsFinite)&&actions.All(float.IsFinite),"non-finite action/observation tensor");
            for(int i=0;i<mask.Length;i++)Check(mask[i]==(i<g.VisibleCount?1:0),"legal mask differs from actual wrapper candidates");
        }
        private static void Resolve(Adapter g,bool maximum=false)
        {
            for(int step=0;g.Decision!=null&&step<300;step++)
            {
                Contexts.Add(g.Decision.Context);Encode(g);
                if(g.Decision.Context=="soi.split")
                {g.Step(Find(g,c=>c.Kind==15&&c.Low==g.Low));continue;}
                if(g.Selected.Count>=g.Decision.Min&&(!maximum||g.Selected.Count==g.Decision.Max||g.Candidates.All(c=>c.Kind!=12)))
                    Commit(g);
                else g.Step(Find(g,c=>c.Kind==12));
            }
            Check(g.Decision==null,"card decision chain did not settle");
        }
        internal static object Run()
        {
            _priority=_answers=_splits=_catalog=_menus=0;Contexts.Clear();Definitions.Clear();
            PrioritySurface();SelectionContracts();SingletonsAndSplitEdges();TutorPaging();CenterOrdering();Heroes();Modes();SplitsAndShields();SpecialMenus();CatalogEffects();MonsterRewards();
            var inventory=(string[])typeof(Encoder).GetField("Contexts",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            Check(inventory.Where(c=>c!=""&&c!="soi.target").All(Contexts.Contains),"a supported two-seat modal context lacks a real action fixture");
            return new {passed=true,priority_submissions=_priority,selection_answers=_answers,integer_split_vectors=_splits,
                card_effect_cases=_catalog,catalog_definitions=Definitions.Count,catalog_definition_ids=Definitions.OrderBy(x=>x).ToArray(),mode_branches=_menus,contexts=Contexts.OrderBy(x=>x).ToArray(),
                checks=new[]{"every advertised priority action including all pages reaches actual Submit",
                    "exhaustive distinct ordered answers, Min/Max/default/disabled/duplicate contracts",
                    "200 real tutor options and same-definition physical copies remain reachable",
                    "all six top-three reorder permutations and sixteen Scry selection/bottom orders",
                    "every hero activation/cost threshold, Volos mode and passive Decima purchase",
                    "both Reactor and Deadly Recruits modes, distinct IDs sharing a card source",
                    "all bounded integer split vectors, 1..1000 boundaries and real deferred taunt/shield outcomes",
                    "singletons automate only forced answers and preserve optional/card-mode choices",
                    "all two-seat modal contexts including keep-fast, Maglev top routing, reset, repeat and mandatory discard",
                    "all catalog definitions: actual play/recruit/take/exhaust/monster-reward paths at all mastery tiers"}};
        }
        private static Adapter PriorityFixture()
        {
            var g=Playing("volos");var s=g.Engine.State;var p=s.Players[0];
            for(int i=0;i<70;i++)Add(g,"crystal",0,ShardsZone.Hand,p.Hand);
            Add(g,"giga_source_adept",0,ShardsZone.Champions,p.Champions);
            Add(g,"deadly_recruits_duel",0,ShardsZone.SetAside,p.Destinies);
            Add(g,"ingeminex_malice",-1,ShardsZone.MonsterSpace,s.ActiveMonsters);
            for(int slot=0;slot<s.CenterRow.Length;slot++)
            {
                string id=slot%2==0?"grim_tutor":"g_48";
                s.CenterRow[slot]=new ShardsCard{InstanceId=s.NextInstanceId++,DefId=id,Zone=ShardsZone.CenterRow};
            }
            p.Gems=100;p.Power=20;p.Mastery=20;Refresh(g);return g;
        }
        private static string Signature(PlayerAction a)=>a.GetType().Name+":"+a.PlayerIndex+":"+a.Describe();
        private static void PrioritySurface()
        {
            var baseline=PriorityFixture();var legal=baseline.Engine.LegalActions(0).Select(Signature).OrderBy(x=>x).ToArray();
            var available=baseline.Candidates.Where(c=>c.Action!=null).Select(c=>Signature(c.Action)).OrderBy(x=>x).ToArray();
            Check(legal.SequenceEqual(available),"wrapper removed, duplicated or added priority actions");
            Check(baseline.Candidates.Count>Encoder.MaxActions,"priority page fixture needs off-page actions");
            var reached=new HashSet<string>();int pages=(baseline.Candidates.Count+62)/63;
            for(int page=0;page<pages;page++)
            {
                Encode(baseline);
                for(int i=0;i<baseline.VisibleCount;i++)if(baseline.Visible(i).Action is PlayerAction action)reached.Add(Signature(action));
                baseline.Step(baseline.VisibleCount-1);
            }
            Check(reached.SetEquals(legal),"off-page priority action absent");
            foreach(string expected in legal)
            {
                var g=PriorityFixture();long before=g.Submissions;
                g.Step(Find(g,c=>c.Action!=null&&Signature(c.Action)==expected));
                Check(g.Submissions==before+1,"advertised priority action was not submitted exactly once");_priority++;
            }
            var kinds=baseline.Candidates.Select(c=>c.Kind).ToHashSet();
            Check(Enumerable.Range(0,12).All(kinds.Contains),"a priority action family lacks a real submission fixture");
        }
        private static IEnumerable<int[]> Sequences(int[] ids,int max)
        {
            yield return Array.Empty<int>();
            if(max==0)yield break;
            for(int i=0;i<ids.Length;i++)foreach(var tail in Sequences(ids.Where((_,j)=>j!=i).ToArray(),max-1))
                yield return new[]{ids[i]}.Concat(tail).ToArray();
        }
        private static Adapter Contract(int min,int max,bool ordered,out List<int> accepted,string context="soi.banish",bool automatic=false,int enabled=3)
        {
            var g=Playing(automatic:automatic);var record=new List<int>();accepted=record;
            var request=new DecisionRequest{PlayerIndex=0,Kind=DecisionKind.ChooseCards,Context=context,Title="Contract fixture",Min=min,Max=max,Ordered=ordered};
            for(int i=0;i<4;i++)request.Options.Add(new DecisionOption(100+i,"Option "+i){DefId="crystal",Disabled=i>=enabled,Required=i==0});
            if(enabled>0&&max>0)request.DefaultOptionIds.Add(enabled>2?102:100);
            IEnumerable<ShardsStep> Flow(ShardsContext ctx)
            {yield return ShardsStep.AwaitDecision(request);record.AddRange(ctx.Answer.ChosenOptionIds);}
            Queue.Invoke(g.Engine,new object[]{new Custom(Flow),0,null});
            g.Step(Find(g,c=>c.Kind==3));Check(ReferenceEquals(g.Decision,request)||automatic&&record.Count>=min,"actual contract flow did not park/resolve its forced answer");return g;
        }
        private static void Rejected(Adapter g,params int[] ids)
        {
            var request=g.Decision;ulong hash=g.Engine.State.ComputeHash();int logs=g.Engine.Log.Count;
            var result=g.Engine.Submit(new SubmitDecisionAction{PlayerIndex=g.Actor,Answer=new DecisionAnswer{DecisionId=request.Id,ChosenOptionIds=ids.ToList()}});
            Check(!result.Accepted&&ReferenceEquals(g.Decision,request)&&g.Engine.State.ComputeHash()==hash&&g.Engine.Log.Count==logs,"illegal answer changed state or was accepted");
        }
        private static void SelectionContracts()
        {
            foreach(bool ordered in new[]{false,true})for(int min=0;min<=3;min++)for(int max=min;max<=3;max++)
                foreach(var answer in Sequences(new[]{100,101,102},max).Where(x=>x.Length>=min))
                {
                    var g=Contract(min,max,ordered,out var accepted);var request=g.Decision;Encode(g);
                    Check(g.Selected.Count==0,"defaults were silently forced");
                    Check(!g.Candidates.Any(c=>c.Option?.Id==103),"disabled option selectable");
                    if(max==0)Rejected(g,100);
                    Check(g.Candidates.Any(c=>c.Kind==12)==(max>0),"zero-pick Max exposes an engine-rejected selectable action");
                    Check(g.Candidates.Any(c=>c.Kind==13)==(min==0),"commit available outside Min bound");
                    foreach(int id in answer)
                    {
                        Pick(g,id);
                        if(ReferenceEquals(g.Decision,request))Check(!g.Candidates.Any(c=>c.Option?.Id==id),"selected identity remains selectable twice");
                    }
                    if(ReferenceEquals(g.Decision,request))Commit(g);
                    Check(accepted.SequenceEqual(answer),"engine answer lost the chosen order or identity");_answers++;
                }
            var negative=Contract(1,2,true,out _);
            Rejected(negative);Rejected(negative,100,100);Rejected(negative,103);Rejected(negative,99999);Rejected(negative,100,101,102);
            foreach(int min in new[]{0,4})foreach(var v in Vectors(3,4).Where(x=>x.Sum()>=min))
            {
                var g=Contract(min,4,true,out var accepted,"soi.split");
                // Disabled fourth option is absent; all three integer targets remain.
                Split(g,v);var expected=v.SelectMany((n,i)=>Enumerable.Repeat(100+i,n)).ToArray();
                Check(accepted.SequenceEqual(expected),"binary split lost an integer vector or repeated IDs");_splits++;
            }
        }
        private static IEnumerable<int[]> Vectors(int n,int remaining)
        {
            if(n==0){yield return Array.Empty<int>();yield break;}
            for(int amount=0;amount<=remaining;amount++)foreach(var tail in Vectors(n-1,remaining-amount))yield return new[]{amount}.Concat(tail).ToArray();
        }
        private static void SingletonsAndSplitEdges()
        {
            var forced=Contract(1,1,false,out var yes,automatic:true,enabled:1);
            Check(yes.SequenceEqual(new[]{100})&&forced.Decision==null&&forced.AutomaticallyApplied==1,"forced singleton not applied exactly once");
            var optional=Contract(0,1,false,out var no,automatic:true,enabled:1);
            Check(optional.Decision!=null&&optional.VisibleCount==2&&optional.AutomaticallyApplied==0&&no.Count==0,"optional yes/no was silently collapsed");Commit(optional);
            var zero=Contract(0,0,false,out _,automatic:true);
            Check(zero.Decision==null&&zero.AutomaticallyApplied==1,"zero-pick completion must be a forced empty answer");
            var mode=Playing(automatic:true);Play(mode,"reactor_drone_duel");
            Check(mode.Decision?.Context=="soi.mode"&&mode.VisibleCount==2,"automation removed a real card mode");Pick(mode,2);
            for(int power=1;power<=1000;power=power<2?2:power<31?31:power<63?63:power<64?64:power<65?65:power<999?999:power+1)
                for(int target=0;target<3;target++)
                {
                    var g=Contract(power,power,true,out var accepted,"soi.split");var vector=new int[3];vector[target]=power;
                    Split(g,vector);Check(accepted.Count==power&&accepted.All(id=>id==100+target),"split boundary loses the full integer endpoint");_splits++;
                }
            var lethal=Playing();lethal.Engine.State.Players[0].Power=1001;Refresh(lethal);lethal.Step(Find(lethal,c=>c.Kind==10));
            Check(lethal.Engine.State.GameOver&&lethal.Engine.State.WinnerIndex==0,"overwhelming power must follow engine's direct terminal route");
        }
        private static void Split(Adapter g,int[] amounts)
        {
            var request=g.Decision;Check(request?.Context=="soi.split","integer split requires actual split request");
            for(int step=0;ReferenceEquals(g.Decision,request)&&step<100;step++)
            {
                int desired=amounts[g.SplitTarget];g.Step(Find(g,c=>c.Kind==15&&c.Low<=desired&&c.High>=desired));
            }
            Check(!ReferenceEquals(g.Decision,request),"binary split failed to settle");
        }
        private static void TutorPaging()
        {
            for(int chosen=0;chosen<200;chosen++)
            {
                var g=Playing();var p=g.Engine.State.Players[0];p.Deck.Clear();
                for(int i=0;i<200;i++)Add(g,i%2==0?"crystal":"blaster",0,ShardsZone.Deck,p.Deck);
                Play(g,"grim_tutor");var request=g.Decision;
                Check(request?.Context=="soi.tutor"&&request.Options.Count==200,"real 200-card tutor menu missing");
                var card=p.Deck.Single(c=>c.InstanceId==request.Options[chosen].Id);int health=p.Health;
                Pick(g,card.InstanceId);Check(p.Hand.Contains(card)&&p.Deck.Count==199&&p.Health==health-3,"off-page tutor pick did not move the correct physical card");
                Check(Enumerable.Range(0,g.Engine.Log.Count).Any(i=>g.Engine.Log[i] is ShardsDeckShuffledEvent),"tutor failed to shuffle");_answers++;
            }
        }
        private static ShardsCard[] Top(Adapter g)
        {
            var deck=g.Engine.State.CenterDeck;var cards=deck.Where(c=>!c.Def.IsMonster).GroupBy(c=>c.DefId).Select(x=>x.First()).Take(3).ToArray();
            foreach(var c in cards)deck.Remove(c);for(int i=2;i>=0;i--)deck.Add(cards[i]);return cards;
        }
        private static void CenterOrdering()
        {
            foreach(var order in Sequences(new[]{0,1,2},3).Where(x=>x.Length==3))
            {
                var g=Playing();var top=Top(g);Play(g,"index_of_futures");
                Check(g.Decision?.Context=="soi.reorder"&&g.Decision.Min==3&&g.Decision.Max==3,"real Index reorder contract");
                Choose(g,order.Select(i=>top[i].InstanceId).ToArray());
                Check(g.Engine.State.CenterDeck.TakeLast(3).Reverse().Select(c=>c.InstanceId).SequenceEqual(order.Select(i=>top[i].InstanceId)),"top reorder permutation changed");_answers++;
            }
            foreach(var selected in Sequences(new[]{0,1,2},3))
            {
                var g=Playing("rez");var top=Top(g);var before=g.Engine.State.CenterDeck.ToArray();
                g.Step(Find(g,c=>c.Kind==9));Check(g.Decision?.Context=="soi.scry","actual Rez Scry menu");
                Choose(g,selected.Select(i=>top[i].InstanceId).ToArray());
                var expected=before.ToList();foreach(int index in selected){expected.Remove(top[index]);expected.Insert(0,top[index]);}
                Check(g.Engine.State.CenterDeck.Select(c=>c.InstanceId).SequenceEqual(expected.Select(c=>c.InstanceId)),"Scry keep/bottom order changed");_answers++;
            }
        }
        private static void Heroes()
        {
            foreach(string hero in ShardsEngine.DraftableCharacters)
            {
                var g=Playing(hero);var p=g.Engine.State.Players[0];p.Mastery=4;Refresh(g);
                Check(!g.Candidates.Any(c=>c.Kind==9),"hero available below M5: "+hero);
                p.Mastery=5;Refresh(g);Check(g.Candidates.Any(c=>c.Kind==9)==(hero!="decima"),"M5 activation missing/passive activated: "+hero);
                if(hero=="decima")
                {
                    var card=g.Engine.State.CenterRow.First(c=>c!=null&&c.Def.Cost>=3);int slot=Array.IndexOf(g.Engine.State.CenterRow,card),before=p.Gems;
                    int price=g.Engine.EffectiveCost(p,card.Def);Check(price==card.Def.Cost-2,"Decima discount metadata");
                    g.Step(Find(g,c=>c.Action is ShardsBuyCardAction a&&!a.FastPlay&&a.SlotIndex==slot));
                    Check(p.Gems==before-price&&p.FirstBuyUsedThisTurn,"Decima purchase path");continue;
                }
                if(hero=="volos")continue;
                if(hero=="tetra"){p.Gems=2;Refresh(g);Check(!g.Candidates.Any(c=>c.Kind==9),"unaffordable Tetra activation");p.Gems=3;}
                if(hero=="kosynwu"){p.Health=1;Refresh(g);Check(!g.Candidates.Any(c=>c.Kind==9),"self-eliminating Sacrifice activation");p.Health=20;}
                Refresh(g);int hand=p.Hand.Count,health=p.Health;
                g.Step(Find(g,c=>c.Kind==9));
                if(hero=="tetra")Check(p.Hand.Count==hand+2&&p.Gems==0,"Tetra activation did not pay/draw");
                if(hero=="kosynwu"){Check(p.Health==health-1&&g.Decision?.Context=="soi.banish","Sacrifice cost/menu");Choose(g,g.Decision.Options[0].Id);}
                if(hero=="rez")Choose(g);
                Check(p.HeroAbilityUsedThisTurn&&!g.Candidates.Any(c=>c.Kind==9),"hero activated twice: "+hero);
                if(p.Gems>0)Check(g.Candidates.Any(c=>c.Kind==3),"hero activation removed independent Focus");
            }
            for(int gems=0;gems<=3;gems++)for(int mode=0;mode<=gems;mode++)
            {
                var g=Playing("volos");var p=g.Engine.State.Players[0];p.Mastery=5;p.Gems=gems;Refresh(g);
                int hand=p.Hand.Count,health=p.Health,power=p.Power,mastery=p.Mastery;
                g.Step(Find(g,c=>c.Kind==9));Check(g.Decision?.Context==VolosAbilityChoice.Context&&g.Decision.Options.Count==4,"all Volos faces must remain visible");
                for(int m=0;m<4;m++)Check(g.Decision.Options[m].Disabled==(m>gems),"Volos affordability hint");
                Pick(g,mode);Check(p.Gems==gems-mode,"Volos selected-mode cost");
                Check(mode switch{0=>p.Health==health+3,1=>p.Power==power+2,2=>p.Hand.Count==hand+1,3=>p.Mastery==mastery+1,_=>false},"Volos selected-mode result");_menus++;
            }
        }
        private static void Modes()
        {
            for(int mode=1;mode<=2;mode++)
            {
                var g=Playing();var p=g.Engine.State.Players[0];int gems=p.Gems;var card=Play(g,"reactor_drone_duel");
                Check(g.Decision?.Context=="soi.mode"&&g.Decision.Options.Count==2,"Reactor modes");Pick(g,mode);
                Check(p.Gems==gems+(mode==1?2:3)&&card.BanishAtCleanup==(mode==2),"Reactor selected branch");
                p.Power=0;Refresh(g);g.Step(Find(g,c=>c.Kind==10));Resolve(g);
                Check((mode==2?g.Engine.State.Banished:p.Discard).Contains(card),"Reactor cleanup branch");_menus++;
            }
            for(int mode=1;mode<=2;mode++)
            {
                var g=Playing();var p=g.Engine.State.Players[0];var destiny=Add(g,"deadly_recruits_duel",0,ShardsZone.SetAside,p.Destinies);
                var card=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId="nil_assassin_duel",Zone=ShardsZone.CenterRow};g.Engine.State.CenterRow[0]=card;Refresh(g);
                g.Step(Find(g,c=>c.Action is ShardsExhaustAction a&&a.CardInstanceId==destiny.InstanceId));
                Check(g.Decision?.Context=="soi.warp","Deadly Recruits target choice");Pick(g,0);
                Check(g.Decision?.Context=="soi.mode"&&g.Decision.Options.Select(o=>o.Id).SequenceEqual(new[]{1,2}),"shared-source choices aliased by physical ID");Pick(g,mode);Resolve(g);
                Check(mode==1?p.PlayZone.Contains(card)&&card.FastPlayed:p.Discard.Contains(card)&&!card.FastPlayed,"Deadly Recruits selected fast-play/recruit branch");_menus++;
            }
        }
        private static void SplitsAndShields()
        {
            foreach(var v in Vectors(3,4).Where(x=>x.Sum()==4))
            {
                var g=Playing();var s=g.Engine.State;var enemy=s.Players[1];enemy.Hand.Clear();
                var a=Add(g,"giga_source_adept",1,ShardsZone.Champions,enemy.Champions);var b=Add(g,"giga_source_adept",1,ShardsZone.Champions,enemy.Champions);
                s.Players[0].Power=4;Refresh(g);g.Step(Find(g,c=>c.Kind==10));Check(g.Decision?.Context=="soi.split"&&g.Decision.Options.Count==3,"actual three-target damage split");
                int health=enemy.Health;Split(g,v);Resolve(g);Check(enemy.Health==health-v[0],"actual face allocation differs from integer vector");_splits++;
            }
            foreach(bool overkill in new[]{false,true})
            {
                var g=Playing();var s=g.Engine.State;var enemy=s.Players[1];enemy.Hand.Clear();
                var taunt=Add(g,"zetta_encryptor",1,ShardsZone.Champions,enemy.Champions);Add(g,"testudo_vanguard",1,ShardsZone.Champions,enemy.Champions);
                var shield=Add(g,"datic_robes_duel",1,ShardsZone.Hand,enemy.Hand);int prevention=g.Engine.ShieldValue(enemy,shield),defense=g.Engine.EffectiveDefense(enemy,taunt);
                Check(prevention>0,"shield fixture needs a real positive shield value");
                int hit=defense+(overkill?prevention:0),face=prevention+3;s.Players[0].Power=hit+face;int health=enemy.Health;Refresh(g);
                g.Step(Find(g,c=>c.Kind==10));var request=g.Decision;
                int[] vector=request.Options.Select(o=>o.CardInstanceId==taunt.InstanceId?hit:o.Id==1?face:0).ToArray();Split(g,vector);
                Check(g.Actor==1&&g.Decision?.Context=="soi.shields","actual defending actor/shield menu");Pick(g,shield.InstanceId);Resolve(g);
                Check(enemy.Hand.Contains(shield),"revealed shield should remain in defender hand");
                Check(enemy.Champions.Contains(taunt)==!overkill&&enemy.Health==health-(overkill?3:0),"taunt/shield over-assignment semantics changed");_splits++;
            }
        }
        private static void Rich(Adapter g)
        {
            var s=g.Engine.State;var p=s.Players[0];
            foreach(string id in new[]{"reactor_drone_duel","spore_cleric_duel","order_initiate_duel","wraethe_skirmisher_duel","dash_duel"})
            {
                var c=Add(g,id,0,ShardsZone.PlayZone,p.PlayZone);p.PlayedThisTurn.Add(c);p.CountFactionPlay(c.Def.Faction,true);
                Add(g,id,0,ShardsZone.Hand,p.Hand);
            }
            foreach(string id in new[]{"li_hin","g_48","thorn_zealot","grim_tutor"})Add(g,id,0,ShardsZone.Discard,p.Discard);
            foreach(string id in new[]{"giga_source_adept","zetta_encryptor"})
            {Add(g,id,0,ShardsZone.Champions,p.Champions);Add(g,id,1,ShardsZone.Champions,s.Players[1].Champions);}
            p.Gems=30;p.Power=0;Refresh(g);
        }
        private static void SpecialMenus()
        {
            foreach(bool keep in new[]{false,true})
            {
                var g=Playing();var p=g.Engine.State.Players[0];Add(g,"swyft_duel",0,ShardsZone.Champions,p.Champions);
                var loan=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId="nil_assassin_duel",Zone=ShardsZone.CenterRow};g.Engine.State.CenterRow[0]=loan;Refresh(g);
                g.Step(Find(g,c=>c.Action is ShardsBuyCardAction a&&a.FastPlay&&a.SlotIndex==0));Resolve(g);p.Power=0;Refresh(g);
                g.Step(Find(g,c=>c.Kind==10));Check(g.Decision?.Context=="soi.keepfast","Swyft real keep-fast menu");
                Choose(g,keep?new[]{loan.InstanceId}:Array.Empty<int>());Resolve(g);
                Check(keep?p.Discard.Contains(loan)&&!loan.FastPlayed:g.Engine.State.CenterDeck.Contains(loan),"keep-fast acquisition/return path");_answers++;
            }
            foreach(bool top in new[]{false,true})
            {
                var g=Playing();var p=g.Engine.State.Players[0];Add(g,"maglev_tunnels",0,ShardsZone.SetAside,p.Destinies);
                var recruit=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId="g_48",Zone=ShardsZone.CenterRow};g.Engine.State.CenterRow[0]=recruit;Refresh(g);
                g.Step(Find(g,c=>c.Action is ShardsBuyCardAction a&&!a.FastPlay&&a.SlotIndex==0));Check(g.Decision?.Context=="soi.maglev","real Maglev routing menu");
                Choose(g,top?new[]{recruit.InstanceId}:Array.Empty<int>());
                Check(top?p.Deck.Last()==recruit:p.Discard.Contains(recruit),"Maglev selected deck-top/discard route");_answers++;
            }
            foreach(bool reset in new[]{false,true})
            {
                var g=Playing();var p=g.Engine.State.Players[0];var target=Play(g,"giga_source_adept");
                g.Step(Find(g,c=>c.Action is ShardsExhaustAction a&&a.CardInstanceId==target.InstanceId));
                var source=Play(g,"g_48");g.Step(Find(g,c=>c.Action is ShardsExhaustAction a&&a.CardInstanceId==source.InstanceId));
                Check(g.Decision?.Context=="soi.reset","real G-48 reset menu");Choose(g,reset?new[]{target.InstanceId}:Array.Empty<int>());
                Check(target.Exhausted==!reset,"reset selected champion readiness");_answers++;
            }
            foreach(bool again in new[]{false,true})
            {
                var g=Playing();var p=g.Engine.State.Players[0];Play(g,"prism");
                var source=Add(g,"shard_defiant",0,ShardsZone.SetAside,p.Destinies);Top(g);Refresh(g);
                g.Step(Find(g,c=>c.Action is ShardsExhaustAction a&&a.CardInstanceId==source.InstanceId));
                Check(g.Decision?.Context=="soi.defiant","real Defiant keep/banish menu");Pick(g,2);
                Check(g.Decision?.Context=="soi.confirm","Aion play must offer the real repeat choice");Choose(g,again?new[]{1}:Array.Empty<int>());
                Check((g.Decision?.Context=="soi.defiant")==again,"repeat confirmation lost the selected branch");Resolve(g);_answers++;
            }
            var discard=Playing();var state=discard.Engine.State;
            var monster=Add(discard,"ingeminex_agony",-1,ShardsZone.MonsterSpace,state.ActiveMonsters);state.PendingMonsterAttacks.Add(monster.InstanceId);
            state.Players[0].Power=0;Refresh(discard);discard.Step(Find(discard,c=>c.Kind==10));
            Check(discard.Decision?.Context=="soi.discard","Agony must discard from the actual redrawn hand");
            var request=discard.Decision;int owner=discard.Actor;var cards=request.Options.Take(request.Min).Select(o=>o.Id).ToArray();
            Choose(discard,cards);
            Check(cards.All(id=>state.Players[owner].Discard.Any(c=>c.InstanceId==id)),"mandatory discard chose the wrong physical cards");Resolve(discard);_answers++;
        }
        private static void CatalogEffects()
        {
            foreach(var def in ShardsCardDatabase.All.OrderBy(c=>c.Id,StringComparer.Ordinal))
                foreach(int mastery in new[]{0,5,10,15,20,30})foreach(bool maximum in new[]{false,true})
                {
                    if(def.IsMonster)continue;
                    Definitions.Add(def.Id);
                    try
                    {
                    var g=Playing(def.Character??"decima");Rich(g);var p=g.Engine.State.Players[0];p.Mastery=mastery;
                    ShardsCard source;
                    if(def.Type==ShardsCardType.Destiny)
                    {
                        p.Mastery=Math.Max(5,mastery);p.DestinyTaken=false;
                        source=Add(g,def.Id,-1,ShardsZone.DestinyRow,g.Engine.State.DestinyRow);Refresh(g);
                        g.Step(Find(g,c=>c.Action is ShardsTakeDestinyAction a&&a.CardInstanceId==source.InstanceId));
                    }
                    else if(def.Type==ShardsCardType.Relic)
                    {
                        p.Mastery=Math.Max(10,mastery);p.RelicRecruited=false;
                        source=Add(g,def.Id,0,ShardsZone.SetAside,p.SetAside);Refresh(g);
                        g.Step(Find(g,c=>c.Action is ShardsRecruitRelicAction a&&a.CardInstanceId==source.InstanceId));
                        Check(p.Discard.Remove(source),"earned relic must first enter discard");
                        // A later draw provides this starting hand state. The tested
                        // play and subsequent exhaust are actual advertised actions.
                        source.Zone=ShardsZone.Hand;p.Hand.Add(source);p.Mastery=mastery;Refresh(g);
                        g.Step(Find(g,c=>c.Action is ShardsPlayCardAction a&&a.CardInstanceId==source.InstanceId));
                    }
                    else source=Play(g,def.Id);
                    Resolve(g,maximum);_catalog++;
                    if(!g.Engine.State.GameOver&&def.ExhaustEffect!=null)
                    {
                        p.Mastery=mastery;p.Gems=30;source.Exhausted=false;Refresh(g);
                        g.Step(Find(g,c=>c.Action is ShardsExhaustAction a&&a.CardInstanceId==source.InstanceId));Resolve(g,maximum);_catalog++;
                    }
                    }
                    catch(Exception error){throw new InvalidOperationException($"Action audit catalog: {def.Id}, M{mastery}, maximum={maximum}: {error.Message}",error);}
                }
        }
        private static void MonsterRewards()
        {
            foreach(var def in ShardsCardDatabase.All.Where(c=>c.IsMonster).OrderBy(c=>c.Id,StringComparer.Ordinal))
                foreach(int mastery in new[]{0,5,10,15,20,30})foreach(bool maximum in new[]{false,true})
                {
                    try
                    {
                        var g=Playing();Rich(g);var p=g.Engine.State.Players[0];p.Mastery=mastery;p.Power=100;
                        var monster=Add(g,def.Id,-1,ShardsZone.MonsterSpace,g.Engine.State.ActiveMonsters);Refresh(g);
                        g.Step(Find(g,c=>c.Action is ShardsAttackMonsterAction a&&a.CardInstanceId==monster.InstanceId));Resolve(g,maximum);
                        Check(g.Engine.State.CenterDeck.Contains(monster)&&!g.Engine.State.ActiveMonsters.Contains(monster),"defeated monster return/reward chain");
                        _catalog++;Definitions.Add(def.Id);
                    }
                    catch(Exception error){throw new InvalidOperationException($"Action audit monster: {def.Id}, M{mastery}, maximum={maximum}: {error.Message}",error);}
                }
            Check(Definitions.Count==Encoder.CardIds.Length,"catalog audit skipped a registered definition");
        }
    }
}
