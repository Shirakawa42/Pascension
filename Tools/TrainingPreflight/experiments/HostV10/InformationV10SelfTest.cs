using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class InformationV10SelfTest
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static int _checks;
        private static void Check(bool okay,string label)
        { _checks++; if(!okay)throw new InvalidOperationException("InformationV10: "+label); }
        private static Adapter Fixture(ulong seed=927993)
        { var g=new Adapter(seed);while(g.Decision?.Context=="soi.herodraft")g.Step(0);return g; }
        private static ShardsCard Add(Adapter g,ShardsPlayer p,string id,ShardsZone zone,List<ShardsCard> list)
        { var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=p?.Index??-1,Zone=zone};list.Add(c);return c; }
        private static void Clear(ShardsPlayer p)
        {p.Hand.Clear();p.Deck.Clear();p.Discard.Clear();p.PlayZone.Clear();p.Champions.Clear();p.Destinies.Clear();p.ResetTurn();p.Health=50;p.Mastery=10;}
        private static void Refresh(Adapter g)
        {g.Engine.State.InvalidateCardIndex();if(g.Decision==null)g.Engine.PendingInput.LegalActions=g.Engine.LegalActions(g.Actor);g.Rebuild();}
        private static void Submit(Adapter g,PlayerAction a)=>typeof(Adapter).GetMethod("Submit",Private).Invoke(g,new object[]{a});
        private static void Answer(Adapter g,int id)=>Submit(g,new SubmitDecisionAction{PlayerIndex=g.Actor,
            Answer=new DecisionAnswer{DecisionId=g.Decision.Id,ChosenOptionIds=new List<int>{id}}});
        private static float[] Encode(Adapter g)
        {
            var x=new float[Encoder.ObsDim+2048+64];Encoder.Encode(g,x.AsSpan(0,Encoder.ObsDim),x.AsSpan(Encoder.ObsDim,2048),x.AsSpan(Encoder.ObsDim+2048,64));return x;
        }
        private static void FormerAlias(Adapter a,Adapter b,string label)
        {
            var x=Encode(a);var y=Encode(b);
            Check(x.Take(2816).SequenceEqual(y.Take(2816)),label+": previous2816 observations still alias");
            Check(x.Skip(Encoder.ObsDim).SequenceEqual(y.Skip(Encoder.ObsDim)),label+": legal candidates unchanged");
            Check(!x.SequenceEqual(y),label+": new public information distinguishes states");
        }
        private static Adapter Banished(bool reverse)
        {
            var g=Fixture();var e=g.Engine;var p=e.State.Players[g.Actor];p.ResetTurn();p.Gems=2;
            var cards=new[]{"fungal_hermit","shard_abstractor"}.Select(id=>e.State.CenterDeck.First(c=>c.DefId==id)).ToArray();
            foreach(var c in cards)e.State.CenterDeck.Remove(c);foreach(var c in reverse?cards.Reverse():cards)e.State.CenterDeck.Add(c);
            var d=Add(g,p,"shard_defiant",ShardsZone.SetAside,p.Destinies);Refresh(g);
            Submit(g,new ShardsExhaustAction{PlayerIndex=p.Index,CardInstanceId=d.InstanceId});Answer(g,2);return g;
        }
        private static Adapter Monster(bool torment)
        {
            var g=Fixture(927994);var e=g.Engine;foreach(var p in e.State.Players)Clear(p);
            e.State.ActiveMonsters.Clear();e.State.PendingMonsterAttacks.Clear();
            var a=Add(g,null,"ingeminex_brutality",ShardsZone.MonsterSpace,e.State.ActiveMonsters);
            var b=Add(g,null,"ingeminex_torment",ShardsZone.MonsterSpace,e.State.ActiveMonsters);
            e.State.PendingMonsterAttacks.Add(torment?b.InstanceId:a.InstanceId);Refresh(g);return g;
        }
        private static Adapter Queue(bool second)
        {
            var g=Fixture(927995);var p=g.Engine.State.Players[g.Actor];Clear(p);p.CopyHomodeusAlliesThisTurn=true;p.Gems=second?0:2;
            var c=Add(g,p,"reactor_drone_duel",ShardsZone.Hand,p.Hand);Refresh(g);
            Submit(g,new ShardsPlayCardAction{PlayerIndex=p.Index,CardInstanceId=c.InstanceId});if(second)Answer(g,1);return g;
        }
        private static Adapter Flags(bool fastBanish)
        {
            var g=Fixture(927996);var p=g.Engine.State.Players[g.Actor];Clear(p);
            var a=Add(g,p,"reactor_drone_duel",ShardsZone.PlayZone,p.PlayZone);
            var b=Add(g,p,"reactor_drone_duel",ShardsZone.PlayZone,p.PlayZone);
            b.FastPlayed=true;a.BanishAtCleanup=!fastBanish;b.BanishAtCleanup=fastBanish;Refresh(g);return g;
        }
        internal static void Run()
        {
            _checks=0;
            FormerAlias(Banished(false),Banished(true),"Public banished identities");
            var ma=Monster(false);var mb=Monster(true);FormerAlias(ma,mb,"Pending monster identity/status");
            var q1=Queue(false);var q2=Queue(true);FormerAlias(q1,q2,"Public duplicate effect progress");
            Check(Encode(q1)[2816+395]==.25f&&Encode(q2)[2816+395]==0,"Exact same-source queued play count");
            FormerAlias(Flags(false),Flags(true),"Per-card temporary/banish association");
            var a=Fixture(927997);var b=Fixture(927997);a.Engine.State.Players[a.Actor].Mastery=9;b.Engine.State.Players[b.Actor].Mastery=9;
            b.Engine.State.Players[b.Actor].SetAside.RemoveAt(0);Refresh(a);Refresh(b);FormerAlias(a,b,"Remaining own relics below mastery10");
            var before=Encode(a);a.Engine.State.Players[a.Actor].SetAside.Reverse();Check(before.SequenceEqual(Encode(a)),"Relic encoding order independent");
            Check(before[Encoder.RemainingRelicOffset]>0&&before[Encoder.RemainingRelicOffset+2]>0&&before[3008]==1&&before[3009]==.1f,
                "All three remaining relic IDs, count and mastery distance visible before eligibility");
            ma.Engine.State.PendingMonsterAttacks.Add(ma.Engine.State.ActiveMonsters[1].InstanceId);before=Encode(ma);
            ma.Engine.State.PendingMonsterAttacks.Reverse();var after=Encode(ma);
            Check(before.Take(2816+398).SequenceEqual(after.Take(2816+398)),"Same pending monster multiset keeps all aggregate/status fields");
            Check(!before.SequenceEqual(after),"Pending monster public attack order represented");
            QueuePrivacy(q1);HiddenAllocationPrivacy();
            Program.Print(new{passed=true,checks=_checks,observationSchema=Encoder.SchemaVersion,
                fixedAliases="banished/pending-monsters/public-duplicate-progress/per-card-temporary-flags/remaining-relics",
                privacy="composition-preserving-hand-draw/order/RNG/hidden-other-source-queue",
                limitations="64 public flag entities;56 pending order entries;only reviewed same-source registered play/exhaust continuation"});
        }
        private static void QueuePrivacy(Adapter g)
        {
            var before=Encode(g);var enemy=g.Engine.State.Players[1-g.Actor];
            var hidden=enemy.Hand.Count>0?enemy.Hand[0]:Add(g,enemy,"crystal",ShardsZone.Hand,enemy.Hand);
            // Preserve all public composition fields: establish the baseline after
            // the fixture creates a private source if the hand happened to be empty.
            before=Encode(g);
            var field=typeof(ShardsEngine).GetField("_effectQueue",Private);
            var queue=(Queue<(IShardsEffect effect,ShardsContext ctx)>)field.GetValue(g.Engine);
            var original=queue.ToArray();
            queue.Enqueue((hidden.Def.PlayEffect,new ShardsContext{Engine=g.Engine,ControllerIndex=enemy.Index,Source=hidden}));
            Check(before.SequenceEqual(Encode(g)),"Unreviewed hidden other-source queued effect cannot alter descriptor");
            queue.Clear();foreach(var entry in original)queue.Enqueue(entry);
            var ctx=(ShardsContext)typeof(ShardsEngine).GetField("_activeContext",Private).GetValue(g.Engine);var saved=ctx.Source;
            ctx.Source=hidden;
            Check(Encode(g)[2816+394]==0,"Hidden current source never activates public queue descriptor");ctx.Source=saved;
        }
        private static void HiddenAllocationPrivacy()
        {
            var rng=new Random(928110);
            for(int seed=0;seed<12;seed++)
            {
                var g=Fixture((ulong)(928110+seed));
                for(int step=0;step<200&&!g.Engine.State.GameOver&&!g.Truncated;step++)
                {
                    var e=g.Engine;var own=e.State.Players[g.Actor];var enemy=e.State.Players[1-g.Actor];
                    var before=Encode(g);var hand=enemy.Hand.ToArray();var deck=enemy.Deck.ToArray();var ownDeck=own.Deck.ToArray();var center=e.State.CenterDeck.ToArray();
                    ulong randomState=e.State.Rng.State;var joined=hand.Concat(deck).Reverse().ToArray();
                    enemy.Hand.Clear();enemy.Hand.AddRange(joined.Take(hand.Length));enemy.Deck.Clear();enemy.Deck.AddRange(joined.Skip(hand.Length));
                    foreach(var c in enemy.Hand)c.Zone=ShardsZone.Hand;foreach(var c in enemy.Deck)c.Zone=ShardsZone.Deck;
                    own.Deck.Reverse();e.State.CenterDeck.Reverse();e.State.Rng.State^=0xD1B54A32D192ED03UL;
                    var after=Encode(g);
                    enemy.Hand.Clear();enemy.Hand.AddRange(hand);enemy.Deck.Clear();enemy.Deck.AddRange(deck);
                    foreach(var c in enemy.Hand)c.Zone=ShardsZone.Hand;foreach(var c in enemy.Deck)c.Zone=ShardsZone.Deck;
                    own.Deck.Clear();own.Deck.AddRange(ownDeck);e.State.CenterDeck.Clear();e.State.CenterDeck.AddRange(center);e.State.Rng.State=randomState;
                    Check(before.SequenceEqual(after),"Hidden allocation/order invariance seed"+seed+" step"+step);
                    g.Step(g.ExerciseChoice(rng));
                }
            }
        }
    }
}
