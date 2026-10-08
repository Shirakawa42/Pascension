using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Pascension.Engine.Actions;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class SelfTest
    {
        private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException("Selftest: "+message);}
        private static float[] Observation(Adapter g)
        {
            var obs=new float[Encoder.ObsDim];Encoder.Encode(g,obs,new float[Encoder.MaxActions*Encoder.ActionDim],new float[Encoder.MaxActions]);return obs;
        }
        private static Adapter Playing(ulong seed)
        {
            var g=new Adapter(new ShardsEngine(Program.Config(seed)));
            while(g.Decision?.Context=="soi.herodraft")g.Step(g.ExerciseChoice());
            return g;
        }
        private static ShardsCard Card(Adapter g,string id,int owner,ShardsZone zone)
        {
            var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=owner,Zone=zone};
            g.Engine.State.InvalidateCardIndex();return c;
        }
        internal static void Run()
        {
            var catalogCache=CatalogCacheSelfTest();
            // Real rules, whole games, both seats, every pending owner. Frozen
            // opponent is separately checked by opponent-selftest.
            var contexts=new HashSet<string>();int states=0,completed=0;
            for(ulong seed=701;seed<705;seed++)
            {
                var a=new Adapter(new ShardsEngine(Program.Config(seed)));
                var b=new Adapter(new ShardsEngine(Program.Config(seed)),automaticSingletons:true);
                while(!a.Engine.State.GameOver&&!a.Truncated)
                {
                    var obs=Observation(a);Check(obs.All(float.IsFinite),"nonfinite observation");
                    Check(a.VisibleCount>0,"no legal wrapper choice");
                    if(a.Decision!=null)contexts.Add(a.Decision.Context??"");
                    int choice=a.ExerciseChoice();a.Step(choice);states++;
                }
                while(!b.Engine.State.GameOver&&!b.Truncated)b.Step(b.ExerciseChoice());
                Check(a.Engine.State.ComputeHash()==b.Engine.State.ComputeHash(),"singleton automation state parity");
                Check(a.Submissions==b.Submissions,"singleton automation submission parity");
                if(a.Engine.State.GameOver)completed++;
            }
            var privacy=new Adapter(new ShardsEngine(Program.Config(9301)));
            // Finish both real hero picks before testing hidden draw permutations.
            while(privacy.Decision?.Context=="soi.herodraft")privacy.Step(privacy.ExerciseChoice());
            var baseline=Observation(privacy);int enemy=1-privacy.Actor;
            privacy.Engine.State.Players[enemy].Deck.Reverse();privacy.Engine.State.Players[enemy].Hand.Reverse();
            Check(baseline.SequenceEqual(Observation(privacy)),"hidden enemy order invariance");
            // Explicitly transfer hidden identities between hand and draw pile while
            // preserving publicly authorized collection and public zone sizes.
            var p=privacy.Engine.State.Players[enemy];
            if(p.Hand.Count>0&&p.Deck.Count>0)
            {
                var h=p.Hand[0];p.Hand[0]=p.Deck[0];p.Deck[0]=h;
                Check(baseline.SequenceEqual(Observation(privacy)),"hidden enemy allocation invariance");
            }
            var page=new Adapter(new ShardsEngine(Program.Config(111)));
            var request=page.Decision;
            for(int i=0;i<130;i++)request.Options.Add(new Pascension.Engine.Decisions.DecisionOption(500+i,"Page option "+i){DefId="decima"});
            page.Rebuild();var reachable=new HashSet<int>();int pageCount=(page.Candidates.Count+62)/63;
            for(int n=0;n<pageCount;n++)
            {
                for(int i=0;i<page.VisibleCount;i++)if(page.Visible(i).Option!=null)reachable.Add(page.Visible(i).Option.Id);
                Check(page.Visible(page.VisibleCount-1).Kind==14,"page control absent");page.Step(page.VisibleCount-1);
            }
            Check(reachable.Count==request.Options.Count,"off-page options unreachable");
            // Disabled options must remain represented independently of candidates.
            request.Options[0].Disabled=true;page.Rebuild();var options=Observation(page);
            Check(options[Encoder.OptionOffset+6]==1,"disabled revealed option absent");
            // More remembered tops than the old capacity four remain visible.
            for(int i=0;i<12;i++)page.Knowledge.Center[page.Actor].Add(new KnownPosition{DefId="crystal",InstanceId=800+i,Min=i,Max=i});
            Check(Observation(page)[167]>=12/384f,"known deck positions truncated");
            bool overflow=false;
            for(int i=request.Options.Count;i<=Encoder.Capacity;i++)request.Options.Add(new Pascension.Engine.Decisions.DecisionOption(9000+i,"Overflow "+i){DefId="decima"});
            try{Observation(page);}catch(InvalidOperationException e){overflow=e.Message.Contains("capacity");}
            Check(overflow,"overflow must fail instead of truncating");
            ReusedBufferParity();
            RevealProvenance();
            CenterProvenance();
            CopyProvenance();
            var visibilityChecks=VisibilitySelfTest.Run();
            var zoneChecks=ZoneVisibilitySelfTest.Run();
            var actionAudit=ActionSelfTest.Run();
            Program.Print(new{passed=true,states,completed_games=completed,contexts=contexts.OrderBy(x=>x).ToArray(),catalog_cache=catalogCache,visibility_checks=visibilityChecks,zone_checks=zoneChecks,action_audit=actionAudit,checks=new[]{"real-rules/automation state-and-submission parity","finite tensors","hidden-order/allocation invariance","all pages reachable","disabled option visibility","remembered tops beyond four","hard overflow","pinned reused-dirty-buffer vs fresh complete encoding", "Fabricator public tops without copy menu", "Legion temporary reveal conservation", "Corruption reward removes inferred set-aside relic", "temporary public identities reentering visible zones", "uncertain singleton center facts", "chronological public center shuffle", "free market bottom-return order", "selected and automatic nested copy/replay provenance"}});
        }
        internal static object CatalogCacheSelfTest()
        {
            var cache=(System.Collections.Concurrent.ConcurrentDictionary<(string hero,ShardsDlc dlc),string[]>)typeof(Encoder)
                .GetField("RelicCatalog",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
            int cases=0,liveChecks=0;
            for(int repeat=0;repeat<2;repeat++)
            {
                Encoder.Initialize();Check(cache.Count==0,"catalog cache survives Initialize");
                foreach(string hero in ShardsEngine.DraftableCharacters.Concat(new string[]{null,"unknown-hero"}))
                for(int mask=0;mask<16;mask++)
                {
                    var expected=ShardsEngine.RelicIdsFor(hero,(ShardsDlc)mask).ToArray();
                    var actual=Encoder.CatalogRelics(hero,(ShardsDlc)mask);
                    Check(expected.SequenceEqual(actual),$"cold relic catalog {hero}/{mask}");
                    var warm=Encoder.CatalogRelics(hero,(ShardsDlc)mask);
                    Check(ReferenceEquals(actual,warm)&&expected.SequenceEqual(warm),$"warm relic catalog {hero}/{mask}");
                    cases++;
                }
            }
            var g=Playing(62801);var enemy=g.Engine.State.Players[1-g.Actor];
            var relics=Encoder.CatalogRelics(enemy.CharacterId,g.Engine.State.Dlc);
            Check(relics.Length>0,"live relic-cache fixture has no relics");
            g.Knowledge.RecruitedRelicDefs[enemy.Index].Clear();var before=Observation(g);
            foreach(string id in relics)
            {
                int column=Encoder.HistogramOffset+12*Encoder.CardCapacity+Encoder.CardIndex(id);
                Check(before[column]==0.1f,"unrecruited cached relic absent");
                g.Knowledge.RecruitedRelicDefs[enemy.Index].Add(id);
                Check(Observation(g)[column]==0,"recruited relic cached instead of live filtered");liveChecks++;
            }
            g.Knowledge.RecruitedRelicDefs[enemy.Index].Clear();
            Check(before.SequenceEqual(Observation(g)),"restored recruited facts differ after warmed cache");liveChecks++;
            Check(ReferenceEquals(relics,Encoder.CatalogRelics(enemy.CharacterId,g.Engine.State.Dlc)),"live recruited facts changed catalog membership");
            Encoder.Initialize();Check(cache.Count==0,"final catalog cache reset");
            return new{passed=true,initialize_cycles=2,heroes=7,dlc_masks=16,cold_warm_cases=cases,live_recruited_fact_checks=liveChecks};
        }
        private static void ReusedBufferParity()
        {
            var reused=new Adapter(new ShardsEngine(Program.Config(54001)));var fresh=new Adapter(new ShardsEngine(Program.Config(54001)));
            var obs=Enumerable.Repeat(77f,Encoder.ObsDim).ToArray();var candidates=Enumerable.Repeat(77f,Encoder.MaxActions*Encoder.ActionDim).ToArray();
            var mask=Enumerable.Repeat(77f,Encoder.MaxActions).ToArray();var handle=GCHandle.Alloc(obs,GCHandleType.Pinned);
            try
            {
                for(int step=0;step<600;step++)
                {
                    var expected=new float[Encoder.ObsDim];var expectedCandidates=new float[candidates.Length];var expectedMask=new float[mask.Length];
                    Encoder.Encode(reused,obs,candidates,mask);Encoder.Encode(fresh,expected,expectedCandidates,expectedMask);
                    Check(obs.SequenceEqual(expected)&&candidates.SequenceEqual(expectedCandidates)&&mask.SequenceEqual(expectedMask),"reused/prefix-cleared buffer differs from fresh complete encoding");
                    if(reused.Engine.State.GameOver||reused.Truncated)break;
                    int choice=reused.ExerciseChoice();Check(choice==fresh.ExerciseChoice(),"fresh/reused policy schedule differs");reused.Step(choice);fresh.Step(choice);
                }
            }
            finally{handle.Free();}
        }
        private static void RevealProvenance()
        {
            var fabricator=Playing(66001);
            foreach(var p in fabricator.Engine.State.Players)
            {p.Deck.Clear();p.Deck.Add(Card(fabricator,"li_hin",p.Index,ShardsZone.Deck));}
            var fab=Card(fabricator,"duplication_fabricator_duel",0,ShardsZone.Hand);fabricator.Engine.State.Players[0].Hand.Add(fab);
            fabricator.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=fab.InstanceId});
            Check(fabricator.Knowledge.Personal.All(f=>f.Count==1&&f[0].DefId=="li_hin"),"public Fabricator tops without any copy menu were forgotten");
            var carrier=Playing(66002);var player=carrier.Engine.State.Players[0];player.Deck.Clear();
            for(int i=0;i<5;i++)player.Deck.Add(Card(carrier,"crystal",0,ShardsZone.Deck));
            var legion=Card(carrier,"legion_carrier_duel",0,ShardsZone.Hand);player.Hand.Add(legion);
            carrier.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=legion.InstanceId});
            Check(carrier.Decision?.Context=="soi.reveal"&&carrier.Decision.Options.Count==5&&carrier.Decision.Options.All(o=>o.Disabled),"carrier must show all nonqualifying revealed cards");
            Check(carrier.Knowledge.Detached.Count==5,"temporarily removed public deck cards missing");
            var view=Observation(carrier);Check(view[Encoder.HistogramOffset+23*Encoder.CardCapacity+Encoder.CardIndex("crystal")]>=0.5f,"full owned collection omits pending revealed cards");
            carrier.Step(carrier.ExerciseChoice());Check(carrier.Knowledge.Detached.Count==0,"resolved temporary reveal cards remain detached");
            // Retire a remembered temporary identity that later reenters a
            // visible zone; duplicate definitions remain separate instances.
            var exposed=player.Discard[0];carrier.Knowledge.Detached.Add(new ShardsCard{InstanceId=exposed.InstanceId,DefId=exposed.DefId,Owner=0,Zone=ShardsZone.Deck});
            Observation(carrier);Check(carrier.Knowledge.Detached.Count==0,"stale revealed identity duplicated a current visible instance");
            // Corruption's public return, rather than normal recruit, spends a
            // formerly set-aside relic and must change the inferred enemy pool.
            var corrupt=Playing(66003);var owner=corrupt.Engine.State.Players[0];var relic=owner.SetAside[0];
            int logStart=corrupt.Engine.Log.Count;var before=corrupt.Knowledge.BeforeSubmit(corrupt.Engine,null,new ShardsEndTurnAction());
            owner.SetAside.Remove(relic);relic.Zone=ShardsZone.Hand;owner.Hand.Add(relic);
            corrupt.Engine.Emit(new ShardsCardReturnedEvent{PlayerIndex=0,InstanceId=relic.InstanceId,DefId=relic.DefId});
            corrupt.Knowledge.AfterSubmit(corrupt.Engine,logStart,null,new ShardsEndTurnAction(),before);
            Check(corrupt.Knowledge.RecruitedRelicDefs[0].Contains(relic.DefId),"reward relic still inferred as set aside");
        }
        private static void CenterProvenance()
        {
            var g=Playing(77001);var action=new ShardsEndTurnAction();
            g.Knowledge.Center[0].Add(new KnownPosition{DefId="crystal",InstanceId=9001,Min=0,Max=1});
            int start=g.Engine.Log.Count;var before=g.Knowledge.BeforeSubmit(g.Engine,null,action);
            g.Engine.Emit(new ShardsRowRefilledEvent{DefId="crystal"});g.Knowledge.AfterSubmit(g.Engine,start,null,action,before);
            var fact=g.Knowledge.Center[0].Single();Check(fact.Min==0&&fact.Max==0&&fact.UncertainPresence,"unknown pop must retain possible absence");
            var obs=Observation(g);Check(obs[Encoder.KnowledgeOffset+4]==0&&obs[Encoder.KnowledgeOffset+5]==1,"uncertain singleton represented as certain");
            Check(obs[Encoder.KnowledgeOffset+7]==9002/65536f,"already-public remembered physical identity omitted");
            start=g.Engine.Log.Count;before=g.Knowledge.BeforeSubmit(g.Engine,null,action);
            g.Engine.Emit(new ShardsRowRefilledEvent{DefId="crystal"});g.Knowledge.AfterSubmit(g.Engine,start,null,action,before);
            Check(g.Knowledge.Center[0].Count==0,"expired possibly-absent fact retained");
            g.Knowledge.Center[0].Add(new KnownPosition{DefId="crystal",InstanceId=9002,Min=0,Max=0});
            start=g.Engine.Log.Count;before=g.Knowledge.BeforeSubmit(g.Engine,null,action);
            g.Engine.ShuffleIngeminexIntoCenterDeck(0);g.Engine.Emit(new ShardsRowRefilledEvent{DefId="blaster"});
            g.Knowledge.AfterSubmit(g.Engine,start,null,action,before);Check(g.Knowledge.Center[0].Count==0,"public shuffle must invalidate before later same-submit reveals");
            var bottom=g.Engine.State.CenterRow[0];start=g.Engine.Log.Count;before=g.Knowledge.BeforeSubmit(g.Engine,null,action);
            g.Engine.State.Players[0].DoomGateFloodUsed=true;g.Engine.ShuffleIngeminexIntoCenterDeck(0);
            g.Engine.BottomRowCardAndRefill(0);g.Knowledge.AfterSubmit(g.Engine,start,null,action,before);
            Check(g.Knowledge.Center.All(fs=>fs.Any(f=>f.DefId==bottom.DefId&&f.InstanceId==bottom.InstanceId&&f.Min==g.Engine.State.CenterDeck.Count-1&&f.Max==f.Min)),"same-submit post-shuffle free removal lost public bottom identity/position");
        }
        private static void CopyProvenance()
        {
            var fab=Playing(88001);var p=fab.Engine.State.Players[0];p.Mastery=20;
            foreach(var player in fab.Engine.State.Players)
            {player.Deck.Clear();player.Deck.Add(Card(fab,"reactor_drone_duel",player.Index,ShardsZone.Deck));}
            var source=Card(fab,"duplication_fabricator_duel",0,ShardsZone.Hand);p.Hand.Add(source);
            fab.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=source.InstanceId});
            Check(fab.Decision?.Context=="soi.copy","Fabricator copy menu absent");
            var ids=fab.Decision.Options.Select(o=>o.Id).ToList();
            fab.ApplyExternal(new SubmitDecisionAction{PlayerIndex=0,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=fab.Decision.Id,ChosenOptionIds=ids}});
            Check(fab.Decision?.Context=="soi.mode","copied Reactor child menu absent");
            var obs=Observation(fab);Check(obs[176]>=5/96f,"copy selections/guard/current and future effects missing");
            Check(obs[Encoder.CopyOffset]==Encoder.CardCode("reactor_drone_duel"),"copied target definition disappeared while child pending");
            var production=new Shards.AI.Adapter(new ShardsEngine(Program.Config(88004))){Engine=fab.Engine};
            var branch=Shards.AI.TacticalSearch.Copy(production);
            var contextField=typeof(ShardsEngine).GetField("_activeContext",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var originalContext=(ShardsContext)contextField.GetValue(fab.Engine);var branchContext=(ShardsContext)contextField.GetValue(branch.Engine);
            Check(!ReferenceEquals(originalContext.PublicSelections,branchContext.PublicSelections)&&!ReferenceEquals(originalContext.PublicCopyScopes,branchContext.PublicCopyScopes)
                &&!ReferenceEquals(originalContext.PublicCopyScopes[0],branchContext.PublicCopyScopes[0]),"search clone shares mutable copy provenance");
            ulong originalHash=fab.Engine.State.ComputeHash();
            var result=branch.Engine.Submit(new SubmitDecisionAction{PlayerIndex=0,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=branch.Engine.PendingInput.Decision.Id,ChosenOptionIds=new List<int>{1}}});
            Check(result.Accepted&&fab.Engine.State.ComputeHash()==originalHash&&originalContext.PublicCopyScopes[0].CurrentIndex==0
                &&branchContext.PublicCopyScopes[0].CurrentIndex==1,"hypothetical child resolution mutated live provenance");
            var auto=Playing(88002);p=auto.Engine.State.Players[0];p.Mastery=20;
            var reactor=Card(auto,"reactor_drone_duel",0,ShardsZone.PlayZone);p.PlayZone.Add(reactor);p.PlayedThisTurn.Add(reactor);
            source=Card(auto,"ojas_genesis_druid",0,ShardsZone.Hand);p.Hand.Add(source);
            auto.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=source.InstanceId});
            Check(auto.Decision?.Context=="soi.mode","automatic single-target copy child absent");
            obs=Observation(auto);Check(obs[176]>=2/96f&&obs[Encoder.CopyOffset+8+7]==0.2f,"automatic copy target/repetition provenance missing");
            var replay=Playing(88003);p=replay.Engine.State.Players[0];
            reactor=Card(replay,"reactor_drone_duel",0,ShardsZone.Hand);p.Hand.Add(reactor);
            source=Card(replay,"warpquartz_duel",0,ShardsZone.Hand);p.Hand.Add(source);
            replay.ApplyExternal(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=source.InstanceId});
            replay.ApplyExternal(new SubmitDecisionAction{PlayerIndex=0,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=replay.Decision.Id,ChosenOptionIds=new List<int>{reactor.InstanceId}}});
            Check(replay.Decision?.Context=="soi.mode","direct banished-effect replay child absent");
            obs=Observation(replay);Check(obs[176]>=1/96f&&obs[Encoder.CopyOffset]==Encoder.CardCode("reactor_drone_duel")&&obs[Encoder.CopyOffset+7]==0.2f,"direct replay selected definition/repetitions missing");
        }
    }
}
