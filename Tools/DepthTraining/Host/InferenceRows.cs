using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
internal sealed class InferenceRows : IDisposable
{
    internal const int Width=Encoder.ObsDim+Encoder.MaxActions*(Encoder.ActionDim+1);
    internal readonly float[][] Rows;
    readonly bool pooled;
    bool disposed;

    internal InferenceRows(Adapter[] games,LaneWorkers workers,bool usePool)
    {
        pooled=usePool;Rows=new float[games.Length][];
        try
        {
            workers.Run(games.Length,i=>
            {
                var row=pooled?ArrayPool<float>.Shared.Rent(Width):new float[Width];Rows[i]=row;
                // An address can be recycled from another adapter. The encoder's
                // incremental clearing cache is valid only for an owned buffer.
                games[i].ObservationAddress=IntPtr.Zero;
                Encoder.Encode(games[i],row.AsSpan(0,Encoder.ObsDim),row.AsSpan(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),row.AsSpan(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim,Encoder.MaxActions));
            });
        }
        catch{Dispose();throw;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(pooled)foreach(var row in Rows)if(row!=null)ArrayPool<float>.Shared.Return(row);
    }

    internal static object Audit()
    {
        var roots=new List<Adapter>();var rng=new Pascension.Engine.Core.DeterministicRng(736191);
        foreach(ulong seed in new ulong[]{736180,736182,736194,736199})
        {
            var game=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(game,seed);
            for(int step=0;step<240&&!game.Engine.State.GameOver&&!game.Truncated;step++)
            {
                if(step%16==0)roots.Add(DepthCopy.Copy(game));
                var legal=Enumerable.Range(0,game.VisibleCount).Where(a=>game.Visible(a).Kind!=11).ToArray();game.Step(legal[rng.Next(legal.Length)]);
            }
        }
        var games=roots.ToArray();var hashes=games.Select(g=>g.Engine.State.ComputeHash()).ToArray();
        using var team=new LaneWorkers(1);
        using var reference=new InferenceRows(games,team,false);
        // Poison a previously encoded/released batch. Reusing the exact same
        // addresses must still clear every reserved and variable-length region.
        using(var previous=new InferenceRows(games,team,true))foreach(var row in previous.Rows)Array.Fill(row,float.NaN);
        using(var actual=new InferenceRows(games,team,true))
            for(int i=0;i<games.Length;i++)if(!reference.Rows[i].AsSpan(0,Width).SequenceEqual(actual.Rows[i].AsSpan(0,Width)))
                throw new Exception("Recycled inference buffer changed the observation");
        SharedInferenceInputs.Audit(games,team,reference.Rows);
        object Bench(bool pool)
        {
            var one=new Adapter[1];one[0]=games[0];
            for(int i=0;i<100;i++)using(var warm=new InferenceRows(one,team,pool)){}
            long allocated=GC.GetAllocatedBytesForCurrentThread();int collections=GC.CollectionCount(2);
            var watch=Stopwatch.StartNew();const int iterations=5000;
            for(int i=0;i<iterations;i++){one[0]=games[i%games.Length];using var batch=new InferenceRows(one,team,pool);}
            return new{pooled=pool,iterations,seconds=watch.Elapsed.TotalSeconds,allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated,gen2Collections=GC.CollectionCount(2)-collections};
        }
        var baseline=Bench(false);var pooled=Bench(true);
        if(!hashes.SequenceEqual(games.Select(g=>g.Engine.State.ComputeHash())))throw new Exception("Inference encoding changed a root");
        return new{passed=true,roots=games.Length,poisonedBufferParity=true,sharedMemoryParity=true,baseline,pooled};
    }
}
}
