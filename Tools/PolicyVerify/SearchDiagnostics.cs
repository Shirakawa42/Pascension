using System;
using System.Linq;
using Pascension.Engine.Core;
using Shards.AI;
using Shards.Engine;
internal static class SearchDiagnostics
{
    internal static void AssertAdvance(Adapter original,int action)
    {
        ulong before=TacticalSearch.Fingerprint(original);int log=original.Engine.Log.Count;
        var fork=TacticalSearch.Copy(original);fork.Step(action);
        if(TacticalSearch.Fingerprint(original)!=before||original.Engine.Log.Count!=log)throw new Exception("Search mutated live engine");
        original.Step(action);
        if(TacticalSearch.Fingerprint(fork)!=TacticalSearch.Fingerprint(original))throw new Exception("Fork transition mismatch");
        var ao=new float[3328];var ac=new float[2048];var am=new float[64];var bo=new float[3328];var bc=new float[2048];var bm=new float[64];
        Encoder.Encode(original,ao,ac,am);Encoder.Encode(fork,bo,bc,bm);
        if(!ao.SequenceEqual(bo)||!ac.SequenceEqual(bc)||!am.SequenceEqual(bm))throw new Exception("Fork memory/candidate mismatch");
    }
    internal static void AssertPrivacy(Adapter source)
    {
        var altered=TacticalSearch.Copy(source);var s=altered.Engine.State;int seat=source.Actor;
        s.Rng=new DeterministicRng(999999);s.Players[seat].Deck.Reverse();
        var enemy=s.Players[1-seat];
        if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
        {
            var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;
            enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;
        }
        int known=source.Knowledge.For(seat).Count;
        for(int i=0;i<s.CenterDeck.Count-known;i++)s.CenterDeck[i].DefId=i%2==0?"crystal":"infinity_shard";
        foreach(var c in s.DestinyDeck)c.DefId="crystal";
        var a=TacticalSearch.PublicWorld(source,713101);var b=TacticalSearch.PublicWorld(altered,713101);
        if(TacticalSearch.Fingerprint(a)!=TacticalSearch.Fingerprint(b))throw new Exception("Public world depends on hidden state");
    }
}
