using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Content;
using Shards.Engine;
using Pascension.Core;
internal static class Program
{
    private sealed class Fixture {public float[] observation,candidates,mask,probabilities;public float value;}
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("SHARDS_SPLIT_BRANCHES", "8");
        var policy=new FrozenPolicy(File.ReadAllBytes(args[0]));
        if(args.Length>1 && args[1]=="--async-search-audit"){AsyncSearchAudit.Run(policy,args[2],args.Length>3?int.Parse(args[3]):10);return;}
        if(args.Length>1 && args[1]=="--search-curriculum"){SearchEvaluation.Run(policy,args[2],int.Parse(args[3]),args[4]);return;}
        if(args.Length>1 && args[1]=="--search-evaluation"){SearchEvaluation.Run(policy,args[2],args.Length>3?int.Parse(args[3]):40,null,4,args.Contains("--fresh"));return;}
        if(args.Length>1 && args[1]=="--search-tactics"){HeroTactics.Run(policy,args[2],args.Length>3?int.Parse(args[3]):2,args.Length>4&& !args[4].StartsWith("--")?args[4]:null,false,true,args.Contains("--perturb"),args.Contains("--sampled"),args.Contains("--fresh"));Console.WriteLine($"Search calls={TacticalSearch.Calls} hits={TacticalSearch.Hits} nodes={TacticalSearch.Nodes}");return;}
        if(args.Length>1 && args[1]=="--hero-tactics"){HeroTactics.Run(policy,args[2],args.Length>3?int.Parse(args[3]):16,args.Length>4&&!args[4].StartsWith("--")?args[4]:null,args.Contains("--require-pass"),false,args.Contains("--perturb"),false,args.Contains("--fresh"));return;}
        if(args.Length>1 && args[1]=="--effect-structure-audit"){EffectStructureAudit.Run(args[2]);return;}
        if(args.Length>1 && args[1]=="--rez-memory-regression"){RezAudit.MemoryRegression();return;}
        if(args.Length>1 && args[1]=="--rez-tactics"){RezTactics.Run(policy,args[2],args.Length>3?int.Parse(args[3]):10000);return;}
        if(args.Length>1 && args[1]=="--rez-audit"){RezAudit.Run(policy,args[2]);return;}
        if(args.Length>1 && args[1]=="--rez-games"){RezGameAudit.Run(policy,args[2],args.Length>3?int.Parse(args[3]):800);return;}
        if(args.Length>1 && args[1]=="--draft-regression"){ChoiceAudit.DraftRegression(policy);return;}
        if(args.Length>1 && args[1]=="--destiny-audit"){ChoiceAudit.Destinies(policy,args[2]);return;}
        var fixtures=JsonConvert.DeserializeObject<Fixture[]>(File.ReadAllText(args[1]));
        float error=0,valueError=0;var watch=Stopwatch.StartNew();
        foreach(var f in fixtures)
        {
            var actual=policy.Probabilities(f.observation,f.candidates,f.mask,out float value);
            for(int a=0;a<64;a++)error=Math.Max(error,Math.Abs(actual[a]-f.probabilities[a]));
            valueError=Math.Max(valueError,Math.Abs(value-f.value));
        }
        if(error>0.0001 || valueError>0.0001)throw new Exception($"Parity: probability {error}, value {valueError}");
        Console.WriteLine($"{fixtures.Length} real policy fixtures: probability error {error}, value error {valueError}; {watch.Elapsed.TotalMilliseconds/fixtures.Length:F3} ms/decision");
        ShardsContentRegistry.EnsureRegistered(); Shards.Preflight.Encoder.Initialize(); Shards.AI.Encoder.Initialize();
        Shards.Preflight.ReactorCleanupV10SelfTest.Run();
        int steps=0,games=0;var rng=new Random(927);
        for(ulong seed=11000;seed<11040;seed++)
        {
            var original=new Shards.Preflight.Adapter(seed);
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
            var native=new Shards.AI.Adapter(new ShardsEngine(config));
            native.SubmitThroughHost=action=>{var result=native.ApplyExternal(action);if(!result.Accepted)throw new Exception(result.Error);};
            // Exercise every ordered distinct-hero pair twice, including the patched
            // Rez passive and all paid abilities, independently of draft preferences.
            int pair=(int)(seed-11000)%20,h0=pair/4,h1=pair%4;if(h1>=h0)h1++;
            foreach(int hero in new[]{h1,h0})
            {
                string id=ShardsEngine.DraftableCharacters[hero];
                int a=original.Candidates.FindIndex(x=>x.Option?.DefId==id);
                int b=native.Candidates.FindIndex(x=>x.Option?.DefId==id);
                if(a<0||b<0)throw new Exception("Missing parity draft option "+id);
                original.Step(a);native.Step(b);
            }
            var o=new float[3328];var c=new float[2048];var m=new float[64];var no=new float[3328];var nc=new float[2048];var nm=new float[64];
            for(int turn=0;turn<10000&&!original.Engine.State.GameOver;turn++)
            {
                Shards.Preflight.Encoder.Encode(original,o,c,m);Shards.AI.Encoder.Encode(native,no,nc,nm);
                if(!o.SequenceEqual(no)||!c.SequenceEqual(nc)||!m.SequenceEqual(nm))throw new Exception($"Encoder mismatch seed={seed} step={turn} obs={Array.FindIndex(o,x=>false)}");
                var p=policy.Probabilities(o,c,m,out _);double draw=rng.NextDouble();int choice=0;
                for(int a=0;a<64;a++)if(m[a]!=0){choice=a;draw-=p[a];if(draw<0)break;}
                original.Step(choice);native.Step(choice);steps++;
            }
            if(!original.Engine.State.GameOver||!native.Engine.State.GameOver||original.Engine.State.WinnerIndex!=native.Engine.State.WinnerIndex)throw new Exception("Game parity failed");
            games++;
        }
        Console.WriteLine($"Exact adapter observation/action parity: {steps} decisions, {games} completed games, all 20 ordered hero pairs");
    }
}
