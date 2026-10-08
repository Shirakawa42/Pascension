using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class CopyBenchmark
    {
        internal static object Run()
        {
            var roots=new List<Adapter>();var rng=new Pascension.Engine.Core.DeterministicRng(736191);
            foreach(ulong seed in new ulong[]{736180,736182,736194,736199})
            {
                var game=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(game,seed);
                for(int step=0;step<400&&!game.Engine.State.GameOver&&!game.Truncated;step++)
                {
                    if(step%40==0)roots.Add(DepthCopy.Copy(game));
                    var legal=Enumerable.Range(0,game.VisibleCount).Where(a=>game.Visible(a).Kind!=11).ToArray();
                    game.Step(legal[rng.Next(legal.Length)]);
                }
            }
            var obs=new float[Encoder.ObsDim];var actions=new float[Encoder.MaxActions*Encoder.ActionDim];var mask=new float[Encoder.MaxActions];
            foreach(var root in roots)Encoder.Encode(root,obs,actions,mask);
            // Warm both the JIT visitor cache and real field/container shapes.
            foreach(var root in roots)GC.KeepAlive(DepthCopy.Copy(root));
            var reference=roots.Select(r=>r.Engine.State.ComputeHash()).ToArray();
            long allocated=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
            int iterations=2000;
            for(int i=0;i<iterations;i++)GC.KeepAlive(DepthCopy.Copy(roots[i%roots.Count]));
            watch.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
            if(!reference.SequenceEqual(roots.Select(r=>r.Engine.State.ComputeHash())))throw new Exception("Copier changed benchmark roots");
            return new{dropEncoderCaches=Environment.GetEnvironmentVariable("SHARDS_DEPTH_DROP_ENCODER_CACHES")=="1",
                roots=roots.Count,iterations,seconds=watch.Elapsed.TotalSeconds,allocatedBytes=allocated,rootHashes=reference};
        }
    }
}
