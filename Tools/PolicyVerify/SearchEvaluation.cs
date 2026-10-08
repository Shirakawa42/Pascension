using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

internal static class SearchEvaluation
{
    internal static void Run(FrozenPolicy policy,string output,int games,string curriculum=null,int workers=4,bool fresh=false)
    {
        if(games%40!=0)throw new ArgumentException("Use multiples of 40: every ordered hero pairing in both treatment seats");
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        using var examples=curriculum==null?null:new StreamWriter(new System.IO.Compression.GZipStream(File.Create(curriculum),System.IO.Compression.CompressionLevel.Fastest));
        int labels=0,retention=0;var sync=new object();
        var rows=new List<object>();var times=new List<double>();var watch=Stopwatch.StartNew();
        int wins=0,losses=0,draws=0,changes=0,decisions=0;
        Parallel.For(0,games,new ParallelOptions{MaxDegreeOfParallelism=workers},index=>
        {
            var localTimes=new List<double>();
            int pair=(index/2)%20,h0=pair/4,h1=pair%4;if(h1>=h0)h1++;
            int treatment=index%2;ulong seed=(curriculum==null?(fresh?0x6d00000000000000UL:0x6a00000000000000UL):0x6b00000000000000UL)+(ulong)(index/2);
            string hero0=ShardsEngine.DraftableCharacters[h0],hero1=ShardsEngine.DraftableCharacters[h1];
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId=hero0},new(){Name="P1",CharacterId=hero1}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(config));g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
            foreach(string h in new[]{hero1,hero0})RezAudit.Step(g,c=>c.Option?.DefId==h);
            // Separate streams per seat; paired games share engine seed and hero placement.
            var random=new[]{new Random(98100+index/2*2),new Random(98101+index/2*2)};
            var obs=new float[3328];var candidates=new float[2048];var mask=new float[64];int steps=0,overrides=0;
            while(!g.Engine.State.GameOver&&!g.Truncated)
            {
                Encoder.Encode(g,obs,candidates,mask);var p=policy.Probabilities(obs,candidates,mask,out float value);
                int target=-1;
                double r=random[g.Actor].NextDouble();int action=0;for(int a=0;a<g.VisibleCount;a++){action=a;r-=p[a];if(r<0)break;}
                var player=g.Engine.State.Players[g.Actor];var enemy=g.Engine.State.Players[1-g.Actor];
                if(g.Actor==treatment&&g.Actor==g.Engine.State.TurnPlayerIndex&&(player.Mastery>=25||enemy.Health<=player.Power+20))
                {
                    var sw=Stopwatch.StartNew();int better=TacticalSearch.Find(g);localTimes.Add(sw.Elapsed.TotalMilliseconds);
                    target=better;
                    if(better>=0&&better!=action){action=better;overrides++;}
                }
                if(examples!=null&&(target>=0||steps%23==0))
                {
                    string example=JsonConvert.SerializeObject(new{schema="public-search-curriculum-v1",hero=player.CharacterId,game=index,seed,
                        kind=target>=0?"search":"retention",targets=target>=0?new[]{target}:Array.Empty<int>(),
                        observation=obs,candidates,mask,probabilities=p,value,
                        target_semantics="same sequence wins in four public-information samples; auxiliary policy target, not a terminal reward"});
                    lock(sync){examples.WriteLine(example);
                    if(target>=0)labels++;else retention++;}
                }
                if(steps%127==0){SearchDiagnostics.AssertPrivacy(g);SearchDiagnostics.AssertAdvance(g,action);}else g.Step(action);
                steps++;
            }
            if(g.Truncated)throw new Exception("Search game truncated");
            lock(sync)
            {
            decisions+=steps;changes+=overrides;times.AddRange(localTimes);
            int winner=g.Engine.State.WinnerIndex;if(winner<0)draws++;else if(winner==treatment)wins++;else losses++;
            rows.Add(new{index,seed,treatment,hero0,hero1,winner,rounds=g.Engine.State.Round,steps,overrides});
            times.Sort();
            var result=new{evaluation_only=true,training_updates=0,workers,fresh,curriculum,labels,retention,games=rows.Count,wins,losses,draws,score=(wins+.5*draws)/rows.Count,decisions,changes,seconds=watch.Elapsed.TotalSeconds,search_calls=times.Count,search_ms_mean=times.Count>0?times.Average():0,search_ms_p95=times.Count>0?times[(int)((times.Count-1)*.95)]:0,rows};
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));File.WriteAllText(output,JsonConvert.SerializeObject(result,Formatting.Indented));
            Console.WriteLine($"{rows.Count}/{games}: {wins}W {losses}L {draws}D, {changes} overrides, {watch.Elapsed.TotalSeconds:F1}s");
            }
        });
    }
}
