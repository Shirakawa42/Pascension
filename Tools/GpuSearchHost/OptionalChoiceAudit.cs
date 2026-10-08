using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

internal static class OptionalChoiceAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();int failures=0;
        for(int seat=0;seat<2;seat++)foreach(bool modelWantsBanish in new[]{true,false})foreach(double scale in new[]{1.0,2.0})
        {
            var root=RezAudit.Game(seat:seat);var p=root.Engine.State.Players[seat];
            p.CharacterId="kosynwu";p.Health=25;p.HeroAbilityUsedThisTurn=false;
            foreach(string id in new[]{"doom_gate","infinity_shard","korvus_legionnaire_duel","legion_carrier_duel"})RezAudit.Add(root,id,seat);
            RezAudit.Refresh(root);RezAudit.Step(root,c=>c.Action is ShardsHeroAbilityAction);
            int banish=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Option?.DefId=="doom_gate");
            int cancel=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Kind==13);
            var prediction=new Prediction{P=new float[64],V=modelWantsBanish?-.985f:-.94f};
            prediction.P[modelWantsBanish?banish:cancel]=1;
            Prediction Predict(Adapter g)
            {
                var own=g.Engine.State.Players[seat];bool retained=own.Hand.Concat(own.Deck).Concat(own.Discard).Concat(own.PlayZone).Concat(own.Champions).Any(c=>c.DefId=="doom_gate");
                // Two observed failure regimes: nearly-certain loss with a useful
                // alternative, versus a small noisy critic preference against a
                // confident and sensible cancellation. Policy is not discarded.
                float value=modelWantsBanish?(retained?-.981f:-.993f):(retained?-.985f:-.980f);
                var probs=new float[64];for(int a=0;a<g.VisibleCount;a++)probs[a]=g.Visible(a).Action is ShardsEndTurnAction||g.Visible(a).Kind==13?1:.001f;
                return new Prediction{P=probs,V=g.Actor==seat?value:-value};
            }
            var settings=new PolicySearchSettings{OptionalChoices=true,ChoicePriorScale=scale,Candidates=modelWantsBanish?1:4,Depth=4,Worlds=2,Workers=1,TerminalNodes=0,Prior=.015};
            var planner=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
            int fallback=modelWantsBanish?banish:cancel;ulong before=TacticalSearch.Fingerprint(root);
            var options=planner.BuildOptions(root,prediction,fallback);
            bool covered=options.Any(o=>o.First==cancel);
            int selected=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            var changed=FastCopy.Copy(root);changed.Engine.State.Players[seat].Deck.Reverse();
            var enemy=changed.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {var c=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=c;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
            int other=planner.Choose(new[]{changed},new[]{prediction},new[]{fallback},new[]{true})[0];
            bool passed=covered&&selected==cancel&&other==selected&&before==TacticalSearch.Fingerprint(root);
            rows.Add(new{seat,modelWantsBanish,scale,covered,selected=ReviewRecorder.Name(root,root.Visible(selected)),passed});if(!passed)failures++;
        }
        for(int seat=0;seat<2;seat++)foreach(bool saturated in new[]{false,true})foreach(double scale in new[]{1.0,2.0})
        {
            var root=RezAudit.Game(seat:seat);var own=root.Engine.State.Players[seat];
            own.CharacterId="kosynwu";own.Health=25;own.HeroAbilityUsedThisTurn=false;
            RezAudit.Add(root,"doom_gate",seat);RezAudit.Add(root,"crystal",seat);
            var discarded=own.Hand.Single(c=>c.DefId=="crystal");own.Hand.Remove(discarded);discarded.Zone=ShardsZone.Discard;own.Discard.Add(discarded);RezAudit.Refresh(root);
            Prediction Predict(Adapter g)
            {
                var p=g.Engine.State.Players[seat];bool retained=p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone).Concat(p.Champions).Any(c=>c.DefId=="doom_gate");
                var probs=new float[64];for(int a=0;a<g.VisibleCount;a++)
                    probs[a]=g.Decision!=null?(g.Visible(a).Option?.DefId=="crystal"?1:0):(g.Visible(a).Action is ShardsEndTurnAction?1:0);
                float value=saturated?(g.Decision!=null?-.9918f:retained?-.95f:-.9476f):(retained?-.9f:-.895f);
                return new Prediction{P=probs,V=g.Actor==seat?value:-value};
            }
            int hero=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsHeroAbilityAction);
            var prediction=Predict(root);Array.Clear(prediction.P,0,64);prediction.P[hero]=1;
            var settings=new PolicySearchSettings{OptionalChoices=true,MenuPlans=true,ChoicePriorScale=scale,Candidates=1,Depth=4,Worlds=2,Workers=1,TerminalNodes=0,Prior=.015};
            var planner=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(root);
            int first=planner.Choose(new[]{root},new[]{prediction},new[]{hero},new[]{true})[0];
            bool passed=before==TacticalSearch.Fingerprint(root)&&first==hero;root.Step(first);
            int crystal=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Option?.DefId=="crystal");
            int target=planner.Choose(new[]{root},new[]{Predict(root)},new[]{crystal},new[]{true})[0];
            passed&=target==crystal;
            rows.Add(new{kind="root-menu-retains-target-prior",seat,saturated,scale,selected=ReviewRecorder.Name(root,root.Visible(target)),passed});if(!passed)failures++;
        }
        // Natural game 14/92: a strong Crystal target policy is overridden by a
        // roughly .10 critic advantage for destroying Infinity. Exercise the
        // activation and the committed live menu, not only a score formula.
        for(int seat=0;seat<2;seat++)foreach(double scale in new[]{1.0,2.0})
        {
            var root=RezAudit.Game(seat:seat,mastery:6);var own=root.Engine.State.Players[seat];
            own.Hand.Clear();own.CharacterId="kosynwu";own.Health=46;own.HeroAbilityUsedThisTurn=false;
            own.Deck.RemoveAll(c=>c.DefId=="infinity_shard");
            RezAudit.Add(root,"infinity_shard",seat);RezAudit.Add(root,"crystal",seat);
            var crystal=own.Hand.Single(c=>c.DefId=="crystal");own.Hand.Remove(crystal);crystal.Zone=ShardsZone.Discard;own.Discard.Add(crystal);RezAudit.Refresh(root);
            Prediction Predict(Adapter g)
            {
                var p=g.Engine.State.Players[seat];bool retained=p.Hand.Concat(p.Deck).Concat(p.Discard).Concat(p.PlayZone).Any(c=>c.DefId=="infinity_shard");
                var probs=new float[64];for(int a=0;a<g.VisibleCount;a++)
                    probs[a]=g.Decision!=null?(g.Visible(a).Option?.DefId=="crystal"?1:0):(g.Visible(a).Action is ShardsEndTurnAction?1:0);
                float value=g.Decision!=null?-.27f:retained?-.76f:-.66f;
                return new Prediction{P=probs,V=g.Actor==seat?value:-value};
            }
            int hero=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsHeroAbilityAction);
            var prediction=Predict(root);Array.Clear(prediction.P,0,64);prediction.P[hero]=1;
            var settings=new PolicySearchSettings{OptionalChoices=true,MenuPlans=true,ChoicePriorScale=scale,Candidates=1,Depth=4,Worlds=2,Workers=1,TerminalNodes=0,Prior=.015};
            var planner=new HybridLookahead(settings,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(root);int first=planner.Choose(new[]{root},new[]{prediction},new[]{hero},new[]{true})[0];
            bool passed=before==TacticalSearch.Fingerprint(root)&&first==hero;root.Step(first);
            int fallback=Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Option?.DefId=="crystal");
            int target=planner.Choose(new[]{root},new[]{Predict(root)},new[]{fallback},new[]{true})[0];
            string selected=root.Visible(target).Option?.DefId;passed&=selected==(scale==1?"infinity_shard":"crystal");
            rows.Add(new{kind="root-irreversible-choice-confidence",seat,scale,selected,passed});if(!passed)failures++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=failures==0,failures,rows},Formatting.Indented));
        if(failures>0)throw new Exception($"Optional choice audit: {failures} failures");
    }
}
