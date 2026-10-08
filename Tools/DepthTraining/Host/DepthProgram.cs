using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class DepthProgram
    {
        [DllImport("libc",SetLastError=true)] static extern int prctl(int option,ulong a2,ulong a3,ulong a4,ulong a5);
        [DllImport("libc")] static extern int getppid();
        static BinaryReader input;static BinaryWriter output;static LaneWorkers team;
        static int depth,width,worlds;static long nodes,overrides,decisions;
        static double prior,priorCap,margin;
        static bool greedyRoot,greedyRollout,masteryFinish,policyOnly;
        static HybridLookahead hybrid;
        static long certifiedFinishes;
        static readonly bool PooledInference=Environment.GetEnvironmentVariable("SHARDS_DEPTH_POOLED_INFERENCE")=="1";
        static double encodeSeconds,writeSeconds,waitSeconds;static long inferenceRows;
        static SharedInferenceInputs sharedInputs;
        sealed class Branch {internal Adapter Game;internal int Root,Action,Seat,Turn,World;internal bool Ended;}
        static void Main(string[] args)
        {
            string owner=Environment.GetEnvironmentVariable("SHARDS_DEPTH_OWNER_PID");
            if(owner!=null&&OperatingSystem.IsLinux())
            {if(prctl(1,9,0,0,0)!=0||getppid()!=int.Parse(owner))throw new InvalidOperationException("Search owner vanished");}
            ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
            if(args[0]=="catalog"){Program.Print(Encoder.Catalog());return;}
            if(args[0]=="statistics-catalog"){Program.Print(StatisticsCatalog.Export());return;}
            if(args[0]=="selftest"){SelfTest();return;}
            if(args[0]=="submit-isolation-selftest"){Program.Print(SubmitIsolationAudit.Run());return;}
            if(args[0]=="mastery-finish-selftest"){Program.Print(MasteryFinish.SelfTest());return;}
            if(args[0]=="hybrid-selftest"){Program.Print(HybridAudit.Run());return;}
            if(args[0]=="copy-bench"){Program.Print(CopyBenchmark.Run());return;}
            if(args[0]=="strategic-coverage-selftest"){Program.Print(StrategicCoverageAudit.Run());return;}
            if(args[0]=="rez-coverage-selftest"){Program.Print(StrategicCoverageAudit.Run(true));return;}
            if(args[0]=="future-scry-selftest"){Program.Print(HybridLookahead.AuditFutureScry());return;}
            if(args[0]=="market-plan-selftest"){Program.Print(MarketPlanAudit.Run());return;}
            if(args[0]=="inference-buffer-audit"){Program.Print(InferenceRows.Audit());return;}
            if(args[0]=="reply-horizon-selftest"){Program.Print(HybridLookahead.AuditReplyHorizon());return;}
            if(args[0]=="visibility-selftest"){Program.Print(new{passed=true,checks=VisibilitySelfTest.Run()});return;}
            if(args[0]=="action-selftest"){Program.Print(ActionSelfTest.Run());return;}
            if(args[0]=="zone-selftest"){Program.Print(new{passed=true,checks=ZoneVisibilitySelfTest.Run()});return;}
            int batch=int.Parse(args[1]);depth=int.Parse(args[2]);width=int.Parse(args[3]);worlds=int.Parse(args[4]);
            int workers=int.Parse(Environment.GetEnvironmentVariable("SHARDS_DEPTH_WORKERS")??"6");
            if(workers<1||workers>6)throw new ArgumentException("Worker budget must be in1..6");
            if(batch<1||batch>64||depth<1||depth>64||width<1||width>64||worlds<1||worlds>16)throw new ArgumentException("Unsafe shape");
            string incumbentPath=Environment.GetEnvironmentVariable("SHARDS_DEPTH_INCUMBENT");
            bool selfplay=Environment.GetEnvironmentVariable("SHARDS_DEPTH_SELFPLAY")=="1";
            string statisticsDirectory=Environment.GetEnvironmentVariable("SHARDS_DEPTH_STATISTICS");
            if(statisticsDirectory!=null&&!selfplay)throw new ArgumentException("Statistics require identical new AI on both seats");
            Shards.Preflight.TrainingStatistics statistics=null;
            string heroFilterText=Environment.GetEnvironmentVariable("SHARDS_DEPTH_LEARNER_HERO_FILTER");
            var heroFilter=string.IsNullOrEmpty(heroFilterText)?null:new HashSet<string>(heroFilterText.Split(','),StringComparer.Ordinal);
            if(heroFilter!=null&&(incumbentPath==null||heroFilter.Any(h=>!ShardsEngine.DraftableCharacters.Contains(h))))
                throw new ArgumentException("Hero routing requires evaluation and recognized public hero identities");
            double Setting(string name,string fallback)=>double.Parse(Environment.GetEnvironmentVariable(name)??fallback,System.Globalization.CultureInfo.InvariantCulture);
            prior=Setting("SHARDS_DEPTH_PRIOR",".015");priorCap=Setting("SHARDS_DEPTH_PRIOR_CAP","6");margin=Setting("SHARDS_DEPTH_MARGIN",".02");
            greedyRoot=Environment.GetEnvironmentVariable("SHARDS_DEPTH_GREEDY_ROOT")=="1";
            greedyRollout=Environment.GetEnvironmentVariable("SHARDS_DEPTH_GREEDY_ROLLOUT")=="1";
            masteryFinish=Environment.GetEnvironmentVariable("SHARDS_DEPTH_MASTERY_FINISH")=="1";
            policyOnly=Environment.GetEnvironmentVariable("SHARDS_DEPTH_POLICY_ONLY")=="1";
            if(policyOnly&&(greedyRoot||greedyRollout||masteryFinish||Environment.GetEnvironmentVariable("SHARDS_DEPTH_HYBRID")=="1"))
                throw new ArgumentException("On-policy collection cannot override sampled actions");
            if(!double.IsFinite(prior)||prior<0||prior>1||!double.IsFinite(priorCap)||priorCap<0||priorCap>30||!double.IsFinite(margin)||margin<0||margin>1)throw new ArgumentException("Invalid search regularization");
            var incumbent=incumbentPath==null?null:CurrentOpponent.LoadBundle(incumbentPath);
            if(Environment.GetEnvironmentVariable("SHARDS_DEPTH_HYBRID")=="1")
            {
                if(incumbent==null)throw new InvalidOperationException("Hybrid evaluation requires a pinned incumbent settings baseline");
                var config=incumbent.Settings.ValidatedCopy();
                config.Workers=workers;config.Depth=depth;config.Candidates=width;config.Worlds=worlds;
                config.Prior=prior;config.ActionPriorCap=priorCap;config.Margin=margin;
                config.HorizonTurns=int.Parse(Environment.GetEnvironmentVariable("SHARDS_DEPTH_HORIZON_TURNS")??"1");
                config.RolloutStyles=int.Parse(Environment.GetEnvironmentVariable("SHARDS_DEPTH_ROLLOUT_STYLES")??"4");
                hybrid=new HybridLookahead(config.ValidatedCopy(),g=>ToPredictions(Infer(g,2)),DepthCopy.Copy);
            }
            string sharedPath=Environment.GetEnvironmentVariable("SHARDS_DEPTH_SHARED_INPUT_PATH");
            try
            {
            using(sharedInputs=sharedPath==null?null:new SharedInferenceInputs(sharedPath))
            using(team=new LaneWorkers(workers))using(input=new BinaryReader(Console.OpenStandardInput()))using(output=new BinaryWriter(Console.OpenStandardOutput()))
            while(true)
            {
                ulong seed;try{seed=input.ReadUInt64();}catch(EndOfStreamException){return;}
                bool evaluation=!selfplay&&(incumbent!=null||Environment.GetEnvironmentVariable("SHARDS_DEPTH_EVAL")=="1");
                if(statisticsDirectory!=null && statistics==null)statistics=new Shards.Preflight.TrainingStatistics(statisticsDirectory,"final_evaluation",seed,batch,new{mode="balanced-random-distinct",search="new-full-information-ai-both-seats"},1);
                statistics?.Reset(batch,seed);
                var games=new Adapter[batch];var opponents=new CurrentOpponent[batch];var gameSeeds=new ulong[batch];
                var excluded=new bool[batch];
                long opponentTicks=0;double selectionSeconds=0;
                var policyRng=new Pascension.Engine.Core.DeterministicRng[batch,2];
                void Advance(int lane)
                {
                    if(excluded[lane])return;
                    var g=games[lane];var old=opponents[lane];
                    long start=System.Diagnostics.Stopwatch.GetTimestamp();
                    if(old!=null)while(!g.Engine.State.GameOver&&!g.Truncated&&!old.Truncated&&g.Actor!=lane%2)old.Step();
                    System.Threading.Interlocked.Add(ref opponentTicks,System.Diagnostics.Stopwatch.GetTimestamp()-start);
                    g.Rebuild();
                }
                team.Run(batch,i=>
                {
                    ulong gameSeed=seed+(ulong)(evaluation?i/2:i);gameSeeds[i]=gameSeed;
                    for(int seat=0;seat<2;seat++)policyRng[i,seat]=new Pascension.Engine.Core.DeterministicRng(gameSeed^(ulong)(seat+1)*0x51ed270bUL);
                    if(incumbent==null||selfplay)games[i]=new Adapter(new ShardsEngine(Program.Config(gameSeed)),automaticSingletons:!selfplay);
                    else
                    {
                        var old=new CurrentOpponent(Program.Config(gameSeed),incumbent,unchecked((int)(gameSeed^0x51ed270b)));opponents[i]=old;
                        if(Environment.GetEnvironmentVariable("SHARDS_DEPTH_GPU_INCUMBENT")=="1")OpponentGpuBridge.Attach(old,incumbent,input,output);
                        var g=new Adapter(old.Engine,old.Submit,automaticSingletons:false);games[i]=g;old.BindSubmit(a=>g.ApplyExternal(a));
                    }
                    HeroAssignments.Apply(games[i],gameSeed);
                    excluded[i]=heroFilter!=null&&!heroFilter.Contains(games[i].Engine.State.Players[i%2].CharacterId);
                    Advance(i);
                });
                nodes=overrides=decisions=certifiedFinishes=0;int step=0;
                while(Enumerable.Range(0,batch).Any(i=>!excluded[i]&&!games[i].Engine.State.GameOver&&!games[i].Truncated))
                {
                    var lanes=Enumerable.Range(0,batch).Where(i=>!excluded[i]&&!games[i].Engine.State.GameOver&&!games[i].Truncated).ToArray();
                    var roots=lanes.Select(i=>games[i]).ToArray();var prediction=Infer(roots,1,lanes);
                    var certified=Enumerable.Repeat(-1,roots.Length).ToArray();
                    if(masteryFinish)team.Run(roots.Length,i=>certified[i]=MasteryFinish.Choose(roots[i]));
                    var selected=Enumerable.Range(0,roots.Length).Where(i=>!policyOnly && (!evaluation||roots[i].Actor==lanes[i]%2) &&
                        certified[i]<0 &&
                        Enumerable.Range(0,roots[i].VisibleCount).Count(a=>roots[i].Visible(a).Kind!=11)>1).ToArray();
                    ++step;
                    // PPO must sample exactly the distribution whose likelihood
                    // is retained. Unlike evaluation search, include every legal
                    // action (including concession) without renormalizing it away.
                    var fallback=Enumerable.Range(0,roots.Length).Select(i=>policyOnly?
                        Sample(prediction.p[i],roots[i].VisibleCount,policyRng[lanes[i],roots[i].Actor]):
                        greedyRoot?BestLegal(roots[i],prediction.p[i]):SampleLegal(roots[i],prediction.p[i],policyRng[lanes[i],roots[i].Actor])).ToArray();var actions=(int[])fallback.Clone();
                    if(selected.Length>0)
                    {
                        var selectionWatch=System.Diagnostics.Stopwatch.StartNew();
                        var selectedRoots=selected.Select(i=>roots[i]).ToArray();
                        var selectedPrediction=(selected.Select(i=>prediction.p[i]).ToArray(),selected.Select(i=>prediction.v[i]).ToArray());
                        var selectedFallback=selected.Select(i=>fallback[i]).ToArray();
                        var searched=hybrid!=null?
                            hybrid.Choose(selectedRoots,ToPredictions(selectedPrediction),selectedFallback,selected.Select(_=>true).ToArray()):
                            Search(selectedRoots,selectedPrediction,selected.Select(i=>gameSeeds[lanes[i]]^(ulong)roots[i].WrapperSteps*0x9e3779b97f4a7c15UL).ToArray(),selectedFallback);
                        for(int j=0;j<selected.Length;j++)actions[selected[j]]=searched[j];
                        selectionSeconds+=selectionWatch.Elapsed.TotalSeconds;
                    }
                    for(int i=0;i<actions.Length;i++)if(certified[i]>=0){actions[i]=certified[i];certifiedFinishes++;}
                    output.Write(3);output.Write(actions.Length);for(int i=0;i<actions.Length;i++){output.Write(actions[i]);output.Write(actions[i]!=fallback[i]?1:0);}output.Flush();
                    team.Run(lanes.Length,i=>
                    {
                        int lane=lanes[i];var game=games[lane];
                        var mark=statistics?.BeginStep(lane,game,actions[i]);
                        game.Step(actions[i]);Advance(lane);
                        if(mark.HasValue)statistics.ObserveStep(lane,game.Engine,mark.Value);
                        if(statistics!=null&&(game.Engine.State.GameOver||game.Truncated))
                            statistics.Finish(lane,game.Engine.State,game.Engine.State.GameOver,gameSeeds[lane]);
                    });
                    statistics?.BatchBoundary();
                }
                string report=JsonSerializer.Serialize(new{games=games.Select((g,i)=>new{lane=i,seed=gameSeeds[i],winner=g.Engine.State.WinnerIndex,completed=g.Engine.State.GameOver,rounds=g.Engine.State.Round,heroes=g.Engine.State.Players.Select(p=>p.CharacterId).ToArray(),mastery=g.Engine.State.Players.Select(p=>p.Mastery).ToArray(),victoryCause=VictoryCause(g)}).Where(g=>!excluded[g.lane]),nodes,overrides,decisions,certifiedFinishes,hybrid=hybrid?.Diagnostics,
                    selectionSeconds,opponentWorkerSeconds=opponentTicks/(double)System.Diagnostics.Stopwatch.Frequency,incumbentGpu=OpponentGpuBridge.Diagnostics,
                    learnerInferenceTransport=new{encodeSeconds,writeSeconds,waitSeconds,inferenceRows},
                    hybridCumulativePerformance=hybrid==null?null:new{hybrid.Branches,hybrid.Steps,hybrid.CloneSeconds,hybrid.RolloutSeconds,hybrid.TerminalSeconds,hybrid.Decisions,hybrid.Overrides}});
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(report);output.Write(4);output.Write(bytes.Length);output.Write(bytes);output.Flush();
            }
            }
            finally {statistics?.Dispose();}
        }
        static string VictoryCause(Adapter game)
        {
            if(!game.Engine.State.GameOver)return "censored";
            var evidence=new Shards.Preflight.VictoryEvidence();
            // Read only real-game events after completion. Search-copy logs are
            // intentionally empty and never enter this attribution.
            for(int i=0;i<game.Engine.Log.Count;i++)evidence.Observe(game.Engine.Log[i]);
            return evidence.Result(game.Engine.State.WinnerIndex);
        }
        // Input and output are framed; stdout is reserved exclusively for this protocol.
        static (float[][] p,float[] v) Infer(Adapter[] games,int kind,int[] lanes=null)
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            bool shared=sharedInputs!=null&&games.Length<=sharedInputs.Capacity;
            using var batch=shared?null:new InferenceRows(games,team,PooledInference);
            if(shared)sharedInputs.Encode(games,team);
            encodeSeconds+=watch.Elapsed.TotalSeconds;inferenceRows+=games.Length;watch.Restart();
            output.Write(shared?kind+5:kind);output.Write(games.Length);
            if(kind==1)for(int i=0;i<games.Length;i++){output.Write(lanes[i]);output.Write(games[i].Actor);}
            if(!shared)foreach(var row in batch.Rows)output.Write(MemoryMarshal.AsBytes(row.AsSpan(0,InferenceRows.Width)));
            output.Flush();
            writeSeconds+=watch.Elapsed.TotalSeconds;watch.Restart();
            var p=new float[games.Length][];var v=new float[games.Length];
            for(int i=0;i<games.Length;i++){p[i]=new float[64];for(int j=0;j<64;j++)p[i][j]=input.ReadSingle();v[i]=input.ReadSingle();}
            waitSeconds+=watch.Elapsed.TotalSeconds;
            return(p,v);
        }
        static int Best(float[] p,int count)=>Enumerable.Range(0,count).OrderByDescending(i=>p[i]).First();
        static Prediction[] ToPredictions((float[][] p,float[] v) prediction)=>prediction.p.Select((logits,i)=>
        {
            double maximum=logits.Max();var mass=logits.Select(x=>Math.Exp(x-maximum)).ToArray();double total=mass.Sum();
            return new Prediction{P=mass.Select(x=>(float)(x/total)).ToArray(),V=prediction.v[i]};
        }).ToArray();
        static int BestLegal(Adapter game,float[] logits)=>Enumerable.Range(0,game.VisibleCount)
            .Where(i=>game.Visible(i).Kind!=11).OrderByDescending(i=>logits[i]).First();
        static int Sample(float[] logits,int count,Pascension.Engine.Core.DeterministicRng rng)
        {
            double maximum=logits.Take(count).Max();var mass=logits.Take(count).Select(x=>Math.Exp(x-maximum)).ToArray();
            double target=(rng.NextUInt()+.5)/4294967296.0*mass.Sum();for(int i=0;i<count;i++){target-=mass[i];if(target<=0)return i;}return count-1;
        }
        static int SampleLegal(Adapter game,float[] logits,Pascension.Engine.Core.DeterministicRng rng)
        {
            var legal=(float[])logits.Clone();
            for(int i=0;i<game.VisibleCount;i++)if(game.Visible(i).Kind==11)legal[i]=-1e9f;
            return Sample(legal,game.VisibleCount,rng);
        }
        static int[] Search(Adapter[] roots,(float[][] p,float[] v) prediction,ulong[] seeds,int[] fallback)
        {
            var candidates=new int[roots.Length][];
            for(int i=0;i<roots.Length;i++)
            {
                var rng=new Pascension.Engine.Core.DeterministicRng(seeds[i]);
                int count=roots[i].VisibleCount;var legal=Enumerable.Range(0,count).Where(a=>roots[i].Visible(a).Kind!=11).ToArray();
                var chosen=legal.OrderByDescending(a=>prediction.p[i][a]).Take(width).ToList();
                // Every legal action, including pagination/decline/multi-mode choices,
                // has a chance to be evaluated even outside the policy shortlist.
                if(!chosen.Contains(fallback[i]))chosen.Add(fallback[i]);int exploration=legal[rng.Next(legal.Length)];if(!chosen.Contains(exploration))chosen.Add(exploration);
                // Preserve a route to mastery and to cashing in overwhelming
                // power even when the learned shortlist has collapsed.
                foreach(int a in legal)if(roots[i].Visible(a).Action is ShardsFocusAction && roots[i].Engine.State.Players[roots[i].Actor].Mastery<30 ||
                    roots[i].Visible(a).Action is ShardsEndTurnAction && roots[i].Engine.State.Players[roots[i].Actor].Power>1000)
                    if(!chosen.Contains(a))chosen.Add(a);
                candidates[i]=chosen.ToArray();
            }
            var templates=new Adapter[roots.Length*worlds];team.Run(templates.Length,k=>templates[k]=PublicWorld.Sample(roots[k/worlds],seeds[k/worlds]+(ulong)(k%worlds)*7919));
            var branches=new List<Branch>();
            for(int i=0;i<roots.Length;i++)foreach(int action in candidates[i])for(int w=0;w<worlds;w++)branches.Add(new Branch{Root=i,Action=action,Seat=roots[i].Actor,Turn=roots[i].Engine.State.TurnPlayerIndex,World=w,Game=templates[i*worlds+w]});
            team.Run(branches.Count,k=>{var b=branches[k];b.Game=DepthCopy.Copy(b.Game);b.Game.Step(b.Action);});nodes+=branches.Count;
            var values=new double[branches.Count];
            for(int d=1;d<=depth;d++)
            {
                var live=Enumerable.Range(0,branches.Count).Where(k=>!branches[k].Ended&&!branches[k].Game.Engine.State.GameOver&&!branches[k].Game.Truncated).ToArray();
                if(live.Length==0)break;
                var pred=Infer(live.Select(k=>branches[k].Game).ToArray(),2);
                team.Run(live.Length,j=>
                {
                    int k=live[j];var b=branches[k];values[k]=(b.Game.Actor==b.Seat?1:-1)*pred.v[j];
                    if(d<depth&&b.Game.Engine.State.TurnPlayerIndex==b.Turn)
                    {
                        int finish=masteryFinish?MasteryFinish.Suggested(b.Game):-1;
                        b.Game.Step(finish>=0?finish:greedyRollout?BestLegal(b.Game,pred.p[j]):SampleLegal(b.Game,pred.p[j],new Pascension.Engine.Core.DeterministicRng(seeds[b.Root]+(ulong)b.World*7919+(ulong)d*104729)));
                        System.Threading.Interlocked.Increment(ref nodes);
                    }
                    else b.Ended=true;
                });
                // Settled turn-boundary leaves leave the inference batch immediately.
            }
            for(int k=0;k<branches.Count;k++)if(branches[k].Game.Engine.State.GameOver)
                values[k]=branches[k].Game.Engine.State.WinnerIndex<0?0:branches[k].Game.Engine.State.WinnerIndex==branches[k].Seat?1:-1;
            var result=new int[roots.Length];
            for(int i=0;i<roots.Length;i++)
            {
                int greedy=fallback[i];double Score(int a)=>Enumerable.Range(0,branches.Count).Where(k=>branches[k].Root==i&&branches[k].Action==a).Average(k=>values[k]);
                // A collapsed policy must not veto a better plan with an
                // unbounded logit penalty larger than the entire [-1,1] value.
                double maximum=candidates[i].Max(a=>(double)prediction.p[i][a]);
                double Regularized(int a)=>Score(a)+prior*Math.Max(-priorCap,prediction.p[i][a]-maximum);
                int best=candidates[i].OrderByDescending(Regularized).First();
                result[i]=Score(best)>Score(greedy)+margin&&Regularized(best)>Regularized(greedy)+margin?best:greedy;
                var winners=candidates[i].Where(a=>branches.Where(b=>b.Root==i&&b.Action==a)
                    .All(b=>b.Game.Engine.State.GameOver&&b.Game.Engine.State.WinnerIndex==b.Seat)).ToArray();
                if(winners.Length>0)result[i]=winners.OrderByDescending(a=>prediction.p[i][a]).First();
                if(result[i]!=greedy)overrides++;decisions++;
            }
            return result;
        }
        static void SelfTest()
        {
            var rng=new Pascension.Engine.Core.DeterministicRng(92031);int checks=0;
            for(ulong seed=100;seed<104;seed++)
            {
                var g=new Adapter(new ShardsEngine(Program.Config(seed)),automaticSingletons:true);HeroAssignments.Apply(g,seed);
                for(int step=0;step<450&&!g.Engine.State.GameOver&&!g.Truncated;step++)
                {
                    ulong original=g.Engine.State.ComputeHash();var copy=DepthCopy.Copy(g);var legal=Enumerable.Range(0,g.VisibleCount).Where(a=>g.Visible(a).Kind!=11).ToArray();int action=legal[rng.Next(legal.Length)];
                    copy.Step(action);if(g.Engine.State.ComputeHash()!=original)throw new Exception("Copy mutated source");
                    var world=PublicWorld.Sample(g,(ulong)step+941);world.Step(action);
                    if(g.Engine.State.ComputeHash()!=original)throw new Exception("Search world mutated source");
                    g.Step(action);if(g.Engine.State.ComputeHash()!=copy.Engine.State.ComputeHash())throw new Exception("Copy transition mismatch");
                    float[] Observation(Adapter x){var a=new float[Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim+Encoder.MaxActions];Encoder.Encode(x,a.AsSpan(0,Encoder.ObsDim),a.AsSpan(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),a.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim));return a;}
                    var go=Observation(g);var co=Observation(copy);if(!go.SequenceEqual(co))throw new Exception($"Copy knowledge/action transition mismatch seed={seed} step={step} context={g.Decision?.Context}: "+string.Join(",",Enumerable.Range(0,go.Length).Where(i=>go[i]!=co[i]).Take(20).Select(i=>$"{i}:{go[i]}/{co[i]}")));
                    if(!g.Engine.State.GameOver&&!g.Truncated&&step%10==0)
                    {
                        var scrambled=DepthCopy.Copy(g);var state=scrambled.Engine.State;var enemy=state.Players[1-g.Actor];
                        var hidden=enemy.Hand.Concat(enemy.Deck).Reverse().ToArray();int hand=enemy.Hand.Count;
                        enemy.Hand.Clear();enemy.Hand.AddRange(hidden.Take(hand));enemy.Deck.Clear();enemy.Deck.AddRange(hidden.Skip(hand));
                        state.CenterDeck.Reverse();state.DestinyDeck.Reverse();state.Players[g.Actor].Deck.Reverse();
                        var a=PublicWorld.Sample(g,413);var b=PublicWorld.Sample(scrambled,413);
                        if(a.Engine.State.ComputeHash()!=b.Engine.State.ComputeHash())throw new Exception("Sampling depends on actual hidden arrangement");
                        if(!Observation(a).SequenceEqual(Observation(b)))throw new Exception("Sample observation leaks hidden arrangement");
                    }
                    checks++;
                }
            }
            Program.Print(new{passed=true,checked_transitions=checks});
        }
    }
}
