using System;
using System.Collections.Generic;
using Shards.Engine;
using Shards.Preflight;
using Shards.Content;
using Pascension.Core;

static class Program
{
    static int checks;
    static void Equal(string expected,string actual){checks++;if(expected!=actual)throw new Exception(expected+" != "+actual);}
    static void Damage(VictoryEvidence v,int from,int to,int amount)
    {
        v.Observe(new ShardsHealthChangedEvent{PlayerIndex=to,Delta=-amount,NewValue=-3});
        v.Observe(new ShardsDamageAssignedEvent{FromPlayerIndex=from,Targets=new List<int>{to},Amounts=new List<int>{amount}});
    }
    static void EngineFinish(string card,int mastery,string expected)
    {
        var engine=new ShardsEngine(ShardsContentRegistry.StandardConfig(9911,
            new List<PlayerSpec>{new(){Name="A",CharacterId="decima"},new(){Name="B",CharacterId="tetra"}},ShardsDlc.None));
        var p=engine.State.Players[0];p.Hand.Clear();p.Mastery=mastery;
        engine.State.Players[1].Hand.Clear();engine.State.Players[1].Health=1;
        var c=new ShardsCard{InstanceId=engine.State.NextInstanceId++,Owner=0,DefId=card,Zone=ShardsZone.Hand};
        p.Hand.Add(c);engine.State.InvalidateCardIndex();
        engine.Submit(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=c.InstanceId});
        if(!engine.State.GameOver)engine.Submit(new ShardsEndTurnAction{PlayerIndex=0});
        if(!engine.State.GameOver)throw new Exception("Fixture failed to end: "+card);
        var evidence=new VictoryEvidence();for(int i=0;i<engine.Log.Count;i++)evidence.Observe(engine.Log[i]);
        Equal(expected,evidence.Result(engine.State.WinnerIndex));
    }
    static void Main()
    {
        var v=new VictoryEvidence();
        v.Observe(new ShardsMasteryChangedEvent{PlayerIndex=0,NewValue=30});
        Damage(v,0,1,20);Equal("normal_damage",v.Result(0)); // M30 alone is not a mastery finish.
        v=new VictoryEvidence();v.Observe(new ShardsPowerChangedEvent{PlayerIndex=1,Delta=9994,NewValue=9999});
        Damage(v,1,0,9999);Equal("mastery",v.Result(1)); // Includes a copied Infinity effect.
        v=new VictoryEvidence();v.Observe(new ShardsCardBoughtEvent{PlayerIndex=0,DefId="comet"});
        Damage(v,0,1,20);Equal("normal_damage",v.Result(0)); // Comet ownership alone is not a Comet win.
        v=new VictoryEvidence();v.Observe(new ShardsHealthChangedEvent{PlayerIndex=0,Delta=-1000000,NewValue=-999950});
        Equal("comet",v.Result(1));Equal("draw",v.Result(-1));
        v=new VictoryEvidence();v.Observe(new ShardsPowerChangedEvent{PlayerIndex=0,Delta=9994});
        v.Observe(new ShardsCleanupEvent{PlayerIndex=0});Damage(v,0,1,20);Equal("normal_damage",v.Result(0));
        v=new VictoryEvidence();v.Observe(new ShardsHealthChangedEvent{PlayerIndex=1,Delta=-5,NewValue=0});Equal("other_health_loss",v.Result(0));
        v=new VictoryEvidence();v.Observe(new ShardsConcededEvent{PlayerIndex=0});Equal("concession",v.Result(1));
        v=new VictoryEvidence();Equal("unknown",v.Result(0));
        EngineFinish("infinity_shard",30,"mastery");
        EngineFinish("infinity_shard",29,"normal_damage");
        EngineFinish("comet",0,"comet");
        Console.WriteLine($"PASS {checks} victory attribution checks");
    }
}
