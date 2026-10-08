using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Pascension.Engine.Core;
using Pascension.Engine.Events;
using Pascension.Engine.Decisions;
using Shards.AI;
using Shards.Engine;

// Optional headless JIT copier. Shares the typed-container/identity-table
// optimizations with TacticalSearch.Copy; compiles access to remaining fields.
// Readonly references keep reflection setters. Audited against the frozen copier.
internal static class FastCopy
{
    sealed class Identity:IEqualityComparer<object>
    {public new bool Equals(object a,object b)=>ReferenceEquals(a,b);public int GetHashCode(object a)=>RuntimeHelpers.GetHashCode(a);}
    static readonly ConcurrentDictionary<Type,Action<object,object,Func<object,object>>> Visitors=new();
    static readonly MethodInfo Memberwise=typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic);
    static readonly Func<object,object> Shallow=MakeShallow();
    static Func<object,object> MakeShallow()
    {var p=Expression.Parameter(typeof(object));return Expression.Lambda<Func<object,object>>(Expression.Call(p,Memberwise),p).Compile();}
    static Action<object,object,Func<object,object>> Visitor(Type type)
    {
        var source=Expression.Parameter(typeof(object));var target=Expression.Parameter(typeof(object));var clone=Expression.Parameter(typeof(Func<object,object>));
        var body=new List<Expression>();
        for(var t=type;t!=null;t=t.BaseType)foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
        {
            if(f.FieldType.IsPrimitive||f.FieldType.IsEnum||f.FieldType==typeof(string))continue;
            Expression value;
            if((type==typeof(Adapter)&&f.Name=="SubmitThroughHost")||(type==typeof(ShardsState)&&f.Name=="_cardIndex"))value=Expression.Constant(null,typeof(object));
            else value=Expression.Invoke(clone,Expression.Convert(Expression.Field(Expression.Convert(source,t),f),typeof(object)));
            if(f.IsInitOnly||type.IsValueType)
                body.Add(Expression.Call(Expression.Constant(f),typeof(FieldInfo).GetMethod("SetValue",new[]{typeof(object),typeof(object)}),target,value));
            else body.Add(Expression.Assign(Expression.Field(Expression.Convert(target,t),f),Expression.Convert(value,f.FieldType)));
        }
        body.Add(Expression.Empty());
        return Expression.Lambda<Action<object,object,Func<object,object>>>(Expression.Block(body),source,target,clone).Compile();
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
            Visitors.GetOrAdd(t,Visitor)(x,obj,Clone);
            return obj;
        }
        Adapter g;
        try {g=(Adapter)Clone(source);} finally {map.Clear();}
        g.Engine.State.InvalidateCardIndex();
        g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new InvalidOperationException(r.Error);};
        return g;
    }
}
