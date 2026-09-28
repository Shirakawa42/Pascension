using System;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    public sealed class September27BalanceTests
    {
        private static ShardsEngineAdapter Game(int players=2)
        {
            ShardsCardDatabase.Clear();ShardsContentRegistry.EnsureRegistered();
            var specs=Enumerable.Range(0,players).Select(i=>new PlayerSpec{Name="P"+i,CharacterId=ShardsEngine.DraftableCharacters[i]}).ToList();
            var a=new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(270927,specs,ShardsDlc.Duel));
            while(a.PendingInput?.Kind==PendingInputKind.Decision)Assert.IsTrue(a.Submit(a.DefaultActionFor(a.PendingInput)).Accepted);
            return a;
        }
        private static ShardsCard Give(ShardsEngine e,string id)
        {
            var c=new ShardsCard{InstanceId=e.State.NextInstanceId++,DefId=id,Owner=0,Zone=ShardsZone.Hand};
            e.State.Players[0].Hand.Add(c);e.State.InvalidateCardIndex();return c;
        }
        private static void Answer(ShardsEngine e,params int[] ids)
        {
            var d=e.PendingInput.Decision;
            Assert.IsTrue(e.Submit(new SubmitDecisionAction{PlayerIndex=d.PlayerIndex,Answer=new DecisionAnswer{DecisionId=d.Id,ChosenOptionIds=ids.ToList()}}).Accepted);
        }
        [Test] public void SecondSeatGetsOneStartingCrystalInsteadOfAnExtraCard()
        {
            var e=Game().Inner;Assert.AreEqual(5,e.State.Players[0].Hand.Count);Assert.AreEqual(5,e.State.Players[1].Hand.Count);
            Assert.AreEqual(0,e.State.Players[0].Mastery);Assert.AreEqual(1,e.State.Players[1].Mastery);
            Assert.AreEqual(0,e.State.Players[0].Gems);Assert.AreEqual(1,e.State.Players[1].Gems);
            Assert.IsTrue(e.Submit(new ShardsEndTurnAction{PlayerIndex=0}).Accepted);
            Assert.AreEqual(5,e.State.Players[0].Hand.Count);Assert.AreEqual(5,e.State.Players[1].Hand.Count);
            Assert.AreEqual(1,e.State.Players[1].Gems);
            Assert.IsTrue(e.Submit(new ShardsEndTurnAction{PlayerIndex=1}).Accepted);Assert.AreEqual(5,e.State.Players[1].Hand.Count);
            Assert.AreEqual(0,e.State.Players[1].Gems, "Unspent opening crystal expires normally");
            Assert.IsTrue(e.Submit(new ShardsEndTurnAction{PlayerIndex=0}).Accepted);
            Assert.AreEqual(0,e.State.Players[1].Gems, "No recurring crystal grant");
        }
        [Test] public void MultiplayerOpeningHandsAreUnchanged()
        {var e=Game(3).Inner;Assert.IsTrue(e.State.Players.All(p=>p.Hand.Count==5 && p.Gems==0));}
        [Test] public void TetraRequiresThreeGemsAndDrawsTwo()
        {
            var e=Game().Inner;var p=e.State.Players[0];p.CharacterId="tetra";p.Mastery=5;p.Gems=2;
            Assert.IsFalse(e.Submit(new ShardsHeroAbilityAction{PlayerIndex=0}).Accepted);p.Gems=3;int before=p.Hand.Count;
            Assert.IsTrue(e.Submit(new ShardsHeroAbilityAction{PlayerIndex=0}).Accepted);Assert.AreEqual(0,p.Gems);Assert.AreEqual(before+2,p.Hand.Count);
        }
        [Test] public void KoSynWuPaysOneHealthForBanish()
        {
            var e=Game().Inner;var p=e.State.Players[0];p.CharacterId="kosynwu";p.Mastery=5;p.Health=5;p.Hand.Clear();p.Discard.Clear();
            var c=Give(e,"crystal");Assert.IsTrue(e.Submit(new ShardsHeroAbilityAction{PlayerIndex=0}).Accepted);Answer(e,c.InstanceId);
            Assert.AreEqual(4,p.Health);Assert.IsTrue(e.State.Banished.Contains(c));
        }
        [Test] public void RezPassiveDoesNotNeedActivationAndScryOffersThree()
        {
            var e=Game().Inner;var p=e.State.Players[0];p.CharacterId="rez";p.Mastery=4;Assert.AreEqual(1,ShardsEngine.RerollCost(p));
            p.Mastery=5;p.Gems=10;Assert.IsFalse(p.HeroAbilityUsedThisTurn);
            for(int cost=0;cost<3;cost++){
                Assert.AreEqual(cost,ShardsEngine.RerollCost(p));Assert.AreEqual(cost,ShardsSnapshotBuilder.Build(e,0).Players[0].NextRerollCost);
                var slot=Array.FindIndex(e.State.CenterRow,c=>c!=null&&!c.Def.CannotBeRerolled);
                Assert.IsTrue(e.Submit(new ShardsRerollRowAction{PlayerIndex=0,SlotIndex=slot}).Accepted);
            }
            Assert.AreEqual(7,p.Gems);Assert.IsFalse(p.HeroAbilityUsedThisTurn);
            Assert.IsTrue(e.Submit(new ShardsHeroAbilityAction{PlayerIndex=0}).Accepted);
            Assert.AreEqual("soi.scry",e.PendingInput.Decision.Context);Assert.AreEqual(3,e.PendingInput.Decision.Options.Count);
            Answer(e);Assert.AreEqual(3,ShardsEngine.RerollCost(p));Assert.AreEqual(0,p.NextRerollDiscount);
        }
        [TestCase(0,1,5,3)] [TestCase(20,3,15,9)]
        public void WarpquartzDoublesEachCopiedEffectButNotItsBanishBonus(int mastery,int count,int gems,int power)
        {
            var e=Game().Inner;var p=e.State.Players[0];p.Hand.Clear();p.Discard.Clear();p.Mastery=mastery;p.Gems=p.Power=0;
            var targets=Enumerable.Range(0,count).Select(_=>Give(e,"crystal")).ToArray();var warp=Give(e,"warpquartz_duel");
            Assert.IsTrue(e.Submit(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=warp.InstanceId}).Accepted);
            Answer(e,targets.Select(c=>c.InstanceId).ToArray());Assert.AreEqual(gems,p.Gems);Assert.AreEqual(power,p.Power);
            Assert.AreEqual(count,p.CardsBanishedThisTurn);
        }
        [Test] public void RelicStatsAndCenterDeckQuantityMatchPatch()
        {
            var e=Game().Inner;Assert.AreEqual(7,ShardsCardDatabase.Get("doom_gate").Defense);
            var def=ShardsCardDatabase.Get("praetorian_02_duel");var p=e.State.Players[0];
            p.Mastery=19;Assert.AreEqual(4,def.DynamicShield(p));p.Mastery=20;Assert.AreEqual(8,def.DynamicShield(p));Assert.IsTrue(def.ShieldInPlay);
            Assert.AreEqual(4,e.State.CenterDeck.Concat(e.State.CenterRow.Where(c=>c!=null)).Count(c=>c.DefId=="cinder_scars_duel"));
            Assert.AreEqual(3,ShardsCardDatabase.Get("cinder_scars").Quantity,"non-Duel definition untouched");
        }

        [Test] public void WarpquartzResumesTwoIndependentCopiedDecisions()
        {
            var e=Game().Inner;var p=e.State.Players[0];p.Hand.Clear();p.Discard.Clear();p.Gems=p.Power=0;
            var champions=Enumerable.Range(0,3).Select(_=>Give(e,"doom_gate")).ToArray();
            foreach(var c in champions){p.Hand.Remove(c);c.Zone=ShardsZone.Discard;p.Discard.Add(c);}
            var copied=Give(e,"korvus_legionnaire_duel");var warp=Give(e,"warpquartz_duel");
            Assert.IsTrue(e.Submit(new ShardsPlayCardAction{PlayerIndex=0,CardInstanceId=warp.InstanceId}).Accepted);
            Answer(e,copied.InstanceId);
            Assert.AreEqual("soi.return",e.PendingInput.Decision.Context);Answer(e,champions[0].InstanceId);
            Assert.AreEqual("soi.return",e.PendingInput.Decision.Context);Answer(e,champions[1].InstanceId);
            Assert.AreEqual(PendingInputKind.Priority,e.PendingInput.Kind);
            Assert.AreEqual(9,p.Power);Assert.AreEqual(3,p.Gems);
            Assert.IsTrue(p.Hand.Contains(champions[0])&&p.Hand.Contains(champions[1]));
            Assert.IsTrue(p.Discard.Contains(champions[2]));Assert.AreEqual(1,p.CardsBanishedThisTurn);
        }
    }
}
