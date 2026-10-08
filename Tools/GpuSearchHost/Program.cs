using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

internal static class Program
{
    const int Width=3328+2048+64,Capacity=8192;
    private sealed class ReplaySpec {public int index {get;set;} public ulong seed {get;set;} public string hero0 {get;set;} public string hero1 {get;set;} public bool review {get;set;}}
    private sealed class Game
    {
        internal Adapter G; internal int Index,Steps,Changes,Lane;
        internal string H0,H1; internal ulong Seed;
        internal Random[] Random;internal ReviewRecorder Review;
    }
    static void Main(string[] args)
    {
        string Get(string key,string fallback=null){int i=Array.IndexOf(args,key);return i<0?fallback:args[i+1];}
        if(Get("--mode")=="sequence-repair-audit"){SequenceRepairAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="knowledge-fuzz-audit"){KnowledgeFuzzAudit.Run(Get("--output"),int.Parse(Get("--games","200")));return;}
        if(Get("--mode")=="public-top-review"){PublicTopAudit.Review(Get("--replay"),Get("--reviews"),Get("--positions"),Get("--output"));return;}
        if(Get("--mode")=="public-top-audit"){PublicTopAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="public-hand-audit"){PublicHandAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="terminal-guard-audit"){TerminalGuardAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="terminal-guard-review"){TerminalGuardAudit.Review(Get("--replay"),Get("--reviews"),Get("--positions"),Get("--output"),Get("--policy"));return;}
        if(Get("--mode")=="hybrid-invariants"){HybridInvariantAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="natural-gain-audit"){NaturalGainAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="menu-plan-audit"){MenuPlanAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="winning-cleanup-audit"){WinningCleanupAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="no-effect-plan-audit"){NoEffectPlanAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="horizon-audit"){HorizonAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="nested-menu-audit"){NestedMenuAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="setup-plan-audit"){SetupPlanAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="shared-rollout-audit")
        {
            var policyFile=Get("--policy");var weights=policyFile==null?null:new FrozenPolicy(File.ReadAllBytes(policyFile));
            SharedRolloutAudit.Run(Get("--output"),weights==null?null:gs=>SharedRolloutAudit.Native(weights,gs));return;
        }
        if(Get("--mode")=="future-scry-audit"){FutureScryAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="scry-plan-audit"){ScryPlanAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="public-identity-audit"){PublicIdentityAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="defensive-world-audit"){DefensiveWorldAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="action-prior-audit"){ActionPriorAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="scry-review"){ScryReview.Run(Get("--replay"),Get("--reviews"),Get("--positions"),Get("--output"),Get("--policy"),Get("--scry-plans","1")=="1");return;}
        if(Get("--mode")=="optional-choice-audit"){OptionalChoiceAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="end-resolution-audit"){EndResolutionAudit.Run(Get("--output"));return;}
        if(Get("--mode")=="position-review")
        {
            PositionReview.Run(Get("--replay"),Get("--reviews"),Get("--positions"),Get("--output"));return;
        }
        if(Get("--mode")=="turn-branch-audit")
        {
            TurnBranchAudit.Run(Get("--replay"),Get("--reviews"),Get("--output"),int.Parse(Get("--node-cap","5000")),int.Parse(Get("--max-positions","1000000")),Get("--audit-scope"));
            return;
        }
        int total=int.Parse(Get("--games","80")),batch=int.Parse(Get("--batch","80")),workers=int.Parse(Get("--workers","8"));
        string mode=Get("--mode","paired"),output=Get("--output"),opponent=Get("--opponent","network");
        var replay=Get("--replay")==null?null:JsonConvert.DeserializeObject<ReplaySpec[]>(File.ReadAllText(Get("--replay")));
        if(replay!=null){if(mode!="replay")throw new ArgumentException("Replay specifications require replay mode");total=replay.Length;}
        if(total<1||(replay==null&&(total<(mode=="statistics"?20:40)||total%(mode=="statistics"?20:40)!=0))||batch<1||batch>1024||workers<1||workers>8)throw new ArgumentException("Use multiples of 40 games (20 for statistics), batch 1..1024, workers 1..8");
        var config=new PolicySearchSettings{Hybrid=Get("--hybrid","0")=="1",TerminalNodes=int.Parse(Get("--terminal-nodes","512")),Candidates=int.Parse(Get("--candidates","8")),Depth=int.Parse(Get("--depth","8")),Worlds=int.Parse(Get("--worlds","2")),Workers=workers,Margin=double.Parse(Get("--margin",".04"),System.Globalization.CultureInfo.InvariantCulture),Prior=double.Parse(Get("--prior",".015"),System.Globalization.CultureInfo.InvariantCulture)};
        if(config.Candidates<1||config.Candidates>64||config.Depth<1||config.Depth>64||config.Worlds<1||config.Worlds>16)throw new ArgumentException("Invalid search bounds");
        config.HybridFinishTurn=Get("--hybrid-finish-turn","0")=="1";
        config.EndTurnExtension=int.Parse(Get("--end-turn-extension","0"));
        config.HorizonTurns=int.Parse(Get("--horizon-turns","1"));
        config.HybridSkipForced=Get("--hybrid-skip-forced","1")=="1";
        config.TacticalGuards=Get("--tactical-guards","0")=="1";
        config.MixedResourcePlans=Get("--mixed-resource-plans","0")=="1";
        config.MenuPlans=Get("--menu-plans","0")=="1";
        config.SetupPlans=Get("--setup-plans","0")=="1";
        config.OptionalChoices=Get("--optional-choices","0")=="1";
        config.ScryPlans=Get("--scry-plans","0")=="1";
        config.ShareRolloutStates=Get("--share-rollout-states","0")=="1";
        config.FutureScryPlans=Get("--future-scry-plans","0")=="1";
        config.FutureScryDepth=int.Parse(Get("--future-scry-depth","4"));
        config.PruneNoEffectPlans=Get("--prune-no-effect-plans","0")=="1";
        config.SimplifyWins=Get("--simplify-wins","0")=="1";
        config.SequenceRepairs=Get("--sequence-repairs","0")=="1";
        config.ActionPriorCap=double.Parse(Get("--action-prior-cap","0"),System.Globalization.CultureInfo.InvariantCulture);
        config.PriorVerificationWorlds=int.Parse(Get("--prior-verification-worlds","0"));
        config.ChoicePriorScale=double.Parse(Get("--choice-prior-scale","1"),System.Globalization.CultureInfo.InvariantCulture);
        config.MenuDepth=int.Parse(Get("--menu-depth","0"));
        config.RolloutStyles=int.Parse(Get("--rollout-styles","1"));
        var parallel=new ParallelOptions{MaxDegreeOfParallelism=workers};
        using var map=MemoryMappedFile.CreateFromFile(Get("--map"),FileMode.Open,null,Capacity*(Width+65L)*4);
        using var view=map.CreateViewAccessor();
        using var shared=new SharedFloats(view);
        using var input=new BinaryReader(Console.OpenStandardInput());using var command=new BinaryWriter(Console.OpenStandardOutput());
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var reference=new FrozenPolicy(File.ReadAllBytes(Get("--policy")));
        var oldReference=Get("--reference-policy")==null?reference:new FrozenPolicy(File.ReadAllBytes(Get("--reference-policy")));
        float parityError=0,valueError=0;int parityCases=0;var parityContexts=new HashSet<string>();
        long inferenceCalls=0,inferenceRows=0;double encodeSeconds=0,waitSeconds=0;
        Prediction[] Infer(Adapter[] positions)=>InferModel(positions,false);
        Prediction[] InferOld(Adapter[] positions)=>InferModel(positions,true);
        Prediction[] InferModel(Adapter[] positions,bool old)
        {
            var answer=new Prediction[positions.Length];
            for(int begin=0;begin<positions.Length;begin+=Capacity)
            {
                int count=Math.Min(Capacity,positions.Length-begin);var clock=Stopwatch.StartNew();
                Parallel.For(0,count,parallel,j=>
                {
                    var row=shared.Slice(j*Width,Width);
                    Encoder.Encode(positions[begin+j],row.Slice(0,3328),row.Slice(3328,2048),row.Slice(5376,64));
                });
                encodeSeconds+=clock.Elapsed.TotalSeconds;clock.Restart();
                command.Write(old?-count:count);command.Flush();if(input.ReadByte()!=1)throw new IOException("Bad inference acknowledgement");
                waitSeconds+=clock.Elapsed.TotalSeconds;
                for(int j=0;j<count;j++)
                {
                    var row=shared.Slice(Capacity*Width+j*65,65);
                    var p=row.Slice(0,64).ToArray();float value=row[64];
                    float sum=0;for(int k=0;k<64;k++){if(!float.IsFinite(p[k])||p[k]<0)throw new Exception("Invalid probabilities");sum+=p[k];}
                    if(Math.Abs(sum-1)>.001||!float.IsFinite(value))throw new Exception("Invalid GPU prediction");
                    for(int k=positions[begin+j].VisibleCount;k<64;k++)if(p[k]>1e-7)throw new Exception("GPU assigned probability to illegal action");
                    string parityContext=(old?"old:":"new:")+positions[begin+j].Engine.State.Players[positions[begin+j].Actor].CharacterId+":"+positions[begin+j].Decision?.Context;
                    bool newContext=parityContexts.Add(parityContext);
                    if(parityCases<32||newContext)
                    {
                        var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(positions[begin+j],o,c,m);
                        var expected=(old?oldReference:reference).Probabilities(o,c,m,out float ev);
                        for(int k=0;k<64;k++)parityError=Math.Max(parityError,Math.Abs(expected[k]-p[k]));
                        valueError=Math.Max(valueError,Math.Abs(ev-value));parityCases++;
                        if(parityError>1e-4||valueError>1e-4)throw new Exception($"Native/GPU parity failed {parityError} {valueError}");
                    }
                    answer[begin+j]=new Prediction{P=p,V=value};
                }
                inferenceCalls++;inferenceRows+=count;
            }
            return answer;
        }
        if(mode=="shared-rollout-gpu"){SharedRolloutAudit.Run(output,Infer);command.Write(0);command.Flush();return;}
        if(mode=="hybrid-equivalence"){HybridInvariantAudit.CompareForced(Infer,output);command.Write(0);command.Flush();return;}
        if(mode=="position-review-gpu"||mode=="outcome-review-gpu")
        {
            object Diagnose(Adapter source,Newtonsoft.Json.Linq.JObject record)
            {
                if(mode=="outcome-review-gpu")return OutcomeReview.Run(source,Infer,int.Parse(Get("--outcome-worlds","32")),workers,int.Parse(Get("--outcome-seed","913103")),Get("--outcome-hybrid","0")=="1"?config:null,Get("--outcome-actions","").Split(',',StringSplitOptions.RemoveEmptyEntries));
                // Replays locate the state; the model being diagnosed must supply
                // its own root value and probabilities, not the transcript model's.
                var rootPrediction=Infer(new[]{source})[0];double policyDifference=0;
                foreach(var entry in record["legal"])policyDifference=Math.Max(policyDifference,Math.Abs(rootPrediction.P[(int)entry["index"]]-(float)entry["probability"]));
                bool fallbackPreserved=policyDifference<1e-5;
                int fallback=fallbackPreserved?Enumerable.Range(0,source.VisibleCount).First(k=>ReviewRecorder.Name(source,source.Visible(k))==(string)record["fallback"]):Array.IndexOf(rootPrediction.P,rootPrediction.P.Max());
                var results=new List<object>();
                foreach(string variant in new[]{"default","depth64","no-prior","worlds8"})
                {
                    var settings=config.ValidatedCopy();
                    if(variant=="depth64")settings.Depth=64;
                    if(variant=="no-prior")settings.Prior=0;
                    if(variant=="worlds8")settings.Worlds=8;
                    var root=FastCopy.Copy(source);var planner=new HybridLookahead(settings,Infer,FastCopy.Copy){CaptureLeaves=true};
                    var options=planner.BuildOptions(root,rootPrediction,fallback);
                    int selected=planner.Choose(new[]{root},new[]{rootPrediction},new[]{fallback},new[]{true})[0];
                    var leaves=planner.DebugLeaves;var verification=planner.DebugVerification;object activation=null;
                    object turn=null;
                    if(Get("--review-turn","0")=="1"&&variant=="default")
                    {
                        var played=FastCopy.Copy(root);planner.TransferPlan(root,played);int action=selected,seat=played.Actor;bool ended=false;
                        var path=new List<object>();
                        for(int n=0;n<128&&!ended;n++)
                        {
                            var own=played.Engine.State.Players[seat];
                            path.Add(new{action=ReviewRecorder.Name(played,played.Visible(action)),own.Health,own.Mastery,own.Gems,own.Power,context=played.Decision?.Context});
                            int log=played.Engine.Log.Count;played.Step(action);
                            ended=played.Engine.State.GameOver;
                            for(int e=log;e<played.Engine.Log.Count;e++)if(played.Engine.Log[e] is ShardsTurnStartedEvent)ended=true;
                            if(ended)break;
                            var prediction=Infer(new[]{played});int greedy=Array.IndexOf(prediction[0].P,prediction[0].P.Max());
                            action=planner.Choose(new[]{played},prediction,new[]{greedy},new[]{true})[0];
                        }
                        turn=new{path,ended,played.Engine.State.GameOver,played.Engine.State.WinnerIndex};
                    }
                    if(settings.MenuPlans&&variant=="default"&&root.Decision==null&&turn==null)
                    {
                        var played=FastCopy.Copy(root);planner.TransferPlan(root,played);int action=selected,seat=played.Actor;
                        var path=new List<string>();
                        for(int n=0;n<16;n++)
                        {
                            path.Add(ReviewRecorder.Name(played,played.Visible(action)));played.Step(action);
                            if(played.Engine.State.GameOver||played.Actor!=seat||played.Decision==null)break;
                            var prediction=Infer(new[]{played});int greedy=Array.IndexOf(prediction[0].P,prediction[0].P.Max());
                            action=planner.Choose(new[]{played},prediction,new[]{greedy},new[]{true})[0];
                        }
                        var player=played.Engine.State.Players[seat];
                        activation=new{path,player.Health,player.Mastery,player.Gems,player.Power,context=played.Decision?.Context};
                    }
                    results.Add(new{variant,rootValue=rootPrediction.V,recordedRootValue=(float)record["value"],policyDifference,fallbackPreserved,selected=ReviewRecorder.Name(root,root.Visible(selected)),
                        options=options.Select((o,i)=>new{option=i,name=ReviewRecorder.Name(root,root.Visible(o.First)),o.Probability,o.ChoiceProbability,o.MenuPlan,o.FirstSubmissions,o.Keys}),leaves,verification,activation,turn});
                }
                return results;
            }
            PositionReview.Run(Get("--replay-records"),Get("--reviews"),Get("--positions"),output,Diagnose);
            command.Write(0);command.Flush();return;
        }
        if(mode=="optimization-audit"){OptimizationAudit.Run(reference,output);command.Write(0);command.Flush();return;}
        if(mode=="copy-audit"){CopyAudit.Run(output);command.Write(0);command.Flush();return;}
        if(mode=="tactics"||mode=="tactics-baseline")
        {TacticalAudit.Run(Infer,config,output,mode=="tactics",int.Parse(Get("--variants","4")),int.Parse(Get("--samples","0")),Get("--case"),Get("--copy")=="compiled"?FastCopy.Copy:null);command.Write(0);command.Flush();return;}
        using var experience=Get("--experience")==null?null:new HybridExperience(Get("--experience"));
        int next=0;var active=new List<Game>();var rows=new List<object>();var search=new Lookahead(config,Infer,Get("--copy")=="compiled"?FastCopy.Copy:null);
        var currentConfig=new PolicySearchSettings{Workers=workers,Hybrid=Get("--reference-hybrid","0")=="1",Candidates=int.Parse(Get("--reference-candidates","8")),Depth=int.Parse(Get("--reference-depth","8")),Worlds=2,TerminalNodes=512,Prior=.015,Margin=.04};
        currentConfig.TacticalGuards=Get("--reference-tactical-guards","0")=="1";
        currentConfig.EndTurnExtension=int.Parse(Get("--reference-end-turn-extension","0"));
        currentConfig.HorizonTurns=int.Parse(Get("--reference-horizon-turns","1"));
        currentConfig.MixedResourcePlans=Get("--reference-mixed-resource-plans","0")=="1";
        currentConfig.MenuPlans=Get("--reference-menu-plans","0")=="1";
        currentConfig.SetupPlans=Get("--reference-setup-plans","0")=="1";
        currentConfig.OptionalChoices=Get("--reference-optional-choices","0")=="1";
        currentConfig.ScryPlans=Get("--reference-scry-plans","0")=="1";
        currentConfig.ShareRolloutStates=Get("--reference-share-rollout-states","0")=="1";
        currentConfig.FutureScryPlans=Get("--reference-future-scry-plans","0")=="1";
        currentConfig.FutureScryDepth=int.Parse(Get("--reference-future-scry-depth","4"));
        currentConfig.PruneNoEffectPlans=Get("--reference-prune-no-effect-plans","0")=="1";
        currentConfig.SimplifyWins=Get("--reference-simplify-wins","0")=="1";
        currentConfig.SequenceRepairs=Get("--reference-sequence-repairs","0")=="1";
        currentConfig.ActionPriorCap=double.Parse(Get("--reference-action-prior-cap","0"),System.Globalization.CultureInfo.InvariantCulture);
        currentConfig.PriorVerificationWorlds=int.Parse(Get("--reference-prior-verification-worlds","0"));
        currentConfig.ChoicePriorScale=double.Parse(Get("--reference-choice-prior-scale","1"),System.Globalization.CultureInfo.InvariantCulture);
        currentConfig.MenuDepth=int.Parse(Get("--reference-menu-depth","0"));
        currentConfig.RolloutStyles=int.Parse(Get("--reference-rollout-styles","1"));
        var current=new Lookahead(currentConfig,InferOld,Get("--copy")=="compiled"?FastCopy.Copy:null);
        int wins=0,losses=0,draws=0;long decisions=0;var watch=Stopwatch.StartNew();
        ulong baseSeed=ulong.Parse(Get("--seed","7998392938210000000"));
        bool statisticsMode=mode=="statistics"||mode=="replay";
        bool independentGames=statisticsMode||experience!=null;
        using var statistics=statisticsMode?new Shards.Preflight.TrainingStatistics(
            Path.Combine(Path.GetDirectoryName(output),"raw","forced-random"),"final_evaluation",baseSeed,batch,
            new {mode="balanced-random-distinct",search="both-seats-all-turns"},100):null;
        statistics?.Reset(batch,baseSeed);
        var freeLanes=new Queue<int>(Enumerable.Range(0,batch));
        // Unique game seeds, balanced hero pairs, separately randomized assignment.
        var heroPairs=Enumerable.Range(0,total).Select(i=>i%20).ToArray();
        var heroRng=new Random(unchecked((int)baseSeed)^871392);
        for(int i=heroPairs.Length-1;i>0;i--){int j=heroRng.Next(i+1);(heroPairs[i],heroPairs[j])=(heroPairs[j],heroPairs[i]);}
        Game NewGame(int index)
        {
            int pair=independentGames?heroPairs[index]:index/2%20,h0=pair/4,h1=pair%4;if(h1>=h0)h1++;
            string a=ShardsEngine.DraftableCharacters[h0],b=ShardsEngine.DraftableCharacters[h1];ulong seed=baseSeed+(ulong)(independentGames?index:index/2);
            var spec=replay?[index];int originalIndex=spec?.index??index;
            if(spec!=null){a=spec.hero0;b=spec.hero1;seed=spec.seed;}
            var cfg=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId=a},new(){Name="P1",CharacterId=b}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(cfg));g.SubmitThroughHost=act=>{var result=g.ApplyExternal(act);if(!result.Accepted)throw new Exception(result.Error);};
            foreach(string hero in new[]{b,a}){int choice=Enumerable.Range(0,g.VisibleCount).Single(k=>g.Visible(k).Option?.DefId==hero);g.Step(choice);}
            return new Game{G=g,Index=originalIndex,Review=spec?.review==true?new ReviewRecorder(Path.Combine(Path.GetDirectoryName(output),"reviews"),originalIndex):null,Lane=freeLanes.Dequeue(),H0=a,H1=b,Seed=seed,Random=new[]{new Random(98100+(independentGames?originalIndex:index/2)*2),new Random(98101+(independentGames?originalIndex:index/2)*2)}};
        }
        void Publish()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output,JsonConvert.SerializeObject(new{schema="gpu-public-rollout-v1",mode,opponent,config,currentConfig=mode=="search-paired"?currentConfig:null,hybridDiagnostics=search.HybridDiagnostics,currentSearch=mode=="search-paired"?new{current.Branches,current.Steps,current.CloneSeconds,current.RolloutSeconds,current.TerminalSeconds}:null,copier=Get("--copy","reflection"),peak_working_set_bytes=Process.GetCurrentProcess().PeakWorkingSet64,allocated_bytes=GC.GetTotalAllocatedBytes(false),server_gc=System.Runtime.GCSettings.IsServerGC,games=rows.Count,total,wins,losses,draws,score=(wins+.5*draws)/Math.Max(1,rows.Count),parityCases,parityError,valueError,seconds=watch.Elapsed.TotalSeconds,games_per_second=rows.Count/watch.Elapsed.TotalSeconds,decisions,inferenceCalls,inferenceRows,encodeSeconds,waitSeconds,branches=search.Branches,rolloutSteps=search.Steps,searchDecisions=search.Decisions,openingSearchDecisions=search.OpeningDecisions,experienceRows=experience?.Rows,overrides=search.Overrides,cloneSeconds=search.CloneSeconds,rolloutSeconds=search.RolloutSeconds,terminalSeconds=search.TerminalSeconds,rows},Formatting.Indented));
        }
        double last=0;
        while(rows.Count<total)
        {
            while(active.Count<batch&&next<total)active.Add(NewGame(next++));
            var positions=active.Select(g=>g.G).ToArray();
            Prediction[] predictions;
            if(mode=="search-paired")
            {
                predictions=new Prediction[positions.Length];
                foreach(bool old in new[]{false,true})
                {
                    var slots=Enumerable.Range(0,active.Count).Where(i=>(active[i].G.Actor!=active[i].Index%2)==old).ToArray();
                    var values=InferModel(slots.Select(i=>positions[i]).ToArray(),old);
                    for(int j=0;j<slots.Length;j++)predictions[slots[j]]=values[j];
                }
            }
            else predictions=Infer(positions);
            var fallback=new int[active.Count];var enabled=new bool[active.Count];
            for(int i=0;i<active.Count;i++)
            {
                var game=active[i];double r=game.Random[game.G.Actor].NextDouble();
                for(int j=0;j<game.G.VisibleCount;j++){fallback[i]=j;r-=predictions[i].P[j];if(r<0)break;}
                enabled[i]=(mode=="both"||statisticsMode)||((mode=="paired"||mode=="search-paired")&&game.G.Actor==game.Index%2);
                if((opponent=="greedy"&&game.G.Actor!=game.Index%2)||(mode=="greedy-paired"&&game.G.Actor==game.Index%2))fallback[i]=Array.IndexOf(predictions[i].P,predictions[i].P.Max());
            }
            foreach(var game in active)if(game.Review!=null){game.Review.SearchDetails=null;game.Review.ExtendedSearch=null;}
            if(mode=="replay")search.ObserveChoices=(root,details)=>{var game=active.First(x=>ReferenceEquals(x.G,root));if(game.Review!=null)game.Review.SearchDetails=details;};
            int[] actions=search.Choose(positions,predictions,fallback,enabled);
            if(mode=="search-paired")
            {
                var other=current.Choose(positions,predictions,fallback,enabled.Select(x=>!x).ToArray());
                for(int i=0;i<actions.Length;i++)if(!enabled[i])actions[i]=other[i];
            }
            if(mode=="replay"&&int.Parse(Get("--review-depth","0"))>0)
            {
                var slots=Enumerable.Range(0,active.Count).Where(i=>active[i].Review?.Probe(active[i].Steps)==true).ToArray();
                if(slots.Length>0)
                {
                    var probes=slots.Select(i=>TacticalSearch.Copy(positions[i])).ToArray();var extended=config.ValidatedCopy();
                    extended.Depth=int.Parse(Get("--review-depth"));extended.TerminalNodes=0;
                    extended.Hybrid=Get("--review-hybrid","0")=="1";
                    if(int.Parse(Get("--review-worlds","0"))>0)extended.Worlds=int.Parse(Get("--review-worlds"));
                    if(int.Parse(Get("--review-candidates","0"))>0)extended.Candidates=int.Parse(Get("--review-candidates"));
                    var analyst=new Lookahead(extended,Get("--reference-policy")==null?Infer:InferOld,FastCopy.Copy);
                    analyst.ObserveChoices=(root,details)=>active[slots[Array.IndexOf(probes,root)]].Review.ExtendedSearch=details;
                    var alternatives=analyst.Choose(probes,Get("--reference-policy")==null?slots.Select(i=>predictions[i]).ToArray():InferOld(probes),slots.Select(i=>fallback[i]).ToArray(),Enumerable.Repeat(true,slots.Length).ToArray());
                    if(extended.Hybrid)for(int j=0;j<slots.Length;j++)active[slots[j]].Review.ExtendedSearch=new{selected=alternatives[j],key=TacticalSearch.Key(probes[j],alternatives[j]),name=RezAudit.Name(probes[j],probes[j].Visible(alternatives[j]))};
                }
            }
            if(opponent=="terminal")Parallel.For(0,active.Count,parallel,i=>
            {
                if(enabled[i]||!TacticalSearch.IsRelevant(positions[i]))return;
                int win=TacticalSearch.Find(positions[i],1536,12,4);if(win>=0)actions[i]=win;
            });
            for(int i=0;i<active.Count;i++)experience?.Observe(active[i].Index,active[i].Steps,positions[i],actions[i],predictions[i]);
            Parallel.For(0,active.Count,parallel,i=>{var game=active[i];game.Review?.Observe(game.G,game.Steps,actions[i],fallback[i],predictions[i]);var mark=statistics?.BeginStep(game.Lane,game.G,actions[i]);game.G.Step(actions[i]);if(mark.HasValue)statistics.ObserveStep(game.Lane,game.G.Engine,mark.Value);game.Steps++;if(actions[i]!=fallback[i])game.Changes++;if(game.G.Truncated)throw new Exception("Truncated game");});
            decisions+=active.Count;
            foreach(var game in active.Where(g=>g.G.Engine.State.GameOver).ToArray())
            {
                int winner=game.G.Engine.State.WinnerIndex,treatment=game.Index%2;if(winner<0)draws++;else if(winner==treatment)wins++;else losses++;
                experience?.Finish(game.Index,winner);
                rows.Add(new{index=game.Index,seed=game.Seed,treatment,hero0=game.H0,hero1=game.H1,winner,rounds=game.G.Engine.State.Round,steps=game.Steps,overrides=game.Changes});game.Review?.Finish(game.G);statistics?.Finish(game.Lane,game.G.Engine.State,true,game.Seed);freeLanes.Enqueue(game.Lane);active.Remove(game);
            }
            statistics?.BatchBoundary();
            if(watch.Elapsed.TotalSeconds-last>10){Publish();last=watch.Elapsed.TotalSeconds;Console.Error.WriteLine($"{rows.Count}/{total}: {wins}W {losses}L, {last:F1}s, {search.Overrides} overrides");}
        }
        statistics?.Dispose();Publish();command.Write(0);command.Flush();
    }
}

internal unsafe sealed class SharedFloats : IDisposable
{
    private readonly MemoryMappedViewAccessor view;
    private byte* pointer;
    internal SharedFloats(MemoryMappedViewAccessor view)
    {this.view=view;view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);pointer+=view.PointerOffset;}
    internal Span<float> Slice(int offset,int count)=>new Span<float>((float*)pointer+offset,count);
    public void Dispose()=>view.SafeMemoryMappedViewHandle.ReleasePointer();
}
