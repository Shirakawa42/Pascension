using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
class Program
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static object Field(object value,string name)=>value.GetType().GetField(name,All)?.GetValue(value)??value.GetType().GetProperty(name,All)?.GetValue(value);
    static string[] Cards(object value)=>((IEnumerable)value).Cast<object>().Select(c=>c==null?null:(string)Field(c,"DefId")).ToArray();
    static void Main(string[] args)
    {
        string directory=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AssemblyLoadContext.Default.Resolving+=(ctx,name)=>File.Exists(Path.Combine(directory,name.Name+".dll"))?ctx.LoadFromAssemblyPath(Path.Combine(directory,name.Name+".dll")):null;
        var host=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
        host.GetType("Shards.Content.ShardsContentRegistry").GetMethod("EnsureRegistered",All).Invoke(null,null);
        host.GetType("Shards.ZeroDepth.Encoder").GetMethod("Initialize",All).Invoke(null,null);
        var config=host.GetType("Shards.ZeroDepth.Program").GetMethod("Config",All);
        var apply=host.GetType("Shards.ZeroDepth.HeroAssignments").GetMethod("Apply",All);
        var adapter=host.GetType("Shards.ZeroDepth.Adapter");
        using var output=new StreamWriter(args[2]);int n=0;
        foreach(var line in File.ReadLines(args[1]))
        {
            using var trace=JsonDocument.Parse(line);var root=trace.RootElement;
            ulong seed=Convert.ToUInt64(root.GetProperty("seed").GetString(),16);
            object engine=Activator.CreateInstance(host.GetType("Shards.Engine.ShardsEngine"),new[]{config.Invoke(null,new object[]{seed})});
            object game=Activator.CreateInstance(adapter,All,null,new[]{engine,null,(object)false},null);apply.Invoke(null,new[]{game,(object)seed});
            var state=Field(engine,"State");var players=((IEnumerable)Field(state,"Players")).Cast<object>().ToArray();
            for(int seat=0;seat<2;seat++)if((string)Field(players[seat],"CharacterId")!=root.GetProperty("players")[seat].GetProperty("hero").GetString())throw new Exception("Seed/hero mismatch");
            if((int)Field(state,"Round")!=1||(int)Field(state,"TurnPlayerIndex")!=0)throw new Exception("Not an opening state");
            output.WriteLine(JsonSerializer.Serialize(new{seed=seed.ToString("x16"),market=Cards(Field(state,"CenterRow")),destinies=Cards(Field(state,"DestinyRow")),
                monsters=Cards(Field(state,"ActiveMonsters")),players=players.Select(p=>new{hero=Field(p,"CharacterId"),gems=Field(p,"Gems"),mastery=Field(p,"Mastery"),hand=Cards(Field(p,"Hand"))}).ToArray()}));n++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{setups=n,played_games=0,policy_inference_calls=0,training_updates=0,all_heroes_match=true,frozen_host=args[0]}));
    }
}
