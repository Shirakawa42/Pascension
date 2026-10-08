using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

// Public-information tactical demonstrations, disjoint seeds for training/test.
// Labels are supported actions, not a claim that every alternative is inferior.
internal static class RezTactics
{
    internal static void Run(FrozenPolicy policy,string output,int origin)
    {
        bool transfer=origin>=600000;
        var rows=new List<object>();var rollouts=new List<object>();int wins=0;
        void Capture(string group,Adapter g,Func<Candidate,bool> target,bool act=true,double? completionProbability=null,bool? completionCorrect=null)
        {
            var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
            var good=Enumerable.Range(0,g.VisibleCount).Where(i=>target(g.Visible(i))).ToArray();
            if(good.Length==0)throw new Exception("No tactical target: "+group);
            var probabilities=policy.Probabilities(o,c,m,out _);
            int best=Array.IndexOf(probabilities,probabilities.Max());
            rows.Add(new{group,seat=g.Actor,round=g.Engine.State.Round,mastery=g.Engine.State.Players[g.Actor].Mastery,
                observation=o,candidates=c,mask=m,targets=good,probability=good.Sum(i=>probabilities[i]),argmax_correct=good.Contains(best),
                completion_probability=completionProbability,greedy_completion_correct=completionCorrect});
            if(act)g.Step(good[0]);
        }
        bool Play(Adapter g,Candidate c,string id)=>c.Action is ShardsPlayCardAction a&&g.Engine.State.Players[g.Actor].Hand.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==id);
        void VerifyWin(Adapter g,int seat)
        {
            RezAudit.Step(g,c=>c.Action is ShardsEndTurnAction);
            for(int n=0;n<30&&!g.Engine.State.GameOver;n++)
                if(g.Candidates.Any(c=>c.Kind==13))RezAudit.Step(g,c=>c.Kind==13);else g.Step(0);
            if(!g.Engine.State.GameOver||g.Engine.State.WinnerIndex!=seat)throw new Exception("Tactical demonstration did not win");
            wins++;
        }
        for(int i=0;i<64;i++)
        {
            int seat=i%2;ulong seed=(ulong)(origin+i);int mastery=28+(i/2)%2;
            string reward=mastery==29&&i%4>=2?"cache_warden":"shard_abstractor";
            string first=i%3==0?"additri_gaiamancer":"testudo_vanguard";
            string second=i%3==0?"testudo_vanguard":"additri_gaiamancer";
            if(transfer){first="giga_source_adept";second="zetta_encryptor";}
            Adapter MasteryPosition()
            {
                var game=RezAudit.Game(mastery,top:new[]{first,second,reward,"wraethe_skirmisher_duel"},seed:seed,seat:seat);
                game.Engine.State.Round=8+i%10;
                var own=game.Engine.State.Players[seat];own.Health=7+i%44;own.Deck.Clear();own.Discard.Clear();own.RerollsThisTurn=1;
                foreach(string id in new[]{"index_of_futures","longshot","infinity_shard"})RezAudit.Add(game,id,seat);
                RezAudit.Step(game,c=>Play(game,c,"index_of_futures"));
                foreach(string id in new[]{first,second,reward})RezAudit.Step(game,c=>c.Option?.DefId==id);
                return game;
            }
            var g=MasteryPosition();
            Capture("known_mastery_scry",g,c=>c.Action is ShardsHeroAbilityAction);
            Capture("scry_bury_champion",g,c=>c.Option?.DefId==first||c.Option?.DefId==second);
            Capture("scry_bury_champion",g,c=>c.Option?.DefId==first||c.Option?.DefId==second);
            Capture("scry_keep_mastery",g,c=>c.Kind==13);
            Capture("longshot_after_setup",g,c=>Play(g,c,"longshot"));
            Capture("warp_mastery",g,c=>c.Option?.DefId==reward);
            if(g.Decision?.Context=="soi.warp")RezAudit.Step(g,c=>c.Kind==13);
            Capture("infinity_at_30",g,c=>Play(g,c,"infinity_shard"));
            VerifyWin(g,seat);
            if(origin>=900000)foreach(bool sampled in new[]{false,true})rollouts.Add(new{family="mastery",seat,sampled,won=PolicyWins(policy,MasteryPosition(),seat,sampled?new Random(origin+i):null)});

            string starter=transfer&&i%2==0?"blaster":"crystal";
            Adapter WarpPosition()
            {
                var game=RezAudit.Game(10+i%21,seed:seed+1000,seat:seat,relic:"warpquartz_duel");game.Engine.State.Round=7+i%12;
                game.Engine.State.Players[seat].Health=8+i%43;
                var enemy=game.Engine.State.Players[1-seat];enemy.Health=1+i%3;
                // The *public collection* must rule out shields too; knowing
                // only the fixture's hidden hand would not prove lethal to AI.
                foreach(var card in enemy.Hand.Concat(enemy.Deck).Concat(enemy.Discard))card.DefId="crystal";
                RezAudit.Add(game,"warpquartz_duel",seat);RezAudit.Add(game,starter,seat);
                RezAudit.Step(game,c=>Play(game,c,"warpquartz_duel"));return game;
            }
            g=WarpPosition();
            Capture("warpquartz_lethal",g,c=>c.Option?.DefId==starter);
            if(g.Decision?.Context=="soi.banish")RezAudit.Step(g,c=>c.Kind==13);
            VerifyWin(g,seat);
            if(origin>=900000)foreach(bool sampled in new[]{false,true})rollouts.Add(new{family="warpquartz",seat,sampled,won=PolicyWins(policy,WarpPosition(),seat,sampled?new Random(origin+i+100):null)});

            string monster=transfer?new[]{"ingeminex_agony","ingeminex_brutality","ingeminex_corruption","ingeminex_malice","ingeminex_torment"}[i%5]:"ingeminex_agony";
            var revealed=new[]{monster,"wraethe_skirmisher_duel","korvus_legionnaire_duel"};
            if(transfer)revealed=revealed.Skip(i%3).Concat(revealed.Take(i%3)).ToArray();
            Adapter MonsterPosition()
            {
                var game=RezAudit.Game(5+i%26,true,0,revealed,seed+2000,seat);
                game.Engine.State.Round=5+i%14;RezAudit.Step(game,c=>c.Action is ShardsHeroAbilityAction);return game;
            }
            g=MonsterPosition();
            var completion=ScryCompletion(policy,MonsterPosition,monster);
            Capture("doom_gate_bury_monster",g,c=>c.Option?.DefId==monster,false,completion.probability,completion.greedy);

            g=RezAudit.Game(5+i%26,top:new[]{first,second,"wraethe_skirmisher_duel"},seed:seed+3000,seat:seat);
            g.Engine.State.Round=4+i%15;RezAudit.Add(g,"longshot",seat);
            Capture("unknown_longshot_scry_preference",g,c=>c.Action is ShardsHeroAbilityAction,false);

            // Counterexample: an immediate Infinity win takes precedence over
            // information gathering even when Scry and Longshot are available.
            g=RezAudit.Game(30,seed:seed+4000,seat:seat);g.Engine.State.Round=9+i%12;
            RezAudit.Add(g,"longshot",seat);RezAudit.Add(g,"infinity_shard",seat);
            Capture("immediate_win_before_scry",g,c=>Play(g,c,"infinity_shard"));
            VerifyWin(g,seat);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{origin,verified_winning_lines=wins,samples=rows,policy_rollouts=rollouts}));
        Console.WriteLine("Tactical samples: "+rows.Count+"; engine-verified winning lines: "+wins+"; saved "+output);
    }
    private static bool PolicyWins(FrozenPolicy policy,Adapter g,int seat,Random random)
    {
        for(int step=0;step<128&&!g.Engine.State.GameOver&&g.Engine.State.TurnPlayerIndex==seat;step++)
        {
            var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
            var probs=policy.Probabilities(o,c,m,out _);int chosen=Array.IndexOf(probs,probs.Max());
            if(random!=null)
            {
                double draw=random.NextDouble();
                for(int i=0;i<g.VisibleCount;i++){chosen=i;draw-=probs[i];if(draw<0)break;}
            }
            g.Step(chosen);
        }
        return g.Engine.State.GameOver&&g.Engine.State.WinnerIndex==seat;
    }
    private static (double probability,bool greedy) ScryCompletion(FrozenPolicy policy,Func<Adapter> factory,string monster)
    {
        double success=0;bool greedySuccess=false;
        void Visit(List<int> prefix,double weight,bool buried,bool greedy)
        {
            var g=factory();foreach(int action in prefix)g.Step(action);
            if(g.Decision?.Context!="soi.scry")
            {if(buried)success+=weight;if(greedy)greedySuccess=buried;return;}
            var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
            var probs=policy.Probabilities(o,c,m,out _);int best=Array.IndexOf(probs,probs.Max());
            for(int a=0;a<g.VisibleCount;a++)
                Visit(new List<int>(prefix){a},weight*probs[a],buried||g.Visible(a).Option?.DefId==monster,greedy&&a==best);
        }
        Visit(new List<int>(),1,false,true);return(success,greedySuccess);
    }
}
