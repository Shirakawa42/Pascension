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
namespace Shards.AI
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
    private static class ListVersion<T>
    {
        internal static readonly FieldInfo Field=typeof(List<T>).GetField("_version",BindingFlags.Instance|BindingFlags.NonPublic);
        internal static List<T> Preserve(List<T> source,List<T> target)
        {Field.SetValue(target,Field.GetValue(source));return target;}
    }
    [ThreadStatic] private static Dictionary<object,object> copyMap;
    internal static Adapter Copy(Adapter source)
    {
        // Copies are synchronous; no user code is invoked while visiting fields.
        // Reuse only the identity table, never cloned state or mutable game objects.
        var map=copyMap??(copyMap=new Dictionary<object,object>(512,new Identity()));
        map.Clear();
        object Clone(object x)
        {
            if(x==null)return null;
            // Frequent graph leaves and containers avoid reflection/boxed array access.
            // Register every mutable object before walking it: iterator closures can
            // refer to the same cards/lists as the board and must keep those aliases.
            if(x is string||x is ShardsCardDef)return x;
            if(map.TryGetValue(x,out var known))return known;
            if(x is ShardsCard card)
            {
                var c=new ShardsCard{InstanceId=card.InstanceId,DefId=card.DefId,Owner=card.Owner,Zone=card.Zone,
                    Exhausted=card.Exhausted,FastPlayed=card.FastPlayed,DamageThisTurn=card.DamageThisTurn,BanishAtCleanup=card.BanishAtCleanup};
                map[x]=c;return c;
            }
            if(x is List<ShardsCard> cards)
            {var c=new List<ShardsCard>(cards.Count);map[x]=c;foreach(var v in cards)c.Add((ShardsCard)Clone(v));return ListVersion<ShardsCard>.Preserve(cards,c);}
            if(x is List<int> ints){var c=new List<int>(ints);map[x]=c;return ListVersion<int>.Preserve(ints,c);}
            if(x is List<string> strings){var c=new List<string>(strings);map[x]=c;return ListVersion<string>.Preserve(strings,c);}
            if(x is List<DecisionOption> options)
            {var c=new List<DecisionOption>(options.Count);map[x]=c;foreach(var v in options)c.Add((DecisionOption)Clone(v));return ListVersion<DecisionOption>.Preserve(options,c);}
            if(x is List<Candidate> candidates)
            {
                var c=new List<Candidate>(candidates.Count);map[x]=c;
                foreach(var v in candidates){var n=v;n.Action=(Pascension.Engine.Actions.PlayerAction)Clone(v.Action);n.Option=(DecisionOption)Clone(v.Option);c.Add(n);}return ListVersion<Candidate>.Preserve(candidates,c);
            }
            var t=x.GetType();
            if(t.IsPrimitive||t.IsEnum||x is string||x is decimal||x is Type||x is MemberInfo||x is ShardsCardDef||t.FullName.Contains("Comparer"))return x;
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
            var obj=Shallow(x);map[x]=obj;
            if(x is ShardsCard||x is DeterministicRng)return obj;
            foreach(var f in CopyFields.GetOrAdd(t,type=>GetFields(type).Where(f=>!f.FieldType.IsPrimitive&&!f.FieldType.IsEnum&&f.FieldType!=typeof(string)).ToArray()))
            {
                if(t==typeof(Adapter)&&f.Name=="SubmitThroughHost"){f.SetValue(obj,null);continue;}
                if(t==typeof(ShardsState)&&f.Name=="_cardIndex"){f.SetValue(obj,null);continue;}
                if(!f.FieldType.IsPrimitive&&!f.FieldType.IsEnum)f.SetValue(obj,Clone(f.GetValue(x)));
            }
            return obj;
        }
        Adapter g;
        try {g=(Adapter)Clone(source);} finally {map.Clear();}
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
        rng.Shuffle(hidden);
        // Supplement deck tops come exclusively from public reveals/returns.
        // Reserve them before sampling the opponent's hidden hand, and retain
        // their order. Private center Scry knowledge remains unavailable.
        var knownEnemy=g.Supplement.Top(1-seat);var reserved=new List<ShardsCard>(knownEnemy.Count);
        if(knownEnemy.Count>enemy.Deck.Count)throw new InvalidOperationException("Known enemy top exceeds public deck size");
        foreach(string id in knownEnemy)
        {
            int at=hidden.FindIndex(c=>c.DefId==id);
            if(at<0)throw new InvalidOperationException("Known enemy top absent from public inventory");
            reserved.Add(hidden[at]);hidden.RemoveAt(at);
        }
        var knownHand=g.Supplement.Hand(1-seat);var held=new List<ShardsCard>(knownHand.Count);
        if(knownHand.Count>hand)throw new InvalidOperationException("Known enemy hand exceeds public hand size");
        foreach(string id in knownHand)
        {
            int at=hidden.FindIndex(c=>c.DefId==id);
            if(at<0)throw new InvalidOperationException("Known enemy hand absent from public inventory");
            held.Add(hidden[at]);hidden.RemoveAt(at);
        }
        enemy.Hand.Clear();enemy.Deck.Clear();
        foreach(var c in held){c.Zone=ShardsZone.Hand;enemy.Hand.Add(c);}
        int unknownHand=hand-held.Count;
        for(int i=0;i<hidden.Count;i++){var c=hidden[i];c.Zone=i<unknownHand?ShardsZone.Hand:ShardsZone.Deck;(i<unknownHand?enemy.Hand:enemy.Deck).Add(c);}
        for(int i=reserved.Count-1;i>=0;i--){reserved[i].Zone=ShardsZone.Deck;enemy.Deck.Add(reserved[i]);}
        ((List<string>)g.Knowledge.For(1-seat)).Clear();
        var counts=g.Engine.InitialCardCounts();
        var monsters=ShardsCardDatabase.All.Where(d=>d.IsMonster&&d.Set=="into_the_horizon").Select(d=>d.Id).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        foreach(var player in s.Players.Where(p=>p.DoomGateFloodUsed))
            for(int i=0;i<25;i++){string id=monsters[i%monsters.Length];counts.TryGetValue(id,out int n);counts[id]=n+1;}
        // Subtract visible zones and both public complete player collections. Never inspect
        // actual center/destiny identities to construct the sampling distribution.
        var reservedIds=new HashSet<int>();
        void Remove(ShardsCard c)
        {
            if(c==null)return;
            reservedIds.Add(c.InstanceId);
            if(counts.ContainsKey(c.DefId))counts[c.DefId]--;
        }
        foreach(var p in s.Players)
            foreach(var c in p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone).Concat(p.Champions).Concat(p.SetAside).Concat(p.Destinies))Remove(c);
        foreach(var c in s.CenterRow.Concat(s.ActiveMonsters).Concat(s.Banished).Concat(s.DestinyRow))Remove(c);
        // Nested public sampling can encounter cards already given synthetic
        // IDs by an earlier sample. Preserve every publicly retained identity,
        // including revealed center cards and cards held by pending decisions.
        foreach(var card in s.CenterDeck.Skip(Math.Max(0,s.CenterDeck.Count-g.Knowledge.For(seat).Count)))reservedIds.Add(card.InstanceId);
        if(g.Decision!=null)foreach(var option in g.Decision.Options)
        {
            if(option.CardInstanceId>=0)reservedIds.Add(option.CardInstanceId);
            if(option.Id>=0)reservedIds.Add(option.Id);
        }
        int Allocate(int preferred)
        {
            while(!reservedIds.Add(preferred))preferred=checked(preferred+1);
            return preferred;
        }
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
                if(top>=known.Count){cards[i].InstanceId=Allocate(100000+i+(cards==s.DestinyDeck?10000:0));cards[i].Owner=-1;
                    cards[i].Exhausted=false;cards[i].FastPlayed=false;cards[i].BanishAtCleanup=false;cards[i].DamageThisTurn=0;}
            }
        }
        Replace(s.CenterDeck,center,g.Knowledge.For(seat));Replace(s.DestinyDeck,destiny,Array.Empty<string>());
        s.NextInstanceId=Math.Max(s.NextInstanceId,Math.Max(200000,reservedIds.Count==0?0:checked(reservedIds.Max()+1)));s.InvalidateCardIndex();
        return g;
    }
    // A public-information stress case, not an extra probability sample. Hidden
    // hand/deck composition is public, but its partition is not. Reserve known
    // deck tops, then give the defender the strongest legal shield hand. Never
    // inspect which allocation is actually held by the live opponent.
    internal static Adapter DefensiveWorld(Adapter source,int seed=713101,Func<Adapter,Adapter> copier=null)
    {
        var g=PublicWorld(source,seed,copier);var enemy=g.Engine.State.Players[1-source.Actor];
        int count=enemy.Hand.Count,known=g.Supplement.Top(enemy.Index).Count;
        var reserved=enemy.Deck.Skip(enemy.Deck.Count-known).ToArray();
        var pool=enemy.Hand.Concat(enemy.Deck.Take(enemy.Deck.Count-known)).ToList();
        var held=new List<ShardsCard>();
        foreach(string id in g.Supplement.Hand(enemy.Index))
        {
            int at=pool.FindIndex(c=>c.DefId==id);
            if(at<0)throw new InvalidOperationException("Known hand missing in defensive world");
            held.Add(pool[at]);pool.RemoveAt(at);
        }
        var hand=held.Concat(pool.OrderByDescending(c=>c.Def.ShieldInPlay?0:g.Engine.ShieldValue(enemy,c))
            .ThenBy(c=>c.DefId,StringComparer.Ordinal).ThenBy(c=>c.InstanceId).Take(count-held.Count)).ToArray();
        var ids=new HashSet<int>(hand.Select(c=>c.InstanceId));
        enemy.Hand.Clear();enemy.Deck.Clear();
        foreach(var card in hand){card.Zone=ShardsZone.Hand;enemy.Hand.Add(card);}
        foreach(var card in pool.Where(c=>!ids.Contains(c.InstanceId)).Concat(reserved)){card.Zone=ShardsZone.Deck;enemy.Deck.Add(card);}
        g.Engine.State.InvalidateCardIndex();return g;
    }
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
            if(source.HeroFixEnabled&&source.Visible(action).Action is ShardsHeroAbilityAction&&
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
