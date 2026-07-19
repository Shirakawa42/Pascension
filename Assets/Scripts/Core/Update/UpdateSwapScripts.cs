namespace Pascension.Core
{
    /// <summary>
    /// Pure generators for the self-update swap scripts plus the path logic around
    /// them — no UnityEngine, so all of it is covered by the headless test suite.
    /// The scripts embed fully-quoted absolute paths (no argument passing, no
    /// quoting pitfalls). Known limitation: a path containing '%' or '!' would break
    /// the Windows script — impossible for %TEMP%, LocalLow and normal install dirs,
    /// so not defended against.
    /// </summary>
    public static class UpdateSwapScripts
    {
        /// <summary>Windows swap script (.cmd): wait for the game PID to exit, robocopy
        /// the staged build over the install dir (no /PURGE — never delete unknown user
        /// files), relaunch, self-delete. Plain cmd only, AV-friendly. robocopy exit
        /// codes &lt; 8 are success; /R:10 /W:1 rides out transient AV/indexer locks.</summary>
        public static string WindowsCmd(int pid, string stagedDir, string installDir,
            string exeName, string logPath)
        {
            return string.Join("\r\n", new[]
            {
                "@echo off",
                "setlocal",
                "rem Pascension self-update: wait for the game to exit, copy the staged",
                "rem build over the install, relaunch, self-delete.",
                "set \"PID=" + pid + "\"",
                "set \"SRC=" + stagedDir + "\"",
                "set \"DST=" + installDir + "\"",
                "set \"EXE=" + exeName + "\"",
                "set \"LOG=" + logPath + "\"",
                "set TRIES=0",
                "",
                "echo [update] waiting for pid %PID% > \"%LOG%\"",
                ":wait",
                "tasklist /FI \"PID eq %PID%\" 2>nul | find /I \"%EXE%\" >nul",
                "if errorlevel 1 goto copy",
                // Not a parenthesized block: %TRIES% must re-expand each iteration.
                "set /a TRIES+=1",
                "if %TRIES% GEQ 120 goto cleanup",
                // ping as sleep: `timeout` dies when stdin is redirected.
                "ping -n 2 127.0.0.1 >nul",
                "goto wait",
                "",
                ":copy",
                "echo [update] copying >> \"%LOG%\"",
                "robocopy \"%SRC%\" \"%DST%\" /E /R:10 /W:1 /NP >> \"%LOG%\" 2>&1",
                "if errorlevel 8 (",
                "  echo [update] COPY FAILED >> \"%LOG%\"",
                "  goto cleanup",
                ")",
                "echo [update] relaunching >> \"%LOG%\"",
                "start \"\" \"%DST%\\%EXE%\"",
                "",
                ":cleanup",
                "rd /s /q \"%SRC%\" >nul 2>&1",
                "(goto) 2>nul & del \"%~f0\"",
                ""
            });
        }

        /// <summary>macOS swap script (bash): wait for the game PID to exit, swap the
        /// .app with rollback (mv old aside, mv staged in, restore on failure), clear
        /// quarantine defensively, relaunch via `open`, self-delete. Run it with
        /// /bin/bash (no chmod needed).</summary>
        public static string MacosSh(int pid, string stagedAppPath, string installedAppPath,
            string logPath)
        {
            return string.Join("\n", new[]
            {
                "#!/bin/bash",
                "# Pascension self-update: wait for the game to exit, swap the .app",
                "# (with rollback), clear quarantine, relaunch, self-delete.",
                "PID=" + pid,
                "SRC=\"" + stagedAppPath + "\"",
                "DST=\"" + installedAppPath + "\"",
                "LOG=\"" + logPath + "\"",
                "",
                "echo \"[update] waiting for pid $PID\" > \"$LOG\"",
                "i=0",
                "while kill -0 \"$PID\" 2>/dev/null; do",
                "  i=$((i+1))",
                "  if [ \"$i\" -ge 120 ]; then echo \"[update] timeout\" >> \"$LOG\"; exit 1; fi",
                "  sleep 1",
                "done",
                "",
                "rm -rf \"$DST.old\" >> \"$LOG\" 2>&1",
                "if ! mv \"$DST\" \"$DST.old\" >> \"$LOG\" 2>&1; then",
                "  echo \"[update] could not move old app\" >> \"$LOG\"; exit 1",
                "fi",
                "if mv \"$SRC\" \"$DST\" >> \"$LOG\" 2>&1; then",
                "  rm -rf \"$DST.old\" >> \"$LOG\" 2>&1",
                "else",
                "  echo \"[update] swap failed - rolling back\" >> \"$LOG\"",
                "  mv \"$DST.old\" \"$DST\" >> \"$LOG\" 2>&1",
                "fi",
                "xattr -dr com.apple.quarantine \"$DST\" >> \"$LOG\" 2>&1",
                "open \"$DST\"",
                "rm -f -- \"$0\"",
                ""
            });
        }

        /// <summary>Walk up from Unity's dataPath to the .app bundle root
        /// ("…/pascension.app/Contents/Resources/Data" → "…/pascension.app").
        /// Null when no ".app" ancestor exists (e.g. translocated or non-mac path).</summary>
        public static string FindAppBundleRoot(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return null;
            int end = dataPath.Length;
            while (end > 0)
            {
                int sep = dataPath.LastIndexOfAny(new[] { '/', '\\' }, end - 1);
                string segment = dataPath.Substring(sep + 1, end - sep - 1);
                if (segment.Length > 4 &&
                    segment.EndsWith(".app", System.StringComparison.OrdinalIgnoreCase))
                    return dataPath.Substring(0, end);
                if (sep < 0) return null;
                end = sep;
            }
            return null;
        }

        /// <summary>Zip-layout tolerance: if the extracted staging dir holds exactly one
        /// directory and no files, the archive wrapped the build in a folder — descend
        /// into it. Otherwise the staging dir itself is the build root.</summary>
        public static string ResolveStagedRoot(string stagedDir, string[] topLevelDirs,
            string[] topLevelFiles)
        {
            bool singleDirOnly = topLevelDirs != null && topLevelDirs.Length == 1 &&
                                 (topLevelFiles == null || topLevelFiles.Length == 0);
            return singleDirOnly ? topLevelDirs[0] : stagedDir;
        }
    }
}
