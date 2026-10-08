// Diagnostic port of the deployed planner to the full-information adapter.
using Shards.Engine;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
// Only recognizes effect shapes with visible, nonnegative immediate gains.
// No draw, paid ability, bespoke callback, or hidden-zone condition is assumed safe.
internal static class SafeTurnGains
{
    internal struct Amounts {internal int Gems,Power,Mastery,Health,Draw;}
    internal static bool WastefulFocus(Adapter g,int action)=>g.Visible(action).Action is ShardsFocusAction&&
        g.Engine.State.Players[g.Actor].Mastery>=g.Engine.State.Rules.MasteryCap;
    internal static bool Read(IShardsEffect effect,int mastery,ref Amounts sum,bool allowDraw=false,ShardsContext context=null)
    {
        if(effect is ShardsNullEffect)return true;
        if(effect is Gain gain)
        {
            if((allowDraw?gain.Draw<0:gain.Draw!=0)||gain.Gems<0||gain.Power<0||gain.Mastery<0||gain.Health<0)return false;
            sum.Gems+=gain.Gems;sum.Power+=gain.Power;sum.Mastery+=gain.Mastery;sum.Health+=gain.Health;sum.Draw+=gain.Draw;return true;
        }
        int effectiveMastery=System.Math.Min(context?.Engine.State.Rules.MasteryCap??30,mastery+sum.Mastery);
        if(effect is AtMastery threshold)return effectiveMastery<threshold.Threshold||Read(threshold.Inner,mastery,ref sum,allowDraw,context);
        if(effect is ShardsComposite sequence)
        {foreach(var part in sequence.Parts)if(!Read(part,mastery,ref sum,allowDraw,context))return false;return true;}
        if(effect is Unify unify)
        {
            var optional=sum;if(!Read(unify.Inner,mastery,ref optional,allowDraw))return false;
            // Unify reads only the controller's hand and public played cards.
            if(context!=null&&unify.ConditionMet(context))return Read(unify.Inner,mastery,ref sum,allowDraw,context);
            return true;
        }
        // Proposal/rollout preference only: Dominion can be completed by revealing
        // cards from the controller's known hand. This is achievable, not a
        // guaranteed gain; the real rollout must resolve the optional reveal.
        // The mandatory free-gain safeguard (allowDraw=false) stays conservative.
        if(effect is Dominion dominion)
        {
            if(allowDraw)return context==null||!dominion.ConditionMet(context)||Read(dominion.Inner,mastery,ref sum,true,context);
            // A nonnegative, draw-free Dominion bonus cannot invalidate the
            // preceding unconditional gain. Hand reveals can be declined;
            // if already satisfied, only the certified bonus happens. Do not
            // claim the optional bonus as a guaranteed gain or probe a reveal.
            var optional=sum;return Read(dominion.Inner,mastery,ref optional,false);
        }
        // Declining a standard WarpUpTo costs nothing and changes no cards.
        // This certifies an earlier guaranteed gain, never a mandatory warp target.
        if(effect is WarpUpTo)return true;
        // ReturnFromDiscard only moves publicly known own cards into hand; it
        // charges nothing and does not play them. Preserve an earlier guaranteed
        // gain without invoking its opaque definition filter or valuing the return.
        if(effect is ReturnFromDiscard)return true;
        if(effect is Do modifier&&modifier.OnlySetsRecruitRouting)return true;
        // A nonnegative optional bonus cannot invalidate an unconditional gain.
        // Do not execute opaque predicates/counters or claim their bonus is met.
        if(effect is If conditional)
        {
            var optional=sum;if(!Read(conditional.Inner,mastery,ref optional,allowDraw))return false;
            if(context!=null&&conditional.ControllerVisible&&conditional.ConditionMet(context))
                return Read(conditional.Inner,mastery,ref sum,allowDraw,context);
            return true;
        }
        if(effect is PerCount perCount)
        {
            var unit=perCount.PerUnit;
            if(unit.gems<0||unit.power<0||unit.mastery<0||unit.health<0||(allowDraw?unit.draw<0:unit.draw!=0))return false;
            // A positive visible count guarantees at least one unit. This is a
            // conservative gain bound, not a replacement for engine resolution.
            if(context!=null&&perCount.ControllerVisible&&perCount.ConditionMet(context))
            {sum.Gems+=unit.gems;sum.Power+=unit.power;sum.Mastery+=unit.mastery;sum.Health+=unit.health;sum.Draw+=unit.draw;}
            return true;
        }
        if(effect is BestByMastery tiers)
        {
            IShardsEffect active=null;int bestThreshold=int.MinValue;
            foreach(var tier in tiers.Tiers)if(effectiveMastery>=tier.threshold&&tier.threshold>=bestThreshold){bestThreshold=tier.threshold;active=tier.effect;}
            return active==null||Read(active,mastery,ref sum,allowDraw,context);
        }
        return false;
    }
    internal static bool Useful(Amounts gain,ShardsPlayer player,int maxHealth)=>
        gain.Gems>0||gain.Power>0||(gain.Mastery>0&&player.Mastery<30)||
        (gain.Health>0&&(player.Health<maxHealth||player.HealthToPowerThisTurn||player.OverflowHealthToPowerThisTurn));
    internal static bool HasAlternative(Adapter g)
    {
        if(g.Decision!=null||g.Actor!=g.Engine.State.TurnPlayerIndex)return false;
        var player=g.Engine.State.Players[g.Actor];
        var context=new ShardsContext{Engine=g.Engine,ControllerIndex=g.Actor};
        for(int a=0;a<g.VisibleCount;a++)
        {
            var candidate=g.Visible(a);
            if(candidate.Action is ShardsPlayCardAction play)
            {
                var card=g.Engine.State.FindCard(play.CardInstanceId);
                if(card.Def.IsChampion)continue;
                context.Source=card;
                var gain=new Amounts();
                if(Read(card.Def.PlayEffect,player.Mastery,ref gain,context:context)&&Useful(gain,player,g.Engine.State.Rules.MaxHealth))return true;
            }
            if(candidate.Action is ShardsHeroAbilityAction&&player.CharacterId=="volos")
            {
                var cost=ShardsEngine.HeroAbilityInfo(player.CharacterId);var gain=new Amounts();
                if(cost.Gems==0&&cost.Health==0&&Read(VolosAbilityChoice.Effect(0),player.Mastery,ref gain)&&
                    Useful(gain,player,g.Engine.State.Rules.MaxHealth))return true;
            }
            if(candidate.Action is ShardsExhaustAction exhaust)
            {
                var card=g.Engine.State.FindCard(exhaust.CardInstanceId);var gain=new Amounts();
                context.Source=card;
                if(card!=null&&card.Def.ExhaustGemCost==0&&Read(card.Def.ExhaustEffect,player.Mastery,ref gain,context:context)&&Useful(gain,player,g.Engine.State.Rules.MaxHealth))return true;
            }
        }
        return false;
    }
    // These are alternative rollout preferences, never mandatory live actions.
    // Each fixed style is averaged over the same worlds before it can win a root
    // comparison; opponent response choices retain the ordinary policy.
    internal static int Preferred(Adapter g,int style)
    {
        if(style==0||g.Decision!=null||g.Actor!=g.Engine.State.TurnPlayerIndex)return -1;
        var p=g.Engine.State.Players[g.Actor];int best=-1,maximum=0;
        var context=new ShardsContext{Engine=g.Engine,ControllerIndex=g.Actor};
        for(int a=0;a<g.VisibleCount;a++)
        {
            var c=g.Visible(a);IShardsEffect effect=null;int score=0;
            if(c.Action is ShardsPlayCardAction play){context.Source=g.Engine.State.FindCard(play.CardInstanceId);effect=context.Source?.Def.PlayEffect;}
            if(c.Action is ShardsExhaustAction exhaust)
            {var card=g.Engine.State.FindCard(exhaust.CardInstanceId);context.Source=card;if(card!=null&&card.Def.ExhaustGemCost==0)effect=card.Def.ExhaustEffect;}
            var gain=new Amounts();
            if(effect!=null)
            {
                // Preferences are hypotheses evaluated by the real rollout, not
                // mandatory safe-action guards. Retain known leading gains even
                // when a later effect needs a custom flow (e.g. extra turn or
                // banish). Otherwise the draw/mastery style ignores precisely
                // the setup cards it is meant to explore. Read never runs opaque
                // callbacks; HasAlternative still requires a fully safe effect.
                Read(effect,p.Mastery,ref gain,style==2,context);
                if(gain.Gems>0)score=200+gain.Gems;
                if(style==2&&gain.Draw>0)score=400+gain.Draw;
                if(style==2&&gain.Mastery>0&&p.Mastery<30)score=500+gain.Mastery;
            }
            if(style==2&&c.Action is ShardsFocusAction&&p.Mastery<30)
                score=p.Mastery==4||p.Mastery==9||p.Mastery==14||p.Mastery==19||p.Mastery==29?600:100;
            if(style==2&&c.Action is ShardsHeroAbilityAction)
            {
                if(p.CharacterId=="tetra")score=350;
                if(p.CharacterId=="rez"&&p.Hand.Exists(card=>card.DefId=="longshot"))score=700;
            }
            if(score>maximum){maximum=score;best=a;}
        }
        return best;
    }
}
}
