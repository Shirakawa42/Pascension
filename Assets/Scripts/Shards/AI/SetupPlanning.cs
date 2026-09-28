using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

namespace Shards.AI
{
internal static class SetupPlanning
{
    internal static int ImproveResourceOrder(Adapter root,IReadOnlyList<string> path,Func<Adapter,Adapter> copy)
    {
        if(root.Decision!=null||path.Count<2)return -1;
        int resource=Find(root,path[0]);if(resource<0||!HybridLookahead.ResourceCard(root,resource))return -1;
        if(root.Engine.State.Players[root.Actor].Gems==0)
        {
            int funded=FundPlannedFocus(root,path,resource,copy);
            if(funded>=0)return funded;
        }
        int setup=-1;
        // Prefer the first already-planned non-resource action. A later planned
        // Focus may move earlier only if the resource tier refunds its entire
        // cost immediately. Never invent a paid mastery strategy.
        for(int i=1;i<path.Count;i++)
        {
            int a=Find(root,path[i]);if(a<0)break;
            if(HybridLookahead.ResourceCard(root,a))continue;
            setup=a;break;
        }
        bool Transparent(IShardsEffect e,int highestMastery)
        {
            if(e is Gain gain)return gain.Draw>=0&&gain.Gems>=0&&gain.Power>=0&&gain.Health>=0&&gain.Mastery>=0;
            if(e is ShardsNullEffect)return true;
            if(e is ShardsComposite sequence)return sequence.Parts.All(part=>Transparent(part,highestMastery));
            if(e is AtMastery at)return highestMastery<at.Threshold||Transparent(at.Inner,highestMastery);
            if(e is BestByMastery tiers)return tiers.Tiers.All(t=>Transparent(t.effect,highestMastery));
            if(e is If condition)return condition.ControllerVisible&&Transparent(condition.Inner,highestMastery);
            if(e is PerCount count)
            {var u=count.PerUnit;return count.ControllerVisible&&u.draw==0&&u.gems>=0&&u.power>=0&&u.mastery>=0&&u.health>=0;}
            return false;
        }
        bool UsableSetup(int a)
        {
            if(a<0)return false;
            var action=root.Visible(a).Action;
            if(action is ShardsFocusAction)return !SafeTurnGains.WastefulFocus(root,a);
            var effect=Effect(root,a,out var card);
            if(card==null||!(action is ShardsExhaustAction&&card.Def.ExhaustGemCost==0||action is ShardsPlayCardAction&&!card.Def.IsChampion))return false;
            var amount=new SafeTurnGains.Amounts();
            int mastery=root.Engine.State.Players[root.Actor].Mastery;
            return SafeTurnGains.Read(effect,mastery,ref amount,context:
                new ShardsContext{Engine=root.Engine,ControllerIndex=root.Actor,Source=card})&&amount.Mastery>0&&Transparent(effect,mastery+amount.Mastery);
        }
        bool requireRefund=false;
        if(!UsableSetup(setup))
        {
            setup=-1;
            for(int a=0;a<root.VisibleCount;a++)if(root.Visible(a).Action is ShardsFocusAction){setup=a;break;}
            if(!UsableSetup(setup)||!path.Contains(TacticalSearch.Key(root,setup)))return -1;
            requireRefund=true;
        }
        var template=TacticalSearch.PublicWorld(root,713101,copy);var original=copy(template);var reordered=copy(template);
        string r=path[0],s=TacticalSearch.Key(root,setup);
        bool Apply(Adapter g,string key)
        {
            if(g.Decision!=null||g.Engine.State.GameOver||g.Actor!=root.Actor)return false;
            int a=Find(g,key);if(a<0)return false;int log=g.Engine.Log.Count;g.Step(a);
            for(int j=log;j<g.Engine.Log.Count;j++)
                if(g.Engine.Log[j] is ShardsCardDrawnEvent or ShardsCardsRevealedEvent or ShardsMonsterRevealedEvent or ShardsDeckShuffledEvent or ShardsTurnStartedEvent)return false;
            return g.Decision==null&&!g.Engine.State.GameOver&&g.Actor==root.Actor;
        }
        if(!Apply(original,r))return -1;
        int unfocusedGems=original.Engine.State.Players[root.Actor].Gems;
        if(!Apply(original,s)||!Apply(reordered,s)||!Apply(reordered,r))return -1;
        var before=original.Engine.State.Players[root.Actor];var after=reordered.Engine.State.Players[root.Actor];
        if(requireRefund&&after.Gems<unfocusedGems)return -1;
        if(after.Gems<before.Gems||after.Power<before.Power||after.Gems==before.Gems&&after.Power==before.Power)return -1;
        // Normalize only the two improved pools on disposable copies for the
        // state-equivalence check. This never mutates the live game or a rollout.
        after.Gems=before.Gems;after.Power=before.Power;
        if(root.Visible(setup).Action is ShardsPlayCardAction)
        {
            // Both known cards were played without information events. Compare
            // the same appended multiset; preserve every pre-existing position.
            // Engine replay and the full fingerprint still reject other effects.
            var initial=root.Engine.State.Players[root.Actor];
            bool Normalize(List<ShardsCard> cards,int count)
            {if(cards.Count!=count+2)return false;cards.Sort(count,2,Comparer<ShardsCard>.Create((a,b)=>a.InstanceId.CompareTo(b.InstanceId)));return true;}
            if(!Normalize(before.PlayZone,initial.PlayZone.Count)||!Normalize(after.PlayZone,initial.PlayZone.Count)||
                !Normalize(before.PlayedThisTurn,initial.PlayedThisTurn.Count)||!Normalize(after.PlayedThisTurn,initial.PlayedThisTurn.Count))return -1;
        }
        return TacticalSearch.Fingerprint(original)==TacticalSearch.Fingerprint(reordered)?setup:-1;
    }
    static int FundPlannedFocus(Adapter root,IReadOnlyList<string> path,int resource,Func<Adapter,Adapter> copy)
    {
        var own=root.Engine.State.Players[root.Actor];
        if(own.FocusedThisTurn||own.Mastery>=root.Engine.State.Rules.MasteryCap)return -1;
        var effect=Effect(root,resource,out _);var current=new SafeTurnGains.Amounts();var focused=new SafeTurnGains.Amounts();
        // Cheap filter before copying: only a currently available neutral starter
        // whose mastery tier improves after one Focus can need this repair.
        if(!SafeTurnGains.Read(effect,own.Mastery,ref current)||!SafeTurnGains.Read(effect,own.Mastery+1,ref focused)||
            focused.Gems<=current.Gems&&focused.Power<=current.Power)return -1;
        bool Apply(Adapter g,string key)
        {
            if(g.Decision!=null||g.Actor!=root.Actor||g.Engine.State.GameOver)return false;
            int a=Find(g,key);if(a<0)return false;int log=g.Engine.Log.Count;g.Step(a);
            for(int j=log;j<g.Engine.Log.Count;j++)
                if(g.Engine.Log[j] is ShardsCardDrawnEvent or ShardsCardsRevealedEvent or ShardsMonsterRevealedEvent or ShardsDeckShuffledEvent or ShardsTurnStartedEvent)return false;
            return g.Decision==null&&g.Actor==root.Actor&&!g.Engine.State.GameOver;
        }
        var template=TacticalSearch.PublicWorld(root,713101,copy);var original=copy(template);
        var resources=new List<string>();string focus=null;
        foreach(string key in path)
        {
            int a=Find(original,key);if(a<0)return -1;
            if(HybridLookahead.ResourceCard(original,a))resources.Add(key);
            else if(original.Visible(a).Action is ShardsFocusAction)focus=key;
            else return -1;
            if(!Apply(original,key))return -1;
            if(focus!=null)break;
        }
        if(focus==null||resources.Count<2)return -1;
        var before=original.Engine.State.Players[root.Actor];
        bool NormalizeResourceTail(ShardsPlayer p)
        {
            // Only the newly played neutral resource cards may permute. Their
            // effects have no draws, callbacks, factions or play-order conditions.
            // Keep every pre-existing card in place. Future uniform shuffles may
            // use another seeded permutation, but reveal no different information.
            bool Normalize(List<ShardsCard> cards,int initial)
            {
                if(cards.Count!=initial+resources.Count)return false;
                cards.Sort(initial,resources.Count,Comparer<ShardsCard>.Create((a,b)=>a.InstanceId.CompareTo(b.InstanceId)));
                return true;
            }
            return Normalize(p.PlayZone,own.PlayZone.Count)&&Normalize(p.PlayedThisTurn,own.PlayedThisTurn.Count);
        }
        if(!NormalizeResourceTail(before))return -1;
        for(int funding=1;funding<resources.Count;funding++)
        {
            int first=Find(root,resources[funding]);if(first<0||!HybridLookahead.ResourceCard(root,first))continue;
            var reordered=copy(template);
            if(!Apply(reordered,resources[funding])||!Apply(reordered,focus))continue;
            bool legal=true;
            for(int i=0;i<resources.Count;i++)if(i!=funding&&!Apply(reordered,resources[i])){legal=false;break;}
            if(!legal)continue;
            var after=reordered.Engine.State.Players[root.Actor];
            if(after.Gems<before.Gems||after.Power<before.Power||after.Gems==before.Gems&&after.Power==before.Power)continue;
            after.Gems=before.Gems;after.Power=before.Power;
            if(NormalizeResourceTail(after)&&TacticalSearch.Fingerprint(original)==TacticalSearch.Fingerprint(reordered))return first;
        }
        return -1;
    }
    internal static void Expand(Adapter root,List<HybridLookahead.Option> options,Prediction prediction,Func<Adapter,Adapter> copy)
    {
        if(root.Decision!=null||root.Actor!=root.Engine.State.TurnPlayerIndex)return;
        bool Variable(IShardsEffect e)=>e is AtMastery or BestByMastery or If or PerCount or Unify or Dominion or FactionTrigger||
            e is ShardsComposite sequence&&sequence.Parts.Any(Variable);
        var targets=options.Where(o=>o.Keys.Length==1&&!o.MenuPlan).Select(o=>o.First).Distinct().Where(a=>
            root.Visible(a).Action is ShardsBuyCardAction||Variable(Effect(root,a,out _))).ToArray();
        if(targets.Length==0)return;
        // Proposal generation sees the same public belief as search. In particular,
        // it never copies the real hidden allocation into its planning probes.
        Adapter template=null;var added=new HashSet<string>();
        var firstProbes=new Dictionary<int,(Adapter game,bool information,SafeTurnGains.Amounts gain)>();
        var visibleIds=new HashSet<int>(root.Engine.State.Players[root.Actor].Hand.Concat(root.Engine.State.Players[root.Actor].Champions)
            .Concat(root.Engine.State.Players[root.Actor].Destinies).Select(c=>c.InstanceId));
        foreach(int target in targets)
        {
            string payoff=TacticalSearch.Key(root,target);template??=TacticalSearch.PublicWorld(root,713101,copy);
            var current=template;var prefix=new List<string>();
            for(int depth=0;depth<3;depth++)
            {
                double baseline=Benefit(current,payoff);if(double.IsNegativeInfinity(baseline))break;
                Adapter best=null;string key=null;double improvement=1e-7;bool stop=false;
                Adapter continuation=null;string continuationKey=null;double continuationImprovement=1e-7;
                for(int a=0;a<current.VisibleCount;a++)
                {
                    string nextKey=TacticalSearch.Key(current,a);
                    if(nextKey==payoff||!Preparation(current,a,visibleIds))continue;
                    Adapter probe;bool information;SafeTurnGains.Amounts gain;
                    if(depth==0&&firstProbes.TryGetValue(a,out var cached))
                    {probe=cached.game;information=cached.information;gain=cached.gain;}
                    else
                    {
                        gain=LeadingGain(current,a);probe=copy(current);int log=probe.Engine.Log.Count;probe.Step(a);information=false;
                        for(int j=log;j<probe.Engine.Log.Count;j++)
                            if(probe.Engine.Log[j] is ShardsCardDrawnEvent or ShardsCardsRevealedEvent or ShardsMonsterRevealedEvent or ShardsDeckShuffledEvent or ShardsTurnStartedEvent)information=true;
                        if(depth==0)firstProbes.Add(a,(probe,information,gain));
                    }
                    if(probe.Engine.State.GameOver||probe.Actor!=root.Actor||probe.Engine.State.TurnPlayerIndex!=root.Actor)continue;
                    double score;
                    if(information||probe.Decision!=null)
                    {
                        // A mastery+draw setup may be useful, but its new sampled
                        // cards must not choose the rest of a committed root plan.
                        // Score only its statically known mastery prefix, then stop.
                        if(gain.Mastery<=0)continue;
                        score=Benefit(current,payoff,Math.Min(current.Engine.State.Rules.MasteryCap,
                            current.Engine.State.Players[root.Actor].Mastery+gain.Mastery))-baseline;
                    }
                    else score=Benefit(probe,payoff)-baseline;
                    if(!information&&probe.Decision==null&&score>continuationImprovement)
                    {continuationImprovement=score;continuation=probe;continuationKey=nextKey;}
                    if(score>improvement)
                    {improvement=score;best=probe;key=nextKey;stop=information||probe.Decision!=null;}
                }
                if(best==null)break;
                void Add(string next)
                {
                    var keys=prefix.Concat(new[]{next,payoff}).ToArray();
                    int first=Find(root,keys[0]);if(first<0)throw new InvalidOperationException("Setup prefix is not rooted in a visible action");
                    if(added.Add(string.Join("\n",keys)))options.Add(new HybridLookahead.Option{First=first,Keys=keys,SetupPlan=true,
                        // A proposal prior: policy payoff plus planner prerequisite.
                        Probability=Math.Max(prediction.P[first],prediction.P[target])});
                }
                Add(key);
                if(stop)
                {
                    // Keep the immediate draw proposal, but also explore visible
                    // preparation before drawing (e.g. +1 mastery, then +2/draw).
                    // No sampled drawn card is allowed to generate a prefix.
                    if(continuation==null)break;
                    best=continuation;key=continuationKey;Add(key);
                }
                prefix.Add(key);
                current=best;
            }
        }
    }
    static int Find(Adapter g,string key)
    {for(int a=0;a<g.VisibleCount;a++)if(TacticalSearch.Key(g,a)==key)return a;return -1;}
    static IShardsEffect Effect(Adapter g,int a,out ShardsCard card)
    {
        card=null;
        if(g.Visible(a).Action is ShardsPlayCardAction play){card=g.Engine.State.FindCard(play.CardInstanceId);return card.Def.PlayEffect;}
        if(g.Visible(a).Action is ShardsExhaustAction exhaust){card=g.Engine.State.FindCard(exhaust.CardInstanceId);return card.Def.ExhaustEffect;}
        return null;
    }
    static SafeTurnGains.Amounts LeadingGain(Adapter g,int a)
    {
        var effect=Effect(g,a,out var card);var gain=new SafeTurnGains.Amounts();
        if(g.Visible(a).Action is ShardsFocusAction){gain.Mastery=1;return gain;}
        if(effect!=null)SafeTurnGains.Read(effect,g.Engine.State.Players[g.Actor].Mastery,ref gain,true,
            new ShardsContext{Engine=g.Engine,ControllerIndex=g.Actor,Source=card});
        return gain;
    }
    static bool Preparation(Adapter g,int a,HashSet<int> ids)
    {
        var action=g.Visible(a).Action;
        if(action is ShardsFocusAction)return !SafeTurnGains.WastefulFocus(g,a);
        var effect=Effect(g,a,out var card);if(card==null||!ids.Contains(card.InstanceId))return false;
        if(action is ShardsExhaustAction&&card.Def.ExhaustGemCost!=0)return false;
        if(action is ShardsPlayCardAction&&card.Def.IsChampion)return true;
        var gain=LeadingGain(g,a);
        return gain.Mastery>0||gain.Health>0||HasModifier(effect);
    }
    static bool HasModifier(IShardsEffect effect)=>effect is Do||
        effect is ShardsComposite sequence&&sequence.Parts.Any(HasModifier)||
        effect is AtMastery at&&HasModifier(at.Inner);
    static double Benefit(Adapter g,string key,int? assumedMastery=null)
    {
        int a=Find(g,key);if(a<0)return double.NegativeInfinity;
        var p=g.Engine.State.Players[g.Actor];int mastery=assumedMastery??p.Mastery;
        if(g.Visible(a).Action is ShardsBuyCardAction buy)
        {
            var def=g.Engine.State.CenterRow[buy.SlotIndex].Def;
            if(buy.FastPlay)return 0;
            bool deployed=def.IsChampion&&(p.NextChampionsIntoPlay>0||p.NextHomodeusChampionsIntoPlay>0&&def.Faction==ShardsFaction.Homodeus);
            return (deployed?8:p.NextRecruitsToHand>0?4:0)+(p.CharacterId=="decima"&&!p.FirstBuyUsedThisTurn&&mastery>=5?ShardsEngine.DecimaFirstBuyDiscount:0);
        }
        var effect=Effect(g,a,out var card);var context=new ShardsContext{Engine=g.Engine,ControllerIndex=g.Actor,Source=card};
        double Score(IShardsEffect e)
        {
            if(e is Gain gain)return gain.Gems+gain.Power+2*gain.Mastery+.5*gain.Health+3*gain.Draw;
            if(e is ShardsComposite sequence)return sequence.Parts.Sum(Score);
            if(e is AtMastery at)return .001*Math.Min(mastery,at.Threshold)+(mastery>=at.Threshold?Score(at.Inner):0);
            if(e is BestByMastery tiers)
            {
                var active=tiers.Tiers.Where(t=>t.threshold<=mastery).OrderByDescending(t=>t.threshold).FirstOrDefault();
                return .001*mastery+Score(active.effect);
            }
            if(e is If conditional)return conditional.ControllerVisible&&conditional.ConditionMet(context)?Score(conditional.Inner):0;
            if(e is PerCount count&&count.ControllerVisible)
            {var unit=count.PerUnit;return count.VisibleCount(context)*(unit.gems+unit.power+2*unit.mastery+.5*unit.health+3*unit.draw);}
            if(e is Unify unify)return unify.ConditionMet(context)?Score(unify.Inner):0;
            if(e is Dominion dominion)return dominion.ConditionMet(context)?Score(dominion.Inner):0;
            if(e is FactionTrigger faction)return faction.ConditionMet(context)?Score(faction.Inner):0;
            if(e is WarpUpTo)return 8;
            return 0;
        }
        return Score(effect);
    }
}
}
