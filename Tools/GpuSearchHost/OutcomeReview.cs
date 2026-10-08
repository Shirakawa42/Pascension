using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Pascension.Engine.Actions;
using Shards.AI;
using Shards.Engine;

// Offline diagnostic, not a live playing policy. Compares actual terminal
// outcomes under one fixed greedy or hybrid policy after each root action.
// Root worlds are paired across alternatives; no actual hidden state is used.
internal static class OutcomeReview
{
    sealed class Trial
    {
        internal Adapter Game;
        internal int Action,World,Steps;
    }

    internal static object Run(Adapter source,Func<Adapter[],Prediction[]> infer,int worlds,int workers,int seed=913103,PolicySearchSettings hybridSettings=null,string[] requestedActions=null)
    {
        if(worlds<1||worlds>256)throw new ArgumentOutOfRangeException(nameof(worlds));
        ulong original=TacticalSearch.Fingerprint(source);int seat=source.Actor;
        var parallel=new ParallelOptions{MaxDegreeOfParallelism=workers};
        var rootPrediction=infer(new[]{source})[0];
        // All distinct legal groups, including low-prior free gains and EndTurn.
        // This is intentionally not capped to the normal four policy candidates.
        var actions=HybridLookahead.Groups(source).Select(xs=>xs[0]).ToArray();
        if(requestedActions!=null&&requestedActions.Length>0)
        {
            foreach(string name in requestedActions)if(!actions.Any(a=>ReviewRecorder.Name(source,source.Visible(a))==name))
                throw new ArgumentException("Requested outcome action is not legal: "+name);
            actions=actions.Where(a=>requestedActions.Contains(ReviewRecorder.Name(source,source.Visible(a)))).ToArray();
        }
        var names=actions.Select(a=>ReviewRecorder.Name(source,source.Visible(a))).ToArray();
        var keys=actions.Select(a=>TacticalSearch.Key(source,a)).ToArray();
        var definitions=actions.Select(a=>source.Visible(a).Action switch
        {
            ShardsRecruitRelicAction relic=>source.Engine.State.FindCard(relic.CardInstanceId)?.DefId,
            ShardsTakeDestinyAction destiny=>source.Engine.State.FindCard(destiny.CardInstanceId)?.DefId,
            _=>source.Visible(a).Option?.DefId
        }).ToArray();
        var templates=new Adapter[worlds];
        Parallel.For(0,worlds,parallel,w=>templates[w]=TacticalSearch.PublicWorld(source,seed+w*7919,FastCopy.Copy));
        var trials=new Trial[actions.Length*worlds];
        var watch=System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0,trials.Length,parallel,i=>
        {
            int option=i/worlds,world=i%worlds;var game=FastCopy.Copy(templates[world]);
            int action=Enumerable.Range(0,game.VisibleCount).Single(a=>TacticalSearch.Key(game,a)==keys[option]);
            game.Step(action);trials[i]=new Trial{Game=game,Action=option,World=world,Steps=1};
        });
        var planner=hybridSettings==null?null:new HybridLookahead(hybridSettings.ValidatedCopy(),infer,FastCopy.Copy);
        // Both seats use the same selected policy, with identical safe-gain guards.
        // The greedy mode has no search. Hybrid mode uses the normal planner.
        // Every later prediction sees only its actor's view, never root-world labels.
        for(int step=1;step<2000;step++)
        {
            var live=trials.Where(t=>!t.Game.Engine.State.GameOver).ToArray();
            if(live.Length==0)break;
            var predictions=infer(live.Select(t=>t.Game).ToArray());
            var fallbacks=new int[live.Length];
            Parallel.For(0,live.Length,parallel,i=>
            {
                var g=live[i].Game;bool omitEnd=SafeTurnGains.HasAlternative(g);
                int action=HybridLookahead.Groups(g)
                    .Where(xs=>!(omitEnd&&g.Visible(xs[0]).Action is ShardsEndTurnAction))
                    .OrderByDescending(xs=>xs.Sum(a=>(double)predictions[i].P[a])).First()[0];
                fallbacks[i]=action;
            });
            var selected=planner==null?fallbacks:planner.Choose(live.Select(t=>t.Game).ToArray(),predictions,fallbacks,live.Select(t=>true).ToArray());
            Parallel.For(0,live.Length,parallel,i=>{live[i].Game.Step(selected[i]);live[i].Steps++;});
        }
        if(TacticalSearch.Fingerprint(source)!=original)throw new Exception("Outcome review mutated source");
        var summary=Enumerable.Range(0,actions.Length).Select(option=>
        {
            var ts=trials.Where(t=>t.Action==option).ToArray();
            int wins=ts.Count(t=>t.Game.Engine.State.GameOver&&t.Game.Engine.State.WinnerIndex==seat);
            int losses=ts.Count(t=>t.Game.Engine.State.GameOver&&t.Game.Engine.State.WinnerIndex==1-seat);
            int draws=ts.Count(t=>t.Game.Engine.State.GameOver&&t.Game.Engine.State.WinnerIndex<0);
            int censored=ts.Count(t=>!t.Game.Engine.State.GameOver);
            return new{option,name=names[option],definition=definitions[option],key=keys[option],rootIndex=actions[option],prior=rootPrediction.P[actions[option]],wins,losses,draws,censored,
                score=censored==0?(double?)(wins+.5*draws)/worlds:null,
                outcomes=ts.Select(t=>new{t.World,winner=t.Game.Engine.State.GameOver?(int?)t.Game.Engine.State.WinnerIndex:null,t.Steps})};
        }).ToArray();
        return new{method=planner==null?"paired-public-world-fixed-guarded-greedy-terminal-rollout":"paired-public-world-fixed-hybrid-terminal-rollout",worlds,seed,seat,hybridSettings,requestedActions,
            search=planner?.Diagnostics,
            seconds=watch.Elapsed.TotalSeconds,transitions=trials.Sum(t=>t.Steps),options=summary,
            limitation=planner==null?"Estimates the fixed greedy continuation, not optimal play or the stronger hybrid; paired root worlds do not guarantee identical later random events.":"Estimates this fixed hybrid continuation, not optimal play; sampled worlds and downstream random events remain uncertain."};
    }
}
