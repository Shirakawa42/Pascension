using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Shards.ZeroDepth
{
// One producer (host) and one consumer (Python), synchronized by the existing
// request/reply pipe. The host cannot overwrite a batch until its reply arrives.
internal sealed unsafe class SharedInferenceInputs : IDisposable
{
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    byte* pointer;
    bool disposed;
    internal readonly int Capacity;
    internal SharedInferenceInputs(string path)
    {
        long bytes=new FileInfo(path).Length,rowBytes=InferenceRows.Width*sizeof(float);
        if(bytes%rowBytes!=0||bytes/rowBytes<1||bytes/rowBytes>8192)throw new ArgumentException("Invalid shared inference size");
        Capacity=(int)(bytes/rowBytes);
        file=MemoryMappedFile.CreateFromFile(path,FileMode.Open,null,bytes,MemoryMappedFileAccess.ReadWrite);
        view=file.CreateViewAccessor(0,bytes,MemoryMappedFileAccess.ReadWrite);
        byte* start=null;view.SafeMemoryMappedViewHandle.AcquirePointer(ref start);pointer=start+view.PointerOffset;
    }
    internal void Encode(Adapter[] games,LaneWorkers workers)
    {
        if(disposed||games.Length>Capacity)throw new InvalidOperationException("Invalid shared inference batch");
        workers.Run(games.Length,i=>
        {
            var row=new Span<float>(pointer+(long)i*InferenceRows.Width*sizeof(float),InferenceRows.Width);
            games[i].ObservationAddress=IntPtr.Zero;
            Encoder.Encode(games[i],row.Slice(0,Encoder.ObsDim),row.Slice(Encoder.ObsDim,Encoder.MaxActions*Encoder.ActionDim),row.Slice(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim,Encoder.MaxActions));
        });
    }
    internal static void Audit(Adapter[] games,LaneWorkers workers,float[][] reference)
    {
        string path=Path.GetTempFileName();
        try
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite))stream.SetLength((long)games.Length*InferenceRows.Width*sizeof(float));
            using var shared=new SharedInferenceInputs(path);
            for(int repeat=0;repeat<2;repeat++)
            {
                new Span<float>(shared.pointer,games.Length*InferenceRows.Width).Fill(float.NaN);
                shared.Encode(games,workers);
                for(int i=0;i<games.Length;i++)
                {
                    var row=new ReadOnlySpan<float>(shared.pointer+(long)i*InferenceRows.Width*sizeof(float),InferenceRows.Width);
                    if(!row.SequenceEqual(reference[i].AsSpan(0,InferenceRows.Width)))
                        throw new Exception("Shared inference differs from exact private-buffer observation");
                }
            }
        }
        finally{File.Delete(path);}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        view.SafeMemoryMappedViewHandle.ReleasePointer();view.Dispose();file.Dispose();pointer=null;
    }
}
}
