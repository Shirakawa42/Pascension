using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using Shards.AI;
using Shards.Engine;

internal static class SharedRolloutAudit
{
    internal static Prediction[] Native(FrozenPolicy policy,Adapter[] games)
    {
        var result=new Prediction[games.Length];
        Parallel.For(0,games.Length,new ParallelOptions{MaxDegreeOfParallelism=8},i=>
        {
            var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(games[i],o,c,m);
            var p=policy.Probabilities(o,c,m,out float value);result[i]=new Prediction{P=p,V=value};
        });
        return result;
    }
    internal static void Run(string output,Func<Adapter[],Prediction[]> actualInference=null)
    {
        Shards.Content.ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var fixtures=HeroTactics.Cases().Select(test=>(id:test.Id,game:test.Factory(95001))).ToList();
        for(int seat=0;seat<2;seat++)
        {
            var game=RezAudit.Game(seat:seat);RezAudit.Add(game,"longshot",seat);
            fixtures.Add(($"future-scry-seat-{seat}",game));
        }
        Prediction[] Synthetic(Adapter[] games)=>games.Select(g=>
        {
            var p=new float[64];
            for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsPlayCardAction?4:g.Visible(a).Action is ShardsHeroAbilityAction?3:g.Visible(a).Action is ShardsFocusAction?2:1;
            float sum=p.Sum();for(int a=0;a<p.Length;a++)p[a]/=sum;
            var own=g.Engine.State.Players[g.Actor];var enemy=g.Engine.State.Players[1-g.Actor];
            return new Prediction{P=p,V=(float)Math.Tanh((own.Mastery-enemy.Mastery)/30.0+(own.Health-enemy.Health)/50.0+own.Power/80.0)};
        }).ToArray();
        Func<Adapter[],Prediction[]> Infer=actualInference??Synthetic;
        var rows=new List<object>();long transitions=0,inferenceRows=0,referenceSteps=0;double maximumValueError=0;
        foreach(var fixture in fixtures)foreach(bool future in new[]{false,true})
        {
            var root=fixture.game;ulong before=TacticalSearch.Fingerprint(root);
            var settings=new PolicySearchSettings{Candidates=4,Depth=24,Worlds=2,Workers=8,TerminalNodes=0,RolloutStyles=4,
                TacticalGuards=true,MixedResourcePlans=true,MenuPlans=true,SetupPlans=true,OptionalChoices=true,ScryPlans=true,
                FutureScryPlans=future,FutureScryDepth=1,PruneNoEffectPlans=true,SequenceRepairs=true,SimplifyWins=true,EndTurnExtension=64};
            var ordinary=new HybridLookahead(settings,Infer,FastCopy.Copy){CaptureLeaves=true};
            var optimizedSettings=settings.ValidatedCopy();optimizedSettings.ShareRolloutStates=true;
            var optimized=new HybridLookahead(optimizedSettings,Infer,FastCopy.Copy){CaptureLeaves=true};
            var prediction=Infer(new[]{root});int fallback=Array.IndexOf(prediction[0].P,prediction[0].P.Max());
            int expected=ordinary.Choose(new[]{root},prediction,new[]{fallback},new[]{true})[0];
            int actual=optimized.Choose(new[]{root},prediction,new[]{fallback},new[]{true})[0];
            if(expected!=actual)throw new Exception($"Shared rollout changed action: {fixture.id}, future={future}");
            if((ordinary.DebugLeaves==null)!=(optimized.DebugLeaves==null))throw new Exception("Mismatched absent leaves");
            if(ordinary.DebugLeaves!=null)
            {
                var left=JArray.FromObject(ordinary.DebugLeaves);var right=JArray.FromObject(optimized.DebugLeaves);
                if(left.Count!=right.Count)throw new Exception("Shared rollout changed leaf count");
                for(int i=0;i<left.Count;i++)
                {
                    double error=Math.Abs((double)left[i]["Value"]-(double)right[i]["Value"]);maximumValueError=Math.Max(maximumValueError,error);
                    ((JObject)left[i]).Remove("Value");((JObject)right[i]).Remove("Value");
                    if(error>(actualInference==null?0:1e-5)||!JToken.DeepEquals(left[i],right[i]))
                        throw new Exception($"Shared rollout changed path, horizon, value or state: {fixture.id}, future={future}");
                }
            }
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Shared rollout mutated live source");
            referenceSteps+=ordinary.Steps;
            transitions+=optimized.SharedTransitionsSaved;inferenceRows+=optimized.SharedInferenceRowsSaved;
            rows.Add(new{fixture.id,future,passed=true,optimized.SharedTransitionsSaved,optimized.SharedInferenceRowsSaved});
        }
        if(transitions<=0||inferenceRows<=0)throw new Exception("Audit did not exercise shared transitions and inference");
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,positions=rows.Count,referenceSteps,maximumValueError,realModel=actualInference!=null,transitionsSaved=transitions,inferenceRowsSaved=inferenceRows,rows},Formatting.Indented));
    }
}
