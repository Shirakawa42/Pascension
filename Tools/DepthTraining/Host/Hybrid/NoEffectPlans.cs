// Diagnostic port of the deployed planner to the full-information adapter.
using Shards.Engine;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
// A narrow, public-information proof of an empty activation. Never infer
// emptiness from the learned value, a sampled draw, or an opaque callback.
internal static class NoEffectPlans
{
    internal static bool InactiveDestiny(Adapter g,int action)
    {
        if(g.Decision!=null||g.Actor!=g.Engine.State.TurnPlayerIndex||
            !(g.Visible(action).Action is ShardsExhaustAction exhaust))return false;
        var player=g.Engine.State.Players[g.Actor];
        var card=player.Destinies.Find(c=>c.InstanceId==exhaust.CardInstanceId);
        // Champion exhaustion can enable a later reset/return choice. Do not
        // discard those actions using a destiny-only argument.
        if(card==null||card.Def.ExhaustGemCost!=0)return false;
        return Empty(card.Def.ExhaustEffect,new ShardsContext{Engine=g.Engine,ControllerIndex=g.Actor,Source=card});
    }
    internal static bool Empty(IShardsEffect effect,ShardsContext context)
    {
        if(effect is ShardsNullEffect)return true;
        if(effect is Gain gain)return gain.Gems==0&&gain.Power==0&&gain.Mastery==0&&gain.Health==0&&gain.Draw==0;
        if(effect is AtMastery threshold)return context.Controller.Mastery<threshold.Threshold||Empty(threshold.Inner,context);
        if(effect is ShardsComposite sequence)
        {foreach(var part in sequence.Parts)if(!Empty(part,context))return false;return true;}
        // Ordinary public callbacks may inspect post-exhaust state. Only an
        // explicitly reviewed, exhaustion-independent predicate can certify
        // inactivity here. Never evaluate an opaque or merely Visible callback.
        if(effect is If conditional&&conditional.ControllerVisible&&conditional.StableOnExhaust)
            return !conditional.ConditionMet(context)||Empty(conditional.Inner,context);
        if(effect is BestByMastery tiers)
        {
            IShardsEffect active=null;int bestThreshold=int.MinValue;
            foreach(var tier in tiers.Tiers)if(context.Controller.Mastery>=tier.threshold&&tier.threshold>=bestThreshold)
            {bestThreshold=tier.threshold;active=tier.effect;}
            return active==null||Empty(active,context);
        }
        return false;
    }
}
}
