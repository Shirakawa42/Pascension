// Diagnostic port of the deployed planner to the full-information adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
// Bounded repairs of a proposed continuation, validated on public-world copies.
internal static class SequenceRepairs
{
    internal static int Improve(Adapter root,IReadOnlyList<string> path,Func<Adapter,Adapter> copy)
        =>Improve(root,path,copy,out _);
    internal static int Improve(Adapter root,IReadOnlyList<string> path,Func<Adapter,Adapter> copy,out List<string> rewritten)
    {
        rewritten=null;
        if(path.Count<2)return -1;
        int first=Find(root,path[0]);if(first<0)return -1;
        bool freeBanish=root.Decision?.Context=="soi.banish"&&!false&&root.Visible(first).Kind==13;
        bool resource=HybridLookahead.ResourceCard(root,first);
        if(!freeBanish&&!resource)return -1;
        var proposals=new List<(string[] keys,string[] original,string[] tail)>();
        if(freeBanish)
        {
            if(root.Engine.State.Players[root.Actor].HeroAbilityUsedThisTurn)return -1;
            for(int i=1;i+1<Math.Min(path.Count,16);i++)
            {
                if(!path[i].Contains(":ShardsHeroAbilityAction:"))continue;
                int target=Find(root,path[i+1]);
                if(target>=0&&root.Visible(target).Kind==12)
                    proposals.Add((new[]{TacticalSearch.Key(root,target)}.Concat(path.Skip(1).Take(i-1)).ToArray(),path.Take(i+2).ToArray(),path.Skip(i+2).ToArray()));
                break;
            }
        }
        else
        {
            var player=root.Engine.State.Players[root.Actor];
            var effect=root.Engine.State.FindCard(((ShardsPlayCardAction)root.Visible(first).Action).CardInstanceId).Def.PlayEffect;
            var now=new SafeTurnGains.Amounts();var later=new SafeTurnGains.Amounts();
            if(!SafeTurnGains.Read(effect,player.Mastery,ref now)||
                !SafeTurnGains.Read(effect,root.Engine.State.Rules.MasteryCap,ref later)||
                later.Gems<=now.Gems&&later.Power<=now.Power)return -1;
            // A planned visible setup can commute directly with the resource,
            // even if the policy unnecessarily draws other cards between them.
            // Preserve its explicit optional decline, never invent a hidden pick.
            for(int i=1;i<Math.Min(path.Count,16);i++)
            {
                int setup=Find(root,path[i]);
                if(setup<0||root.Visible(setup).Action is not (ShardsPlayCardAction or ShardsExhaustAction or ShardsFocusAction))continue;
                int length=i+1<path.Count&&path[i+1].StartsWith("13:",StringComparison.Ordinal)?2:1;
                var setupKeys=path.Skip(i).Take(length).ToArray();
                proposals.Add((setupKeys.Concat(new[]{path[0]}).ToArray(),new[]{path[0]}.Concat(setupKeys).ToArray(),
                    path.Skip(1).Take(i-1).Concat(path.Skip(i+length)).ToArray()));
            }
            // Move only a neutral resource card, preserving every other action's
            // order. The prefix may include a public optional menu (e.g. a
            // mastery effect after declining a market removal).
            for(int end=2;end<=Math.Min(path.Count,12);end++)
                proposals.Add((path.Skip(1).Take(end-1).Concat(new[]{path[0]}).ToArray(),path.Take(end).ToArray(),path.Skip(end).ToArray()));
        }
        if(proposals.Count==0)return -1;
        var templates=Enumerable.Range(0,4).Select(w=>TacticalSearch.PublicWorld(root,713101+w*7919,copy)).ToArray();
        foreach(var proposal in proposals)
        {
            int replacement=Find(root,proposal.keys[0]);if(replacement<0||replacement==first)continue;
            bool passed=true;List<string> canonical=null;
            for(int world=0;world<templates.Length&&passed;world++)
            {
                var original=copy(templates[world]);var better=copy(templates[world]);
                if(!Apply(original,proposal.original,root.Actor,resource,out _)||
                    !Apply(better,proposal.keys,root.Actor,resource,out var keys)||
                    original.Decision!=null||better.Decision!=null){passed=false;break;}
                var before=original.Engine.State.Players[root.Actor];var after=better.Engine.State.Players[root.Actor];
                if(freeBanish)
                {
                    int cost=ShardsEngine.HeroAbilityInfo(before.CharacterId).Health;
                    if(cost<=0||!before.HeroAbilityUsedThisTurn||after.HeroAbilityUsedThisTurn||after.Health!=before.Health+cost)
                    {passed=false;break;}
                    after.Health=before.Health;after.HeroAbilityUsedThisTurn=before.HeroAbilityUsedThisTurn;
                    // Decision IDs are opaque input-routing counters. Both
                    // prefixes are fully resolved; the cheaper one opened one
                    // fewer menu. Removed cards have no order-sensitive effect.
                    better.Engine.State.NextDecisionId=original.Engine.State.NextDecisionId;
                    original.Engine.State.Banished.Sort((a,b)=>a.InstanceId.CompareTo(b.InstanceId));
                    better.Engine.State.Banished.Sort((a,b)=>a.InstanceId.CompareTo(b.InstanceId));
                }
                else
                {
                    if(after.Gems<before.Gems||after.Power<before.Power||after.Gems==before.Gems&&after.Power==before.Power)
                    {passed=false;break;}
                    after.Gems=before.Gems;after.Power=before.Power;
                    int id=((ShardsPlayCardAction)root.Visible(first).Action).CardInstanceId;
                    var initial=root.Engine.State.Players[root.Actor];
                    bool Normalize(List<ShardsCard> cards,int offset)
                    {
                        int index=cards.FindIndex(c=>c.InstanceId==id);if(index<offset)return false;
                        var card=cards[index];cards.RemoveAt(index);cards.Insert(offset,card);return true;
                    }
                    if(!Normalize(after.PlayZone,initial.PlayZone.Count)||!Normalize(after.PlayedThisTurn,initial.PlayedThisTurn.Count))
                    {passed=false;break;}
                }
                passed=TacticalSearch.Fingerprint(original)==TacticalSearch.Fingerprint(better)&&SameKnowledge(original,better);
                if(world==0)canonical=keys;
            }
            if(!passed)continue;
            rewritten=canonical.Concat(proposal.tail).ToList();
            return replacement;
        }
        return -1;
    }
    // Ordinals change after moving a banish. Keep exact instance, definition,
    // range and kind; never replace the selected card with a positional neighbor.
    internal static int Find(Adapter game,string key)
    {
        for(int a=0;a<game.VisibleCount;a++)if(TacticalSearch.Key(game,a)==key)return a;
        if(!key.StartsWith("12:",StringComparison.Ordinal))return -1;
        var wanted=key.Split(new[]{':'},6);int found=-1;
        if(wanted.Length!=6)return -1;
        for(int a=0;a<game.VisibleCount;a++)
        {
            var current=TacticalSearch.Key(game,a).Split(new[]{':'},6);
            if(current.Length==6&&current[0]==wanted[0]&&current.Skip(2).SequenceEqual(wanted.Skip(2)))
            {if(found>=0)return -1;found=a;}
        }
        return found;
    }
    static bool Apply(Adapter game,IEnumerable<string> path,int seat,bool noInformation,out List<string> keys)
    {
        keys=new List<string>();
        foreach(string key in path)
        {
            if(game.Engine.State.GameOver||game.Actor!=seat)return false;
            int a=Find(game,key);if(a<0)return false;
            keys.Add(TacticalSearch.Key(game,a));int log=game.Engine.Log.Count;game.Step(a);
            for(int e=log;e<game.Engine.Log.Count;e++)
            {
                var entry=game.Engine.Log[e];
                if(entry is ShardsTurnStartedEvent)return false;
                if(noInformation&&entry is ShardsCardDrawnEvent or ShardsCardsRevealedEvent or ShardsDeckShuffledEvent or ShardsMonsterRevealedEvent or ShardsRowRefilledEvent)return false;
            }
        }
        return !game.Engine.State.GameOver&&game.Actor==seat;
    }
    static bool SameKnowledge(Adapter a,Adapter b)
    {
        for(int seat=0;seat<2;seat++)
        {
            var x=a.Engine.State.Players[seat];var y=b.Engine.State.Players[seat];
            if(!x.PlayedThisTurn.Select(c=>c.InstanceId).SequenceEqual(y.PlayedThisTurn.Select(c=>c.InstanceId)))return false;
            foreach(ShardsFaction faction in Enum.GetValues(typeof(ShardsFaction)))
                if(x.FactionPlays(faction)!=y.FactionPlays(faction)||x.FactionAllyPlays(faction)!=y.FactionAllyPlays(faction))return false;
            if(HybridKnowledge.ObserverSignature(a.Knowledge,seat)!=HybridKnowledge.ObserverSignature(b.Knowledge,seat))return false;
        }
        return true;
    }
}
}
