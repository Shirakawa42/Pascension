using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Shards.AI;
internal static class CopyAudit
{
    internal static void Run(string output)
    {
        var positions=HeroTactics.Cases().SelectMany(c=>Enumerable.Range(0,4).Select(v=>c.Factory(v))).ToArray();
        foreach(var p in positions)
        {
            ulong before=TacticalSearch.Fingerprint(p);var a=TacticalSearch.Copy(p);var b=FastCopy.Copy(p);
            if(TacticalSearch.Fingerprint(a)!=before||TacticalSearch.Fingerprint(b)!=before)throw new Exception("Copy mismatch");
            a.Step(0);b.Step(0);
            if(TacticalSearch.Fingerprint(a)!=TacticalSearch.Fingerprint(b)||TacticalSearch.Fingerprint(p)!=before)throw new Exception("Copy transition/source mismatch");
        }
        var rows=new List<object>();
        foreach(bool fast in new[]{false,true,true,false})
        {
            GC.Collect();GC.WaitForPendingFinalizers();var clock=Stopwatch.StartNew();long n=0;
            for(int repeat=0;repeat<20;repeat++)foreach(var p in positions)
            {var copy=fast?FastCopy.Copy(p):TacticalSearch.Copy(p);n+=copy.VisibleCount;}
            rows.Add(new{fast,copies=20*positions.Length,seconds=clock.Elapsed.TotalSeconds,checksum=n});
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,positions=positions.Length,rows},Formatting.Indented));
    }
}
