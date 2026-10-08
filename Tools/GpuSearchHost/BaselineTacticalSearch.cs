// Frozen 2026-09-27 pre-optimization reference. Verification only; never used by match policies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Shards.Engine;

// Bounded, same-turn tactical search over the real rules. Hidden state is replaced
// by public-information samples before expansion. Only a common winning sequence
// validated across all sampled worlds overrides the network; no value heuristic
// alone may change an action. Sampling is approximate, not a proof of forced mate.
namespace Shards.AI
{
internal static class BaselineTacticalSearch
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
    private static FieldInfo[] GetFields(Type t)
    {
        if(Fields.TryGetValue(t,out var result))return result;
        var list=new List<FieldInfo>();
        for(var c=t;c!=null;c=c.BaseType)list.AddRange(c.GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly));
        return Fields[t]=list.ToArray();
    }
    internal static Adapter Copy(Adapter source)
    {
        var map=new Dictionary<object,object>(new Identity());
        object Clone(object x)
        {
            if(x==null)return null;
            var t=x.GetType();
            if(t.IsPrimitive||t.IsEnum||x is string||x is decimal||x is Type||x is MemberInfo||x is ShardsCardDef||t.FullName.Contains("Comparer"))return x;
            if(map.TryGetValue(x,out var seen))return seen;
            if(x is EventLog){var log=new EventLog();map[x]=log;return log;}
            if(x is Delegate d)
            {
                if(d.Target==null)return x;
                var target=Clone(d.Target);var copy=Delegate.CreateDelegate(t,target,d.Method);map[x]=copy;return copy;
            }
            if(x is Array arr)
            {
                var copy=(Array)arr.Clone();map[x]=copy;
                if(!t.GetElementType().IsPrimitive&&!t.GetElementType().IsEnum)
                    for(int i=0;i<arr.Length;i++)copy.SetValue(Clone(arr.GetValue(i)),i);
                return copy;
            }
            var obj=Memberwise.Invoke(x,null);map[x]=obj;
            if(x is ShardsCard||x is DeterministicRng)return obj;
            foreach(var f in GetFields(t))
            {
                if(t==typeof(Adapter)&&f.Name=="SubmitThroughHost"){f.SetValue(obj,null);continue;}
                if(t==typeof(ShardsState)&&f.Name=="_cardIndex"){f.SetValue(obj,null);continue;}
                if(!f.FieldType.IsPrimitive&&!f.FieldType.IsEnum)f.SetValue(obj,Clone(f.GetValue(x)));
            }
            return obj;
        }
        var g=(Adapter)Clone(source);
        g.Engine.State.InvalidateCardIndex();
        g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new InvalidOperationException(r.Error);};
        return g;
    }
    internal static Adapter PublicWorld(Adapter source,int seed,Func<Adapter,Adapter> copier=null)
    {
        var g=(copier??Copy)(source);int seat=source.Actor;var s=g.Engine.State;
        var rng=new DeterministicRng((ulong)seed);s.Rng=rng;
        // Only unordered composition of a personal deck is public.
        void Shuffle(List<ShardsCard> cards,IReadOnlyList<string> known)
        {
            cards.Sort((a,b)=>{int c=StringComparer.Ordinal.Compare(a.DefId,b.DefId);return c!=0?c:a.InstanceId.CompareTo(b.InstanceId);});
            rng.Shuffle(cards);
            for(int i=0;i<known.Count&&i<cards.Count;i++)
            {
                int end=cards.Count-1-i;int at=cards.FindIndex(0,end+1,c=>c.DefId==known[i]);
                if(at>=0)(cards[at],cards[end])=(cards[end],cards[at]);
            }
        }
        Shuffle(s.Players[seat].Deck,g.Supplement.Top(seat));
        var enemy=s.Players[1-seat];int hand=enemy.Hand.Count;
        var hidden=enemy.Hand.Concat(enemy.Deck).OrderBy(c=>c.DefId,StringComparer.Ordinal).ThenBy(c=>c.InstanceId).ToList();
        rng.Shuffle(hidden);enemy.Hand.Clear();enemy.Deck.Clear();
        for(int i=0;i<hidden.Count;i++){var c=hidden[i];c.Zone=i<hand?ShardsZone.Hand:ShardsZone.Deck;(i<hand?enemy.Hand:enemy.Deck).Add(c);}
        ((List<string>)g.Supplement.Top(1-seat)).Clear();((List<string>)g.Knowledge.For(1-seat)).Clear();
        var counts=g.Engine.InitialCardCounts();
        var monsters=ShardsCardDatabase.All.Where(d=>d.IsMonster&&d.Set=="into_the_horizon").Select(d=>d.Id).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        foreach(var player in s.Players.Where(p=>p.DoomGateFloodUsed))
            for(int i=0;i<25;i++){string id=monsters[i%monsters.Length];counts.TryGetValue(id,out int n);counts[id]=n+1;}
        // Subtract visible zones and both public complete player collections. Never inspect
        // actual center/destiny identities to construct the sampling distribution.
        void Remove(ShardsCard c){if(c!=null&&counts.ContainsKey(c.DefId))counts[c.DefId]--;}
        foreach(var p in s.Players)
            foreach(var c in p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone).Concat(p.Champions).Concat(p.SetAside).Concat(p.Destinies))Remove(c);
        foreach(var c in s.CenterRow.Concat(s.ActiveMonsters).Concat(s.Banished).Concat(s.DestinyRow))Remove(c);
        var center=counts.Where(kv=>kv.Value>0&&ShardsCardDatabase.Get(kv.Key).Type!=ShardsCardType.Destiny&&ShardsCardDatabase.Get(kv.Key).Type!=ShardsCardType.Starter&&ShardsCardDatabase.Get(kv.Key).Type!=ShardsCardType.Relic).SelectMany(kv=>Enumerable.Repeat(kv.Key,kv.Value)).OrderBy(x=>x,StringComparer.Ordinal).ToList();
        var destiny=counts.Where(kv=>kv.Value>0&&ShardsCardDatabase.Get(kv.Key).Type==ShardsCardType.Destiny).SelectMany(kv=>Enumerable.Repeat(kv.Key,kv.Value)).OrderBy(x=>x,StringComparer.Ordinal).ToList();
        void Replace(List<ShardsCard> cards,List<string> pool,IReadOnlyList<string> known)
        {
            foreach(string id in known)pool.Remove(id);
            rng.Shuffle(pool);
            for(int i=0;i<cards.Count;i++)
            {
                int top=cards.Count-1-i;
                // Keep already revealed objects/IDs: paused scry continuations reference them.
                cards[i].DefId=top<known.Count?known[top]:pool.Count>0?pool[i%pool.Count]:"crystal";
                if(top>=known.Count){cards[i].InstanceId=100000+i+(cards==s.DestinyDeck?10000:0);cards[i].Owner=-1;
                    cards[i].Exhausted=false;cards[i].FastPlayed=false;cards[i].BanishAtCleanup=false;cards[i].DamageThisTurn=0;}
            }
        }
        Replace(s.CenterDeck,center,g.Knowledge.For(seat));Replace(s.DestinyDeck,destiny,Array.Empty<string>());
        s.NextInstanceId=200000;s.InvalidateCardIndex();
        return g;
    }
    private sealed class Node {internal Adapter Game;internal int Root,Depth;internal double Score;internal List<string> Path;}
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
            int fast=FindCore(source,maxNodes,maxDepth,worlds,12,copier,diversify);
            if(fast>=0||maxNodes<=384)return fast;
            int broad=FindCore(source,maxNodes,maxDepth,worlds,64,copier,diversify);
            if(broad>=0)return broad;
            // Information-ordering menus can leave many equal-valued prefixes.
            // Deepen only these rare menus instead of taxing every self-play step.
            string context=source.Decision?.Context;
            return context=="soi.scry"||context=="soi.reorder"
                ?FindCore(source,maxNodes*4,maxDepth,worlds,128,copier,diversify):-1;
        }
        finally{if(hash!=Fingerprint(source)||log!=source.Engine.Log.Count||submissions!=source.Submissions||steps!=source.WrapperSteps)throw new Exception("Planner mutated source state");}
    }
    private static int FindCore(Adapter source,int maxNodes,int maxDepth,int worlds,int beamWidth,Func<Adapter,Adapter> copier,bool diversify)
    {
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
        var root=PublicWorld(source,713101,copier);
        var frontier=new List<Node>{new(){Game=root,Root=-1,Depth=0,Score=0,Path=new List<string>()}};
        var plans=new List<Node>();int budget=maxNodes;
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
                var child=(copier??Copy)(g);var path=new List<string>(node.Path){Key(g,a)};
                Advance(child,a,seat);Nodes++;budget--;
                var next=new Node{Game=child,Root=first,Depth=node.Depth+1,Path=path,Score=Score(child,seat,node.Depth+1)};
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
                var layer=frontier.Where(x=>x.Depth==next.Depth).OrderByDescending(x=>x.Score).ToArray();
                if(layer.Length>beamWidth)
                {
                    // Protect different first moves from being eliminated solely
                    // because another move produces resources earlier in the line.
                    var drop=layer[layer.Length-1];
                    if(diversify)
                    {
                        var counts=layer.GroupBy(n=>n.Root).ToDictionary(x=>x.Key,x=>x.Count());
                        drop=layer.Reverse().FirstOrDefault(n=>counts[n.Root]>1)??drop;
                    }
                    frontier.Remove(drop);
                }
            }
        }

        return -1;
    }
    internal static bool AcceptWinningLine(Adapter source,List<string> path,Func<Adapter,Adapter> copier=null)
    {
        if(source.Actor!=source.Engine.State.TurnPlayerIndex||!Validate(source,path,0,4,copier))return false;
        Save(source,path);return true;
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
    // This remains a sampled check, not a mathematical guarantee.
    private static bool Validate(Adapter source,List<string> path,int firstWorld,int worlds,Func<Adapter,Adapter> copier)
    {
        int seat=source.Actor;
        for(int w=firstWorld;w<worlds;w++)
        {
            var g=PublicWorld(source,713101+w*7919,copier);
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
