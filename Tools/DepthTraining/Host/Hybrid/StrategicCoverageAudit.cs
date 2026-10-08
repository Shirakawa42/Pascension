using System;
using System.Linq;
using System.Reflection;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class StrategicCoverageAudit
    {
        internal static object Run(bool rezOnly=false)
        {
            int checkedStates=0;
            foreach(string hero in ShardsEngine.DraftableCharacters)
            foreach(int mastery in new[]{4,5,29})
            {
                ulong seed=738020;while(HeroAssignments.ForSeed(seed).Seat0!=hero)seed++;
                var game=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(game,seed);
                var player=game.Engine.State.Players[0];player.Mastery=mastery;player.Gems=3;
                typeof(ShardsEngine).GetMethod("RoutePriority",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(game.Engine,null);game.Rebuild();
                int fallback=Enumerable.Range(0,game.VisibleCount).First(a=>game.Visible(a).Action is ShardsPlayCardAction);
                var prediction=new Prediction{P=new float[64],V=0};prediction.P[fallback]=1;
                // A collapsed prior must not erase available strategic actions.
                var options=HybridLookahead.Options(game,prediction,fallback,1,guards:true,mixed:true,optionalChoices:true,pruneNoEffect:true);
                foreach(int action in Enumerable.Range(0,game.VisibleCount).Where(a=>rezOnly?
                    hero=="rez"&&game.Visible(a).Action is ShardsHeroAbilityAction:
                    game.Visible(a).Action is ShardsFocusAction or ShardsHeroAbilityAction))
                    if(!options.Any(o=>o.First==action))throw new Exception($"Search omitted {game.Visible(action).Action.GetType().Name} for {hero} M{mastery}");
                if(rezOnly&&hero!="rez"&&options.Any(o=>game.Visible(o.First).Action is ShardsFocusAction or ShardsHeroAbilityAction))
                    throw new Exception("Rez-only coverage changed another hero's collapsed-prior shortlist");
                checkedStates++;
            }
            return new{passed=true,checkedStates,heroes=5,priorProbabilityOfRequiredActions=0};
        }
    }
}
