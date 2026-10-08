using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Shards.AI;

// Actual play only: hypothetical search branches never enter the outcome data.
// Records have 5,440 public input floats, 64 behavior probabilities, 64 equivalent
// expert-target bits, then value/game/step/actor/hero/round/outcome (5,575 floats).
internal sealed class HybridExperience : IDisposable
{
    internal const int Width=5575;
    private readonly BinaryWriter writer;
    private readonly Dictionary<int,List<float[]>> pending=new Dictionary<int,List<float[]>>();
    private readonly Dictionary<int,(int round,int turn)> turns=new Dictionary<int,(int,int)>();
    internal long Rows;
    internal HybridExperience(string path){writer=new BinaryWriter(File.Create(path));}
    internal void Observe(int game,int step,Adapter g,int action,Prediction prediction)
    {
        var turn=(g.Engine.State.Round,g.Engine.State.TurnPlayerIndex);
        bool first=!turns.TryGetValue(game,out var prior)||prior!=turn;
        turns[game]=turn;
        // Independent deterministic subsampling, without consuming either game
        // RNG or policy RNG. Turn boundaries cover the search leaf distribution.
        if(!first&&(step+game)%8!=0)return;
        var row=new float[Width];
        Encoder.Encode(g,row.AsSpan(0,3328),row.AsSpan(3328,2048),row.AsSpan(5376,64));
        Array.Copy(prediction.P,0,row,5440,64);
        var group=HybridLookahead.Groups(g).FirstOrDefault(xs=>xs.Contains(action));
        if(group==null)row[5504+action]=1;
        else foreach(int a in group)row[5504+a]=1;
        row[5568]=prediction.V;row[5569]=game;row[5570]=step;row[5571]=g.Actor;
        row[5572]=Array.IndexOf(Shards.Engine.ShardsEngine.DraftableCharacters,g.Engine.State.Players[g.Actor].CharacterId);
        row[5573]=g.Engine.State.Round;
        if(!pending.TryGetValue(game,out var rows))pending.Add(game,rows=new List<float[]>());
        rows.Add(row);
    }
    internal void Finish(int game,int winner)
    {
        foreach(var row in pending[game])
        {
            row[5574]=winner<0?0:winner==(int)row[5571]?1:-1;
            writer.Write(MemoryMarshal.AsBytes(row.AsSpan()));Rows++;
        }
        pending.Remove(game);turns.Remove(game);writer.Flush();
    }
    public void Dispose(){writer.Dispose();if(pending.Count>0)throw new InvalidOperationException("Unfinished experience episodes");}
}
