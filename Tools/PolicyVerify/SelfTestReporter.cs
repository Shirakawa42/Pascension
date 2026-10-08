namespace Shards.Preflight
{
    internal static class Program
    {
        internal static void Print(object value) => System.Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(value));
    }
}
