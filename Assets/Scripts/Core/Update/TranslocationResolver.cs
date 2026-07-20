namespace Pascension.Core
{
    /// <summary>
    /// Recovers the real on-disk .app path of a Gatekeeper-translocated macOS app.
    /// A quarantined app launched in place runs from a read-only nullfs mount at
    /// /private/var/folders/…/T/AppTranslocation/&lt;UUID&gt;/d/&lt;App&gt;.app; the mount's
    /// SOURCE is the original bundle — the same mount-table data Apple's private
    /// SecTranslocateCreateOriginalPathForURL reads. Pure string logic over the
    /// output of /sbin/mount, so the headless suite covers it.
    /// </summary>
    public static class TranslocationResolver
    {
        private const string Marker = "/AppTranslocation/";

        public static bool IsTranslocated(string appBundlePath) =>
            !string.IsNullOrEmpty(appBundlePath) && appBundlePath.Contains(Marker);

        /// <summary>The original .app path for a translocated bundle path, or null when
        /// the mount table doesn't confirm it (wrong shape, no nullfs line, renamed app).
        /// Expects the raw stdout of /sbin/mount, lines like
        /// "/Users/x/Downloads/Game.app on /private/var/folders/…/AppTranslocation/UUID (nullfs, …)".</summary>
        public static string ResolveOriginalAppPath(string translocatedAppPath, string mountOutput)
        {
            if (!IsTranslocated(translocatedAppPath) || string.IsNullOrEmpty(mountOutput)) return null;

            // <mountPoint>/d/<name>: the mount point is the …/AppTranslocation/<UUID>
            // dir; the lone "d" directory is synthesized inside the mount.
            int marker = translocatedAppPath.IndexOf(Marker, System.StringComparison.Ordinal);
            int uuidEnd = translocatedAppPath.IndexOf('/', marker + Marker.Length);
            if (uuidEnd < 0) return null;
            string mountPoint = translocatedAppPath.Substring(0, uuidEnd);
            string remainder = translocatedAppPath.Substring(uuidEnd);
            if (!remainder.StartsWith("/d/", System.StringComparison.Ordinal)) return null;
            string name = remainder.Substring(3);
            if (name.Length == 0 || name.IndexOf('/') >= 0) return null;

            // The kernel reports mounts under /private/var/…; Unity may report either
            // /var/… (symlink) or the /private form — try both spellings.
            foreach (string candidate in MountPointSpellings(mountPoint))
            {
                string source = NullfsSourceForMountPoint(mountOutput, candidate);
                if (source == null) continue;
                // Trimmed: a trailing '/' would break Path.GetDirectoryName upstream.
                string original = source.TrimEnd('/');
                if (Basename(original) == name)
                    return original;
            }
            return null;
        }

        private static string[] MountPointSpellings(string mountPoint)
        {
            if (mountPoint.StartsWith("/private/var/", System.StringComparison.Ordinal))
                return new[] { mountPoint, mountPoint.Substring("/private".Length) };
            if (mountPoint.StartsWith("/var/", System.StringComparison.Ordinal))
                return new[] { mountPoint, "/private" + mountPoint };
            return new[] { mountPoint };
        }

        private static string NullfsSourceForMountPoint(string mountOutput, string mountPoint)
        {
            string token = " on " + mountPoint + " (";
            foreach (string line in mountOutput.Split('\n'))
            {
                int at = line.LastIndexOf(token, System.StringComparison.Ordinal);
                if (at <= 0) continue;
                string flags = line.Substring(at + token.Length).TrimEnd('\r');
                if (!flags.Contains("nullfs")) continue;
                string source = line.Substring(0, at);
                if (source.StartsWith("/", System.StringComparison.Ordinal)) return source;
            }
            return null;
        }

        private static string Basename(string path)
        {
            string trimmed = path.TrimEnd('/');
            return trimmed.Substring(trimmed.LastIndexOf('/') + 1);
        }
    }
}
