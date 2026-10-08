using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Shards.AI;
using Shards.Engine;

namespace Shards.ZeroDepth
{
 internal static class InferencePerf
 {
  internal static object Run(string bundle)
  {
   byte[] weights=File.ReadAllBytes(Path.Combine(bundle,"shards-policy.bytes"));var normal=new FrozenPolicy(weights);var sparse=new SparseFrozenPolicy(weights);
   var states=new List<(float[] Obs,float[] Cards,float[] Mask)>();int actions=0;
   for(int seed=0;seed<16;seed++)
   {
    var setup=new Adapter(new ShardsEngine(Program.Config(FixedMatchups.SeedBase+(ulong)seed)),automaticSingletons:false);FixedMatchups.Apply(setup,FixedMatchups.SeedBase+(ulong)seed,0);
    var game=new Shards.AI.Adapter(setup.Engine);game.SubmitThroughHost=a=>game.ApplyExternal(a);var rng=new Random(1709+seed);
    for(int step=0;step<160&&!game.Engine.State.GameOver&&!game.Truncated;step++)
    {
     var obs=new float[3328];var cards=new float[2048];var mask=new float[64];Shards.AI.Encoder.Encode(game,obs,cards,mask);
     var p=normal.Probabilities(obs,cards,mask,out float value);var q=sparse.Probabilities(obs,cards,mask,out float other);
     if(BitConverter.SingleToInt32Bits(value)!=BitConverter.SingleToInt32Bits(other))throw new Exception("Value differs");
     for(int i=0;i<64;i++)if(BitConverter.SingleToInt32Bits(p[i])!=BitConverter.SingleToInt32Bits(q[i]))throw new Exception("Probability differs");
     if(step%4==0)states.Add((obs,cards,mask));double draw=rng.NextDouble();int chosen=-1;
     for(int i=0;i<64;i++)if(mask[i]!=0){chosen=i;draw-=p[i];if(draw<0)break;}
     game.Step(chosen);actions++;
    }
   }
   double Measure(bool optimized)
   {
    var timer=Stopwatch.StartNew();for(int repeat=0;repeat<3;repeat++)foreach(var x in states)
    {if(optimized)sparse.Probabilities(x.Obs,x.Cards,x.Mask,out _);else normal.Probabilities(x.Obs,x.Cards,x.Mask,out _);}
    return timer.Elapsed.TotalSeconds;
   }
   double old1=Measure(false),next1=Measure(true),next2=Measure(true),old2=Measure(false);
   return new {passed=true,actualDecisionParityChecks=actions,actualPublicStates=states.Count,probabilitiesAndValuesBitwiseEqual=true,normalSeconds=new[]{old1,old2},sparseSeconds=new[]{next1,next2},speedup=(old1+old2)/(next1+next2)};
  }
 }
}
