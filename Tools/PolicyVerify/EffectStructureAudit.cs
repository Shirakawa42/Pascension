using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Shards.Engine;
using Shards.Content;

internal static class EffectStructureAudit
{
    internal static void Run(string output)
    {
        // Pure synthetic effect programs: no registered card definition is edited.
        object Compare(string name,IShardsEffect a,IShardsEffect b,int mastery)
        {
            float[] Describe(IShardsEffect effect)=>Shards.Preflight.EffectDescriptors.BuildCard(new ShardsCardDef{Id="diagnostic_only",Type=ShardsCardType.Ally,PlayEffect=effect});
            int Resolve(IShardsEffect effect)
            {
                var g=RezAudit.Game(mastery);var ctx=new ShardsContext{Engine=g.Engine,ControllerIndex=0};
                foreach(var step in effect.Resolve(ctx))throw new Exception("Unexpected decision in pure resource probe");
                return g.Engine.State.Players[0].Power;
            }
            var av=Describe(a);var bv=Describe(b);int ap=Resolve(a),bp=Resolve(b);
            bool collision=av.SequenceEqual(bv)&&ap!=bp;
            if(!collision)throw new Exception("Descriptor collision was not reproduced: "+name);
            return new{name,mastery,descriptor_identical=true,width=av.Length,first_power=ap,second_power=bp};
        }
        var rows=new[]{
            Compare("threshold_binding",E.Seq(E.At(10,E.Power(2)),E.At(20,E.Power(8))),E.Seq(E.At(10,E.Power(8)),E.At(20,E.Power(2))),15),
            Compare("effect_execution_order",E.Seq(E.Mastery(2),E.At(20,E.Power(20))),E.Seq(E.At(20,E.Power(20)),E.Mastery(2)),18)
        };
        string json=JsonConvert.SerializeObject(new{evaluation_only=true,probes=rows},Formatting.Indented);
        File.WriteAllText(output,json);Console.WriteLine(json);
    }
}
