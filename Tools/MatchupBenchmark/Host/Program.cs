using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pascension.Core;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if(!BitConverter.IsLittleEndian)throw new NotSupportedException("Little endian required");
            ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
            switch(args.FirstOrDefault()??"selftest")
            {
                case "serve":Serve();break;
                case "fixed-matchups-selftest":Print(FixedMatchups.SelfTest());break;
                case "catalog":Print(Encoder.Catalog());break;
                case "benchmark":Benchmark(args);break;
                case "selftest":LaneWorkers.SelfTest();HeroAssignmentsSelfTest.Run();SelfTest.Run();break;
                case "hero-assignment-selftest":Print(HeroAssignmentsSelfTest.Run());break;
                case "lane-workers-selftest":Print(LaneWorkers.SelfTest());break;
                case "visibility-selftest":Print(new{passed=true,checks=VisibilitySelfTest.Run()});break;
                case "zone-selftest":case "zone-visibility-selftest":Print(new{passed=true,checks=ZoneVisibilitySelfTest.Run()});break;
                case "stress-selftest":Print(StressSelfTest.Run(args));break;
                case "stress-replay":Print(StressSelfTest.Replay(args[1]));break;
                case "action-selftest":Print(ActionSelfTest.Run());break;
                case "opponent-selftest":Print(CurrentOpponentSelfTest.Run(args[1]));break;
                default:throw new ArgumentException("Expected serve, catalog, selftest, hero-assignment-selftest, visibility-selftest, zone-selftest, stress-selftest, stress-replay, action-selftest, lane-workers-selftest, opponent-selftest or benchmark");
            }
        }
        internal static void Print(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
        internal static ShardsConfig Config(ulong seed)=>ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
        private static void Work(int count,LaneWorkers workers,Action<int> action)=>workers.Run(count,action);
        private static unsafe void Serve()
        {
            using var input=new BinaryReader(Console.OpenStandardInput());using var output=new BinaryWriter(Console.OpenStandardOutput());
            bool automatic=Environment.GetEnvironmentVariable("SHARDS_ZERO_AUTOMATION")=="singleton";
            bool paired=Environment.GetEnvironmentVariable("SHARDS_ZERO_PAIRED")=="1";
            string heroMode=HeroAssignments.ParseMode(Environment.GetEnvironmentVariable(HeroAssignments.EnvironmentKey));
            string opponentPath=Environment.GetEnvironmentVariable("SHARDS_ZERO_OPPONENT");
            string sharedPath=Environment.GetEnvironmentVariable("SHARDS_SHARED_BUFFER");
            CurrentOpponent.Bundle bundle=opponentPath==null?null:CurrentOpponent.LoadBundle(opponentPath);
            if(heroMode==HeroAssignments.BalancedRandom&&(paired||bundle!=null))throw new ArgumentException("Balanced random heroes are training-only; evaluation uses policy mode");
            if(bundle!=null&&!paired)throw new ArgumentException("Incumbent evaluation requires paired seeds");
            Console.Error.WriteLine(JsonSerializer.Serialize(new{host="shards-zero-depth-v1",observationSchema=Encoder.SchemaVersion,
                evaluationMode=bundle!=null,pairedSeeds=paired,learnerOnlyPending=bundle!=null,incumbentTacticalSearch=bundle!=null,
                automaticSingletons=automatic,initialHeroMode=heroMode,heroSamplerVersion=heroMode==HeroAssignments.BalancedRandom?HeroAssignments.Version:null,
                forcedHeroDraftSubmissionsPerGame=heroMode==HeroAssignments.BalancedRandom?2:0,sharedDirect=sharedPath!=null,noAutoReset=true}));
            Adapter[] games=null;CurrentOpponent[] opponents=null;int count=0,workers=1;
            Payload payload=null;LaneWorkers team=null;int[] actions=null,laneJobs=null;byte[] actionBytes=null;
            var envelope=new byte[96];var metrics=new double[8];
            try
            {
                while(true)
                {
                    uint op;try{op=input.ReadUInt32();}catch(EndOfStreamException){return;}
                    if(op==3)return;
                    bool reset=op==1;
                    if(reset)
                    {
                        count=checked((int)input.ReadUInt32());workers=checked((int)input.ReadUInt32());ulong seed=input.ReadUInt64();
                        if(count<1||count>32768||workers<1||workers>128||paired&&(count%2!=0))throw new ArgumentException("Invalid batch/workers/paired shape");
                        if(team==null||team.Workers!=workers){team?.Dispose();team=new LaneWorkers(workers);}
                        payload?.Dispose();payload=new Payload(count,sharedPath);games=new Adapter[count];opponents=new CurrentOpponent[count];actions=new int[count];laneJobs=new int[count];actionBytes=new byte[count*4];
                        Work(count,team,i=>
                        {
                            ulong gameSeed=seed+(ulong)(paired?i/2:i);
                            if(bundle==null)
                            {
                                games[i]=new Adapter(new ShardsEngine(Config(gameSeed)),automaticSingletons:automatic);
                                if(heroMode==HeroAssignments.BalancedRandom)HeroAssignments.Apply(games[i],gameSeed);
                            }
                            else
                            {
                                var opponent=new CurrentOpponent(Config(gameSeed),bundle,unchecked((int)(gameSeed^0x51ed270b)));opponents[i]=opponent;
                                var game=new Adapter(opponent.Engine,opponent.Submit,automaticSingletons:false);games[i]=game;opponent.BindSubmit(a=>game.ApplyExternal(a));
                                FixedMatchups.Apply(game,gameSeed,i%2);
                                AdvanceOpponent(game,opponent,i%2);
                            }
                        });
                    }
                    else
                    {
                        if(op!=2&&op!=4)throw new ArgumentException("Unknown opcode");
                        if(games==null)throw new InvalidOperationException("Reset before step/observe");
                        if(op==2){input.BaseStream.ReadExactly(actionBytes);Buffer.BlockCopy(actionBytes,0,actions,0,actionBytes.Length);}
                    }
                    var clock=Stopwatch.StartNew();int jobs=count;
                    if(op==2)
                    {
                        jobs=0;for(int i=0;i<count;i++)
                        {
                            if(actions[i]<-1)throw new ArgumentException("Only -1 holds a lane");
                            if(actions[i]>=0)laneJobs[jobs++]=i;
                        }
                    }
                    Work(jobs,team,job=>
                    {
                        int i=op==2?laneJobs[job]:job;var game=games[i];
                        if(op==2)
                        {
                            if(payload.Done()[i]!=0)throw new InvalidOperationException("Terminal lane must be held until reset");
                            game.Step(actions[i]);
                            if(opponents[i]!=null)AdvanceOpponent(game,opponents[i],i%2);
                        }
                        bool terminal=game.Engine.State.GameOver;bool censor=game.Truncated||opponents[i]?.Truncated==true;
                        payload.Done()[i]=terminal?1:censor?2:0;
                        payload.Rewards()[i*2]=payload.Rewards()[i*2+1]=0;
                        if(terminal&&game.Engine.State.WinnerIndex>=0)
                        {int winner=game.Engine.State.WinnerIndex;payload.Rewards()[i*2+winner]=1;payload.Rewards()[i*2+1-winner]=-1;}
                        Encoder.Encode(game,payload.Observation(i),payload.Candidates(i),payload.Mask(i));
                        payload.Actors()[i]=game.Actor;
                    });
                    metrics[0]=clock.Elapsed.TotalMilliseconds;metrics[1]=0;
                    metrics[2]=games.Sum(g=>(double)g.PolicyChoices);metrics[3]=games.Sum(g=>(double)g.Submissions);
                    metrics[4]=payload.Done().ToArray().Count(d=>d==1);metrics[5]=payload.Done().ToArray().Count(d=>d==2);
                    metrics[6]=games.Sum(g=>(double)g.Engine.Log.Count);metrics[7]=GC.GetTotalMemory(false);
                    var header=MemoryMarshal.Cast<byte,uint>(envelope.AsSpan(0,32));header[0]=0x534f4931;header[1]=1;header[2]=(uint)count;
                    header[3]=Encoder.ObsDim;header[4]=Encoder.MaxActions;header[5]=Encoder.ActionDim;header[6]=(uint)payload.Length;header[7]=64;
                    metrics.AsSpan().CopyTo(MemoryMarshal.Cast<byte,double>(envelope.AsSpan(32,64)));
                    output.Write(envelope.AsSpan(0,32));if(sharedPath==null)output.Write(payload.Bytes());output.Write(envelope.AsSpan(32,64));output.Flush();
                }
            }
            finally{team?.Dispose();payload?.Dispose();}
        }
        private static void AdvanceOpponent(Adapter game,CurrentOpponent opponent,int learner)
        {
            while(!game.Engine.State.GameOver&&!game.Truncated&&!opponent.Truncated&&game.Actor!=learner)opponent.Step();
            game.Rebuild();
        }
        private static void Benchmark(string[] args)
        {
            int count=int.Parse(args[1]),workers=int.Parse(args[2]);bool automatic=args[3]=="singleton";ulong seed=ulong.Parse(args[4]);
            var outcomes=new string[count];long[] submissions=new long[count],choices=new long[count],wrappers=new long[count],auto=new long[count];
            int[] done=new int[count];long encodeTicks=0;var watch=Stopwatch.StartNew();using var team=new LaneWorkers(workers);
            Work(count,team,i=>
            {
                var game=new Adapter(new ShardsEngine(Config(seed+(ulong)i)),automaticSingletons:automatic);
                var obs=new float[Encoder.ObsDim];var candidates=new float[Encoder.MaxActions*Encoder.ActionDim];var mask=new float[Encoder.MaxActions];
                while(!game.Engine.State.GameOver&&!game.Truncated)
                {
                    long start=Stopwatch.GetTimestamp();Encoder.Encode(game,obs,candidates,mask);
                    System.Threading.Interlocked.Add(ref encodeTicks,Stopwatch.GetTimestamp()-start);
                    game.Step(game.ExerciseChoice());
                }
                submissions[i]=game.Submissions;choices[i]=game.PolicyChoices;wrappers[i]=game.WrapperSteps;auto[i]=game.AutomaticallyApplied;
                done[i]=game.Engine.State.GameOver?1:2;
                outcomes[i]=$"{seed+(ulong)i}:{game.Engine.State.WinnerIndex}:{game.Engine.State.ComputeHash():x16}:{game.Submissions}";
            });
            string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",outcomes)))).ToLowerInvariant();
            Print(new{games=count,workers,automation=automatic?"singleton":"none",seconds=watch.Elapsed.TotalSeconds,
                completed_games=done.Count(d=>d==1),censored_games=done.Count(d=>d==2),games_per_second=count/watch.Elapsed.TotalSeconds,
                engine_submissions=submissions.Sum(),policy_choices=choices.Sum(),wrapper_choices=wrappers.Sum(),automatically_applied=auto.Sum(),
                encode_cpu_seconds=encodeTicks/(double)Stopwatch.Frequency,final_outcome_digest=digest});
        }
        private sealed unsafe class Payload:IDisposable
        {
            private readonly MemoryMappedFile _map;private readonly MemoryMappedViewAccessor _view;private byte* _pointer;private readonly bool _mapped;
            internal readonly int Count,Length;private readonly int _candidate,_mask,_reward,_done,_actor;
            internal Payload(int count,string path)
            {
                Count=count;_candidate=count*Encoder.ObsDim*4;_mask=_candidate+count*Encoder.MaxActions*Encoder.ActionDim*4;
                _reward=_mask+count*Encoder.MaxActions*4;_done=_reward+count*2*4;_actor=_done+count*4;Length=_actor+count*4;
                if(path==null)_pointer=(byte*)Marshal.AllocHGlobal(Length);
                else
                {
                    var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
                    if(file.Length!=Length){file.Dispose();throw new ArgumentException("Shared payload has wrong byte length");}
                    _map=MemoryMappedFile.CreateFromFile(file,null,Length,MemoryMappedFileAccess.ReadWrite,System.IO.HandleInheritability.None,false);
                    _view=_map.CreateViewAccessor(0,Length,MemoryMappedFileAccess.ReadWrite);byte* raw=null;_view.SafeMemoryMappedViewHandle.AcquirePointer(ref raw);
                    _pointer=raw+_view.PointerOffset;_mapped=true;
                }
                Bytes().Clear();
            }
            internal Span<byte> Bytes()=>new(_pointer,Length);
            internal Span<float> Observation(int i)=>new((float*)(_pointer+i*Encoder.ObsDim*4),Encoder.ObsDim);
            internal Span<float> Candidates(int i)=>new((float*)(_pointer+_candidate+i*Encoder.MaxActions*Encoder.ActionDim*4),Encoder.MaxActions*Encoder.ActionDim);
            internal Span<float> Mask(int i)=>new((float*)(_pointer+_mask+i*Encoder.MaxActions*4),Encoder.MaxActions);
            internal Span<float> Rewards()=>new((float*)(_pointer+_reward),Count*2);
            internal Span<int> Done()=>new((int*)(_pointer+_done),Count);
            internal Span<int> Actors()=>new((int*)(_pointer+_actor),Count);
            public void Dispose(){if(_mapped){_view.SafeMemoryMappedViewHandle.ReleasePointer();_view.Dispose();_map.Dispose();}else Marshal.FreeHGlobal((IntPtr)_pointer);_pointer=null;}
        }
    }
}
