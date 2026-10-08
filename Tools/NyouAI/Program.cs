using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Pascension.Net;
using Shards.Content;
using Shards.Engine;
using N=Shards.Nyou;
using Z=Shards.ZeroDepth;
class Program
{
    static ShardsConfig Config(ulong seed)=>ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="rez"}},ShardsDlc.Duel);
    static void Main(string[] args)
    {
        ShardsContentRegistry.EnsureRegistered();N.Encoder.Initialize();Z.Encoder.Initialize();
        var policy=new N.FrozenPolicy(File.ReadAllBytes(args[1]));
        if(args[0]=="capture")Capture(policy,args[2]);
        else if(args[0]=="search")Search(policy,args[2],args.Length>3?int.Parse(args[3]):24);
        else throw new ArgumentException("Unknown validation command");
    }
    static void Capture(N.FrozenPolicy policy,string output)
    {
        using var writer=new BinaryWriter(File.Create(output));
        int records=0,steps=0,completed=0;var contexts=new HashSet<string>();var timer=Stopwatch.StartNew();
        for(int game=0;game<5;game++)
        {
            var n=new N.Adapter(new ShardsEngine(Config((ulong)(837298+game))));var z=new Z.Adapter(new ShardsEngine(Config((ulong)(837298+game))));
            var rng=new Random(747+game);
            var no=new float[24576];var nc=new float[3072];var nm=new float[64];var zo=new float[24576];var zc=new float[3072];var zm=new float[64];
            while(!n.Engine.State.GameOver&&steps<18000)
            {
                N.Encoder.Encode(n,no,nc,nm);Z.Encoder.Encode(z,zo,zc,zm);
                if(!no.SequenceEqual(zo)||!nc.SequenceEqual(zc)||!nm.SequenceEqual(zm))throw new Exception("Exported encoder diverged from evaluated host at "+steps);
                var logits=policy.Evaluate(no,nc,nm,out float value);
                string context=n.Decision?.Context??"priority";
                if(contexts.Add(context)||steps%25==0)
                {
                    foreach(var a in new[]{no,nc,nm,logits})foreach(float v in a)writer.Write(v);writer.Write(value);records++;
                }
                int chosen=-1;
                if(context=="soi.herodraft")
                {
                    string hero=n.Actor==0?ShardsEngine.DraftableCharacters[game]:ShardsEngine.DraftableCharacters[(game+1)%5];
                    for(int i=0;i<n.VisibleCount;i++)if(n.Visible(i).Option?.DefId==hero)chosen=i;
                }
                else
                {
                    var probabilities=N.FrozenPolicy.Probabilities(logits);double total=0;
                    for(int i=0;i<n.VisibleCount;i++)if(n.Visible(i).Kind!=11)total+=probabilities[i];
                    double draw=rng.NextDouble()*total;
                    for(int i=0;i<n.VisibleCount;i++)if(n.Visible(i).Kind!=11){chosen=i;draw-=probabilities[i];if(draw<=0)break;}
                }
                if(chosen<0)throw new Exception("No choice");n.Step(chosen);z.Step(chosen);steps++;
            }
            if(!n.Engine.State.GameOver)throw new Exception("Direct game exceeded budget");completed++;
        }
        Console.WriteLine(JsonConvert.SerializeObject(new{passed=true,records,steps,completed,contexts,seconds=timer.Elapsed.TotalSeconds,encoderParity=true}));
    }
    static void Search(N.FrozenPolicy policy,string settingsFile,int limit)
    {
        var settings=JsonConvert.DeserializeObject<Shards.AI.PolicySearchSettings>(File.ReadAllText(settingsFile));
        var engine=new N.PolicyEngine(Config(382725),policy,283,settings);
        var host=new GameHost(engine,2,0);int submitted=0;engine.BindSubmit(a=>{long beforeSubmit=((N.Adapter)typeof(N.PolicyEngine).GetField("adapter",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine)).Submissions;host.Submit(a.PlayerIndex,a);if(((N.Adapter)typeof(N.PolicyEngine).GetField("adapter",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine)).Submissions<=beforeSubmit)throw new Exception("Host rejected AI");submitted++;});
        var field=typeof(N.PolicyEngine).GetField("adapter",BindingFlags.NonPublic|BindingFlags.Instance);
        var game=(N.Adapter)field.GetValue(engine);host.Start();int choices=0;var times=new List<double>();
        while(choices<limit&&!engine.GameOver)
        {
            int count=engine.EventCount;long before=game.Submissions;var watch=Stopwatch.StartNew();engine.PreparePolicy();
            var pending=(System.Threading.Tasks.Task)typeof(N.PolicyEngine).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine);
            if(pending!=null){if(!pending.Wait(120000))throw new Exception("Search exceeded two minutes");if(count!=engine.EventCount||before!=game.Submissions)throw new Exception("Background search mutated live game");}
            if(!engine.TryStepPolicy())throw new Exception("Completed policy did not step");
            choices++;times.Add(watch.Elapsed.TotalMilliseconds);
        }
        // A human action must travel through the same observation-memory bridge.
        var action=engine.GameOver?null:engine.DefaultActionFor(engine.PendingInput);if(action!=null){long previous=game.Submissions;host.Submit(action.PlayerIndex,action);if(game.Submissions<=previous)throw new Exception("Human action rejected");}
        Console.WriteLine(JsonConvert.SerializeObject(new{passed=true,choices,submitted,completeGame=engine.GameOver,heroes=game.Engine.State.Players.Select(p=>p.CharacterId),backgroundMilliseconds=new{mean=times.Average(),maximum=times.Max()},liveMutation=false}));
    }
}
