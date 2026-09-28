using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pascension.Engine.Decisions;
using Shards.AI;

[TestFixture]
public sealed class HeroDraftPolicyTests
{
    private static readonly string[] Heroes={"decima","tetra","volos","kosynwu","rez"};
    private static IEnumerable<List<DecisionOption>> Menus(string[] ids)
    {
        if(ids.Length==0){yield return new List<DecisionOption>();yield break;}
        foreach(var id in ids)
            foreach(var tail in Menus(ids.Where(x=>x!=id).ToArray()))
            { tail.Insert(0,new DecisionOption(100+Array.IndexOf(Heroes,id),id){DefId=id});yield return tail; }
    }
    [TestCase(0)] [TestCase(1)]
    public void FirstPickUsesIdentityRegardlessOfEveryMenuPermutation(int seat)
    {
        foreach(var menu in Menus(Heroes))
            Assert.That(HeroDraftPolicy.Choose(menu,seat,null),Is.EqualTo(101));
    }
    [TestCase(0)] [TestCase(1)]
    public void BestResponseUsesIdentityRegardlessOfEveryMenuPermutation(int seat)
    {
        var expected = seat == 0 ? new[] { 101, 100, 104, 100, 103 } : new[] { 104, 104, 103, 101, 101 };
        foreach(var opponent in Heroes)
            foreach(var menu in Menus(Heroes.Where(x=>x!=opponent).ToArray()))
                Assert.That(HeroDraftPolicy.Choose(menu,seat,opponent),Is.EqualTo(expected[Array.IndexOf(Heroes,opponent)]),$"seat={seat},opponent={opponent}");
    }
    [Test]
    public void DisabledHeroCannotBeSelected()
    {
        var menu=Menus(Heroes).First();menu.Single(x=>x.DefId=="tetra").Disabled=true;
        Assert.That(HeroDraftPolicy.Choose(menu,0,"decima"),Is.EqualTo(103));
    }
    [Test]
    public void UnknownHeroFailsRatherThanSilentlyUsingPosition()
    {
        Assert.Throws<ArgumentException>(()=>HeroDraftPolicy.Choose(new[]{new DecisionOption(0,"unknown"){DefId="unknown"}},0,null));
    }
}
