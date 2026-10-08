using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Content;
using Shards.Engine;
using Old = Shards.AI;
using New = Shards.Nyou;

[TestFixture]
public sealed class BalancePatchPolicyTests
{
    private static string Resource(string name)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ProjectSettings", "ProjectVersion.txt"))) root = root.Parent;
        return Path.Combine(root.FullName, "Assets", "Resources", "AI", name);
    }
    [SetUp] public void Register()
    {
        ShardsCardDatabase.Clear(); ShardsContentRegistry.EnsureRegistered();
        Old.Encoder.Initialize(); New.Encoder.Initialize();
    }
    private static ShardsEngine Game(ShardsDlc dlc = ShardsDlc.Duel)
    {
        var bridge = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(108108,
            new List<PlayerSpec> { new PlayerSpec { Name="A", CharacterId="decima" },
                new PlayerSpec { Name="B", CharacterId="tetra" } }, dlc));
        while (bridge.PendingInput.Kind == PendingInputKind.Decision)
            Assert.That(bridge.Submit(bridge.DefaultActionFor(bridge.PendingInput)).Accepted, Is.True);
        return bridge.Inner;
    }
    private static void Refresh(ShardsEngine e)
    {
        e.State.InvalidateCardIndex();
        typeof(ShardsEngine).GetMethod("RoutePriority", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(e, null);
    }
    private static ShardsCard Give(ShardsEngine e, string id, ShardsZone zone, int owner=0)
    {
        var card = new ShardsCard { InstanceId=e.State.NextInstanceId++, DefId=id, Owner=owner, Zone=zone };
        var p=e.State.Players[owner];
        (zone==ShardsZone.Champions?p.Champions:zone==ShardsZone.DestinyRow?p.Destinies:zone==ShardsZone.Discard?p.Discard:p.Hand).Add(card);
        e.State.GeneratedCardCounts.TryGetValue(id, out int n);e.State.GeneratedCardCounts[id]=n+1;
        return card;
    }
    private static Dictionary<string,int> Counts(IEnumerable<ShardsCard> cards) => cards.GroupBy(c=>c.DefId).ToDictionary(g=>g.Key,g=>g.Count());
    private static void SameStock(New.Adapter game)
    {
        var sample=New.PublicWorld.Sample(game, 77881);
        CollectionAssert.AreEquivalent(Counts(game.Engine.State.CenterDeck), Counts(sample.Engine.State.CenterDeck));
        CollectionAssert.AreEquivalent(Counts(game.Engine.State.DestinyDeck), Counts(sample.Engine.State.DestinyDeck));
    }
    [Test]
    public void InstalledModelsKeepEveryTrainedIdentityAndAcceptAllOctoberDefinitions()
    {
        using var reader=new BinaryReader(File.OpenRead(Resource("nyou-policy.bytes")));
        for(int n=0;n<7;n++)reader.ReadInt32();
        for(int i=0;i<189;i++)
        {
            string id=Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            Assert.That(Old.Encoder.CardIndex(id), Is.EqualTo(i), id);
            Assert.That(New.Encoder.CardIndex(id), Is.EqualTo(i), id);
        }
        foreach(var def in ShardsCardDatabase.All)
        {
            Assert.That(Old.Encoder.CardIndex(def.Id), Is.InRange(0,188), def.Id);
            Assert.That(New.Encoder.CardIndex(def.Id), Is.EqualTo(Old.Encoder.CardIndex(def.Id)), def.Id);
            if(!Old.FrozenCatalog.Ids.Contains(def.Id)&&!string.IsNullOrEmpty(def.ReplacesId))
                Assert.That(New.Encoder.CardIndex(def.Id), Is.EqualTo(New.Encoder.CardIndex(def.ReplacesId)));
        }
        Assert.DoesNotThrow(()=>new Old.FrozenPolicy(File.ReadAllBytes(Resource("shards-policy.bytes"))));
        Assert.DoesNotThrow(()=>new New.FrozenPolicy(File.ReadAllBytes(Resource("nyou-policy.bytes"))));
    }
    [TestCase(ShardsDlc.None)] [TestCase(ShardsDlc.Duel)]
    public void SampledWorldUsesExactDefinitionsInsteadOfFrozenAliases(ShardsDlc dlc)
    {
        var e=Game(dlc);var game=new New.Adapter(e);
        SameStock(game);
        foreach(var pair in e.InitialCardCounts())
        {
            int expected=e.InitialCardCounts().Where(p=>New.Encoder.CardIndex(p.Key)==New.Encoder.CardIndex(pair.Key)).Sum(p=>p.Value);
            Assert.That(game.InitialCounts()[New.Encoder.CardIndex(pair.Key)], Is.EqualTo(expected));
        }
        if(dlc==ShardsDlc.Duel)
        {
            var sampled=New.PublicWorld.Sample(game,55499);
            Assert.That(sampled.Engine.State.CenterDeck.Concat(sampled.Engine.State.CenterRow).Any(c=>c?.DefId=="horizon_seeker"), Is.True);
            Assert.That(sampled.Engine.State.CenterDeck.Concat(sampled.Engine.State.CenterRow).Any(c=>c?.DefId=="shard_seer_duel"), Is.True);
        }
    }
    [Test]
    public void DoomGateGeneratedSupplyIsCountedExactlyOnceByBothSearches()
    {
        var e=Game();e.State.Players[0].DoomGateFloodUsed=true;
        e.ShuffleIngeminexIntoCenterDeck(35);
        SameStock(new New.Adapter(e));
        var old=new Old.Adapter(e);var sample=Old.TacticalSearch.PublicWorld(old,55309);
        CollectionAssert.AreEquivalent(Counts(e.State.CenterDeck),Counts(sample.Engine.State.CenterDeck));
        Assert.That(sample.Engine.State.CenterDeck.Any(c=>c.DefId=="ingeminex_corruption_duel"),Is.True);
    }
    [Test]
    public void ChampionAttackEncodesActualTargetAmountAndTemporaryDefenseForBothPolicies()
    {
        var e=Game();var target=Give(e,"systema_ai_duel",ShardsZone.Champions,1);
        target.TemporaryDefenseUntilNextTurn=2;e.State.Players[0].Power=20;Refresh(e);
        var old=new Old.Adapter(e);var current=new New.Adapter(e);
        int oa=Enumerable.Range(0,old.VisibleCount).Single(a=>old.Visible(a).Action is ShardsAttackChampionAction hit&&hit.CardInstanceId==target.InstanceId);
        int na=Enumerable.Range(0,current.VisibleCount).Single(a=>current.Visible(a).Action is ShardsAttackChampionAction hit&&hit.CardInstanceId==target.InstanceId);
        var oo=new float[Old.Encoder.ObsDim];var oc=new float[64*32];var om=new float[64];Old.Encoder.Encode(old,oo,oc,om);
        var no=new float[New.Encoder.ObsDim];var nc=new float[64*48];var nm=new float[64];New.Encoder.Encode(current,no,nc,nm);
        foreach(var data in new[]{(c:oc,at:oa*32),(c:nc,at:na*48)})
        {
            Assert.That(data.c[data.at+5], Is.EqualTo(1));
            Assert.That(data.c[data.at+16], Is.EqualTo((New.Encoder.CardIndex(target.DefId)+1)/192f));
            Assert.That(data.c[data.at+19], Is.EqualTo(4/13f));
            Assert.That(data.c[data.at+20], Is.EqualTo(6/50f));
            Assert.That(data.c[data.at+26], Is.EqualTo(6/1000f));
            Assert.That(data.c[data.at+28], Is.EqualTo(1));
        }
        var op=new Old.Prediction{P=Enumerable.Repeat(0.001f,64).ToArray()};op.P[0]=1;
        var np=new New.Prediction{P=Enumerable.Repeat(0.001f,64).ToArray()};np.P[0]=1;
        Assert.That(Old.HybridLookahead.Options(old,op,0,1,guards:true).Any(o=>o.First==oa),Is.True);
        Assert.That(New.HybridLookahead.Options(current,np,0,1,guards:true).Any(o=>o.First==na),Is.True);
        Assert.That(Old.TacticalSearch.Copy(old).Engine.State.FindCard(target.InstanceId).TemporaryDefenseUntilNextTurn,Is.EqualTo(2));
        Assert.That(New.DepthCopy.Copy(current).Engine.State.FindCard(target.InstanceId).TemporaryDefenseUntilNextTurn,Is.EqualTo(2));
        current.Step(na);Assert.That(e.State.Players[1].Champions.Contains(target),Is.False);
        Assert.That(e.State.Players[0].Power,Is.EqualTo(14));
    }
    [Test]
    public void SoulSyphonReadinessMatchesTwoFactionPatchAndDnaHasNoFactionPenalty()
    {
        var e=Game();var order=Give(e,"horizon_seeker",ShardsZone.Hand);
        var wraethe=Give(e,"riftbreaker",ShardsZone.Hand);
        e.State.Players[0].PlayedThisTurn.Add(order);e.State.Players[0].PlayedThisTurn.Add(wraethe);
        var game=new Old.Adapter(e);var obs=new float[Old.Encoder.ObsDim];
        Old.Encoder.Encode(game,obs,new float[64*32],new float[64]);
        Assert.That(obs[Old.Encoder.SupplementOffset+16+13],Is.EqualTo(1));
        var policy=new Old.FrozenPolicy(File.ReadAllBytes(Resource("shards-policy.bytes")));
        var weights=(Dictionary<string,float[]>)typeof(Old.FrozenPolicy).GetField("_weights",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(policy);
        Assert.That(weights["readiness_index"][Old.Encoder.CardIndex("dna")+1],Is.EqualTo(-1));
    }
    [Test]
    public void BothPoliciesRecognizeInfinitePowerWinsThroughZetta()
    {
        var e=Game();var zetta=Give(e,"zetta_encryptor",ShardsZone.Champions,1);
        e.State.Players[0].Power=2000;Refresh(e);
        var old=new Old.Adapter(e);var current=new New.Adapter(e);
        int oldEnd=Old.TacticalSearch.ImmediateWinningEnd(old,Old.TacticalSearch.Copy);
        int newEnd=New.TacticalSearch.ImmediateWinningEnd(current,New.DepthCopy.Copy);
        Assert.That(oldEnd,Is.GreaterThanOrEqualTo(0));
        Assert.That(newEnd,Is.GreaterThanOrEqualTo(0));
        Assert.That(old.Visible(oldEnd).Action,Is.TypeOf<ShardsEndTurnAction>());
        Assert.That(current.Visible(newEnd).Action,Is.TypeOf<ShardsEndTurnAction>());
        Assert.That(zetta.Zone,Is.EqualTo(ShardsZone.Champions));
        Assert.That(e.State.Players[0].Power,Is.EqualTo(2000));
        Assert.That(e.State.GameOver,Is.False,"Win probes must not mutate the live game");
        current.Step(newEnd);
        Assert.That(e.State.GameOver,Is.True);
        Assert.That(e.State.WinnerIndex,Is.EqualTo(0));
    }
    [Test]
    public void DnaCopyDoesNotConsumeAnotherMarketCardOrAliasItsPublicStock()
    {
        var e=Game();var dna=Give(e,"dna",ShardsZone.DestinyRow);e.State.Players[0].Gems=30;Refresh(e);
        var game=new New.Adapter(e);var original=new Dictionary<string,int>(e.InitialCardCounts());
        var result=game.ApplyExternal(new ShardsExhaustAction{PlayerIndex=0,CardInstanceId=dna.InstanceId});Assert.That(result.Accepted,Is.True);
        Assert.That(e.State.Players[0].PendingRecruitCopies,Is.EqualTo(1));
        var clone=New.DepthCopy.Copy(game);
        Assert.That(clone.Engine.State.Players[0].PendingRecruitCopies,Is.EqualTo(1));
        Assert.That(clone.Engine.State.GeneratedCardCounts,Is.Not.SameAs(e.State.GeneratedCardCounts));
        int slot=Array.FindIndex(e.State.CenterRow,c=>c!=null&&!c.Def.CannotBeFastPlayed);
        string bought=e.State.CenterRow[slot].DefId;
        Assert.That(game.ApplyExternal(new ShardsBuyCardAction{PlayerIndex=0,SlotIndex=slot}).Accepted,Is.True);
        Assert.That(e.State.Players[0].Discard.Count(c=>c.DefId==bought),Is.EqualTo(2));
        CollectionAssert.AreEquivalent(original,e.InitialCardCounts());
        Assert.That(e.State.GeneratedCardCounts[bought],Is.EqualTo(1));
        SameStock(game);
        var obs=new float[New.Encoder.ObsDim];New.Encoder.Encode(game,obs,new float[64*48],new float[64]);
        Assert.That(obs.All(v=>!float.IsNaN(v)&&!float.IsInfinity(v)),Is.True);
    }
    [Test]
    public void NewCardDecisionsAreReachableAndPackagedNetworksReturnFiniteScores()
    {
        var e=Game();e.State.Players[0].Gems=20;
        foreach(string id in new[]{"horizon_seeker","riftbreaker","rift_scout"})Give(e,id,ShardsZone.Hand);
        Give(e,"dna",ShardsZone.DestinyRow);Refresh(e);
        var old=new Old.Adapter(e);var current=new New.Adapter(e);
        var oo=new float[Old.Encoder.ObsDim];var oc=new float[64*32];var om=new float[64];Old.Encoder.Encode(old,oo,oc,om);
        var no=new float[New.Encoder.ObsDim];var nc=new float[64*48];var nm=new float[64];New.Encoder.Encode(current,no,nc,nm);
        foreach(string id in new[]{"horizon_seeker","riftbreaker","rift_scout","dna"})
        {
            int instance=e.State.Players[0].Hand.Concat(e.State.Players[0].Destinies).Single(c=>c.DefId==id).InstanceId;
            Assert.That(old.Candidates.Any(c=>c.Action is ShardsPlayCardAction p&&p.CardInstanceId==instance||c.Action is ShardsExhaustAction x&&x.CardInstanceId==instance),Is.True,id);
            Assert.That(current.Candidates.Any(c=>c.Action is ShardsPlayCardAction p&&p.CardInstanceId==instance||c.Action is ShardsExhaustAction x&&x.CardInstanceId==instance),Is.True,id);
        }
        var oldScores=new Old.FrozenPolicy(File.ReadAllBytes(Resource("shards-policy.bytes"))).Probabilities(oo,oc,om,out float ov);
        var newScores=new New.FrozenPolicy(File.ReadAllBytes(Resource("nyou-policy.bytes"))).Evaluate(no,nc,nm,out float nv);
        Assert.That(oldScores.Concat(newScores).Append(ov).Append(nv).All(v=>!float.IsNaN(v)&&!float.IsInfinity(v)),Is.True);
        for(int step=0;step<120&&!e.State.GameOver&&!current.Truncated;step++)current.Step(current.ExerciseChoice());
        Assert.That(e.PendingInput,Is.Not.Null);
        SameStock(current);
    }
}
