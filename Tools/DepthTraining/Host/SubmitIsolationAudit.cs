using System;
using System.IO;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class SubmitIsolationAudit
    {
        internal static object Run()
        {
            string marker=Path.GetTempFileName();
            try
            {
                var engine=new ShardsEngine(Program.Config(716933));
                var game=new Adapter(engine,action=>{File.AppendAllText(marker,"submit\n");return engine.Submit(action);});
                HeroAssignments.Apply(game,716933);
                string before=File.ReadAllText(marker);ulong hash=engine.State.ComputeHash();
                var copy=DepthCopy.Copy(game);
                int action=Enumerable.Range(0,copy.VisibleCount).First(i=>copy.Visible(i).Kind==0);
                copy.Step(action);
                if(File.ReadAllText(marker)!=before)throw new Exception("Search copy called the real external submission bridge");
                if(engine.State.ComputeHash()!=hash)throw new Exception("Search copy mutated real state");
                if(copy.Engine.State.ComputeHash()==hash)throw new Exception("Search copy failed to apply its own action");
                return new{passed=true,external_bridge_isolated=true,copy_transition_applied=true};
            }
            finally{File.Delete(marker);}
        }
    }
}
