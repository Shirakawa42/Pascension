using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Core;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Only the short, visible Focus -> Infinity Shard -> End Turn line.
    // Certify an actual rules win in four public worlds before overriding.
    internal static class MasteryFinish
    {
        internal static int Suggested(Adapter game)
        {
            if(game.Decision!=null||game.Engine.State.GameOver)return -1;
            var player=game.Engine.State.Players[game.Actor];
            int focus=-1,shard=-1,end=-1;
            for(int i=0;i<game.VisibleCount;i++)
            {
                var action=game.Visible(i).Action;
                if(action is ShardsFocusAction)focus=i;
                if(action is ShardsEndTurnAction)end=i;
                if(action is ShardsPlayCardAction play && player.Hand.Any(c=>c.InstanceId==play.CardInstanceId&&c.DefId=="infinity_shard"))shard=i;
            }
            if(player.Power>1000)return end;
            if(shard<0)return -1;
            if(player.Mastery>=30)return shard;
            return player.Mastery==29?focus:-1;
        }

        internal static int Choose(Adapter game)
        {
            int first=Suggested(game);if(first<0)return -1;
            int seat=game.Actor;
            for(int world=0;world<4;world++)
            {
                var copy=PublicWorld.Sample(game,716931UL+(ulong)world*7919);
                for(int step=0;step<3&&!copy.Engine.State.GameOver;step++)
                {
                    if(copy.Actor!=seat)return -1;
                    int next=Suggested(copy);if(next<0)return -1;
                    copy.Step(next);
                }
                if(!copy.Engine.State.GameOver||copy.Engine.State.WinnerIndex!=seat)return -1;
            }
            return first;
        }

        internal static object SelfTest()
        {
            int checks=0,attributionChecks=0;
            foreach(string hero in new[]{"decima","tetra","volos","kosynwu","rez"})
            foreach(var scenario in new[]{(28,1,false),(29,0,false),(29,1,true),(30,0,true)})
            {
                string other=hero=="tetra"?"volos":"tetra";
                var game=new Adapter(new ShardsEngine(ShardsContentRegistry.StandardConfig(716941,
                    new List<PlayerSpec>{new(){Name="P0",CharacterId=hero},new(){Name="P1",CharacterId=other}},ShardsDlc.Duel)));
                while(game.Decision?.Context=="soi.herodraft")
                {
                    string choice=game.Actor==0?hero:other;
                    int index=Enumerable.Range(0,game.VisibleCount).Single(i=>game.Visible(i).Option?.DefId==choice);
                    game.Step(index);
                }
                var player=game.Engine.State.Players[0];
                var shard=player.Hand.Concat(player.Deck).Concat(player.Discard).Single(c=>c.DefId=="infinity_shard");
                player.Hand.Remove(shard);player.Deck.Remove(shard);player.Discard.Remove(shard);
                shard.Zone=ShardsZone.Hand;player.Hand.Add(shard);
                player.Mastery=scenario.Item1;player.Gems=scenario.Item2;player.Power=0;
                player.FocusedThisTurn=player.CharacterExhausted=false;
                game.Engine.State.InvalidateCardIndex();
                typeof(ShardsEngine).GetMethod("RoutePriority",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(game.Engine,null);game.Rebuild();
                ulong before=game.Engine.State.ComputeHash();int action=Choose(game);
                if(game.Engine.State.ComputeHash()!=before)throw new Exception("Finish certification mutated the real game");
                if((action>=0)!=scenario.Item3)throw new Exception($"Invalid mastery certification for {hero} M{scenario.Item1} G{scenario.Item2}");
                if(action>=0)
                {
                    for(int step=0;step<3&&!game.Engine.State.GameOver;step++)
                    {int next=Choose(game);if(next<0)throw new Exception("Certified mastery line disappeared");game.Step(next);}
                    if(!game.Engine.State.GameOver||game.Engine.State.WinnerIndex!=0)throw new Exception("Certified mastery line did not win");
                    var evidence=new Shards.Preflight.VictoryEvidence();
                    for(int i=0;i<game.Engine.Log.Count;i++)evidence.Observe(game.Engine.Log[i]);
                    if(evidence.Result(0)!="mastery")throw new Exception("Infinity win was misclassified");
                    attributionChecks++;
                }
                checks++;
            }
            return new{passed=true,scenarios=checks,heroes=5,attributionChecks};
        }
    }
}
