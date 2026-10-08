using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;

internal static class PublicHandAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        void Check(string name,int seat,bool passed)
        {rows.Add(new{name,seat,passed});if(!passed)failures++;}
        void Observe(Adapter g,Action action)
        {
            g.Supplement.BeforeSubmit(g.Engine.State.Players[0].Deck.Count,g.Engine.State.Players[1].Deck.Count,null,null);
            int log=g.Engine.Log.Count;action();g.Supplement.AfterSubmit(g.Engine,log);RezAudit.Refresh(g);
        }
        foreach(int seat in new[]{0,1})foreach(bool compiled in new[]{false,true})foreach(bool redraw in new[]{false,true})
        {
            var g=RezAudit.Game(mastery:0,seat:redraw?1-seat:seat);int owner=1-seat;
            var p=g.Engine.State.Players[owner];string id=redraw?"prism":"korvus_legionnaire_duel";
            var card=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=owner,Zone=redraw?ShardsZone.Discard:ShardsZone.Hand};
            if(redraw)p.Discard.Add(card);else p.Hand.Add(card);
            g.Supplement.BeforeSubmit(g.Engine.State.Players[0].Deck.Count,g.Engine.State.Players[1].Deck.Count,null,null);
            int log=g.Engine.Log.Count;
            if(redraw)
            {
                p.Discard.Remove(card);card.Zone=ShardsZone.Deck;p.Deck.Add(card);
                g.Engine.Emit(new ShardsCardReturnedEvent{PlayerIndex=owner,InstanceId=card.InstanceId,DefId=id,ToDeckTop=true});
            }
            else g.Engine.Emit(new ShardsShieldsRevealedEvent{PlayerIndex=owner,DefIds=new List<string>{id},Prevented=1});
            g.Supplement.AfterSubmit(g.Engine,log);RezAudit.Refresh(g);
            if(redraw)RezAudit.Step(g,c=>c.Action is ShardsEndTurnAction);
            if(g.Actor!=seat||!p.Hand.Contains(card))throw new Exception("Public hand fixture did not reach the expected observer turn");
            ulong before=TacticalSearch.Fingerprint(g);int retained=0;bool defensive=true,invariant=true;
            Func<Adapter,Adapter> copy=compiled?FastCopy.Copy:TacticalSearch.Copy;
            var changed=copy(g);var other=changed.Engine.State.Players[owner];
            var unknown=other.Hand.FirstOrDefault(c=>c.DefId!=id);
            if(unknown!=null&&other.Deck.Count>0)
            {
                var swap=other.Deck[0];int at=other.Hand.IndexOf(unknown);
                other.Hand[at]=swap;other.Deck[0]=unknown;swap.Zone=ShardsZone.Hand;unknown.Zone=ShardsZone.Deck;
            }
            for(int w=0;w<32;w++)
            {
                var world=TacticalSearch.PublicWorld(g,713101+w*7919,copy);
                if(world.Engine.State.Players[owner].Hand.Any(c=>c.DefId==id))retained++;
                invariant&=TacticalSearch.Fingerprint(world)==TacticalSearch.Fingerprint(TacticalSearch.PublicWorld(changed,713101+w*7919,copy));
                defensive&=TacticalSearch.DefensiveWorld(g,713101+w*7919,copy).Engine.State.Players[owner].Hand.Any(c=>c.DefId==id);
            }
            bool passed=retained==32&&defensive&&invariant&&before==TacticalSearch.Fingerprint(g)&&g.Supplement.Hand(owner).SequenceEqual(new[]{id});
            rows.Add(new{name=redraw?"public-top-drawn-during-cleanup-stays-known-in-hand":"public-shield-reveal-stays-known-in-hand",seat,compiled,retained,defensive,invariant,passed});if(!passed)failures++;
        }
        foreach(int seat in new[]{0,1})
        {
            var g=RezAudit.Game(mastery:0,seat:seat);var p=g.Engine.State.Players[seat];
            var first=RezAudit.Add(g,"korvus_legionnaire_duel",seat);var second=RezAudit.Add(g,"korvus_legionnaire_duel",seat);
            for(int n=0;n<3;n++)Observe(g,()=>g.Engine.Emit(new ShardsCardsRevealedEvent{PlayerIndex=seat,FromHand=true,DefIds=new List<string>{first.DefId,second.DefId}}));
            Check("repeated-hand-reveals-use-max-multiplicity",seat,g.Supplement.Hand(seat).Count==2);
            RezAudit.Step(g,c=>c.Action is ShardsPlayCardAction a&&a.CardInstanceId==first.InstanceId);
            Check("real-play-consumes-one-known-copy",seat,g.Supplement.Hand(seat).SequenceEqual(new[]{second.DefId}));
            p.PlayZone.Remove(first);p.PlayedThisTurn.Remove(first);first.Zone=ShardsZone.Discard;p.Discard.Add(first);
            Observe(g,()=>g.Engine.Banish(first,p.Discard));
            Check("banishing-discard-copy-preserves-hand-knowledge",seat,g.Supplement.Hand(seat).SequenceEqual(new[]{second.DefId}));
            Observe(g,()=>g.Engine.Banish(second,p.Hand));
            Check("banishing-hand-copy-removes-knowledge",seat,g.Supplement.Hand(seat).Count==0);

            var old=RezAudit.Add(g,"prism",seat);
            Observe(g,()=>g.Engine.Emit(new ShardsCardsRevealedEvent{PlayerIndex=seat,FromHand=true,DefIds=new List<string>{old.DefId}}));
            RezAudit.Step(g,c=>c.Action is ShardsEndTurnAction);
            Check("cleanup-forgets-old-hand-reveal",seat,g.Supplement.Hand(seat).Count==0);

            var unknownDraw=RezAudit.Game(mastery:0,seat:seat);
            Observe(unknownDraw,()=>
            {
                int start=unknownDraw.Engine.Log.Count;unknownDraw.Engine.DrawCards(seat,1);
                for(int n=start;n<unknownDraw.Engine.Log.Count;n++)
                    if(unknownDraw.Engine.Log[n] is ShardsCardDrawnEvent ev)ev.DefId="PRIVATE_POISON";
            });
            Check("private-draw-identity-is-never-read",seat,unknownDraw.Supplement.Hand(seat).Count==0);
            Observe(unknownDraw,()=>unknownDraw.Engine.Emit(new ShardsCardsRevealedEvent{PlayerIndex=seat,DefIds=new List<string>{"prism"}}));
            Check("deck-reveal-is-not-a-hand-reveal",seat,unknownDraw.Supplement.Hand(seat).Count==0);

            var unify=RezAudit.Game(mastery:0,seat:seat);
            var aspirant=RezAudit.Add(unify,"undergrowth_aspirant",seat);var guardian=RezAudit.Add(unify,"shardwood_guardian_duel",seat);
            RezAudit.Step(unify,c=>c.Action is ShardsPlayCardAction a&&a.CardInstanceId==aspirant.InstanceId);
            Check("real-unify-reveal-is-remembered",seat,unify.Supplement.Hand(seat).Contains(guardian.DefId));

            var carrier=RezAudit.Game(mastery:0,seat:seat);var cp=carrier.Engine.State.Players[seat];
            var champion=RezAudit.Add(carrier,"giga_source_adept",seat);cp.Hand.Remove(champion);champion.Zone=ShardsZone.Deck;cp.Deck.Add(champion);
            var legion=RezAudit.Add(carrier,"legion_carrier_duel",seat);
            RezAudit.Step(carrier,c=>c.Action is ShardsPlayCardAction a&&a.CardInstanceId==legion.InstanceId);
            RezAudit.Step(carrier,c=>c.Kind==12&&c.Option?.CardInstanceId==champion.InstanceId);
            if(carrier.Decision?.Context=="soi.reveal")RezAudit.Step(carrier,c=>c.Kind==13);
            Check("real-carrier-recruited-champion-is-remembered",seat,carrier.Supplement.Hand(seat).Contains(champion.DefId));

            var discarded=RezAudit.Game(mastery:0,seat:seat);
            var known=RezAudit.Add(discarded,"prism",seat);RezAudit.Add(discarded,"korvus_legionnaire_duel",seat);
            Observe(discarded,()=>discarded.Engine.Emit(new ShardsCardsRevealedEvent{PlayerIndex=seat,FromHand=true,DefIds=new List<string>{known.DefId}}));
            Observe(discarded,()=>
            {
                typeof(ShardsEngine).GetMethod("QueueEffect",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(discarded.Engine,new object[]{new AllPlayersDiscard(1),seat,null});
                typeof(ShardsEngine).GetMethod("PumpEffects",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(discarded.Engine,null);
            });
            RezAudit.Step(discarded,c=>c.Kind==12&&c.Option?.CardInstanceId==known.InstanceId);
            // Wrapper multi-selection may require an explicit commit.
            if(discarded.Actor==seat&&discarded.Decision?.Context=="soi.discard")RezAudit.Step(discarded,c=>c.Kind==13);
            Check("accepted-discard-forgets-only-public-selected-card",seat,discarded.Supplement.Hand(seat).Count==0);

            var gate=RezAudit.Game(mastery:0,seat:seat);RezAudit.Add(gate,"oblivion_gatekeeper",seat,ShardsZone.Champions);
            RezAudit.Step(gate,c=>c.Action is ShardsExhaustAction);
            Check("gatekeeper-public-draw-is-retained",seat,gate.Supplement.Hand(seat).Count==1&&gate.Supplement.Hand(seat).All(id=>gate.Engine.State.Players[seat].Hand.Any(c=>c.DefId==id)));
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Public hand audit failed {failures} cases");
    }
}
