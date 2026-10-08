using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;
internal static class AsyncSearchAudit
{
    private static Adapter Adapter(PolicyEngine g)=>(Adapter)typeof(PolicyEngine).GetField("_adapter",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(g);
    private static Task Pending(PolicyEngine g)=>(Task)typeof(PolicyEngine).GetField("_pendingSearch",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(g);
    internal static void Run(FrozenPolicy policy,string output,int games)
    {
        var rows=new List<object>();int decisions=0;
        for(int game=0;game<games;game++)
        {
            int h0=game%5,h1=(game+1)%5;
            var config=ShardsContentRegistry.StandardConfig(0x6c00000000000000UL+(ulong)game,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
            var sync=new PolicyEngine(config,policy,9900+game,true);sync.BindSubmit(a=>sync.Submit(a));
            var asyncGame=new PolicyEngine(config,policy,9900+game,true);asyncGame.BindSubmit(a=>asyncGame.Submit(a));
            var sa=Adapter(sync);var aa=Adapter(asyncGame);
            foreach(string hero in new[]{ShardsEngine.DraftableCharacters[h1],ShardsEngine.DraftableCharacters[h0]})
            {RezAudit.Step(sa,c=>c.Option?.DefId==hero);RezAudit.Step(aa,c=>c.Option?.DefId==hero);}
            int steps=0;
            while(!sync.GameOver&&steps<3000)
            {
                asyncGame.PreparePolicy();
                var pending=Pending(asyncGame);
                // Search cannot advance the host while the UI is presenting an action.
                ulong before=TacticalSearch.Fingerprint(aa);pending?.Wait();
                if(before!=TacticalSearch.Fingerprint(aa))throw new Exception("Background work mutated host");
                sync.StepPolicy();
                if(!asyncGame.TryStepPolicy())throw new Exception("Finished async search did not advance");
                if(TacticalSearch.Fingerprint(sa)!=TacticalSearch.Fingerprint(aa)||sa.WrapperSteps!=aa.WrapperSteps||sa.Submissions!=aa.Submissions)
                    throw new Exception($"Async/sync disagreement game {game} step {steps}");
                SearchDiagnostics.AssertPrivacy(aa);
                steps++;decisions++;
            }
            if(!sync.GameOver||!asyncGame.GameOver)throw new Exception("Parity game truncated");
            rows.Add(new{game,steps,winner=sync.WinnerIndex});
            Console.WriteLine($"async parity {game+1}/{games}, {decisions} decisions");
        }
        // A real external submission invalidates work already in flight.
        var cfg=ShardsContentRegistry.StandardConfig(77111,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
        var stale=new PolicyEngine(cfg,policy,91,true);stale.BindSubmit(a=>stale.Submit(a));var g=Adapter(stale);
        foreach(string hero in new[]{"tetra","decima"})RezAudit.Step(g,c=>c.Option?.DefId==hero);
        g.Engine.State.Players[g.Actor].Mastery=29;
        stale.PreparePolicy();var work=Pending(stale);
        var action=g.Candidates.First(c=>c.Action is ShardsPlayCardAction).Action;
        if(!stale.Submit(action).Accepted)throw new Exception("Invalid stale-work fixture");
        ulong expected=TacticalSearch.Fingerprint(g);work.Wait();
        if(stale.TryStepPolicy()||expected!=TacticalSearch.Fingerprint(g))throw new Exception("Stale search action was applied");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{games,decisions,passed=true,stale_work_rejected=true,rows},Formatting.Indented));
    }
}
