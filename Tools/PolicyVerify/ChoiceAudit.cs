using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Content;
using Shards.Engine;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;

internal static class ChoiceAudit
{
    internal static void DraftRegression(FrozenPolicy policy)
    {
        int checks=0;
        for (ulong seed=100001;seed<=100032;seed++)
        foreach(string opponent in new string[]{null,"decima","tetra","volos","kosynwu","rez"})
        {
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
            var game=new PolicyEngine(config,policy,(int)seed);
            game.BindSubmit(a=>{var r=game.Submit(a);if(!r.Accepted)throw new Exception(r.Error);});
            if(opponent!=null)
            {
                var first=game.PendingInput.Decision;
                var result=game.Submit(new SubmitDecisionAction{PlayerIndex=1,Answer=new DecisionAnswer{DecisionId=first.Id,ChosenOptionIds=new List<int>{first.Options.Single(o=>o.DefId==opponent).Id}}});
                if(!result.Accepted)throw new Exception(result.Error);
            }
            string picked=null;
            var options=game.PendingInput.Decision.Options.ToArray();
            game.BindSubmit(a=>{picked=options.Single(o=>o.Id==((SubmitDecisionAction)a).Answer.ChosenOptionIds[0]).DefId;var r=game.Submit(a);if(!r.Accepted)throw new Exception(r.Error);});
            game.StepPolicy();
            string expected=opponent=="tetra"?"volos":"tetra";
            if(picked!=expected)throw new Exception($"Draft regression seed={seed}: opponent={opponent}, picked={picked}, expected={expected}");
            checks++;
        }
        Console.WriteLine($"PASS: actual PolicyEngine draft: {checks} checks, 32 seeds, first pick and every opponent hero");
    }
    private static int DestinyInstance(Candidate c) => c.Action is ShardsTakeDestinyAction a ? a.CardInstanceId : c.Option.CardInstanceId;
    internal static void Destinies(FrozenPolicy policy,string output)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var rng=new Random(9275037);var records=new List<object>();
        float menuError=0,rowError=0;int menuTopChanges=0,rowTopChanges=0,bonusStates=0,bonusTopChanges=0; float bonusOrderError=0;
        var ids=new HashSet<string>();int completed=0;
        for(ulong seed=15000;seed<15250 && (records.Count<300 || bonusStates<100);seed++)
        {
            var config=ShardsContentRegistry.StandardConfig(seed,new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}},ShardsDlc.Duel);
            var game=new Adapter(new ShardsEngine(config));
            game.SubmitThroughHost=a=>{var r=game.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
            // Cover every ordered distinct-hero pair; these diagnostics never enter balance statistics.
            int h0=(int)(seed-15000)%5, h1=((int)(seed-15000)/5)%4;
            if(h1>=h0)h1++;
            foreach(int hero in new[]{h1,h0})
            {
                int selected=game.Candidates.FindIndex(x=>x.Option?.DefId==ShardsEngine.DraftableCharacters[hero]);
                game.Step(selected);
            }
            var obs=new float[3328];var c=new float[2048];var mask=new float[64];
            while(!game.Engine.State.GameOver && !game.Truncated)
            {
                Encoder.Encode(game,obs,c,mask);var p=policy.Probabilities(obs,c,mask,out _);
                bool bonus=game.Decision?.Context=="soi.destiny";
                var ds=Enumerable.Range(0,game.VisibleCount).Where(i=>game.Visible(i).Action is ShardsTakeDestinyAction || bonus && game.Visible(i).Option!=null).ToArray();
                if(ds.Length>=2 && (records.Count<300 || bonus && bonusStates<100))
                {
                    var candidates=game.Candidates.ToArray();
                    var baseline=ds.ToDictionary(i=>DestinyInstance(game.Visible(i)),i=>p[i]);
                    int top=baseline.OrderByDescending(x=>x.Value).First().Key;
                    game.Candidates.Reverse();
                    var no=new float[3328];var nc=new float[2048];var nm=new float[64];
                    Encoder.Encode(game,no,nc,nm);var np=policy.Probabilities(no,nc,nm,out _);
                    var perm=Enumerable.Range(0,game.VisibleCount).Where(i=>game.Visible(i).Action is ShardsTakeDestinyAction || bonus && game.Visible(i).Option!=null).ToDictionary(i=>DestinyInstance(game.Visible(i)),i=>np[i]);
                    float me=baseline.Max(x=>Math.Abs(x.Value-perm[x.Key]));menuError=Math.Max(menuError,me);
                    int menuTop=perm.OrderByDescending(x=>x.Value).First().Key;
                    float menuTopGap=baseline[top]-baseline[menuTop];
                    if(menuTop!=top && menuTopGap>0.000001f)menuTopChanges++;
                    game.Candidates.Clear();game.Candidates.AddRange(candidates);
                    game.Engine.State.DestinyRow.Reverse();
                    Encoder.Encode(game,no,nc,nm);np=policy.Probabilities(no,nc,nm,out _);
                    var row=ds.ToDictionary(i=>DestinyInstance(game.Visible(i)),i=>np[i]);
                    float re=baseline.Max(x=>Math.Abs(x.Value-row[x.Key]));rowError=Math.Max(rowError,re);
                    if(row.OrderByDescending(x=>x.Value).First().Key!=top)rowTopChanges++;
                    game.Engine.State.DestinyRow.Reverse();
                    float boe=0;
                    if(bonus)
                    {
                        bonusStates++;
                        game.Decision.Options.Reverse();game.Rebuild();
                        Encoder.Encode(game,no,nc,nm);np=policy.Probabilities(no,nc,nm,out _);
                        var bp=Enumerable.Range(0,game.VisibleCount).Where(i=>game.Visible(i).Option!=null).ToDictionary(i=>DestinyInstance(game.Visible(i)),i=>np[i]);
                        boe=baseline.Max(x=>Math.Abs(x.Value-bp[x.Key]));bonusOrderError=Math.Max(bonusOrderError,boe);
                        int bt=bp.OrderByDescending(x=>x.Value).First().Key;
                        if(bt!=top && baseline[top]-baseline[bt]>0.000001f)bonusTopChanges++;
                        game.Decision.Options.Reverse();game.Rebuild();
                    }
                    var choices=baseline.Select(x=>{var card=game.Engine.State.DestinyRow.Single(d=>d.InstanceId==x.Key);ids.Add(card.DefId);return new {id=card.DefId,probability=x.Value};}).ToArray();
                    records.Add(new {seed,round=game.Engine.State.Round,actor=game.Actor,bonus,bonus_order_error=boe,choices,menu_error=me,row_error=re,menu_top_gap=menuTopGap});
                }
                double draw=rng.NextDouble();int choice=0;for(int a=0;a<64;a++)if(mask[a]!=0){choice=a;draw-=p[a];if(draw<0)break;}
                game.Step(choice);
            }
            if(!game.Engine.State.GameOver)throw new Exception("Audit game truncated");completed++;
        }
        var summary=new {states=records.Count,completed_games=completed,destiny_ids=ids.OrderBy(x=>x),menu_max_probability_error=menuError,menu_top_changes=menuTopChanges,row_max_probability_error=rowError,row_top_changes=rowTopChanges,bonus_states=bonusStates,bonus_order_max_error=bonusOrderError,bonus_top_changes=bonusTopChanges};
        File.WriteAllText(output,JsonConvert.SerializeObject(new {summary,records},Formatting.Indented));
        Console.WriteLine(JsonConvert.SerializeObject(summary));
        if(records.Count<300 || menuError>0.0001 || rowError>0.0001)throw new Exception("Destiny permutation audit failed");
    }
}
