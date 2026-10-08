using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Shards.Nyou
{
    /// <summary>Frozen full-information network, with the evaluated Scry calibration.
    /// Sparse first-layer inputs and SIMD preserve the architecture without a GPU or subprocess.</summary>
    public sealed class FrozenPolicy
    {
        private const int Width=512, Key=64;
        private readonly Dictionary<string,float[]> weights=new();
        private readonly float[] firstColumns;
        private readonly int cards;
        private readonly float temperature;
        public FrozenPolicy(byte[] bytes)
        {
            Shards.Content.ShardsContentRegistry.EnsureRegistered();
            Encoder.Initialize();
            using var reader=new BinaryReader(new MemoryStream(bytes));
            if(reader.ReadInt32()!=0x534F4932||reader.ReadInt32()!=Encoder.ObsDim||reader.ReadInt32()!=Encoder.ActionDim||reader.ReadInt32()!=Width)
                throw new InvalidDataException("Unsupported Nyou policy schema");
            cards=reader.ReadInt32();temperature=reader.ReadSingle();
            if(cards!=Encoder.CardIds.Length||temperature!=64||reader.ReadInt32()!=cards)throw new InvalidDataException("Nyou catalog/calibration mismatch");
            string Text(){int n=reader.ReadInt32();if(n<1||n>256)throw new InvalidDataException("Invalid policy name");return Encoding.UTF8.GetString(reader.ReadBytes(n));}
            for(int i=0;i<cards;i++)if(Text()!=Encoder.CardIds[i])throw new InvalidDataException("Nyou card identities differ from this engine");
            int entries=reader.ReadInt32();if(entries!=17)throw new InvalidDataException("Invalid Nyou tensor count");
            for(int i=0;i<entries;i++)
            {
                string name=Text();int n=reader.ReadInt32();if(n<1||n>Encoder.ObsDim*Width)throw new InvalidDataException("Invalid policy tensor");
                var a=new float[n];for(int j=0;j<n;j++){a[j]=reader.ReadSingle();if(float.IsNaN(a[j])||float.IsInfinity(a[j]))throw new InvalidDataException("Nonfinite weight");}weights.Add(name,a);
            }
            if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Trailing policy bytes");
            void Shape(string name,int n){if(!weights.TryGetValue(name,out var a)||a.Length!=n)throw new InvalidDataException("Invalid shape: "+name);}
            Shape("trunk.0.weight",Width*Encoder.ObsDim);Shape("trunk.0.bias",Width);Shape("trunk.2.weight",Width*Width);Shape("trunk.2.bias",Width);
            Shape("query.weight",Key*Width);Shape("query.bias",Key);Shape("candidate.0.weight",Key*Encoder.ActionDim);Shape("candidate.0.bias",Key);
            Shape("candidate_bias.weight",Encoder.ActionDim);Shape("value.weight",Width);Shape("value.bias",1);Shape("zone_projection.weight",Width*24*Key);
            Shape("menu_projection.weight",Width*Key);Shape("kind_prior",16);Shape("optional_exploration",1);Shape("embedding_table",(cards+1)*Key);Shape("known_projection",Width*78);
            if(weights["optional_exploration"][0]<0||weights["optional_exploration"][0]>.1f)throw new InvalidDataException("Invalid exploration coefficient");
            var first=weights["trunk.0.weight"];firstColumns=new float[first.Length];
            for(int c=0;c<Encoder.ObsDim;c++)for(int r=0;r<Width;r++)firstColumns[c*Width+r]=first[r*Encoder.ObsDim+c];
            weights.Remove("trunk.0.weight");
        }
        private static float Numeric(float x)=>(float)(Math.Sign(x)*Math.Log(1+Math.Abs(x)));
        private static float Silu(float x)=>(float)(x/(1+Math.Exp(-x)));
        private static float Dot(float[] a,int offset,float[] b)
        {
            var sums=Vector<float>.Zero;int k=0,n=Vector<float>.Count;
            for(;k<=b.Length-n;k+=n)sums+=new Vector<float>(a,offset+k)*new Vector<float>(b,k);
            float value=Vector.Dot(sums,Vector<float>.One);for(;k<b.Length;k++)value+=a[offset+k]*b[k];return value;
        }
        private static void AddScaled(float[] target,float[] source,int offset,float scale)
        {
            int k=0,n=Vector<float>.Count;var multiplier=new Vector<float>(scale);
            for(;k<=target.Length-n;k+=n)(new Vector<float>(target,k)+new Vector<float>(source,offset+k)*multiplier).CopyTo(target,k);
            for(;k<target.Length;k++)target[k]+=source[offset+k]*scale;
        }
        private float[] Linear(string name,float[] input,int size)
        {
            var result=new float[size];var w=weights[name+".weight"];weights.TryGetValue(name+".bias",out var bias);
            for(int r=0;r<size;r++)result[r]=Dot(w,r*input.Length,input)+(bias==null?0:bias[r]);return result;
        }
        /// <summary>Calibrated masked logits and bounded value, matching the frozen Python forward pass.</summary>
        public float[] Evaluate(float[] obs,float[] candidates,float[] mask,out float value)
        {
            if(obs.Length!=Encoder.ObsDim||candidates.Length!=Encoder.MaxActions*Encoder.ActionDim||mask.Length!=Encoder.MaxActions)
                throw new ArgumentException("Nyou input dimensions differ from the export");
            var hidden=(float[])weights["trunk.0.bias"].Clone();
            for(int c=0;c<obs.Length;c++)if(obs[c]!=0)AddScaled(hidden,firstColumns,c*Width,Numeric(obs[c]));
            for(int k=0;k<Width;k++)hidden[k]=Silu(hidden[k]);
            hidden=Linear("trunk.2",hidden,Width);for(int k=0;k<Width;k++)hidden[k]=Silu(hidden[k]);
            var table=weights["embedding_table"];var zones=new float[24*Key];
            for(int z=0;z<24;z++)for(int c=0;c<cards;c++)
            {
                float count=obs[256+z*192+c];if(count==0)continue;
                for(int k=0;k<Key;k++)zones[z*Key+k]+=count*table[(c+1)*Key+k];
            }
            for(int k=0;k<zones.Length;k++)zones[k]*=10;
            var zone=Linear("zone_projection",zones,Width);var menu=new float[Key];var ids=new int[64];int legal=0;bool select=false,finish=false;
            for(int a=0;a<64;a++)
            {
                ids[a]=(int)Math.Round(candidates[a*48+16]*192);if(ids[a]<0||ids[a]>cards)throw new ArgumentException("Invalid card code");
                if(mask[a]==0)continue;legal++;select|=candidates[a*48+12]>.5f;finish|=candidates[a*48+13]>.5f;
                for(int k=0;k<Key;k++)menu[k]+=table[ids[a]*Key+k];
            }
            if(legal==0)throw new InvalidOperationException("Nyou has no legal choice");
            for(int k=0;k<Key;k++)menu[k]/=legal;var projected=Linear("menu_projection",menu,Width);
            var known=new float[78];
            for(int top=0;top<3;top++)
            {
                int id=0,matches=0;
                for(int i=0;i<384;i++)
                {
                    int p=17152+i*8,code=(int)Math.Round(obs[p]*192);
                    if(obs[p+1]==1f/3&&obs[p+4]>.5f&&obs[p+5]<.5f&&obs[p+6]>.5f&&code>0&&code<=cards&&obs[p+2]==top/384f&&obs[p+3]==top/384f){id=code;matches++;}
                }
                if(matches==1)Array.Copy(table,id*Key,known,top*26,26);
            }
            for(int k=0;k<Width;k++)hidden[k]=Silu(hidden[k]+zone[k]+projected[k]+Dot(weights["known_projection"],k*78,known));
            var query=Linear("query",hidden,Key);value=(float)Math.Tanh(Linear("value",hidden,1)[0]);
            var logits=new float[64];var input=new float[48];
            for(int a=0;a<64;a++)
            {
                if(mask[a]==0){logits[a]=-1e9f;continue;}
                for(int k=0;k<48;k++)input[k]=Numeric(candidates[a*48+k]);
                var keys=Linear("candidate.0",input,Key);float score=0;
                for(int k=0;k<Key;k++)score+=(Silu(keys[k])+table[ids[a]*Key+k])*query[k];
                score=score/8+Dot(weights["candidate_bias.weight"],0,input);
                for(int k=0;k<16;k++)score+=candidates[a*48+k]*weights["kind_prior"][k];logits[a]=score;
            }
            float epsilon=weights["optional_exploration"][0];
            if(select&&finish&&epsilon>0)
            {
                var probabilities=Probabilities(logits);
                for(int a=0;a<64;a++)if(mask[a]!=0)logits[a]=(float)Math.Log(probabilities[a]*(1-epsilon)+epsilon/legal);
            }
            if(obs[144]>.5f&&(int)Math.Round(obs[157]*26)==18)
                for(int a=0;a<64;a++)if(mask[a]!=0)logits[a]/=temperature;
            return logits;
        }
        internal static float[] Probabilities(float[] logits)
        {
            float max=float.NegativeInfinity;foreach(float x in logits)max=Math.Max(max,x);
            var result=new float[logits.Length];double sum=0;for(int a=0;a<result.Length;a++){result[a]=(float)Math.Exp(logits[a]-max);sum+=result[a];}
            for(int a=0;a<result.Length;a++)result[a]=(float)(result[a]/sum);return result;
        }
    }
}
