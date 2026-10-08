using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Pascension.Engine.Targeting;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // These checks read the ACTUAL policy tensors. Hidden engine lists are used
    // only as test oracles; they are never supplied as observation repair data.
    internal static class ZoneVisibilitySelfTest
    {
        private static void Check(bool value,string message)
        { if(!value)throw new InvalidOperationException("Zone visibility: "+message); }
        private static void Near(float actual,float expected,string message)
        { Check(Math.Abs(actual-expected)<0.00002f,message+": "+actual+" != "+expected); }
        private static (float[] obs,float[] candidates,float[] mask) Encode(Adapter g)
        {
            var o=new float[Encoder.ObsDim];var c=new float[Encoder.MaxActions*Encoder.ActionDim];var m=new float[Encoder.MaxActions];
            Encoder.Encode(g,o,c,m);return(o,c,m);
        }
        private static void Same((float[] obs,float[] candidates,float[] mask) a,(float[] obs,float[] candidates,float[] mask) b,string message)
        { Check(a.obs.SequenceEqual(b.obs)&&a.candidates.SequenceEqual(b.candidates)&&a.mask.SequenceEqual(b.mask),message); }
        private static Adapter Playing(ulong seed,ShardsDlc dlc=ShardsDlc.Duel,int seat=0)
        {
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},dlc);
            var g=new Adapter(new ShardsEngine(config));
            while(g.Decision?.Context=="soi.herodraft")g.Step(g.ExerciseChoice());
            if(seat==1)
            {
                g.ApplyExternal(new ShardsEndTurnAction{PlayerIndex=0});
                for(int n=0;g.Decision!=null&&n<100;n++)g.Step(g.ExerciseChoice());
                Check(g.Actor==1&&g.Decision==null,"could not reach seat 1 priority");
            }
            return g;
        }
        private static ShardsCard Card(Adapter g,string id,int owner,ShardsZone zone)
        {
            var card=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=owner,Zone=zone};
            g.Engine.State.InvalidateCardIndex();return card;
        }
        private static float Count(float[] obs,int channel,string id)=>obs[Encoder.HistogramOffset+channel*Encoder.CardCapacity+Encoder.CardIndex(id)]*10;
        private static float[] Entity(Adapter g,float[] obs,int instance)
        {
            Check(g.EntityMap.TryGetValue(instance,out int at),"missing visible entity "+instance);
            return obs.Skip(Encoder.EntityOffset+(at-1)*12).Take(12).ToArray();
        }
        internal static string[] Run()
        {
            PublicPlayerFields();PublicWorldFields();ZoneChannels();CardStatuses();HiddenInformationInvariance();AllOptions();
            int states=ConservationAcrossRules();DoomGateConservation();RewardSources();
            return new[]{"both-seat perturbation of every public player primitive and faction counter","all rule and world scalars",
                "separate own/opponent/shared zone tensors, owner and ordered play history","every visible mutable card status",
                "exact complete tensor invariance under hidden allocation/order/RNG permutations","330 pending options including disabled/off-page targets/defaults",
                "public monster reward sources after return to center bottom","exact center/destiny conservation and hand/set-aside facts in "+states+" real rule states"};
        }
        private static void PublicPlayerFields()
        {
            var columns=new Dictionary<string,int>{[nameof(ShardsPlayer.Health)]=0,[nameof(ShardsPlayer.Mastery)]=1,[nameof(ShardsPlayer.Gems)]=2,[nameof(ShardsPlayer.Power)]=3,
                [nameof(ShardsPlayer.CharacterExhausted)]=7,[nameof(ShardsPlayer.FocusedThisTurn)]=8,[nameof(ShardsPlayer.HeroAbilityUsedThisTurn)]=9,
                [nameof(ShardsPlayer.FirstBuyUsedThisTurn)]=10,[nameof(ShardsPlayer.RelicRecruited)]=11,[nameof(ShardsPlayer.DestinyTaken)]=12,
                [nameof(ShardsPlayer.ExtraTurnUsed)]=13,[nameof(ShardsPlayer.DoomGateFloodUsed)]=14,[nameof(ShardsPlayer.Eliminated)]=15,
                [nameof(ShardsPlayer.IgnoreShieldsThisTurn)]=16,[nameof(ShardsPlayer.HealthToPowerThisTurn)]=17,[nameof(ShardsPlayer.HealingDoubledThisTurn)]=18,
                [nameof(ShardsPlayer.OverflowHealthToPowerThisTurn)]=19,[nameof(ShardsPlayer.ShieldsDoubledUntilNextTurn)]=20,[nameof(ShardsPlayer.NextRecruitsToHand)]=21,
                [nameof(ShardsPlayer.NextHomodeusChampionsIntoPlay)]=22,[nameof(ShardsPlayer.NextChampionsIntoPlay)]=23,[nameof(ShardsPlayer.CopyHomodeusAlliesThisTurn)]=24,
                [nameof(ShardsPlayer.BonusDrawsOnBigHit)]=25,[nameof(ShardsPlayer.MaxDamageDealtToOneOpponent)]=26,[nameof(ShardsPlayer.CardsBanishedThisTurn)]=27,
                [nameof(ShardsPlayer.RerollsThisTurn)]=28,[nameof(ShardsPlayer.NextRerollDiscount)]=29,[nameof(ShardsPlayer.FullControl)]=31};
            var fields=typeof(ShardsPlayer).GetFields(BindingFlags.Public|BindingFlags.Instance)
                .Where(f=>(f.FieldType==typeof(int)||f.FieldType==typeof(bool))&&f.Name!=nameof(ShardsPlayer.Index)).ToArray();
            Check(fields.All(f=>columns.ContainsKey(f.Name))&&fields.Length==columns.Count,"new public player primitive lacks audited tensor column");
            for(int actor=0;actor<2;actor++)
            {
                var g=Playing((ulong)(95001+actor),seat:actor);
                for(int owner=0;owner<2;owner++)
                {
                    int start=16+Encoder.Relative(owner,actor)*64;var p=g.Engine.State.Players[owner];
                    foreach(var field in fields)
                    {
                        var baseline=Encode(g).obs;object old=field.GetValue(p);
                        field.SetValue(p,field.FieldType==typeof(bool)?!(bool)old:(int)old+7);
                        Check(baseline[start+columns[field.Name]]!=Encode(g).obs[start+columns[field.Name]],"public field invisible: "+owner+" "+field.Name);
                        field.SetValue(p,old);
                    }
                    var before=Encode(g).obs;p.CountFactionPlay(ShardsFaction.Order,true);var after=Encode(g).obs;
                    Near(after[start+32+(int)ShardsFaction.Order]-before[start+32+(int)ShardsFaction.Order],0.05f,"faction play count");
                    Near(after[start+39+(int)ShardsFaction.Order]-before[start+39+(int)ShardsFaction.Order],0.05f,"faction ally count");
                    string original=p.CharacterId;p.CharacterId=original=="decima"?"tetra":"decima";
                    Check(after[start+6]!=Encode(g).obs[start+6],"hero identity invisible");p.CharacterId=original;
                }
            }
        }
        private static void PublicWorldFields()
        {
            var g=Playing(95003);var s=g.Engine.State;
            var rules=new Dictionary<string,int>{[nameof(ShardsRules.StartingHealth)]=8,[nameof(ShardsRules.MaxHealth)]=9,[nameof(ShardsRules.HandSize)]=10,
                [nameof(ShardsRules.MasteryCap)]=11,[nameof(ShardsRules.CenterRowSize)]=12,[nameof(ShardsRules.ResponseTimerSeconds)]=13};
            var fields=typeof(ShardsRules).GetFields(BindingFlags.Public|BindingFlags.Instance);
            Check(fields.Length==rules.Count&&fields.All(f=>rules.ContainsKey(f.Name)),"new public rule lacks audited tensor column");
            foreach(var field in fields)
            {
                var before=Encode(g).obs;object old=field.GetValue(s.Rules);
                field.SetValue(s.Rules,field.FieldType==typeof(float)?(object)((float)old+3):(int)old+3);
                Check(before[rules[field.Name]]!=Encode(g).obs[rules[field.Name]],"rule invisible: "+field.Name);field.SetValue(s.Rules,old);
            }
            var baseline=Encode(g).obs;s.Round++;Near(Encode(g).obs[2]-baseline[2],0.01f,"round");s.Round--;
            s.ExtraTurnForPlayer=g.Actor;Near(Encode(g).obs[14],0,"extra turn own recipient");
            s.ExtraTurnForPlayer=1-g.Actor;Near(Encode(g).obs[14],1,"extra turn opponent recipient");s.ExtraTurnForPlayer=-1;
            s.GameOver=true;s.WinnerIndex=g.Actor;var terminal=Encode(g);Near(terminal.obs[6],1,"game over");Near(terminal.obs[7],0,"own winner");
            s.WinnerIndex=1-g.Actor;Near(Encode(g).obs[7],1,"opponent winner");s.WinnerIndex=-1;Near(Encode(g).obs[7],-1,"draw winner");
        }
        private static void ZoneChannels()
        {
            for(int actor=0;actor<2;actor++)
            {
                var g=Playing((ulong)(95010+actor),seat:actor);var s=g.Engine.State;var own=s.Players[actor];var enemy=s.Players[1-actor];
                var zones=new (List<ShardsCard> cards,int owner,ShardsZone zone,int channel,bool entity)[]{
                    (own.Hand,actor,ShardsZone.Hand,0,true),(own.Deck,actor,ShardsZone.Deck,1,false),(own.Discard,actor,ShardsZone.Discard,2,true),
                    (own.PlayZone,actor,ShardsZone.PlayZone,3,true),(own.Champions,actor,ShardsZone.Champions,4,true),(own.Destinies,actor,ShardsZone.SetAside,5,true),
                    (own.SetAside,actor,ShardsZone.SetAside,6,true),(enemy.Deck,1-actor,ShardsZone.Deck,7,false),(enemy.Hand,1-actor,ShardsZone.Hand,7,false),
                    (enemy.Discard,1-actor,ShardsZone.Discard,8,true),(enemy.PlayZone,1-actor,ShardsZone.PlayZone,9,true),
                    (enemy.Champions,1-actor,ShardsZone.Champions,10,true),(enemy.Destinies,1-actor,ShardsZone.SetAside,11,true),
                    (s.DestinyRow,-1,ShardsZone.DestinyRow,14,true),(s.ActiveMonsters,-1,ShardsZone.MonsterSpace,15,true),(s.Banished,1-actor,ShardsZone.Banished,16,true)};
                foreach(var zone in zones)
                {
                    var before=Encode(g).obs;var card=Card(g,"crystal",zone.owner,zone.zone);zone.cards.Add(card);var after=Encode(g).obs;
                    Near(Count(after,zone.channel,"crystal")-Count(before,zone.channel,"crystal"),1,"zone histogram "+zone.channel);
                    Check(g.EntityMap.ContainsKey(card.InstanceId)==zone.entity,"entity visibility for "+zone.zone+" owner "+zone.owner);
                    if(zone.entity){var row=Entity(g,after,card.InstanceId);Near(row[0],Encoder.CardCode("crystal"),"zone card definition");Near(row[1],Encoder.Relative(zone.owner,actor),"zone card owner");Near(row[2],((int)zone.zone+1)/16f,"zone card location");}
                    if(zone.channel==8||zone.channel==9||zone.channel==10)Near(Count(after,7,"crystal")-Count(before,7,"crystal"),1,"public opponent permanent collection");
                    if(zone.channel==0||zone.channel==1||zone.channel==2||zone.channel==3||zone.channel==4)Near(Count(after,23,"crystal")-Count(before,23,"crystal"),1,"own permanent collection");
                    zone.cards.Remove(card);
                }
                int slot=Array.FindIndex(s.CenterRow,c=>c!=null);var previous=s.CenterRow[slot];var market=Card(g,"crystal",-1,ShardsZone.CenterRow);s.CenterRow[slot]=market;
                var view=Encode(g).obs;Check(g.EntityMap.ContainsKey(market.InstanceId),"market entity missing");Near(Entity(g,view,market.InstanceId)[10],(slot+1)/384f,"market slot");s.CenterRow[slot]=previous;
                var first=Card(g,"blaster",actor,ShardsZone.PlayZone);var second=Card(g,"crystal",actor,ShardsZone.PlayZone);
                own.PlayZone.Add(first);own.PlayZone.Add(second);own.PlayedThisTurn.Add(second);own.PlayedThisTurn.Add(first);
                view=Encode(g).obs;Near(view[Encoder.PlayedOffset],Encoder.CardCode("crystal"),"played semantic order first");Near(view[Encoder.PlayedOffset+4],Encoder.CardCode("blaster"),"played semantic order second");
                Near(Count(view,19,"crystal"),1,"played this turn histogram");
                var detached=Card(g,"crystal",1-actor,ShardsZone.Deck);g.Knowledge.Detached.Add(detached);view=Encode(g).obs;
                Near(Entity(g,view,detached.InstanceId)[2],12/16f,"temporary public reveal location");Check(Count(view,7,"crystal")>=1,"temporary opponent reveal absent from collection");
                g.Knowledge.Hand[1-actor].Add("blaster");Near(Count(Encode(g).obs,17,"blaster"),1,"public known opponent hand");
            }
        }
        private static void CardStatuses()
        {
            var g=Playing(95020);var p=g.Engine.State.Players[g.Actor];var card=Card(g,"li_hin",p.Index,ShardsZone.Champions);p.Champions.Add(card);
            var baseline=Encode(g).obs;card.Exhausted=true;Near(Entity(g,Encode(g).obs,card.InstanceId)[3],1,"exhausted");card.DamageThisTurn=13;
            Near(Entity(g,Encode(g).obs,card.InstanceId)[4],13/50f,"marked damage");card.BanishAtCleanup=true;
            Near(Entity(g,Encode(g).obs,card.InstanceId)[8],1,"cleanup banish");
            var loan=Card(g,"crystal",p.Index,ShardsZone.PlayZone);p.PlayZone.Add(loan);float before=Count(Encode(g).obs,23,"crystal");loan.FastPlayed=true;var view=Encode(g).obs;
            Near(Entity(g,view,loan.InstanceId)[7],1,"fast loan status");Near(Count(view,23,"crystal"),before-1,"fast loan must not enter permanent collection");
            var monster=Card(g,"ingeminex_corruption",-1,ShardsZone.MonsterSpace);g.Engine.State.ActiveMonsters.Add(monster);
            g.Engine.State.PendingMonsterAttacks.Add(monster.InstanceId);Near(Entity(g,Encode(g).obs,monster.InstanceId)[9],1/384f,"pending monster attack order");
            // Past the incumbent's 56-entry limit, original public attack order
            // and individual statuses must still reach the policy unchanged.
            for(int index=1;index<70;index++)
            {
                var next=Card(g,index%2==0?"ingeminex_agony":"ingeminex_malice",-1,ShardsZone.MonsterSpace);
                next.DamageThisTurn=index;g.Engine.State.ActiveMonsters.Add(next);g.Engine.State.PendingMonsterAttacks.Add(next.InstanceId);
            }
            view=Encode(g).obs;
            for(int index=0;index<70;index++)Near(Entity(g,view,g.Engine.State.ActiveMonsters[index].InstanceId)[9],(index+1)/384f,"uncapped pending attack order "+index);
            for(int owner=0;owner<2;owner++)
            {
                var player=g.Engine.State.Players[owner];var champion=Card(g,"li_hin",owner,ShardsZone.Champions);
                champion.Exhausted=true;champion.DamageThisTurn=9;champion.BanishAtCleanup=true;player.Champions.Add(champion);
                var aura=Card(g,"ferrata_guard_duel",owner,ShardsZone.Champions);player.Champions.Add(aura);
                var shield=Card(g,"datic_robes_duel",owner,ShardsZone.Hand);player.Hand.Add(shield);player.Mastery=19;
                view=Encode(g).obs;var row=Entity(g,view,champion.InstanceId);
                Near(row[3],1,"both owner exhausted");Near(row[4],9/50f,"both owner damage");Near(row[8],1,"both owner cleanup banish");
                Near(row[5],g.Engine.EffectiveDefense(player,champion)/50f,"effective public aura defense");
                if(owner==g.Actor)Near(Entity(g,view,shield.InstanceId)[6],g.Engine.ShieldValue(player,shield)/20f,"visible dynamic shield");
                else Check(!g.EntityMap.ContainsKey(shield.InstanceId),"opponent private dynamic-shield card exposed");
            }
            Check(baseline.Length==Encoder.ObsDim,"observation size");
        }
        private static void HiddenInformationInvariance()
        {
            for(int seat=0;seat<2;seat++)
            {
                var g=Playing((ulong)(95030+seat),seat:seat);var s=g.Engine.State;var p=s.Players[1-seat];
                p.Hand.Add(Card(g,"crystal",p.Index,ShardsZone.Hand));p.Deck.Add(Card(g,"blaster",p.Index,ShardsZone.Deck));
                var baseline=Encode(g);
                s.CenterDeck.Reverse();s.DestinyDeck.Reverse();s.Players[seat].Deck.Reverse();p.Deck.Reverse();p.Hand.Reverse();p.SetAside.Reverse();
                s.Rng.NextUInt();s.NextInstanceId+=100;s.NextDecisionId+=100;
                Same(baseline,Encode(g),"unrevealed order/RNG/private creation counter leaked for seat "+seat);
                int hand=p.Hand.FindIndex(c=>p.Deck.Any(d=>d.DefId!=c.DefId));Check(hand>=0,"privacy fixture needs different hidden definitions");
                int deck=p.Deck.FindIndex(c=>c.DefId!=p.Hand[hand].DefId);var h=p.Hand[hand];var d=p.Deck[deck];
                p.Hand[hand]=d;d.Zone=ShardsZone.Hand;p.Deck[deck]=h;h.Zone=ShardsZone.Deck;s.InvalidateCardIndex();
                Same(baseline,Encode(g),"hidden opponent hand/draw allocation leaked for seat "+seat);
                foreach(string id in Encoder.CardIds)
                    Near(Count(baseline.obs,7,id),p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone.Where(c=>!c.FastPlayed)).Concat(p.Champions).Count(c=>c.DefId==id),"public permanent multiset "+id);
            }
        }
        private static void AllOptions()
        {
            var g=new Adapter(new ShardsEngine(Program.Config(95040)));var d=g.Decision;d.Context="soi.reveal";d.Title="Test 330 revealed cards";
            d.Min=0;d.Max=330;d.Options.Clear();d.DefaultOptionIds.Clear();
            for(int i=0;i<330;i++)
            {
                var option=new DecisionOption(20000+i,"Amount "+i){DefId=i%2==0?"crystal":"blaster",CardInstanceId=30000+i,
                    Amount=i,Required=i%3==0,OwnerIndex=i%2,Disabled=i%7==0,Target=TargetRef.PlayerAt(i%2)};
                d.Options.Add(option);if(i%11==0)d.DefaultOptionIds.Add(option.Id);
            }
            g.Rebuild();var view=Encode(g);Near(view.obs[149],330/384f,"full pending option count");var reachable=new HashSet<int>();
            for(int i=0;i<330;i++)
            {
                var option=d.Options[i];int at=Encoder.OptionOffset+i*20;
                Near(view.obs[at],Encoder.CardCode(option.DefId),"all option definitions "+i);Near(view.obs[at+1],(i+1)/384f,"all option original order "+i);
                Near(view.obs[at+3],i/1000f,"all option amounts "+i);Near(view.obs[at+4],option.Required?1:0,"required hint "+i);
                Near(view.obs[at+5],Encoder.Relative(i%2,g.Actor),"option owner "+i);Near(view.obs[at+6],option.Disabled?1:0,"disabled option "+i);
                Near(view.obs[at+8],i%11==0?0.001f:0,"default option "+i);Near(view.obs[at+9],(int)TargetKind.Player+1,"target kind "+i);
                Near(view.obs[at+10],Encoder.Relative(i%2,g.Actor),"target owner "+i);Near(view.obs[at+16],i/1000f,"dynamic option amount label "+i);
            }
            int pages=(g.Candidates.Count+62)/63;
            for(int page=0;page<pages;page++)
            {
                var encoded=Encode(g);int selectable=0;
                for(int index=0;index<g.VisibleCount;index++)
                {var c=g.Visible(index);if(c.Option!=null){Check(!c.Option.Disabled,"disabled option candidate");reachable.Add(c.Option.Id);selectable++;}Near(encoded.mask[index],1,"legal action mask");}
                Check(selectable>0,"empty visible option page");g.Step(g.VisibleCount-1);
            }
            Check(reachable.SetEquals(d.Options.Where(o=>!o.Disabled).Select(o=>o.Id)),"valid off-page options unreachable");
        }
        private static void RewardSources()
        {
            foreach(string id in new[]{"ingeminex_corruption","ingeminex_agony","ingeminex_malice"})
            {
                var g=Playing((ulong)(95050+Encoder.CardIndex(id)));var s=g.Engine.State;var p=s.Players[g.Actor];p.Power=10;
                if(id=="ingeminex_malice")p.Discard.Add(Card(g,"li_hin",p.Index,ShardsZone.Discard));
                var monster=s.CenterDeck.First(c=>c.DefId==id);s.CenterDeck.Remove(monster);monster.Zone=ShardsZone.MonsterSpace;s.ActiveMonsters.Add(monster);s.PendingMonsterAttacks.Add(monster.InstanceId);
                g.ApplyExternal(new ShardsAttackMonsterAction{PlayerIndex=p.Index,CardInstanceId=monster.InstanceId});
                Check(g.Decision!=null,"reward must park a real child decision: "+id);var view=Encode(g).obs;
                Check(!g.EntityMap.ContainsKey(monster.InstanceId),"reward source should now be in hidden center pile");
                Near(view[169],Encoder.Relative(p.Index,g.Actor),"returned public reward controller "+id);Near(view[170],Encoder.CardCode(id),"returned public reward source "+id);
                Check(g.Knowledge.Center[g.Actor].Any(f=>f.InstanceId==monster.InstanceId),"public returned source identity lost");
                int fact=-1;
                for(int at=0;at<Math.Round(view[167]*384);at++)
                    if(view[Encoder.KnowledgeOffset+at*8+7]==(monster.InstanceId+1)/65536f)fact=at;
                Check(fact>=0,"known returned source row missing");Near(view[171],-(fact+1)/384f,"returned source reference must point to known-row identity");
            }
            // DestroyActiveMonster is the same public helper used by Doom Gate;
            // its reward can wait in the public queue after the monster is bottomed.
            var queued=Playing(95060);var state=queued.Engine.State;int actor=queued.Actor;
            var source=state.CenterDeck.First(c=>c.DefId=="ingeminex_corruption");state.CenterDeck.Remove(source);source.Zone=ShardsZone.MonsterSpace;state.ActiveMonsters.Add(source);
            var action=new ShardsEndTurnAction{PlayerIndex=actor};int start=queued.Engine.Log.Count;var before=queued.Knowledge.BeforeSubmit(queued.Engine,null,action);
            queued.Engine.DestroyActiveMonster(source,actor);queued.Knowledge.AfterSubmit(queued.Engine,start,null,action,before);
            var observed=Encode(queued);Near(observed.obs[174],1/64f,"returned monster public reward queue count");
            Near(observed.obs[Encoder.ContinuationOffset],Encoder.CardCode(source.DefId),"returned monster queued reward source");
            Near(observed.obs[Encoder.ContinuationOffset+5],1,"queued registered reward classification");
            Check(observed.obs[Encoder.ContinuationOffset+2]<0,"queued source should reference known position without hidden entity");

            // A public definition alone is not authority to expose an unseen
            // physical card, nor private source/continuation-count/provenance data.
            var hidden=Playing(95061);var secret=hidden.Engine.State.Players[1-hidden.Actor].Deck[0];var original=Encode(hidden);
            var queueField=typeof(ShardsEngine).GetField("_effectQueue",BindingFlags.NonPublic|BindingFlags.Instance);
            var queue=(Queue<(IShardsEffect effect,ShardsContext ctx)>)queueField.GetValue(hidden.Engine);
            var ctx=new ShardsContext{Engine=hidden.Engine,ControllerIndex=secret.Owner,Source=secret};ctx.MarkCopied(secret);
            queue.Enqueue((secret.Def.PlayEffect,ctx));Same(original,Encode(hidden),"private queued source leaked through public continuation count/table");queue.Clear();
            var activeField=typeof(ShardsEngine).GetField("_activeContext",BindingFlags.NonPublic|BindingFlags.Instance);var previous=activeField.GetValue(hidden.Engine);
            activeField.SetValue(hidden.Engine,ctx);Same(original,Encode(hidden),"unrevealed active source leaked through public continuation/copy provenance");activeField.SetValue(hidden.Engine,previous);
        }
        private static int ConservationAcrossRules()
        {
            int states=0;
            foreach(var dlc in new[]{ShardsDlc.None,ShardsDlc.RelicsOfTheFuture,ShardsDlc.ShadowOfSalvation,ShardsDlc.IntoTheHorizon,ShardsDlc.Duel})
                for(ulong seed=95101;seed<95103;seed++)
                {
                    var g=Playing(seed,dlc);
                    for(int step=0;step<2000&&!g.Truncated;step++)
                    {
                        var view=Encode(g).obs;var s=g.Engine.State;int actor=g.Actor,enemy=1-actor;
                        var center=s.CenterDeck.GroupBy(c=>c.DefId).ToDictionary(x=>x.Key,x=>x.Count());var destinies=s.DestinyDeck.GroupBy(c=>c.DefId).ToDictionary(x=>x.Key,x=>x.Count());
                        foreach(string id in Encoder.CardIds)
                        {
                            var def=ShardsCardDatabase.Get(id);
                            if(def.Type!=ShardsCardType.Starter&&def.Type!=ShardsCardType.Relic&&def.Type!=ShardsCardType.Destiny)
                                Near(Count(view,21,id),center.TryGetValue(id,out int actual)?actual:0,"center conservation dlc="+dlc+" seed="+seed+" step="+step+" "+id);
                            if(def.Type==ShardsCardType.Destiny)
                                Near(Count(view,22,id),destinies.TryGetValue(id,out int actual)?actual:0,"destiny conservation "+id);
                            Near(Count(view,12,id),s.Players[enemy].SetAside.Count(c=>c.DefId==id),"inferred enemy set-aside "+id);
                        }
                        foreach(var p in s.Players)
                            foreach(var fact in g.Knowledge.Hand[p.Index].GroupBy(id=>id))
                                Check(fact.Count()<=p.Hand.Count(c=>c.DefId==fact.Key),"known hand fact absent from actual hidden hand: "+fact.Key);
                        states++;if(s.GameOver)break;g.Step(g.ExerciseChoice());
                    }
                    Check(g.Engine.State.GameOver,"conservation exercise did not complete naturally: "+dlc+" "+seed);
                }
            return states;
        }
        private static void DoomGateConservation()
        {
            var g=Playing(95200);var s=g.Engine.State;var action=new ShardsEndTurnAction{PlayerIndex=g.Actor};
            int start=g.Engine.Log.Count;var before=g.Knowledge.BeforeSubmit(g.Engine,null,action);
            s.Players[g.Actor].DoomGateFloodUsed=true;g.Engine.ShuffleIngeminexIntoCenterDeck(35);
            g.Knowledge.AfterSubmit(g.Engine,start,null,action,before);var view=Encode(g).obs;
            foreach(string id in Encoder.CardIds.Where(id=>ShardsCardDatabase.Get(id).IsMonster))
                Near(Count(view,21,id),s.CenterDeck.Count(c=>c.DefId==id),"public Doom Gate flood pool conservation "+id);
        }
    }
}
