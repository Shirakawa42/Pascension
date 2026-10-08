using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class EffectDescriptorSelfTest
    {
        internal static void Audit()
        {
            var matrix=EffectDescriptors.AuditMatrix();
            Program.Print(new {rows=matrix.Length,width=EffectDescriptors.Width,manifest=EffectDescriptors.ManifestText(),descriptor=EffectDescriptors.Descriptor});
        }
        internal static void Run()
        {
            var matrix=EffectDescriptors.BuildMatrix();
            int tests=0;
            Check(matrix.Length==193&&matrix.All(x=>x.Length==512),"matrix shape");tests++;
            Check(matrix[0].All(x=>x==0)&&matrix.Skip(Encoder.CardIds.Length+1).All(x=>x.All(y=>y==0)),"null/padding");tests++;
            Check(matrix.SelectMany(x=>x).All(x=>!float.IsNaN(x)&&!float.IsInfinity(x)),"finite catalog");tests++;
            var crystal=ShardsCardDatabase.Get("crystal");var gain=crystal.PlayEffect as Gain;
            Check(gain!=null,"Crystal primitive Gain");
            int original=gain.Gems;var before=EffectDescriptors.BuildCard(crystal);
            try {gain.Gems=original+3;var after=EffectDescriptors.BuildCard(crystal);
                Check(Math.Abs(after[64]-before[64]-.3f)<1e-6,"effect-only crystal rebalance");
                Check(after.Where((x,i)=>i!=64).SequenceEqual(before.Where((x,i)=>i!=64)),"only gems semantic changes");tests++;
            } finally {gain.Gems=original;}
            var left=new ShardsCardDef{Id="test_a",Name="Test A",PlayEffect=new Gain{Gems=2}};
            var right=new ShardsCardDef{Id="totally_different",Name="Unrelated",RulesText="stale bogus text",PlayEffect=new Gain{Gems=2}};
            Check(EffectDescriptors.BuildCard(left).SequenceEqual(EffectDescriptors.BuildCard(right)),"no ID/name/text leakage into semantics");tests++;
            right.PlayEffect=new AtMastery(10,new Gain{Gems=2});var gated=EffectDescriptors.BuildCard(right);
            Check(!gated.SequenceEqual(EffectDescriptors.BuildCard(left))&&gated[64]==0&&gated[69]>.19f,"gate distinct from unconditional");tests++;
            right.PlayEffect=new Gain{Gems=2};right.ExhaustEffect=right.PlayEffect;right.PlayEffect=null;
            Check(!EffectDescriptors.BuildCard(left).SequenceEqual(EffectDescriptors.BuildCard(right)),"phase matters");tests++;
            right.ExhaustEffect=null;right.PlayEffect=new UnknownEffect();
            ExpectFailure(()=>EffectDescriptors.BuildCard(right),"unknown effect fails closed");tests++;
            ExpectFailure(()=>EffectDescriptors.VerifyManifest(new Dictionary<string,string>()),"unknown callback manifest fails closed");tests++;
            var wrong=EffectDescriptors.CurrentManifest();string first=wrong.Keys.First();wrong[first]="wrong";
            ExpectFailure(()=>EffectDescriptors.VerifyManifest(wrong),"changed callback fails closed");tests++;
            int capturedThreshold=10;
            right.PlayEffect=new If(ctx=>ctx.Controller.Mastery>=capturedThreshold,new Gain{Gems=1});
            var capturedBefore=EffectDescriptors.BuildCard(right);capturedThreshold=20;
            Check(!capturedBefore.SequenceEqual(EffectDescriptors.BuildCard(right)),"actual captured threshold changes descriptor");tests++;
            var rebuilt=EffectDescriptors.BuildMatrix();Check(matrix.Zip(rebuilt,(a,b)=>a.SequenceEqual(b)).All(x=>x),"deterministic rebuild, restoration");tests++;
            Program.Print(new {pass=true,tests,rows=matrix.Length,width=EffectDescriptors.Width,descriptor=EffectDescriptors.Descriptor});
        }
        private static void Check(bool pass,string message){if(!pass)throw new InvalidOperationException("Effect descriptor test: "+message);}
        private static void ExpectFailure(Action action,string message)
        {try{action();}catch(InvalidOperationException){return;}throw new InvalidOperationException(message);}
        private sealed class UnknownEffect:IShardsEffect
        {public IEnumerable<ShardsStep> Resolve(ShardsContext ctx){yield break;}}
    }
}
