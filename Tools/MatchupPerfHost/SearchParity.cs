using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.AI;
using Shards.Engine;
namespace Shards.ZeroDepth
{
 internal static class SearchParity
 {
  internal static object Run(string folder)
  {
   var bundle=CurrentOpponent.LoadBundle(folder);var optimized=new SparseFrozenPolicy(File.ReadAllBytes(Path.Combine(folder,"shards-policy.bytes")));
   int verified=0;long branches=0,searches=0;double normalTime=0,sparseTime=0;
   for(int seed=0;seed<8;seed++)
   {
    var config=Program.Config(FixedMatchups.SeedBase+(ulong)(seed*2));var a=new PolicyEngine(config,bundle.Policy,913+seed,true,bundle.Settings);var b=new SparsePolicyEngine(config,optimized,913+seed,true,bundle.Settings);
    a.BindSubmit(x=>a.Submit(x));b.BindSubmit(x=>b.Submit(x));
    var aa=(Shards.AI.Adapter)typeof(PolicyEngine).GetField("_adapter",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(a);
    var bb=(Shards.AI.Adapter)typeof(SparsePolicyEngine).GetField("_adapter",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(b);
    var setupA=new Adapter(aa.Engine,a.Submit,false);var setupB=new Adapter(bb.Engine,b.Submit,false);FixedMatchups.Apply(setupA,FixedMatchups.SeedBase+(ulong)(seed*2),seed%2);FixedMatchups.Apply(setupB,FixedMatchups.SeedBase+(ulong)(seed*2),seed%2);
    for(int step=0;step<48&&!a.GameOver;step++)
    {
     var timer=Stopwatch.StartNew();a.StepPolicy();normalTime+=timer.Elapsed.TotalSeconds;
     timer.Restart();b.StepPolicy();sparseTime+=timer.Elapsed.TotalSeconds;
     if(aa.Engine.State.ComputeHash()!=bb.Engine.State.ComputeHash()||aa.WrapperSteps!=bb.WrapperSteps||aa.Submissions!=bb.Submissions)throw new Exception("Optimized search changed an action");
     verified++;
    }
    var sa=(Lookahead)typeof(PolicyEngine).GetField("_lookahead",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(a);
    var sb=(Lookahead)typeof(SparsePolicyEngine).GetField("_lookahead",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(b);
    if(sa.Branches!=sb.Branches||sa.Decisions!=sb.Decisions)throw new Exception("Search budget or traversal changed");branches+=sa.Branches;searches+=sa.Decisions;
   }
   return new {passed=true,verifiedSearchActions=verified,searches,branches,normalTime,sparseTime,speedup=normalTime/sparseTime,settingsUnchanged=true,stateHashesAndWrapperCountsIdentical=true};
  }
 }
}
