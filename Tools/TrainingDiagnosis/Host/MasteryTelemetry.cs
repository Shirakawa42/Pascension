using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Observes accepted REAL submissions only. Never attached to a search copy.
    internal sealed class MasteryTelemetry
    {
        readonly ShardsEngine engine;
        readonly string directory;
        readonly ulong seed;
        readonly int learner;
        readonly Shards.Preflight.VictoryEvidence victory = new();
        readonly int[] focus = new int[2], gains = new int[2], losses = new int[2], infinity = new int[2];
        readonly int[] unusedFocus = new int[2], shardAt29WithFocus = new int[2], shardBelow30 = new int[2];
        readonly List<object> ordering = new();
        bool emitted;

        internal MasteryTelemetry(ShardsEngine engine, string directory, ulong seed, int learner)
        { this.engine=engine; this.directory=directory; this.seed=seed; this.learner=learner; Directory.CreateDirectory(directory); }

        internal SubmitResult Apply(Func<PlayerAction,SubmitResult> submit, PlayerAction action)
        {
            int start=engine.Log.Count, seat=action.PlayerIndex;
            var player=engine.State.Players[seat];
            bool focusAvailable=engine.PendingInput?.Decision==null && !player.FocusedThisTurn &&
                !player.CharacterExhausted && player.Gems>=1 && player.Mastery<30;
            bool ended=action is ShardsEndTurnAction;
            bool shard=action is ShardsPlayCardAction play &&
                player.Hand.Any(c=>c.InstanceId==play.CardInstanceId && c.DefId=="infinity_shard");
            int mastery=player.Mastery, round=engine.State.Round, power=player.Power;
            var result=submit(action);
            if(!result.Accepted)return result;
            if(action is ShardsFocusAction)focus[seat]++;
            if(shard && mastery<30)shardBelow30[seat]++;
            if(shard && mastery==29 && focusAvailable)
            {
                shardAt29WithFocus[seat]++;
                ordering.Add(new{seat,round,mastery,power,issue="shard_played_at_29_with_legal_focus"});
            }
            if(ended && focusAvailable && !engine.State.GameOver)unusedFocus[seat]++;
            for(int index=start;index<engine.Log.Count;index++)
            {
                var entry=engine.Log[index]; victory.Observe(entry);
                if(entry is ShardsMasteryChangedEvent m)
                { if(m.Delta>0)gains[m.PlayerIndex]+=m.Delta; else losses[m.PlayerIndex]-=m.Delta; }
                if(entry is ShardsPowerChangedEvent p && p.Delta==9994)infinity[p.PlayerIndex]++;
            }
            return result;
        }

        internal void Complete(bool censored=false)
        {
            if(emitted || !engine.State.GameOver&&!censored)return;
            emitted=true;
            var state=engine.State;
            var record=new{seed,learner,completed=state.GameOver,censored,winner=state.WinnerIndex,
                rounds=state.Round,heroes=state.Players.Select(p=>p.CharacterId).ToArray(),
                mastery=state.Players.Select(p=>p.Mastery).ToArray(),health=state.Players.Select(p=>p.Health).ToArray(),
                victoryCause=state.GameOver?victory.Result(state.WinnerIndex):"censored",
                focus,gains,losses,infinity,unusedFocus,shardAt29WithFocus,shardBelow30,ordering};
            string file=Path.Combine(directory,$"game-{seed}-{learner}.json");
            using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write);
            JsonSerializer.Serialize(stream,record);
        }

        internal static object Expert(string bundlePath,string output,int count)
        {
            if(count<20 || count>400 || count%20!=0)throw new ArgumentException("Use 20..400 balanced games");
            if(Directory.Exists(output))throw new IOException("Use a fresh output directory");
            Directory.CreateDirectory(output);var bundle=CurrentOpponent.LoadBundle(bundlePath);
            using var workers=new LaneWorkers(6);
            var timer=System.Diagnostics.Stopwatch.StartNew();
            workers.Run(count,i=>
            {
                ulong seed=0x6300000000000000UL/20*20+(ulong)i;
                var expert=new CurrentOpponent(Program.Config(seed),bundle,unchecked((int)(seed^716921)));
                var telemetry=new MasteryTelemetry(expert.Engine,output,seed,-1);
                var adapter=new Adapter(expert.Engine,a=>telemetry.Apply(expert.Submit,a));
                expert.BindSubmit(a=>adapter.ApplyExternal(a));HeroAssignments.Apply(adapter,seed);
                while(!expert.Engine.State.GameOver&&!expert.Truncated)
                { if(timer.Elapsed.TotalSeconds>900)throw new TimeoutException("Expert audit exceeded fifteen minutes"); expert.Step(); }
                telemetry.Complete(expert.Truncated);
            });
            return new{completed=count,seconds=timer.Elapsed.TotalSeconds,output};
        }
    }
}
