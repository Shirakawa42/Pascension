using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Read-only comparison of callback IL descriptions from two frozen assemblies.
// Only the assembly name inside a resolved generic member type is normalized.
internal static class Program
{
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static string Key(MethodBase method) => method.DeclaringType.FullName + "::" + method;
    static Dictionary<string, MethodInfo> Methods(Assembly assembly) => assembly.GetTypes()
        .SelectMany(type => type.GetMethods(Flags)).ToDictionary(method => Key(method));
    static string Normalize(string value, Assembly assembly) => value.Replace(
        ", " + assembly.GetName().Name + ", Version=", ", <host>, Version=");

    static void Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("original.dll current.dll descriptor-diff.json report.json");
        Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "Newtonsoft.Json.dll"));
        var oldAssembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var newAssembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        var oldMethods = Methods(oldAssembly); var newMethods = Methods(newAssembly);
        MethodInfo Canonical(Assembly assembly) => assembly.GetType("Shards.Preflight.EffectDescriptors")
            .GetMethod("Canonical", BindingFlags.NonPublic | BindingFlags.Static);
        var oldCanonical = Canonical(oldAssembly); var newCanonical = Canonical(newAssembly);
        using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
        var reports = new List<object>(); var failures = new List<string>();
        foreach (var difference in input.RootElement.EnumerateArray())
        {
            string path = difference.GetProperty("path").GetString();
            const string prefix = "/coverage/methodChecksums/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected catalog difference");
            string key = path.Substring(prefix.Length);
            string oldText = (string)oldCanonical.Invoke(null, new object[] { oldMethods[key] });
            string newText = (string)newCanonical.Invoke(null, new object[] { newMethods[key] });
            if (Hash(oldText) != difference.GetProperty("old").GetString() ||
                Hash(newText) != difference.GetProperty("new").GetString())
                throw new InvalidDataException("The supplied binaries do not explain catalog checksum " + key);
            bool identical = Normalize(oldText, oldAssembly) == Normalize(newText, newAssembly);
            if (!identical) failures.Add(key);
            reports.Add(new { method = key, identicalAfterAssemblyNameNormalization = identical,
                oldCanonical = identical ? null : oldText, newCanonical = identical ? null : newText });
        }
        var result = new { passed = failures.Count == 0, examinedMethods = reports.Count,
            originalAssembly = oldAssembly.FullName, currentAssembly = newAssembly.FullName,
            normalization = "Only the host assembly name within resolved generic member types; opcodes, literals, branches and other operands remain exact",
            changedMethods = failures, reports };
        File.WriteAllText(args[3], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { result.passed, result.examinedMethods, result.originalAssembly, result.currentAssembly, result.changedMethods }));
        if (!result.passed) Environment.ExitCode = 1;
    }
}
