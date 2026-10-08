// Diagnostic port of the deployed planner to the full-information adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Decisions;
using Shards.Engine;

// Bounded, same-turn tactical search over the real rules. Hidden state is replaced
// by public-information samples before expansion. Only a common winning sequence
// validated across all sampled worlds overrides the network; no value heuristic
// alone may change an action. Sampling is approximate, not a proof of forced mate.
using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
internal static class TacticalSearch
{
    internal static long Nodes, Calls, Hits, Continuations;
    private sealed class SavedPlan {internal List<string> Path;internal long Step;internal int Seat,Round;}
    private static readonly ConditionalWeakTable<Adapter,SavedPlan> Plans=new();
    private sealed class Identity : IEqualityComparer<object>
    {
        public new bool Equals(object a,object b)=>ReferenceEquals(a,b);
        public int GetHashCode(object a)=>RuntimeHelpers.GetHashCode(a);
    }
    private static readonly MethodInfo Memberwise=typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,FieldInfo[]> Fields=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,FieldInfo[]> CopyFields=new();
    private static readonly Func<object,object> Shallow=(Func<object,object>)Memberwise.CreateDelegate(typeof(Func<object,object>));
    private static FieldInfo[] GetFields(Type t)
    {
        if(Fields.TryGetValue(t,out var result))return result;
        var list=new List<FieldInfo>();
        for(var c=t;c!=null;c=c.BaseType)list.AddRange(c.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly));
        return Fields[t]=list.ToArray();
    }
    internal static Adapter Copy(Adapter source)=>DepthCopy.Copy(source);
    internal static Adapter PublicWorld(Adapter source,int seed,Func<Adapter,Adapter> copier=null)=>
        Shards.ZeroDepth.PublicWorld.Sample(source,unchecked((ulong)seed));
    internal static Adapter DefensiveWorld(Adapter source,int seed=713101,Func<Adapter,Adapter> copier=null)=>
        HybridKnowledge.DefensiveWorld(source,unchecked((ulong)seed));
    private sealed class Node {internal Adapter Game;internal int Root,Depth;internal double Score;internal List<string> Path;internal Node[] Children;}
    private static double Score(Adapter g,int seat,int depth)
    {
        var s=g.Engine.State;var p=s.Players[seat];var e=s.Players[1-seat];
        return Math.Min(p.Power,e.Health+20)*4+p.Mastery*2+p.Gems*.6+p.Hand.Count*1.5+
            p.Champions.Count*2+(p.Mastery==30?80:0)-e.Health*4-depth*.15;
    }
    internal static string Key(Adapter g,int index)
    {
        var c=g.Visible(index);
        string key=c.Kind+":"+c.Ordinal+":"+c.Low+":"+c.High+":"+c.Option?.Id+":"+c.Option?.DefId;
        if(c.Action!=null)
        {
            key+=":"+c.Action.GetType().Name;
            foreach(var f in GetFields(c.Action.GetType()))key+=":"+f.Name+"="+f.GetValue(c.Action);
            if(c.Action is ShardsBuyCardAction buy)key+=":"+g.Engine.State.CenterRow[buy.SlotIndex]?.DefId;
            if(c.Action is ShardsPlayCardAction play)key+=":"+g.Engine.State.FindCard(play.CardInstanceId)?.DefId;
        }
        return key;
    }
    private static void Advance(Adapter g,int action,int seat)
    {
        g.Step(action);
        // Never choose an opponent mistake: reveal every available defensive shield.
        for(int n=0;n<64&&!g.Engine.State.GameOver&&g.Actor!=seat&&g.Decision?.Context=="soi.shields";n++)
        {
            int shield=-1,finish=-1;
            for(int k=0;k<g.VisibleCount;k++){if(g.Visible(k).Kind==12)shield=k;if(g.Visible(k).Kind==13)finish=k;}
            if(shield>=0)g.Step(shield);else if(finish>=0)g.Step(finish);else break;
        }
    }
    // Integrity only: this hash never scores a node or enters the policy.
    internal static ulong Fingerprint(Adapter g)
    {
        var s=g.Engine.State;ulong h=s.ComputeHash();
        void Mix(ulong n){unchecked{h=(h^n)*1099511628211UL;}}
        void Scalars(object obj)
        {
            foreach(var f in GetFields(obj.GetType()))
            {
                if(!f.IsPublic)continue;
                object v=f.GetValue(obj);
                if(v is bool b)Mix(b?1UL:0UL);else if(v is int n)Mix(unchecked((ulong)n));
                else if(v is Enum e)Mix(Convert.ToUInt64(e));
            }
        }
        Scalars(s);Mix(s.Rng.State);Mix(s.Rng.Inc);Mix((ulong)g.Actor);
        foreach(var p in s.Players)
        {
            Scalars(p);
            foreach(var zone in new[]{p.Hand,p.Deck,p.Discard,p.PlayZone,p.Champions,p.Destinies,p.SetAside})
            {Mix((ulong)zone.Count);foreach(var c in zone)Scalars(c);}
        }
        foreach(var zone in new[]{s.CenterDeck,s.DestinyDeck,s.DestinyRow,s.ActiveMonsters,s.Banished})
        {Mix((ulong)zone.Count);foreach(var c in zone)Scalars(c);}
        return h;
    }
    internal static bool IsRelevant(Adapter source)
    {
        int seat=source.Actor;var s=source.Engine.State;
        return seat==s.TurnPlayerIndex && source.Decision?.Context!="soi.herodraft" &&
            (s.Players[seat].Mastery>=25||s.Players[1-seat].Health<=s.Players[seat].Power+20);
    }
    internal static int Find(Adapter source,int maxNodes=1536,int maxDepth=12,int worlds=4,Func<Adapter,Adapter> copier=null,bool diversify=false)
    {
        ulong hash=Fingerprint(source);long submissions=source.Submissions,steps=source.WrapperSteps;int log=source.Engine.Log.Count;
        try
        {
            Node tree=null;
            int fast=FindCore(source,maxNodes,maxDepth,worlds,12,copier,diversify,ref tree,out bool pruned);
            if(fast>=0||maxNodes<=384)return fast;
            // Without any pruning, a larger beam repeats the identical prefix
            // under the identical node budget. It cannot discover a new line.
            int broad=pruned?FindCore(source,maxNodes,maxDepth,worlds,64,copier,diversify,ref tree,out _):-1;
            if(broad>=0)return broad;
            // Information-ordering menus can leave many equal-valued prefixes.
            // Deepen only these rare menus instead of taxing every self-play step.
            string context=source.Decision?.Context;
            return context=="soi.scry"||context=="soi.reorder"
                ?FindCore(source,maxNodes*4,maxDepth,worlds,128,copier,diversify,ref tree,out _):-1;
        }
        finally{if(hash!=Fingerprint(source)||log!=source.Engine.Log.Count||submissions!=source.Submissions||steps!=source.WrapperSteps)throw new Exception("Planner mutated source state");}
    }
    private static int FindCore(Adapter source,int maxNodes,int maxDepth,int worlds,int beamWidth,Func<Adapter,Adapter> copier,bool diversify,ref Node tree,out bool pruned)
    {
        pruned=false;
        if(source.Actor!=source.Engine.State.TurnPlayerIndex||source.Decision?.Context=="soi.herodraft")return -1;
        Calls++;int seat=source.Actor;
        if(Plans.TryGetValue(source,out var saved))
        {
            Plans.Remove(source);
            if(saved.Step+1==source.WrapperSteps&&saved.Seat==seat&&saved.Round==source.Engine.State.Round&&saved.Path.Count>0&&Validate(source,saved.Path,0,worlds,copier))
            {
                for(int a=0;a<source.VisibleCount;a++)if(Key(source,a)==saved.Path[0])
                {
                    Save(source,saved.Path);Hits++;Continuations++;return a;
                }
            }
        }
        // Reuse exact action-prefix states between beam attempts. This is a
        // deterministic tree cache, not state-hash merging: distinct sequences
        // never alias. The logical expansion budget/order stays unchanged.
        if(tree==null)tree=new Node{Game=PublicWorld(source,713101,copier),Root=-1,Depth=0,Score=0,Path=new List<string>()};
        var frontier=new List<Node>{tree};
        var plans=new List<Node>();int budget=maxNodes;var rootCounts=new int[Encoder.MaxActions];
        while(frontier.Count>0&&budget>0)
        {
            int best=0;for(int i=1;i<frontier.Count;i++)
                if(frontier[i].Depth<frontier[best].Depth||(frontier[i].Depth==frontier[best].Depth&&frontier[i].Score>frontier[best].Score))best=i;
            var node=frontier[best];frontier.RemoveAt(best);var g=node.Game;
            if(node.Depth>=maxDepth||g.Engine.State.TurnPlayerIndex!=seat||g.Actor!=seat)continue;
            for(int a=0;a<g.VisibleCount&&budget>0;a++)
            {
                if(g.Visible(a).Kind==11||g.Visible(a).Kind==14)continue;
                int first=node.Root<0?a:node.Root;
                if(plans.Count(x=>x.Root==first)>=4)continue;
                if(node.Children==null)node.Children=new Node[g.VisibleCount];
                var next=node.Children[a];
                if(next==null)
                {
                    var created=(copier??Copy)(g);var sequence=new List<string>(node.Path){Key(g,a)};
                    Advance(created,a,seat);
                    next=new Node{Game=created,Root=first,Depth=node.Depth+1,Path=sequence,Score=Score(created,seat,node.Depth+1)};
                    node.Children[a]=next;
                }
                Nodes++;budget--;
                var child=next.Game;var path=next.Path;
                if(child.Engine.State.GameOver)
                {
                    if(child.Engine.State.WinnerIndex==seat)
                    {
                        if(Validate(source,path,1,worlds,copier)){Save(source,path);Hits++;return first;}
                        plans.Add(next);
                    }
                    continue;
                }
                if(child.Engine.State.TurnPlayerIndex!=seat||child.Actor!=seat)continue;
                // Replay hashes omit zone boundaries and continuation state.
                // Do not use them to merge search nodes (e.g. a tutored card can
                // otherwise collapse into the state where it is still in discard).
                frontier.Add(next);
                // Preserve stable OrderByDescending ties: the last inserted node
                // is dropped first on equal score. Reuse fixed counters instead of
                // sorting and allocating a dictionary for every generated child.
                Array.Clear(rootCounts,0,rootCounts.Length);
                int layerCount=0;
                foreach(var n in frontier)if(n.Depth==next.Depth){layerCount++;rootCounts[n.Root]++;}
                if(layerCount>beamWidth)
                {
                    pruned=true;
                    int drop=-1,duplicate=-1;
                    for(int k=0;k<frontier.Count;k++)
                    {
                        var n=frontier[k];if(n.Depth!=next.Depth)continue;
                        if(drop<0||n.Score<=frontier[drop].Score)drop=k;
                        if(rootCounts[n.Root]>1&&(duplicate<0||n.Score<=frontier[duplicate].Score))duplicate=k;
                    }
                    frontier.RemoveAt(diversify&&duplicate>=0?duplicate:drop);
                }
            }
        }

        return -1;
    }
    internal static bool IsWinningLine(Adapter source,List<string> path,Func<Adapter,Adapter> copier=null)=>
        source.Actor==source.Engine.State.TurnPlayerIndex&&Validate(source,path,0,4,copier);
    internal static int ImmediateWinningEnd(Adapter source,Func<Adapter,Adapter> copier)
    {
        var state=source.Engine.State;int seat=source.Actor;
        // Cheap public precondition; it is never itself a proof of lethal.
        if(source.Decision!=null||seat!=state.TurnPlayerIndex||state.GameOver||
            state.Players[seat].Power<state.Players[1-seat].Health)return -1;
        for(int a=0;a<source.VisibleCount;a++)if(source.Visible(a).Action is ShardsEndTurnAction)
            return AcceptWinningLine(source,new List<string>{Key(source,a)},copier)?a:-1;
        return -1;
    }
    internal static bool AcceptWinningLine(Adapter source,List<string> path,Func<Adapter,Adapter> copier=null)
    {
        if(!IsWinningLine(source,path,copier))return false;
        Save(source,path);return true;
    }
    internal static int SimplifyWinningPrefix(Adapter source,int first,Func<Adapter,Adapter> copier)
    {
        if(first<0||source.Decision!=null||!Plans.TryGetValue(source,out var saved)||
            saved.Step!=source.WrapperSteps||saved.Seat!=source.Actor||saved.Round!=source.Engine.State.Round)return first;
        var path=new List<string>{Key(source,first)};path.AddRange(saved.Path);
        bool changed=false;
        while(path.Count>1)
        {
            int action=-1;for(int a=0;a<source.VisibleCount;a++)if(Key(source,a)==path[0]){action=a;break;}
            if(action<0)break;
            if(NoEffectPlans.InactiveDestiny(source,action))
            {path.RemoveAt(0);changed=true;continue;}
            if(false&&source.Visible(action).Action is ShardsHeroAbilityAction&&
                source.Engine.State.Players[source.Actor].CharacterId=="kosynwu"&&path.Count>2&&
                path[1].StartsWith("13:",StringComparison.Ordinal))
            {path.RemoveRange(0,2);changed=true;continue;}
            break;
        }
        if(!changed)return first;
        int next=-1;for(int a=0;a<source.VisibleCount;a++)if(Key(source,a)==path[0]){next=a;break;}
        // Revalidate the complete shortened sequence, including public-possible
        // shields. Never discard a winning line merely to reduce action count.
        return next>=0&&AcceptWinningLine(source,path,copier)?next:first;
    }
    internal static int SimplifyPaidWinningPrefix(Adapter source,int first,Func<Adapter,Adapter> copier)
    {
        if(first<0||source.Decision!=null||!(source.Visible(first).Action is ShardsHeroAbilityAction)||
            ShardsEngine.HeroAbilityInfo(source.Engine.State.Players[source.Actor].CharacterId).Health<=0||
            !Plans.TryGetValue(source,out var saved)||saved.Step!=source.WrapperSteps||
            saved.Seat!=source.Actor||saved.Round!=source.Engine.State.Round)return first;
        // Try deleting the paid activation and its immediate answers. This is
        // a candidate rewrite, never a claim that a real banish is a no-op.
        var original=saved.Path;int skip=0;
        while(skip<original.Count&&(original[skip].StartsWith("12:",StringComparison.Ordinal)||original[skip].StartsWith("13:",StringComparison.Ordinal)))skip++;
        if(skip==original.Count)return first;
        var probe=PublicWorld(source,713101,copier);var rewritten=new List<string>();int extraAnswers=0;
        for(int i=skip;i<original.Count&&!probe.Engine.State.GameOver;i++)
        {
            if(probe.Actor!=source.Actor)return first;
            string wanted=original[i];int action=-1;
            for(int a=0;a<probe.VisibleCount;a++)if(Key(probe,a)==wanted){action=a;break;}
            if(action<0&&wanted.StartsWith("12:",StringComparison.Ordinal))
            {
                // Removing a card changes menu ordinals. Preserve the exact
                // option identity, definition and range, not the old position.
                var fields=wanted.Split(new[]{':'},6);
                for(int a=0;a<probe.VisibleCount;a++)
                {
                    var current=Key(probe,a).Split(new[]{':'},6);
                    if(current.Length==6&&fields.Length==6&&current[0]==fields[0]&&current[2]==fields[2]&&
                        current[3]==fields[3]&&current[4]==fields[4]&&current[5]==fields[5])
                    {if(action>=0)return first;action=a;}
                }
            }
            if(action<0&&probe.Decision?.Context=="soi.reveal"&&!wanted.StartsWith("12:",StringComparison.Ordinal)&&extraAnswers<8)
            {
                // Keeping the banished card can leave an additional optional
                // hand-reveal menu. Decline that extra reveal, then retry the
                // same planned action. Other unknown menus abort the rewrite.
                for(int a=0;a<probe.VisibleCount;a++)if(probe.Visible(a).Kind==13){action=a;break;}
                if(action>=0){i--;extraAnswers++;}
            }
            if(action<0)return first;
            rewritten.Add(Key(probe,action));Advance(probe,action,source.Actor);
        }
        if(!probe.Engine.State.GameOver||probe.Engine.State.WinnerIndex!=source.Actor||rewritten.Count==0)return first;
        int next=-1;for(int a=0;a<source.VisibleCount;a++)if(Key(source,a)==rewritten[0]){next=a;break;}
        // One fixed rewritten sequence must win across all verification worlds
        // and the defensive shield world. Never substitute world-specific plans.
        return next>=0&&AcceptWinningLine(source,rewritten,copier)?next:first;
    }
    internal static void TransferPlan(Adapter from, Adapter to)
    {
        if(from!=null&&Plans.TryGetValue(from,out var plan))
        {Plans.Remove(to);Plans.Add(to,plan);}
    }
    private static void Save(Adapter source,List<string> path)
    {
        Plans.Remove(source);
        Plans.Add(source,new SavedPlan{Path=path.Skip(1).ToList(),Step=source.WrapperSteps,Seat=source.Actor,Round=source.Engine.State.Round});
    }
    // The identical sequence must win in every sampled world, without replanning.
    // Also challenge the line with maximum public-possible hand shields. Other
    // hidden draws/reveals remain sampled; this is not a general forced-mate proof.
    private static bool Validate(Adapter source,List<string> path,int firstWorld,int worlds,Func<Adapter,Adapter> copier)
    {
        int seat=source.Actor;
        for(int w=firstWorld;w<=worlds;w++)
        {
            var g=w==worlds?DefensiveWorld(source,713101,copier):PublicWorld(source,713101+w*7919,copier);
            foreach(string key in path)
            {
                if(g.Engine.State.GameOver)break;
                if(g.Actor!=seat)return false;
                int action=-1;for(int a=0;a<g.VisibleCount;a++)if(Key(g,a)==key){action=a;break;}
                if(action<0)return false;
                Advance(g,action,seat);Nodes++;
            }
            if(!g.Engine.State.GameOver||g.Engine.State.WinnerIndex!=seat)return false;
        }
        return true;
    }

}
}
