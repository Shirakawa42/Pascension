using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Serialization;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

namespace Shards.Nyou
{
    /// <summary>Human-facing host bridge for the frozen full-information search controller.</summary>
    public sealed class PolicyEngine : IPlayablePolicyEngine
    {
        private readonly ShardsEngineAdapter inner;
        private readonly Adapter adapter;
        private readonly FrozenPolicy policy;
        private readonly Random sampling;
        private readonly HybridLookahead search;
        private readonly int workers;
        private Task<int> pending;
        private Adapter planning;
        private long steps, submissions;
        public PolicyEngine(ShardsConfig config,FrozenPolicy policy,int seed,PolicySearchSettings settings)
        {
            this.policy=policy;sampling=new Random(seed);var profile=settings.ValidatedCopy();workers=profile.Workers;
            inner=new ShardsEngineAdapter(config);adapter=new Adapter(inner.Inner);
            search=new HybridLookahead(profile,Infer,DepthCopy.Copy);
        }
        public void BindSubmit(Action<PlayerAction> submit)=>adapter.SubmitThroughHost=submit;
        private Prediction[] Infer(Adapter[] positions)
        {
            var results=new Prediction[positions.Length];
            Parallel.For(0,positions.Length,new ParallelOptions{MaxDegreeOfParallelism=workers},i=>
            {
                var obs=new float[Encoder.ObsDim];var candidates=new float[64*48];var mask=new float[64];
                Encoder.Encode(positions[i],obs,candidates,mask);
                var logits=policy.Evaluate(obs,candidates,mask,out float value);
                results[i]=new Prediction{P=FrozenPolicy.Probabilities(logits),V=value};
            });return results;
        }
        private int Sample(Adapter position,Prediction prediction)
        {
            double total=0;for(int i=0;i<position.VisibleCount;i++)if(position.Visible(i).Kind!=11)total+=prediction.P[i];
            double draw=sampling.NextDouble()*total;int last=-1;
            for(int i=0;i<position.VisibleCount;i++)if(position.Visible(i).Kind!=11){last=i;draw-=prediction.P[i];if(draw<=0)return i;}
            if(last<0)throw new InvalidOperationException("No non-concession action");return last;
        }
        public void PreparePolicy()
        {
            if(pending!=null||GameOver||adapter.VisibleCount<=1||adapter.Decision?.Context=="soi.herodraft")return;
            var snapshot=DepthCopy.Copy(adapter);
            TacticalSearch.TransferPlan(planning,snapshot);search.TransferPlan(planning,snapshot);planning=snapshot;
            steps=adapter.WrapperSteps;submissions=adapter.Submissions;
            // The snapshot carries the observer's public memory. Search replaces
            // hidden order with sampled public worlds before simulating branches.
            pending=Task.Run(()=>
            {
                var roots=new[]{snapshot};var predictions=Infer(roots);int fallback=Sample(snapshot,predictions[0]);
                return search.Choose(roots,predictions,new[]{fallback},new[]{true})[0];
            });
        }
        public bool TryStepPolicy()
        {
            if(pending!=null)
            {
                if(!pending.IsCompleted)return false;
                var result=pending;pending=null;int action=result.GetAwaiter().GetResult();
                if(GameOver||steps!=adapter.WrapperSteps||submissions!=adapter.Submissions){planning=null;return false;}
                adapter.Step(action);return true;
            }
            if(GameOver)return false;
            if(adapter.Decision?.Context=="soi.herodraft")
            {
                // The training/statistics profile assigns heroes uniformly. Do
                // not reuse the old controller's matchup preferences for Nyou.
                var options=new List<int>();for(int i=0;i<adapter.VisibleCount;i++)if(adapter.Visible(i).Kind==12)options.Add(i);
                adapter.Step(options[sampling.Next(options.Count)]);return true;
            }
            if(adapter.VisibleCount==1){adapter.Step(0);return true;}
            PreparePolicy();return false;
        }
        public SubmitResult Submit(PlayerAction action)=>adapter.ApplyExternal(action);
        public PendingSnap PendingInput=>inner.PendingInput;
        public List<GameEvent> FilterEventsFor(int playerIndex,int sinceSeq)=>inner.FilterEventsFor(playerIndex,sinceSeq);
        public int EventCount=>inner.EventCount;
        public SnapshotBase BuildSnapshot(int playerIndex)=>inner.BuildSnapshot(playerIndex);
        public bool GameOver=>inner.GameOver;
        public int WinnerIndex=>inner.WinnerIndex;
        public PlayerAction DefaultActionFor(PendingSnap input)=>inner.DefaultActionFor(input);
    }
}
