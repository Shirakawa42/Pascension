// Diagnostic port of the deployed planner to the full-information adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shards.Engine;

using PolicySearchSettings = Shards.AI.PolicySearchSettings;

namespace Shards.ZeroDepth
{
// Explicitly evaluates an activation together with its first visible choice.
// Hidden reveals/scry and opponent responses are excluded. Every probe starts
// from a sampled public world, never from the live hidden state.
internal static class MenuPlanning
{
    internal static bool VisibleMenu(Adapter g)
    {
        if(g.Actor!=g.Engine.State.TurnPlayerIndex||g.Decision==null)return false;
        string context=g.Decision.Context;
        if(context is "soi.volos" or "soi.mode" or "soi.banish")return true;
        // Longshot also uses soi.warp, but its newly revealed cards are not
        // market slots. Keep those choices outside committed root plans.
        return (context is "soi.warp" or "soi.removeshop")&&g.Decision.Options.All(o=>
            g.Engine.State.CenterRow.Any(c=>c!=null&&c.InstanceId==o.CardInstanceId));
    }

    sealed class Probe
    {
        internal HybridLookahead.Option Option;
        internal Adapter Game;
        internal long FirstSubmissions;
        internal HashSet<int> VisibleIds;
    }
    internal static void Expand(Adapter root,List<HybridLookahead.Option> options,Func<Adapter,Adapter> copy,Func<Adapter[],Prediction[]> infer,bool pruneNoEffect=false)=>
        Expand(new[]{root},new[]{options},copy,infer,1,pruneNoEffect);

    internal static void Expand(Adapter[] roots,List<HybridLookahead.Option>[] options,Func<Adapter,Adapter> copy,Func<Adapter[],Prediction[]> infer,int workers,bool pruneNoEffect=false)
    {
        var probes=new List<Probe>[roots.Length];
        Parallel.For(0,roots.Length,new ParallelOptions{MaxDegreeOfParallelism=workers},i=>
        {if(options[i]!=null)probes[i]=Collect(roots[i],options[i],copy);});
        var flat=probes.Where(xs=>xs!=null).SelectMany(xs=>xs).ToArray();if(flat.Length==0)return;
        // All independent live games share one inference batch. Calling the GPU
        // separately for each root makes small menus needlessly synchronization-bound.
        var predictions=infer(flat.Select(x=>x.Game).ToArray());int index=0;
        for(int root=0;root<roots.Length;root++)
        {
            if(probes[root]==null)continue;int added=0;
            foreach(var item in probes[root])
            {
                var prediction=predictions[index++];var probe=item.Game;var option=item.Option;
                bool optional=Enumerable.Range(0,probe.VisibleCount).Any(a=>probe.Visible(a).Kind==13);
                int coveredTargets=0;
                for(int a=0;a<probe.VisibleCount&&added<32;a++)
                {
                    var next=probe.Visible(a);
                    if(next.Kind!=12&&next.Kind!=13)continue;
                    if(next.Kind==12&&probe.Decision.Context is not "soi.volos" and not "soi.mode"&&
                        (next.Option==null||!item.VisibleIds.Contains(next.Option.CardInstanceId)))continue;
                    options[root].Add(new HybridLookahead.Option{First=option.First,Keys=new[]{option.Keys[0],TacticalSearch.Key(probe,a)},
                        Probability=option.Probability,ChoiceProbability=prediction.P[a],ChoiceUncertainty=HybridLookahead.ChoiceUncertainty(prediction.V),
                        OptionalMenu=optional,MenuPlan=true,FirstSubmissions=item.FirstSubmissions});
                    added++;
                    if(next.Kind==12)coveredTargets++;
                }
                // Sacrifice is an unsubmitted, actor-visible preview. Canceling
                // it has no engine effect. Once every actual target is present,
                // ordinary root actions already represent not activating it.
                // Keep the primitive when paging/budgets prevented coverage.
                if(pruneNoEffect&&false&&item.FirstSubmissions==0&&
                    coveredTargets>0&&coveredTargets==Enumerable.Range(0,probe.VisibleCount).Count(a=>probe.Visible(a).Kind==12)&&
                    options[root].Any(o=>o.First!=option.First))
                    options[root].RemoveAll(o=>o.First==option.First&&(o.Keys.Length==1||
                        o.MenuPlan&&o.Keys.Length==2&&o.Keys[1].StartsWith("13:",StringComparison.Ordinal)));
            }
        }
    }
    static List<Probe> Collect(Adapter root,List<HybridLookahead.Option> options,Func<Adapter,Adapter> copy)
    {
        var probes=new List<Probe>();if(root.Decision!=null)return probes;
        Adapter template=null;var original=options.ToArray();
        var visibleIds=new HashSet<int>();
        foreach(var p in root.Engine.State.Players)
        {
            foreach(var card in p.Discard.Concat(p.PlayZone).Concat(p.Champions).Concat(p.Destinies))visibleIds.Add(card.InstanceId);
            if(p.Index==root.Actor)foreach(var card in p.Hand)visibleIds.Add(card.InstanceId);
        }
        foreach(var card in root.Engine.State.CenterRow)if(card!=null)visibleIds.Add(card.InstanceId);
        foreach(var option in original)
        {
            if(option.Keys.Length!=1||HybridLookahead.ResourceCard(root,option.First))continue;
            var action=root.Visible(option.First).Action;
            if(!(action is ShardsPlayCardAction)&&!(action is ShardsExhaustAction)&&!(action is ShardsHeroAbilityAction))continue;
            template??=TacticalSearch.PublicWorld(root,713101,copy);
            var probe=copy(template);int first=-1;
            for(int a=0;a<probe.VisibleCount;a++)if(TacticalSearch.Key(probe,a)==option.Keys[0]){first=a;break;}
            if(first<0)continue;
            long submissions=probe.Submissions;int log=probe.Engine.Log.Count;probe.Step(first);
            if(probe.Engine.State.GameOver||probe.Actor!=root.Actor||!VisibleMenu(probe))continue;
            bool revealed=false;
            for(int i=log;i<probe.Engine.Log.Count;i++)
                if(probe.Engine.Log[i] is ShardsCardDrawnEvent or ShardsCardsRevealedEvent or ShardsMonsterRevealedEvent or ShardsDeckShuffledEvent){revealed=true;break;}
            if(revealed)continue;
            probes.Add(new Probe{Option=option,Game=probe,FirstSubmissions=probe.Submissions-submissions,VisibleIds=visibleIds});
        }
        return probes;
    }
}
}
