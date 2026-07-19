using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Pascension.Editor
{
    /// <summary>
    /// CI entry point for game-ci/unity-builder (invoked via -executeMethod).
    /// unity-builder supplies -buildTarget, -customBuildPath and (from
    /// versioning: Custom) -buildVersion; the workflow adds -ciPublicRelease.
    /// Baking -buildVersion into bundleVersion is the updater contract:
    /// Application.version must equal the release manifest's version.
    /// </summary>
    public static class CiBuild
    {
        public static void Build()
        {
            var args = ParseCommandLine();
            string targetName = Require(args, "buildTarget");     // StandaloneWindows64 | StandaloneOSX
            string outputPath = Require(args, "customBuildPath"); // build/<target>/pascension[.exe|.app]
            string version = args.TryGetValue("buildVersion", out var v) && !string.IsNullOrEmpty(v)
                ? v : "0.0.0";
            bool publicRelease = args.TryGetValue("ciPublicRelease", out var p)
                                 && p.Equals("true", StringComparison.OrdinalIgnoreCase);

            var target = (BuildTarget)Enum.Parse(typeof(BuildTarget), targetName, true);

            PlayerSettings.bundleVersion = version;

            if (target == BuildTarget.StandaloneOSX)
                TrySetMacUniversal();

            if (publicRelease)
                AddDefine("PUBLIC_RELEASE"); // strips SoI registration — see GameCatalog

            // PUBLIC_RELEASE also drops the SoI table scene. (The SoI assemblies and
            // card art still ship — unreachable — until a fuller strip is needed.)
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .Where(path => !publicRelease
                            || !path.EndsWith("GameShards.unity", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Debug.Log($"[CiBuild] target={target} out={outputPath} version={version} publicRelease={publicRelease}");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                target = target,
                locationPathName = outputPath,
                options = BuildOptions.None
            });

            var summary = report.summary;
            Debug.Log($"[CiBuild] result={summary.result} errors={summary.totalErrors} " +
                      $"size={summary.totalSize} time={summary.totalTime}");
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static void AddDefine(string define)
        {
            var standalone = NamedBuildTarget.Standalone;
            string defines = PlayerSettings.GetScriptingDefineSymbols(standalone);
            if (defines.Split(';').Contains(define)) return;
            PlayerSettings.SetScriptingDefineSymbols(standalone,
                string.IsNullOrEmpty(defines) ? define : defines + ";" + define);
        }

        /// <summary>UnityEditor.OSXStandalone.UserBuildSettings only exists when the mac
        /// support module is installed, so the windows-mono CI image could not compile a
        /// direct reference — reflection keeps this one script valid on both images.</summary>
        private static void TrySetMacUniversal()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.OSXStandalone.UserBuildSettings"))
                .FirstOrDefault(t => t != null);
            var prop = type?.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
            {
                Debug.LogWarning("[CiBuild] mac architecture API not found; project default (x64) will be used");
                return;
            }
            prop.SetValue(null, Enum.Parse(prop.PropertyType, "x64ARM64"));
            Debug.Log("[CiBuild] macOS architecture = universal (x64ARM64)");
        }

        private static Dictionary<string, string> ParseCommandLine()
        {
            var argv = Environment.GetCommandLineArgs();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("-")) continue;
                map[argv[i].TrimStart('-')] =
                    i + 1 < argv.Length && !argv[i + 1].StartsWith("-") ? argv[i + 1] : "";
            }
            return map;
        }

        private static string Require(Dictionary<string, string> args, string key) =>
            args.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
                ? value
                : throw new ArgumentException($"[CiBuild] missing required arg -{key}");
    }
}
