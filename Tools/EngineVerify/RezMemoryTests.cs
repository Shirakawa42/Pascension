using System;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

public class RezMemoryTests
{
    private static Adapter Setup(params string[] hand)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var config=ShardsContentRegistry.StandardConfig(270927,new List<PlayerSpec>{new(){Name="P0"},new(){Name="P1"}},ShardsDlc.Duel);
        var g=new Adapter(new ShardsEngine(config));
        g.SubmitThroughHost=a=>Assert.That(g.ApplyExternal(a).Accepted,Is.True);
        foreach(string hero in new[]{"kosynwu","rez"})Step(g,c=>c.Option?.DefId==hero);
        var state=g.Engine.State;var p=state.Players[0];p.Mastery=5;p.Hand.Clear();state.DestinyRow.Clear();
        foreach(string id in hand)p.Hand.Add(new ShardsCard{InstanceId=state.NextInstanceId++,DefId=id,Owner=0,Zone=ShardsZone.Hand});
        foreach(string id in new[]{"command_seer_duel","korvus_legionnaire_duel","wraethe_skirmisher_duel"})
        {
            var card=state.CenterDeck.FirstOrDefault(c=>c.DefId==id);
            if(card!=null)state.CenterDeck.Remove(card);
            else card=new ShardsCard{InstanceId=state.NextInstanceId++,DefId=id,Owner=-1,Zone=ShardsZone.CenterDeck};
            state.CenterDeck.Add(card);
        }
        state.InvalidateCardIndex();
        typeof(ShardsEngine).GetMethod("RoutePriority",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(g.Engine,null);
        g.Rebuild();Step(g,c=>c.Action is ShardsHeroAbilityAction);Step(g,c=>c.Kind==13);
        Assert.That(g.Knowledge.For(0).Count,Is.EqualTo(3));return g;
    }
    private static void Step(Adapter g,Func<Candidate,bool> predicate)
    {
        int index=g.Candidates.FindIndex(c=>predicate(c));Assert.That(index,Is.GreaterThanOrEqualTo(0));g.Step(index);
    }
    private static void Play(Adapter g,string id)=>Step(g,c=>c.Action is ShardsPlayCardAction a&&g.Engine.State.Players[0].Hand.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==id));
    [Test] public void HandRevealDoesNotEraseKnownCenterCards()
    {
        var g=Setup("undergrowth_aspirant_duel","shardwood_guardian_duel");var before=g.Knowledge.For(0).ToArray();
        Play(g,"undergrowth_aspirant_duel");Assert.That(g.Knowledge.For(0),Is.EqualTo(before));
        Assert.That(g.Knowledge.For(1),Is.Empty);
    }
    [Test] public void LongshotPreservesKnownSuffixAcrossTakeAndBottomReturn()
    {
        var g=Setup("longshot");Play(g,"longshot");
        Assert.That(g.Knowledge.For(0),Is.EqualTo(new[]{"command_seer_duel"}));
        Step(g,c=>c.Kind==13);
        Assert.That(g.Knowledge.For(0),Is.EqualTo(new[]{"command_seer_duel"}));
    }
}
