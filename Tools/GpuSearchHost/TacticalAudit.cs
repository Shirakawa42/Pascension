using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;

internal static class TacticalAudit
{
    sealed class Trial
    {
        internal HeroTactics.Case Case; internal Adapter Game; internal int Variant,Seat,Steps,Sample;
        internal List<object> Trace=new(); internal Random Random;
    }
    internal static void Run(Func<Adapter[],Prediction[]> infer,PolicySearchSettings config,string output,bool enabled,int variants,int samples,string filter=null,Func<Adapter,Adapter> copier=null)
    {
        var tests=HeroTactics.Cases();if(filter!=null)tests=tests.Where(t=>t.Id==filter).ToList();if(tests.Count==0||variants<1||variants>256||samples<0||samples>64)throw new ArgumentException("Invalid tactical audit bounds or case");var live=new List<Trial>();var rows=new List<object>();
        foreach(var test in tests)for(int v=0;v<variants;v++)
        {
            Adapter Make()
            {
                var position=test.Factory(v+95000);
                if(position.Decision==null)
                {
                    string[] cards={"crystal","blaster","spore_cleric_duel","li_hin_duel","testudo_vanguard","order_initiate_duel","reactor_drone_duel","wraethe_skirmisher_duel"};
                    var random=new Random(828319+v*101);
                    for(int n=0,count=1+random.Next(3);n<count;n++)RezAudit.Add(position,cards[random.Next(cards.Length)],position.Actor);
                    var hand=position.Engine.State.Players[position.Actor].Hand;
                    for(int n=hand.Count-1;n>0;n--){int j=random.Next(n+1);(hand[n],hand[j])=(hand[j],hand[n]);}
                    RezAudit.Refresh(position);
                }
                return position;
            }
            var teacher=Make();test.Demonstrate(teacher);
            if(!test.Goal(teacher))throw new Exception("Invalid demonstration "+test.Id);
            SearchDiagnostics.AssertPrivacy(Make());
            for(int sample=-1;sample<samples;sample++)
            {
                var g=Make();
                live.Add(new Trial{Case=test,Game=g,Variant=v,Seat=g.Actor,Sample=sample,Random=sample<0?null:new Random(927000+v*101+sample)});
            }
        }
        var planner=new Lookahead(config,infer,copier);var timer=System.Diagnostics.Stopwatch.StartNew();
        while(live.Count>0)
        {
            var active=live.Take(128).ToArray();
            var games=active.Select(t=>t.Game).ToArray();var p=infer(games);
            var fallback=p.Select(x=>Array.IndexOf(x.P,x.P.Max())).ToArray();
            for(int i=0;i<active.Length;i++)if(active[i].Random!=null)
            {double r=active[i].Random.NextDouble();for(int a=0;a<games[i].VisibleCount;a++){fallback[i]=a;r-=p[i].P[a];if(r<0)break;}}
            var hashes=games.Select(TacticalSearch.Fingerprint).ToArray();
            var actions=planner.Choose(games,p,fallback,Enumerable.Repeat(enabled,games.Length).ToArray());
            for(int i=0;i<active.Length;i++)
            {
                var t=active[i];var g=t.Game;
                if(TacticalSearch.Fingerprint(g)!=hashes[i])throw new Exception("Rollout search mutated source");
                var candidate=g.Visible(actions[i]);
                t.Trace.Add(new{t.Steps,context=g.Decision?.Context,action=RezAudit.Name(g,candidate),p=p[i].P[actions[i]],fallback=RezAudit.Name(g,g.Visible(fallback[i]))});
                var quick=FastCopy.Copy(g);quick.Step(actions[i]);
                SearchDiagnostics.AssertAdvance(g,actions[i]);
                if(TacticalSearch.Fingerprint(quick)!=TacticalSearch.Fingerprint(g))throw new Exception("Fast-copy transition mismatch");
                t.Steps++;
            }
            foreach(var t in active.Where(t=>t.Game.Engine.State.GameOver||t.Game.Engine.State.TurnPlayerIndex!=t.Seat||t.Steps>=100||(t.Case.Stop?.Invoke(t.Game)??false)).ToArray())
            {rows.Add(new{hero=t.Case.Hero,id=t.Case.Id,variant=t.Variant,sample=t.Sample,passed=t.Case.Goal(t.Game),steps=t.Steps,trace=t.Trace});live.Remove(t);}
        }
        var data=new{schema="gpu-tactical-audit-v1",enabled,config,variants,samples,seconds=timer.Elapsed.TotalSeconds,rows};
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));File.WriteAllText(output,JsonConvert.SerializeObject(data,Formatting.Indented));
    }
}
