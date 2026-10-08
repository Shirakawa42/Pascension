using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Content;
using Shards.Engine;
class Program
{
    static void Main(string[] args)
    {
        ShardsContentRegistry.EnsureRegistered();var policy=new FrozenPolicy(File.ReadAllBytes(args[0]));
        var cfg=ShardsContentRegistry.StandardConfig(827492,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
        // Four-argument constructor is the binary ABI used by the installed game.
        var sync=new PolicyEngine(cfg,policy,8372,true);
        var asyncGame=new PolicyEngine(cfg,policy,8372,true);
        PolicySearchSettings settings=null;
        if(args.Length>2)
        {
            settings=JsonConvert.DeserializeObject<PolicySearchSettings>(File.ReadAllText(args[2]));
            sync=new PolicyEngine(cfg,policy,8372,true,settings);
            asyncGame=new PolicyEngine(cfg,policy,8372,true,settings);
        }
        sync.BindSubmit(a=>sync.Submit(a));asyncGame.BindSubmit(a=>asyncGame.Submit(a));
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var adapter=typeof(PolicyEngine).GetField("_adapter",flags);var task=typeof(PolicyEngine).GetField("_pendingSearch",flags);
        var fingerprint=typeof(PolicyEngine).Assembly.GetType("Shards.AI.TacticalSearch").GetMethod("Fingerprint",BindingFlags.NonPublic|BindingFlags.Static);
        ulong Hash(PolicyEngine g)=>(ulong)fingerprint.Invoke(null,new[]{adapter.GetValue(g)});
        int limit=args.Length>1?int.Parse(args[1]):64;
        if(limit<1||limit>4096)throw new ArgumentException("Decision budget must be 1..4096");
        int decisions=0,backgroundSearches=0;var timings=new List<double>();
        while(decisions<limit&&!sync.GameOver)
        {
            ulong before=Hash(asyncGame);var watch=Stopwatch.StartNew();asyncGame.PreparePolicy();var pending=(Task)task.GetValue(asyncGame);if(pending!=null)backgroundSearches++;pending?.Wait();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            if(before!=Hash(asyncGame))throw new Exception("Background mutation");
            sync.StepPolicy();if(!asyncGame.TryStepPolicy()||Hash(sync)!=Hash(asyncGame))throw new Exception("Binary sync/async mismatch");decisions++;
        }
        if(backgroundSearches<1)throw new Exception("No background search");
        var sorted=timings.OrderBy(x=>x).ToArray();
        Console.WriteLine(JsonConvert.SerializeObject(new{passed=true,decisions,backgroundSearches,completeGame=sync.GameOver,assembly=typeof(PolicyEngine).Assembly.FullName,settings,
            backgroundMilliseconds=new{mean=timings.Average(),median=sorted[sorted.Length/2],maximum=sorted.Last()},unity=false}));
    }
}
