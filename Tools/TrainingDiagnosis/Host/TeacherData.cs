using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using Pascension.Engine.Actions;
using Shards.AI;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Supervised targets are complete accepted production actions, decomposed
    // through the learner's real wrapper. State hashes must agree after Submit.
    internal static class TeacherData
    {
        private sealed class FieldsOnly : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override IList<Newtonsoft.Json.Serialization.JsonProperty> CreateProperties(Type type, MemberSerialization mode)
                => base.CreateProperties(type,mode).Where(p=>type.GetField(p.UnderlyingName)!=null && !typeof(Delegate).IsAssignableFrom(p.PropertyType)).ToList();
        }
        private static string StateJson(ShardsState state)=>JsonConvert.SerializeObject(state,new JsonSerializerSettings{ContractResolver=new FieldsOnly(),ReferenceLoopHandling=ReferenceLoopHandling.Ignore});
        internal static object Run(string bundlePath, string output, int games)
        {
            if (games < 1 || games > 200) throw new ArgumentOutOfRangeException(nameof(games));
            Directory.CreateDirectory(output);
            var bundle = CurrentOpponent.LoadBundle(bundlePath);
            var timer = Stopwatch.StartNew(); var reports = new object[games];
            using var workers = new LaneWorkers(Math.Min(6, games));
            workers.Run(games, index =>
            {
                // Disjoint training namespace, separate from all held-out arenas.
                ulong seed = (0x6000000000000000UL/20)*20 + (ulong)index;
                var config = Program.Config(seed);
                var expert = new PolicyEngine(config, bundle.Policy, 615000+index, true, bundle.Settings);
                var legacy = (Shards.AI.Adapter)typeof(PolicyEngine).GetField("_adapter", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(expert);
                var shadow = new Adapter(new ShardsEngine(config), automaticSingletons:false);
                var obs = new float[Encoder.ObsDim]; var candidates = new float[Encoder.MaxActions*Encoder.ActionDim];
                var mask = new float[Encoder.MaxActions]; int rows = 0, submissions = 0;
                var contexts = new Dictionary<string,int>();
                using var stream = new BinaryWriter(File.Open(Path.Combine(output,$"game-{index:000}.bin"),FileMode.CreateNew));
                Action<PlayerAction> accept = action =>
                {
                    bool draft = shadow.Decision?.Context == "soi.herodraft";
                    long before = shadow.Submissions; int steps = 0;
                    string encoded = JsonConvert.SerializeObject(action);
                    while (shadow.Submissions == before)
                    {
                        if (++steps > 4096) throw new InvalidOperationException("Teacher action mapping failed to terminate");
                        int chosen = -1, page = -1;
                        var answer = action as SubmitDecisionAction;
                        for (int i=0;i<shadow.VisibleCount;i++)
                        {
                            var c = shadow.Visible(i);
                            if (c.Kind == 14) { page=i;continue; }
                            // Focus, End Turn and Concede can have identical fields.
                            // The concrete action type is part of its identity.
                            if (c.Action != null && c.Action.GetType()==action.GetType()
                                && JsonConvert.SerializeObject(c.Action)==encoded) chosen=i;
                            else if (answer != null)
                            {
                                var ids = answer.Answer.ChosenOptionIds;
                                if (c.Kind == 15)
                                {
                                    int amount = ids.Count(id=>id==c.Option.Id);
                                    if (c.Low<=amount && amount<=c.High) chosen=i;
                                }
                                else if (c.Kind==12 && shadow.Selected.Count<ids.Count && c.Option.Id==ids[shadow.Selected.Count]) chosen=i;
                                else if (c.Kind==13 && shadow.Selected.Count==ids.Count) chosen=i;
                            }
                        }
                        if(chosen<0) chosen=page;
                        if(chosen<0)throw new InvalidOperationException("Accepted teacher action unavailable in learner wrapper: "+encoded);
                        if (!draft && shadow.VisibleCount>1)
                        {
                            Encoder.Encode(shadow,obs,candidates,mask);
                            if(mask[chosen]!=1)throw new InvalidOperationException("Teacher target is not legal");
                            string context=shadow.Decision?.Context??"priority";
                            contexts.TryGetValue(context,out int count);contexts[context]=count+1;
                            stream.Write(index);stream.Write(shadow.Actor);stream.Write(chosen);
                            foreach(float value in obs)stream.Write(value);
                            foreach(float value in candidates)stream.Write(value);
                            foreach(float value in mask)stream.Write(value);
                            rows++;
                        }
                        shadow.Step(chosen);
                    }
                    var result=expert.Submit(action);
                    if(!result.Accepted)throw new InvalidOperationException("Teacher action rejected");
                    if(shadow.Engine.State.ComputeHash()!=legacy.Engine.State.ComputeHash())
                        { File.WriteAllText(Path.Combine(output,"divergence-shadow.json"),StateJson(shadow.Engine.State)); File.WriteAllText(Path.Combine(output,"divergence-expert.json"),StateJson(legacy.Engine.State)); throw new InvalidOperationException("Teacher mapping changed resulting rules state after "+submissions+" submissions: "+encoded); }
                    submissions++;
                };
                expert.BindSubmit(accept);
                var pair=HeroAssignments.ForSeed(seed);
                while(shadow.Decision?.Context=="soi.herodraft")
                {
                    var request=shadow.Decision;string hero=request.PlayerIndex==0?pair.Seat0:pair.Seat1;
                    var option=request.Options.Find(o=>o.DefId==hero&&!o.Disabled);
                    accept(new SubmitDecisionAction{PlayerIndex=request.PlayerIndex,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=request.Id,ChosenOptionIds=new List<int>{option.Id}}});
                }
                int decisions=0;
                while(!expert.GameOver && !legacy.Truncated)
                {
                    if(timer.Elapsed.TotalSeconds>300)throw new TimeoutException("Teacher collection exceeded five minutes");
                    expert.StepPolicy();decisions++;
                }
                if(!expert.GameOver)throw new InvalidOperationException("Teacher game censored");
                reports[index]=new{index,seed,seat0=pair.Seat0,seat1=pair.Seat1,rows,submissions,decisions,contexts,winner=expert.WinnerIndex,rounds=shadow.Engine.State.Round,final_hash=shadow.Engine.State.ComputeHash()};
            });
            return new{passed=true,games,seconds=timer.Elapsed.TotalSeconds,observation_dimension=Encoder.ObsDim,candidate_dimension=Encoder.MaxActions*Encoder.ActionDim,mask_dimension=Encoder.MaxActions,record_bytes=12+4*(Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim+Encoder.MaxActions),search_settings_unchanged=true,full_state_hash_parity=true,reports};
        }
    }
}
