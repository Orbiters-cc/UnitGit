using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitBackup
    {
        public string Path = string.Empty;
        public DateTime Created;
        public long Bytes;
        // Set once checked: null until then.
        public bool? Healthy;
        public int Branches;
        public string Problem = string.Empty;

        public string FileName
        {
            get { return System.IO.Path.GetFileName(Path); }
        }
    }

    /// <summary>
    /// Backups of a project's whole Git history: one <c>git bundle</c> file per backup, with every branch and tag, kept in a
    /// folder the user picks (another drive or a synced folder). Unit Git never uploads anything; a backup is the copy
    /// that survives a broken disk or a deleted project. Uncommitted changes and ignored files are not part of it.
    /// </summary>
    internal static class UnitGitBackups
    {
        public const string Extension = ".bundle";
        private const string AutomaticSessionKey = "Orbiters.UnitGit.Backups.AutomaticChecked";
        private static readonly Dictionary<string, UnitGitBackup> Checked = new Dictionary<string, UnitGitBackup>(StringComparer.OrdinalIgnoreCase);

        public static string DefaultFolder(string projectRoot)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(documents, "Unit Git Backups", ProjectName(projectRoot));
        }

        public static string Folder(string projectRoot)
        {
            string chosen = UnitGitSettings.BackupFolder;
            return string.IsNullOrWhiteSpace(chosen) ? DefaultFolder(projectRoot) : chosen;
        }

        public static string ProjectName(string projectRoot)
        {
            string name = System.IO.Path.GetFileName(System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/'));
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Project" : name;
        }

        /// <summary>True when the folder is inside the project: a backup there is lost with the project.</summary>
        public static bool IsInsideProject(string folder, string projectRoot)
        {
            string full = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            string root = System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The backups in the folder, newest first, with what an earlier check found.</summary>
        public static List<UnitGitBackup> List(string folder)
        {
            var backups = new List<UnitGitBackup>();
            if (!Directory.Exists(folder)) return backups;
            foreach (string path in Directory.GetFiles(folder, "*" + Extension))
            {
                var info = new FileInfo(path);
                var backup = new UnitGitBackup { Path = info.FullName, Created = info.LastWriteTime, Bytes = info.Length };
                lock (Checked)
                {
                    if (Checked.TryGetValue(info.FullName, out var known) && known.Bytes == info.Length && known.Created == info.LastWriteTime)
                    {
                        backup.Healthy = known.Healthy;
                        backup.Branches = known.Branches;
                        backup.Problem = known.Problem;
                    }
                }
                backups.Add(backup);
            }
            return backups.OrderByDescending(b => b.Created).ToList();
        }

        /// <summary>Writes a new backup (to a temporary name, checked, then renamed) and removes the oldest beyond <paramref name="keep"/>.</summary>
        public static GitCommandResult Create(UnitGitService git, string folder, int keep)
        {
            Directory.CreateDirectory(folder);
            string name = ProjectName(git.ProjectRoot) + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string final = System.IO.Path.Combine(folder, name + Extension);
            for (int n = 2; File.Exists(final); n++) final = System.IO.Path.Combine(folder, name + "_" + n + Extension);
            string temporary = final + ".partial";
            var result = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "bundle", "create", temporary, "--all");
            if (!result.Success)
            {
                TryDelete(temporary);
                return result;
            }
            var check = Check(git, temporary);
            if (check.Healthy != true)
            {
                TryDelete(temporary);
                return Failure("The backup could not be read back: " + check.Problem);
            }
            File.Move(temporary, final);
            check.Path = System.IO.Path.GetFullPath(final);
            Remember(final, check);
            if (keep > 0)
                foreach (var old in List(folder).Skip(keep)) TryDelete(old.Path);
            return new GitCommandResult { StandardOutput = "Backed up " + check.Branches + " branch" + (check.Branches == 1 ? "" : "es") + " to " + System.IO.Path.GetFileName(final) + "." };
        }

        /// <summary>
        /// Reads the whole backup back into a temporary copy: Git checks every object on the way, which "git bundle verify"
        /// alone does not (it reads the header), so a truncated or corrupted file is caught here.
        /// </summary>
        public static UnitGitBackup Check(UnitGitService git, string path)
        {
            var info = new FileInfo(path);
            var backup = new UnitGitBackup { Path = info.FullName, Created = info.LastWriteTime, Bytes = info.Exists ? info.Length : 0 };
            string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unitgit-check-" + Guid.NewGuid().ToString("N"));
            GitCommandResult verify;
            try
            {
                verify = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "clone", "--bare", "--quiet", path, scratch);
            }
            finally
            {
                DeleteFolder(scratch);
            }
            var heads = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", path);
            backup.Healthy = verify.Success && heads.Success;
            backup.Branches = heads.Success
                ? heads.StandardOutput.Split('\n').Count(line => line.Contains(" refs/heads/"))
                : 0;
            backup.Problem = backup.Healthy == true ? string.Empty : (verify.Success ? heads.Message : verify.Message);
            if (info.Exists) Remember(info.FullName, backup);
            return backup;
        }

        /// <summary>
        /// Recreates the project from a backup in a new, empty folder: its files at the commit that was checked out when the
        /// backup was made. Unity rebuilds the Library on first open.
        /// </summary>
        public static GitCommandResult RestoreCopy(UnitGitService git, string bundle, string destination)
        {
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                return Failure("Choose an empty folder: " + destination + " already has files.");
            var heads = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", bundle);
            if (!heads.Success) return heads;
            // Every branch and tag becomes a local one, with nothing pointing back at the backup file.
            Directory.CreateDirectory(destination);
            var copy = new UnitGitService(destination);
            var result = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "init", "--quiet");
            if (result.Success)
                result = copy.RunGit(UnitGitService.LongTimeoutMilliseconds, "fetch", "--quiet", "--update-head-ok", bundle, "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*");
            string branch = CheckedOutBranch(heads.StandardOutput);
            if (result.Success && branch != null)
                result = copy.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "symbolic-ref", "HEAD", "refs/heads/" + branch);
            if (result.Success)
                result = copy.RunGit(UnitGitService.LongTimeoutMilliseconds, "reset", "--hard", "--quiet");
            if (!result.Success) return result;
            result.StandardOutput = "Restored the project to " + destination + ". Open it with Unity Hub (Add project from disk).";
            return result;
        }

        // The branch the backup's HEAD pointed to: the one at the same commit, preferring main and master.
        private static string CheckedOutBranch(string listHeads)
        {
            var refs = listHeads.Split('\n').Select(line => line.Trim().Split(' ')).Where(parts => parts.Length == 2).ToList();
            string head = refs.Where(parts => parts[1] == "HEAD").Select(parts => parts[0]).FirstOrDefault();
            var branches = refs.Where(parts => parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                .Where(parts => head == null || parts[0] == head).Select(parts => parts[1].Substring(11)).ToList();
            return branches.FirstOrDefault(name => name == "main") ?? branches.FirstOrDefault(name => name == "master") ?? branches.FirstOrDefault();
        }

        public static bool Delete(string path)
        {
            lock (Checked) Checked.Remove(path);
            return TryDelete(path);
        }

        /// <summary>
        /// Once per editor session: backs up in the background when automatic backups are on, the last one is a day old and
        /// the history changed since.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ScheduleAutomatic()
        {
            if (SessionState.GetBool(AutomaticSessionKey, false)) return;
            SessionState.SetBool(AutomaticSessionKey, true);
            if (!UnitGitSettings.AutomaticBackups) return;
            string root = UnitGitService.GetUnityProjectRoot();
            int keep = UnitGitSettings.BackupsToKeep;
            string folder = Folder(root);
            EditorApplication.delayCall += () => Task.Run(() =>
            {
                try
                {
                    var git = new UnitGitService(root);
                    if (!git.IsGitAvailable() || !UnitGitService.HasRepository(root) || !IsDue(git, folder)) return;
                    var result = Create(git, folder, keep);
                    if (!result.Success) UnityEngine.Debug.LogWarning("[Unit Git] Automatic backup failed: " + result.Message);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    UnityEngine.Debug.LogWarning("[Unit Git] Automatic backup failed: " + ex.Message);
                }
            });
        }

        internal static bool IsDue(UnitGitService git, string folder)
        {
            var last = List(folder).FirstOrDefault();
            if (last == null) return HasHistory(git);
            if ((DateTime.Now - last.Created).TotalHours < 24) return false;
            var current = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/tags");
            var saved = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "bundle", "list-heads", last.Path);
            if (!current.Success || !saved.Success) return true;
            return !SameLines(current.StandardOutput, saved.StandardOutput.Split('\n').Select(line => line.Trim()).Where(line => !line.EndsWith(" HEAD", StringComparison.Ordinal)));
        }

        private static bool HasHistory(UnitGitService git)
        {
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", "HEAD").Success;
        }

        private static bool SameLines(string a, IEnumerable<string> b)
        {
            var left = new HashSet<string>(a.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
            var right = new HashSet<string>(b.Select(line => line.Trim()).Where(line => line.Length > 0));
            return left.SetEquals(right);
        }

        private static void Remember(string path, UnitGitBackup backup)
        {
            var info = new FileInfo(path);
            backup.Created = info.LastWriteTime;
            backup.Bytes = info.Length;
            lock (Checked) Checked[info.FullName] = backup;
        }

        private static void DeleteFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult { ExitCode = 1, StandardError = message };
        }
    }
}
