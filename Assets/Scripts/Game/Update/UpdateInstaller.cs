using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Pascension.Core;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Pascension.Game.Update
{
    /// <summary>User-visible update failure carrying a friendly English message
    /// (localize with Loc.T at display time); the raw cause goes to the log.</summary>
    public sealed class UpdateFailedException : Exception
    {
        public UpdateFailedException(string userMessageEnglish, Exception inner = null)
            : base(userMessageEnglish, inner) { }
    }

    /// <summary>
    /// The self-update pipeline: re-fetch manifest → stream the package to disk →
    /// SHA256 verify → extract to a staging dir → hand off to a swap script that
    /// waits for this process to exit, copies the new build over the install and
    /// relaunches. On success RunAsync does not return (Application.Quit) — except
    /// in the editor, where the swap is skipped after staging (dev dry-run).
    /// </summary>
    public sealed class UpdateInstaller
    {
        public enum Phase { Idle, Downloading, Verifying, Extracting, Launching }

        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public float DownloadProgress { get; private set; }

        public static string UpdatesRoot => Path.Combine(Application.persistentDataPath, "updates");
        private static string DownloadDir => Path.Combine(UpdatesRoot, "download");
        private static string StagedDir => Path.Combine(UpdatesRoot, "staged");
        private static string SwapLogPath => Path.Combine(UpdatesRoot, "swap.log");

        private static bool IsMac =>
            Application.platform == RuntimePlatform.OSXPlayer ||
            Application.platform == RuntimePlatform.OSXEditor;

        /// <summary>Boot housekeeping: sweep partial downloads and stale staging from an
        /// aborted or completed update. Best-effort — IO errors are swallowed.</summary>
        public static void CleanupStale()
        {
            try { if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true); } catch { }
            try { if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, true); } catch { }
        }

        /// <summary>Can this install self-swap? False (→ OPEN DOWNLOAD PAGE fallback)
        /// when the install dir isn't writable or the macOS app is translocated.</summary>
        public static bool CanSelfInstall(out string installRoot)
        {
            installRoot = null;
            string probeDir;
            if (IsMac && !Application.isEditor)
            {
                string app = UpdateSwapScripts.FindAppBundleRoot(Application.dataPath);
                if (app == null || app.Contains("/AppTranslocation/")) return false;
                installRoot = app;
                probeDir = Path.GetDirectoryName(app);
            }
            else
            {
                installRoot = Path.GetDirectoryName(Application.dataPath);
                probeDir = installRoot;
            }
            try
            {
                string probe = Path.Combine(probeDir, ".update_probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Full pipeline; throws UpdateFailedException with a friendly message.
        /// Outside the editor this method does not return on success.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            // Fresh manifest right before downloading: the cached check may be hours
            // old and a newer release would make its sha256 stale.
            CurrentPhase = Phase.Downloading;
            DownloadProgress = 0f;
            var manifest = await UpdateChecker.FetchManifestAsync(10);
            var package = UpdateChecker.PackageForThisPlatform(manifest);
            if (package == null)
                throw new UpdateFailedException("Automatic update unavailable — opening the download page.");

            CheckDiskSpace(package.SizeBytes);

            CleanupStale();
            Directory.CreateDirectory(DownloadDir);
            Directory.CreateDirectory(StagedDir);

            string archivePath = Path.Combine(DownloadDir, IsMac ? "pkg.tar.gz" : "pkg.zip");
            string partPath = archivePath + ".part";
            await DownloadAsync(package.Url, partPath, ct);
            File.Move(partPath, archivePath);

            CurrentPhase = Phase.Verifying;
            string hash = await ComputeSha256Async(archivePath, ct);
            if (!string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(archivePath); } catch { }
                throw new UpdateFailedException("File verification failed — try again.",
                    new Exception("sha256 " + hash + " != manifest " + package.Sha256));
            }

            CurrentPhase = Phase.Extracting;
            string stagedRoot = await ExtractAsync(archivePath, StagedDir, ct);
            try { File.Delete(archivePath); } catch { } // free the archive's disk space

            CurrentPhase = Phase.Launching;
            LaunchSwapAndQuit(stagedRoot);
        }

        private static void CheckDiskSpace(long packageBytes)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Application.persistentDataPath));
                // archive + staged copy + slack
                if (packageBytes > 0 && drive.AvailableFreeSpace < packageBytes * 5 / 2)
                    throw new UpdateFailedException("Not enough disk space for the update.");
            }
            catch (UpdateFailedException) { throw; }
            catch { /* DriveInfo can throw on exotic mounts — skip the preflight */ }
        }

        private async Task DownloadAsync(string url, string partPath, CancellationToken ct)
        {
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
            request.downloadHandler = new DownloadHandlerFile(partPath) { removeFileOnAbort = true };
            request.timeout = 0; // big file; progress is the liveness signal
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    request.Abort(); // removeFileOnAbort deletes the .part
                    throw new OperationCanceledException(ct);
                }
                DownloadProgress = request.downloadProgress;
                await Task.Yield();
            }
            if (request.result != UnityWebRequest.Result.Success)
                throw new UpdateFailedException("Download failed — check your connection.",
                    new Exception(request.error + " (" + request.responseCode + ") " + url));
            DownloadProgress = 1f;
        }

        private static Task<string> ComputeSha256Async(string file, CancellationToken ct) =>
            Task.Run(() =>
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(file);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                var hex = new System.Text.StringBuilder(64);
                foreach (byte b in sha.Hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }, ct);

        private static async Task<string> ExtractAsync(string archive, string stagedDir, CancellationToken ct)
        {
            if (IsMac)
            {
                // System.IO.Compression would strip the .app's exec bits and symlinks —
                // extract with the system tar, which preserves both.
                var info = new ProcessStartInfo
                {
                    FileName = "/usr/bin/tar",
                    Arguments = "-xzf \"" + archive + "\" -C \"" + stagedDir + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(info);
                string stderr = await Task.Run(() =>
                {
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    return err;
                }, ct);
                if (proc.ExitCode != 0)
                    throw new UpdateFailedException("Update failed — see the log.",
                        new Exception("tar exited " + proc.ExitCode + ": " + stderr));
                return stagedDir; // the swap script targets the .app inside (FindStagedApp)
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(archive, stagedDir), ct);
            return UpdateSwapScripts.ResolveStagedRoot(stagedDir,
                Directory.GetDirectories(stagedDir), Directory.GetFiles(stagedDir));
        }

        private void LaunchSwapAndQuit(string stagedRoot)
        {
            if (Application.isEditor)
            {
                Debug.Log("[Update] editor — swap skipped (staged at " + stagedRoot + ")");
                return;
            }

            using var self = Process.GetCurrentProcess();
            string scriptPath;
            ProcessStartInfo start;
            if (IsMac)
            {
                string installedApp = UpdateSwapScripts.FindAppBundleRoot(Application.dataPath)
                    ?? throw new UpdateFailedException("Automatic update unavailable — opening the download page.");
                string stagedApp = FindStagedApp(stagedRoot)
                    ?? throw new UpdateFailedException("Update failed — see the log.",
                        new Exception("no .app found in the staged update"));
                scriptPath = Path.Combine(Path.GetTempPath(), "pascension_update.sh");
                File.WriteAllText(scriptPath, UpdateSwapScripts.MacosSh(self.Id, stagedApp, installedApp, SwapLogPath));
                start = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "\"" + scriptPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                string installDir = Path.GetDirectoryName(Application.dataPath);
                string exeName = Path.GetFileName(self.MainModule?.FileName ?? "pascension.exe");
                scriptPath = Path.Combine(Path.GetTempPath(), "pascension_update.cmd");
                File.WriteAllText(scriptPath, UpdateSwapScripts.WindowsCmd(self.Id, stagedRoot, installDir, exeName, SwapLogPath));
                start = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }

            Debug.Log("[Update] launching swap script " + scriptPath + " and quitting");
            Process.Start(start);

            if (Unity.Netcode.NetworkManager.Singleton != null &&
                Unity.Netcode.NetworkManager.Singleton.IsListening)
                Pascension.Net.NetLauncher.Shutdown();
            Application.Quit();
        }

        /// <summary>The .app inside the staging dir (tolerating one wrapper folder).</summary>
        private static string FindStagedApp(string stagedRoot)
        {
            var apps = Directory.GetDirectories(stagedRoot, "*.app");
            if (apps.Length > 0) return apps[0];
            var dirs = Directory.GetDirectories(stagedRoot);
            var files = Directory.GetFiles(stagedRoot);
            if (dirs.Length == 1 && files.Length == 0)
            {
                apps = Directory.GetDirectories(dirs[0], "*.app");
                if (apps.Length > 0) return apps[0];
            }
            return null;
        }
    }
}
