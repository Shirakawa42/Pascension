using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

namespace Shards.Nyou
{
    // Public-information proposals only. The live actor replans after the reroll;
    // these two-action prefixes prevent a collapsed prior from hiding a known buy.
    internal static class MarketPlanning
    {
        internal static readonly bool Enabled=false;
        internal static void Expand(Adapter root,List<HybridLookahead.Option> options,Prediction prediction,Func<Adapter,Adapter> copy)
        {
            if(!Enabled||root.Decision!=null||root.Actor!=root.Engine.State.TurnPlayerIndex||
                ShardsEngine.RerollCost(root.Engine.State.Players[root.Actor])!=0)return;
            var known=root.Knowledge.Center[root.Actor].FirstOrDefault(f=>f.Min==0&&f.Max==0&&!f.UncertainPresence);
            // A monster is revealed into its own zone and refill continues. Its
            // successor may still be unknown, so this narrow acquisition proposal
            // must not treat the monster as an ordinary row card.
            if(known==null||ShardsCardDatabase.Get(known.DefId).IsMonster)return;
            Adapter template=null;
            for(int a=0;a<root.VisibleCount;a++)
            {
                if(!(root.Visible(a).Action is ShardsRerollRowAction reroll))continue;
                template??=TacticalSearch.PublicWorld(root,713101,copy);
                var probe=copy(template);string first=TacticalSearch.Key(root,a);
                int match=-1;for(int i=0;i<probe.VisibleCount;i++)if(TacticalSearch.Key(probe,i)==first){match=i;break;}
                if(match<0)throw new InvalidOperationException("Public-world copy lost legal reroll");
                probe.Step(match);
                if(probe.Engine.State.GameOver||probe.Decision!=null||probe.Actor!=root.Actor)continue;
                var revealed=probe.Engine.State.CenterRow[reroll.SlotIndex];
                if(revealed==null||revealed.InstanceId!=known.InstanceId||revealed.DefId!=known.DefId)
                    throw new InvalidOperationException("Remembered top card disagrees with reroll outcome");
                for(int b=0;b<probe.VisibleCount;b++)
                {
                    if(!(probe.Visible(b).Action is ShardsBuyCardAction buy)||buy.SlotIndex!=reroll.SlotIndex)continue;
                    options.Add(new HybridLookahead.Option{First=a,Keys=new[]{first,TacticalSearch.Key(probe,b)},
                        Probability=prediction.P[a],SetupPlan=true});
                }
            }
        }
    }
}
