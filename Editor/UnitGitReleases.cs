using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>
    /// A single key/value pair of tool-specific release data, displayed generically by Unit Git.
    /// </summary>
    [Serializable]
    public sealed class UnitGitReleaseField
    {
        public string key = string.Empty;
        public string value = string.Empty;

        public UnitGitReleaseField()
        {
        }

        public UnitGitReleaseField(string key, string value)
        {
            this.key = key ?? string.Empty;
            this.value = value ?? string.Empty;
        }
    }

    /// <summary>
    /// A release checkpoint recorded in the repository. Intentionally tool-agnostic so any
    /// publisher (MCB versions, VRChat avatar uploads via the VRCSDK, ...) can record releases
    /// that Unit Git keeps displaying even after the publishing tool is removed.
    /// </summary>
    [Serializable]
    public sealed class UnitGitReleaseEntry
    {
        public string id = string.Empty;
        public string tool = string.Empty;       // e.g. "MCB", "VRChat SDK"
        public string type = string.Empty;       // e.g. "mcb-version", "vrchat-avatar-upload"
        public string name = string.Empty;       // asset / product name
        public string version = string.Empty;    // e.g. "1.3.1"
        public string title = string.Empty;      // release title
        public string changelog = string.Empty;
        public string scope = string.Empty;      // e.g. "PUBLIC", "BETA"
        public string date = string.Empty;       // ISO-8601 (UTC)
        public string author = string.Empty;
        public List<UnitGitReleaseField> fields = new List<UnitGitReleaseField>();
    }

    [Serializable]
    public sealed class UnitGitReleaseFile
    {
        public int formatVersion = 1;
        public List<UnitGitReleaseEntry> releases = new List<UnitGitReleaseEntry>();
    }

    public sealed class UnitGitReleaseResult
    {
        public bool Success;
        public string Message = string.Empty;
        public string CommitHash = string.Empty;
        public string ReleaseId = string.Empty;
    }

    /// <summary>
    /// Public entry point other tools use to record release checkpoints in the Git history.
    /// The release data lives in <c>.unitgit-releases.json</c> at the project root (committed with
    /// the release), and the commit is associated with its entry through a
    /// <c>UnitGit-Release: &lt;id&gt;</c> commit-message trailer.
    /// </summary>
    public static class UnitGitReleases
    {
        private sealed class ReleaseFileSnapshot
        {
            public bool FileExists;
            public string FileContents = string.Empty;
            public bool HasIndexEntry;
            public string IndexMode = string.Empty;
            public string IndexHash = string.Empty;
        }

        public const int ApiVersion = 2;
        public const string CommitFilesCapability = "commit-files";
        public const string ScopedReleaseCheckpointCapability = "scoped-release-checkpoint";
        public const string FullProjectReleaseCheckpointCapability = "full-project-release-checkpoint";
        public const string ReleasesFileName = ".unitgit-releases.json";
        public const string TrailerKey = "UnitGit-Release";

        /// <summary>
        /// Raised after an external tool successfully created a commit through this API, so open
        /// Unit Git windows can refresh their history immediately.
        /// </summary>
        public static event Action ChangedExternally;

        private static void RaiseChangedExternally()
        {
            try
            {
                ChangedExternally?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnitGit] ChangedExternally handler failed: " + ex.Message);
            }
        }

        public static string GetReleasesFilePath(string projectRoot)
        {
            return Path.Combine(projectRoot, ReleasesFileName);
        }

        public static UnitGitReleaseFile Load(string projectRoot)
        {
            try
            {
                string path = GetReleasesFilePath(projectRoot);
                if (!File.Exists(path))
                {
                    return new UnitGitReleaseFile();
                }

                var parsed = JsonUtility.FromJson<UnitGitReleaseFile>(File.ReadAllText(path));
                if (parsed == null)
                {
                    return new UnitGitReleaseFile();
                }

                parsed.releases = parsed.releases ?? new List<UnitGitReleaseEntry>();
                return parsed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnitGit] Could not read " + ReleasesFileName + ": " + ex.Message);
                return new UnitGitReleaseFile();
            }
        }

        public static UnitGitReleaseEntry FindById(UnitGitReleaseFile file, string id)
        {
            if (file == null || file.releases == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            string normalized = id.Trim();
            return file.releases.FirstOrDefault(entry =>
                entry != null &&
                string.Equals((entry.id ?? string.Empty).Trim(), normalized, StringComparison.Ordinal));
        }

        public static string[] GetCapabilities()
        {
            return new[]
            {
                CommitFilesCapability,
                ScopedReleaseCheckpointCapability,
                FullProjectReleaseCheckpointCapability
            };
        }

        /// <summary>
        /// Appends the release entry to the releases file and commits only the release file plus
        /// the explicit project-relative paths supplied by the publisher.
        /// </summary>
        public static UnitGitReleaseResult PublishRelease(UnitGitReleaseEntry entry, string commitTitle, params string[] projectRelativePaths)
        {
            return PublishRelease(entry, commitTitle, new UnitGitService(), projectRelativePaths);
        }

        internal static UnitGitReleaseResult PublishRelease(UnitGitReleaseEntry entry, string commitTitle, UnitGitService service, params string[] projectRelativePaths)
        {
            if (entry == null)
            {
                return Fail("Release entry is required.");
            }

            if (service == null)
            {
                return Fail("Unit Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            if (string.IsNullOrWhiteSpace(entry.id))
            {
                entry.id = GenerateReleaseId(entry);
            }

            if (string.IsNullOrWhiteSpace(entry.date))
            {
                entry.date = DateTime.UtcNow.ToString("o");
            }

            ReleaseFileSnapshot snapshot = CaptureReleaseFileSnapshot(service);
            try
            {
                UnitGitReleaseFile file = Load(service.ProjectRoot);
                file.releases.Add(entry);
                File.WriteAllText(GetReleasesFilePath(service.ProjectRoot), JsonUtility.ToJson(file, true));
            }
            catch (Exception ex)
            {
                RestoreReleaseFileSnapshot(service, snapshot, out _);
                return Fail("Could not write " + ReleasesFileName + ": " + ex.Message);
            }

            string title = string.IsNullOrWhiteSpace(commitTitle)
                ? BuildDefaultCommitTitle(entry)
                : commitTitle.Trim();
            UnitGitReleaseResult result = CommitFiles(
                service,
                title,
                TrailerKey + ": " + entry.id,
                BuildReleaseCommitPaths(projectRelativePaths));
            if (!result.Success)
            {
                if (!RestoreReleaseFileSnapshot(service, snapshot, out string rollbackMessage) &&
                    !string.IsNullOrWhiteSpace(rollbackMessage))
                {
                    result.Message = result.Message + "\nRollback failed: " + rollbackMessage;
                }

                return result;
            }

            result.ReleaseId = result.Success ? entry.id : result.ReleaseId;
            return result;
        }

        /// <summary>
        /// Appends the release entry and commits every pending project change. Used by upload
        /// workflows where the built content can touch scenes, assets, settings, and package files.
        /// </summary>
        public static UnitGitReleaseResult PublishReleaseAll(UnitGitReleaseEntry entry, string commitTitle)
        {
            return PublishReleaseAll(entry, commitTitle, new UnitGitService(), true);
        }

        internal static UnitGitReleaseResult PublishReleaseAll(UnitGitReleaseEntry entry, string commitTitle, UnitGitService service, bool notifyChangedExternally)
        {
            if (entry == null)
            {
                return Fail("Release entry is required.");
            }

            if (service == null)
            {
                return Fail("Unit Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            if (string.IsNullOrWhiteSpace(entry.id))
            {
                entry.id = GenerateReleaseId(entry);
            }

            if (string.IsNullOrWhiteSpace(entry.date))
            {
                entry.date = DateTime.UtcNow.ToString("o");
            }

            ReleaseFileSnapshot snapshot = CaptureReleaseFileSnapshot(service);
            try
            {
                UnitGitReleaseFile file = Load(service.ProjectRoot);
                file.releases.Add(entry);
                File.WriteAllText(GetReleasesFilePath(service.ProjectRoot), JsonUtility.ToJson(file, true));
            }
            catch (Exception ex)
            {
                RestoreReleaseFileSnapshot(service, snapshot, out _);
                return Fail("Could not write " + ReleasesFileName + ": " + ex.Message);
            }

            string title = string.IsNullOrWhiteSpace(commitTitle)
                ? BuildDefaultCommitTitle(entry)
                : commitTitle.Trim();
            UnitGitReleaseResult result = StageAllAndCommit(
                service,
                title,
                TrailerKey + ": " + entry.id,
                notifyChangedExternally);
            if (!result.Success)
            {
                if (!RestoreReleaseFileSnapshot(service, snapshot, out string rollbackMessage) &&
                    !string.IsNullOrWhiteSpace(rollbackMessage))
                {
                    result.Message = result.Message + "\nRollback failed: " + rollbackMessage;
                }

                return result;
            }

            result.ReleaseId = entry.id;
            return result;
        }

        /// <summary>
        /// Stages every pending change and commits it. Used by external tools (and connector
        /// tests) that want a plain commit without recording a release entry.
        /// </summary>
        public static UnitGitReleaseResult CommitAll(string commitTitle, string trailingParagraph = null)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            var service = new UnitGitService();
            return CommitAll(commitTitle, trailingParagraph, service, true);
        }

        internal static UnitGitReleaseResult CommitAll(string commitTitle, string trailingParagraph, UnitGitService service, bool notifyChangedExternally)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            if (service == null)
            {
                return Fail("Unit Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            return StageAllAndCommit(service, commitTitle.Trim(), trailingParagraph, notifyChangedExternally);
        }

        /// <summary>
        /// Commits only the given project-relative paths (staging them first), leaving any other
        /// pending change untouched. Used by connector tests.
        /// </summary>
        public static UnitGitReleaseResult CommitFiles(string commitTitle, string trailingParagraph, params string[] projectRelativePaths)
        {
            return CommitFiles(new UnitGitService(), commitTitle, trailingParagraph, projectRelativePaths);
        }

        private static UnitGitReleaseResult CommitFiles(UnitGitService service, string commitTitle, string trailingParagraph, params string[] projectRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(commitTitle))
            {
                return Fail("Commit title is required.");
            }

            string[] paths = (projectRelativePaths ?? Array.Empty<string>())
                .Select(NormalizeProjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return Fail("At least one file path is required.");
            }

            if (service == null)
            {
                return Fail("Unit Git service is required.");
            }

            UnitGitReleaseResult precondition = CheckPreconditions(service);
            if (precondition != null)
            {
                return precondition;
            }

            var addArgs = new List<string> { "add", "--" };
            addArgs.AddRange(paths);
            GitCommandResult stage = RunWithIndexLockRetry(() => service.RunGit(UnitGitService.LongTimeoutMilliseconds, addArgs.ToArray()));
            if (!stage.Success && IsIndexLockFailure(stage) && TryRemoveStaleIndexLock(service.ProjectRoot))
            {
                stage = RunWithIndexLockRetry(() => service.RunGit(UnitGitService.LongTimeoutMilliseconds, addArgs.ToArray()));
            }

            if (!stage.Success)
            {
                return Fail("Staging files failed: " + stage.Message);
            }

            var commitArgs = new List<string> { "commit", "-m", commitTitle.Trim() };
            if (!string.IsNullOrWhiteSpace(trailingParagraph))
            {
                commitArgs.Add("-m");
                commitArgs.Add(trailingParagraph.Trim());
            }

            commitArgs.Add("--");
            commitArgs.AddRange(paths);
            GitCommandResult commit = RunWithIndexLockRetry(() => service.RunGit(UnitGitService.LongTimeoutMilliseconds, commitArgs.ToArray()));
            if (!commit.Success)
            {
                return Fail("Commit failed: " + commit.Message);
            }

            var result = new UnitGitReleaseResult
            {
                Success = true,
                Message = commit.Message
            };

            GitCommandResult head = service.RunGit(10000, "rev-parse", "HEAD");
            if (head.Success)
            {
                result.CommitHash = head.StandardOutput.Trim();
            }

            RaiseChangedExternally();
            return result;
        }

        internal static void NotifyChangedExternally()
        {
            RaiseChangedExternally();
        }

        private static string[] BuildReleaseCommitPaths(IEnumerable<string> projectRelativePaths)
        {
            var paths = new List<string> { ReleasesFileName };
            foreach (string path in projectRelativePaths ?? Enumerable.Empty<string>())
            {
                string normalized = NormalizeProjectPath(path);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                paths.Add(normalized);
            }

            return paths.ToArray();
        }

        private static ReleaseFileSnapshot CaptureReleaseFileSnapshot(UnitGitService service)
        {
            var snapshot = new ReleaseFileSnapshot();
            string path = GetReleasesFilePath(service.ProjectRoot);
            snapshot.FileExists = File.Exists(path);
            if (snapshot.FileExists)
            {
                snapshot.FileContents = File.ReadAllText(path);
            }

            GitCommandResult indexEntry = service.RunGit(10000, "ls-files", "--stage", "--", ReleasesFileName);
            if (indexEntry.Success && !string.IsNullOrWhiteSpace(indexEntry.StandardOutput))
            {
                string line = indexEntry.StandardOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 2)
                    {
                        snapshot.HasIndexEntry = true;
                        snapshot.IndexMode = fields[0];
                        snapshot.IndexHash = fields[1];
                    }
                }
            }

            return snapshot;
        }

        private static bool RestoreReleaseFileSnapshot(UnitGitService service, ReleaseFileSnapshot snapshot, out string message)
        {
            message = string.Empty;
            if (service == null || snapshot == null)
            {
                message = "Missing rollback context.";
                return false;
            }

            try
            {
                string path = GetReleasesFilePath(service.ProjectRoot);
                if (snapshot.FileExists)
                {
                    File.WriteAllText(path, snapshot.FileContents ?? string.Empty);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                GitCommandResult indexResult = snapshot.HasIndexEntry
                    ? service.RunGit(10000, "update-index", "--cacheinfo", snapshot.IndexMode, snapshot.IndexHash, ReleasesFileName)
                    : service.RunGit(10000, "rm", "--cached", "--ignore-unmatch", "--", ReleasesFileName);

                if (!indexResult.Success)
                {
                    message = indexResult.Message;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private static string NormalizeProjectPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/');
        }

        private static UnitGitReleaseResult CheckPreconditions(UnitGitService service)
        {
            if (!UnitGitService.IsUnityProject(service.ProjectRoot))
            {
                return Fail("No Unity project markers were found at " + service.ProjectRoot + ".");
            }

            if (!UnitGitService.HasRepository(service.ProjectRoot))
            {
                return Fail("No Git repository exists at the Unity project root. Initialize one with Unit Git first.");
            }

            if (!service.IsGitAvailable())
            {
                return Fail("Git was not found on PATH.");
            }

            return null;
        }

        private static UnitGitReleaseResult StageAllAndCommit(UnitGitService service, string title, string trailingParagraph, bool notifyChangedExternally = true)
        {
            GitCommandResult stage = RunWithIndexLockRetry(() => service.StageAll());
            if (!stage.Success && IsIndexLockFailure(stage) && TryRemoveStaleIndexLock(service.ProjectRoot))
            {
                stage = RunWithIndexLockRetry(() => service.StageAll());
            }

            if (!stage.Success)
            {
                return Fail("Staging changes failed: " + stage.Message);
            }

            GitCommandResult commit = RunWithIndexLockRetry(() => service.Commit(title, trailingParagraph));
            if (!commit.Success)
            {
                return Fail("Commit failed: " + commit.Message);
            }

            var result = new UnitGitReleaseResult
            {
                Success = true,
                Message = commit.Message
            };

            GitCommandResult head = service.RunGit(10000, "rev-parse", "HEAD");
            if (head.Success)
            {
                result.CommitHash = head.StandardOutput.Trim();
            }

            if (notifyChangedExternally)
            {
                RaiseChangedExternally();
            }

            return result;
        }

        internal static string BuildDefaultCommitTitle(UnitGitReleaseEntry entry)
        {
            string tool = string.IsNullOrWhiteSpace(entry.tool) ? "Release" : entry.tool.Trim();
            string version = string.IsNullOrWhiteSpace(entry.version) ? string.Empty : " v" + entry.version.Trim();
            string title = string.IsNullOrWhiteSpace(entry.title) ? string.Empty : " - " + entry.title.Trim();
            return tool + " :" + version + title;
        }

        internal static string GenerateReleaseId(UnitGitReleaseEntry entry)
        {
            string tool = Sanitize(entry != null ? entry.tool : null, "release");
            string version = Sanitize(entry != null ? entry.version : null, "0");
            return tool + "-" + version + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        /// <summary>
        /// Retries an index-mutating Git command when another process (typically a concurrent
        /// "git status" refreshing the index) is briefly holding .git/index.lock.
        /// </summary>
        private static GitCommandResult RunWithIndexLockRetry(Func<GitCommandResult> operation, int maxAttempts = 6, int delayMilliseconds = 500)
        {
            GitCommandResult result = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                result = operation();
                if (result.Success || !IsIndexLockFailure(result))
                {
                    return result;
                }

                System.Threading.Thread.Sleep(delayMilliseconds);
            }

            return result;
        }

        private static bool IsIndexLockFailure(GitCommandResult result)
        {
            return result != null &&
                   !string.IsNullOrEmpty(result.StandardError) &&
                   result.StandardError.IndexOf("index.lock", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Removes .git/index.lock only when it is clearly stale (left behind by a crashed git
        /// process): retries already failed and the lock has not been touched for a while.
        /// </summary>
        private static bool TryRemoveStaleIndexLock(string projectRoot)
        {
            try
            {
                string lockPath = Path.Combine(projectRoot, ".git", "index.lock");
                if (!File.Exists(lockPath))
                {
                    return false;
                }

                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromMinutes(2))
                {
                    return false;
                }

                File.Delete(lockPath);
                Debug.LogWarning("[UnitGit] Removed stale .git/index.lock left behind by a previous git process.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnitGit] Could not remove stale .git/index.lock: " + ex.Message);
                return false;
            }
        }

        private static string Sanitize(string value, string fallback)
        {
            string sanitized = Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9._-]+", "-").Trim('-').ToLowerInvariant();
            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        private static UnitGitReleaseResult Fail(string message)
        {
            return new UnitGitReleaseResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
