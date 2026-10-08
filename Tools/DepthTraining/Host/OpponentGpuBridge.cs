using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Shards.AI;

namespace Shards.ZeroDepth
{
    // Evaluation-only replacement of the incumbent's inference callback.
    // Its policy, root sampling, search settings, and game rules are retained.
    internal static class OpponentGpuBridge
    {
        static readonly object Gate=new();
        static readonly HashSet<string> Contexts=new();
        static long calls,rows,checks;static float probabilityError,valueError;

        internal static object Diagnostics=>new{calls,rows,checks,probabilityError,valueError,contexts=Contexts.Count};

        internal static void Attach(CurrentOpponent opponent,CurrentOpponent.Bundle bundle,BinaryReader input,BinaryWriter output)
        {
            var engine=(PolicyEngine)typeof(CurrentOpponent).GetField("_policyEngine",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(opponent);
            var field=typeof(PolicyEngine).GetField("_lookahead",BindingFlags.Instance|BindingFlags.NonPublic)
                ??throw new InvalidOperationException("Incumbent inference integration changed");
            field.SetValue(engine,new Lookahead(bundle.Settings,positions=>Infer(positions,bundle.Policy,input,output)));
        }

        static Shards.AI.Prediction[] Infer(Shards.AI.Adapter[] positions,FrozenPolicy reference,BinaryReader input,BinaryWriter output)
        {
            // AdvanceOpponent can run concurrently in six lanes. Serialize
            // framed requests/replies; nothing touches the learner inference.
            lock(Gate)
            {
                var packets=new float[positions.Length][];
                for(int i=0;i<positions.Length;i++)
                {
                    var row=new float[5440];Shards.AI.Encoder.Encode(positions[i],row.AsSpan(0,3328),row.AsSpan(3328,2048),row.AsSpan(5376,64));packets[i]=row;
                }
                output.Write(5);output.Write(positions.Length);
                foreach(var row in packets)output.Write(MemoryMarshal.AsBytes(row.AsSpan()));output.Flush();
                var result=new Shards.AI.Prediction[positions.Length];
                for(int i=0;i<positions.Length;i++)
                {
                    var probabilities=new float[64];double sum=0;
                    for(int a=0;a<64;a++)
                    {
                        float p=input.ReadSingle();probabilities[a]=p;sum+=p;
                        if(!float.IsFinite(p)||p<0||packets[i][5376+a]==0&&p>1e-7)
                            throw new InvalidDataException("Invalid incumbent GPU probability");
                    }
                    float value=input.ReadSingle();
                    if(!float.IsFinite(value)||Math.Abs(value)>1.00001||Math.Abs(sum-1)>.0001)
                        throw new InvalidDataException("Invalid incumbent GPU prediction");
                    string context=positions[i].Engine.State.Players[positions[i].Actor].CharacterId+":"+positions[i].Decision?.Context;
                    if(checks<32||Contexts.Add(context)||calls%256==0&&i==0)
                    {
                        var row=packets[i];var expected=reference.Probabilities(row.AsSpan(0,3328).ToArray(),row.AsSpan(3328,2048).ToArray(),row.AsSpan(5376,64).ToArray(),out float expectedValue);
                        for(int a=0;a<64;a++)probabilityError=Math.Max(probabilityError,Math.Abs(expected[a]-probabilities[a]));
                        valueError=Math.Max(valueError,Math.Abs(value-expectedValue));checks++;
                        if(probabilityError>1e-4||valueError>2e-5)
                            throw new InvalidDataException($"Incumbent CPU/GPU parity failed: probability {probabilityError}, value {valueError}");
                    }
                    result[i]=new Shards.AI.Prediction{P=probabilities,V=value};
                }
                calls++;rows+=positions.Length;return result;
            }
        }
    }
}
