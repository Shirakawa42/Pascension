using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class ReactorCleanupV10SelfTest
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static int _checks;
        private static void Check(bool okay,string label)
        { _checks++;if(!okay)throw new InvalidOperationException("ReactorCleanupV10: "+label); }
        private static Adapter Fixture(ulong seed)
        {
            var g=new Adapter(seed);while(g.Decision?.Context=="soi.herodraft")g.Step(0);
            var p=g.Engine.State.Players[g.Actor];p.Hand.Clear();p.Deck.Clear();p.Discard.Clear();p.PlayZone.Clear();p.Champions.Clear();p.Destinies.Clear();
            p.ResetTurn();p.Health=50;p.Mastery=20;return g;
        }
        private static void Refresh(Adapter g)
        { g.Engine.State.InvalidateCardIndex();g.Engine.PendingInput.LegalActions=g.Engine.LegalActions(g.Actor);g.Rebuild(); }
        private static ShardsCard Add(Adapter g,ShardsPlayer p,string id,ShardsZone zone,List<ShardsCard> list)
        {var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=p.Index,Zone=zone};list.Add(c);return c;}
        private static void Submit(Adapter g,PlayerAction action)=>typeof(Adapter).GetMethod("Submit",Private).Invoke(g,new object[]{action});
        private static void Answer(Adapter g,params int[] ids)=>Submit(g,new SubmitDecisionAction{PlayerIndex=g.Actor,
            Answer=new DecisionAnswer{DecisionId=g.Decision.Id,ChosenOptionIds=ids.ToList()}});
        private static float[] Observation(Adapter g)
        {var x=new float[Encoder.ObsDim];Encoder.Encode(g,x,new float[2048],new float[64]);return x;}
        internal static void Run()
        {
            _checks=0;
            ExerciseWarp(false,2,928406); // actual reported failure: unkept + explicit banish
            ExerciseWarp(false,1,928407); // unkept + two gems still returns normally
            ExerciseWarp(true,2,928408);  // kept + explicit banish still banishes normally
            ExerciseWarp(true,1,928409);  // kept + two gems joins discard normally
            ExerciseHand(2,928410);ExerciseHand(1,928411);
            Program.Print(new{passed=true,checks=_checks,
                paths="DeadlyRecruitsM20/decline-or-keep/2-or-3gems/endTurn;normal-hand/2-or-3gems/endTurn",
                rule="Explicit end-of-turn banish precedes temporary fast-play return and clears both transient flags",
                scope="Constructed initial positions followed only by accepted real engine actions; no manual fast/banish flag setup"});
        }
        private static void ExerciseWarp(bool keep,int mode,ulong seed)
        {
            var g=Fixture(seed);var e=g.Engine;var p=e.State.Players[g.Actor];
            var old=e.State.CenterRow[0];old.Zone=ShardsZone.CenterDeck;e.State.CenterDeck.Add(old);
            var reactor=new ShardsCard{InstanceId=e.State.NextInstanceId++,DefId="reactor_drone_duel",Owner=-1,Zone=ShardsZone.CenterRow};e.State.CenterRow[0]=reactor;
            var destiny=Add(g,p,"deadly_recruits_duel",ShardsZone.SetAside,p.Destinies);Refresh(g);
            Submit(g,new ShardsExhaustAction{PlayerIndex=p.Index,CardInstanceId=destiny.InstanceId});
            Check(g.Decision?.Context=="soi.warp","Actual Deadly Recruits row choice");Answer(g,0);
            Check(g.Decision?.Context=="soi.keepfast","Actual keep decision");
            if(keep)Answer(g,reactor.InstanceId);else Answer(g);
            Check(g.Decision?.Context=="soi.mode","Actual physical Reactor mode");Answer(g,mode);
            Check(p.Gems==(mode==2?3:2),"Selected Reactor gem gain");
            Check(reactor.FastPlayed==!keep&&reactor.BanishAtCleanup==(mode==2),"Flags arise from accepted card effects");
            FinishAndCheck(g,p,reactor,mode==2,returnToCenter:!keep&&mode==1);
        }
        private static void ExerciseHand(int mode,ulong seed)
        {
            var g=Fixture(seed);var p=g.Engine.State.Players[g.Actor];var reactor=Add(g,p,"reactor_drone_duel",ShardsZone.Hand,p.Hand);Refresh(g);
            Submit(g,new ShardsPlayCardAction{PlayerIndex=p.Index,CardInstanceId=reactor.InstanceId});Answer(g,mode);
            Check(p.Gems==(mode==2?3:2)&&!reactor.FastPlayed,"Normal hand play mode gain and permanent status");
            FinishAndCheck(g,p,reactor,mode==2,returnToCenter:false);
        }
        private static void FinishAndCheck(Adapter g,ShardsPlayer p,ShardsCard card,bool banish,bool returnToCenter)
        {
            var e=g.Engine;int log=e.Log.Count;
            Submit(g,new ShardsEndTurnAction{PlayerIndex=p.Index});
            int banishEvents=0,returnEvents=0;
            for(int i=log;i<e.Log.Count;i++)
            {
                if(e.Log[i] is ShardsCardBanishedEvent b&&b.InstanceId==card.InstanceId&&b.PlayerIndex==p.Index)banishEvents++;
                if(e.Log[i] is ShardsMercenaryReturnedEvent r&&r.DefId==card.DefId&&r.PlayerIndex==p.Index)returnEvents++;
            }
            Check(!card.BanishAtCleanup&&!card.FastPlayed,"No stale transient flags survive cleanup");
            Check(e.State.Banished.Contains(card)==banish,"Exact banished membership");
            Check(e.State.CenterDeck.Contains(card)==returnToCenter,"Exact center-return membership");
            bool owned=p.Hand.Contains(card)||p.Deck.Contains(card)||p.Discard.Contains(card)||p.PlayZone.Contains(card)||p.Champions.Contains(card);
            Check(owned==(!banish&&!returnToCenter),"Exact surviving permanent collection membership");
            Check(banishEvents==(banish?1:0)&&returnEvents==(returnToCenter?1:0),"Exactly one correct public cleanup event");
            Check(p.CardsBanishedThisTurn==0,"Cleanup resets the per-turn banish counter after emitting events");
            Check(card.Owner==(returnToCenter?-1:p.Index),"Owner retained for banish and permanent cards only");
            if(banish)Check(card.Zone==ShardsZone.Banished,"Banished zone preserved");
            // The new actor sees the former player's exact permanent collection.
            Check(g.Actor!=p.Index,"End turn advances to opponent");var obs=Observation(g);int code=Encoder.CardIndex(card.DefId);
            Check(obs[2560+code]==(owned ? .1f : 0f),"Opponent public collection immediately reflects cleanup");
            if(banish)Check(obs[2816+code]>=.1f,"Public banished histogram immediately reflects cleanup");
        }
    }
}
