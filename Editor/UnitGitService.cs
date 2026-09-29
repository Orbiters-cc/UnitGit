using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitService
    {
        private const char FieldSeparator = '\x1f';
        private const char BranchFieldSeparator = '\t';
        private const int MaxUntrackedDiffPreviewBytes = 512 * 1024;
        private const int BinarySniffBytes = 4096;
        internal const int DefaultTimeoutMilliseconds = 30000;
        public const int LongTimeoutMilliseconds = 900000;
        private static readonly Regex AheadRegex = new Regex(@"ahead\s+(\d+)", RegexOptions.Compiled);
        private static readonly Regex BehindRegex = new Regex(@"behind\s+(\d+)", RegexOptions.Compiled);

        private sealed class CommitRewriteInfo
        {
            public string Hash = string.Empty;
            public string TreeHash = string.Empty;
            public List<string> Parents = new List<string>();
            public string AuthorName = string.Empty;
            public string AuthorEmail = string.Empty;
            public string AuthorDate = string.Empty;
            public string Message = string.Empty;
        }

        public UnitGitService()
            : this(GetUnityProjectRoot())
        {
        }

        public UnitGitService(string projectRoot)
        {
            ProjectRoot = projectRoot;
        }

        public string ProjectRoot { get; private set; }

        public UnitGitProcessLogHandler ProcessLogReceived { get; set; }
        public Func<bool> ReadSuperseded { get; set; }

        public UnitGitSnapshot BuildSnapshot(string logSearch, int historyLimit = 300)
        {
            var snapshot = new UnitGitSnapshot
            {
                ProjectRoot = ProjectRoot,
                GitAvailable = IsGitAvailable(),
                IsUnityProject = IsUnityProject(ProjectRoot),
                HasRepository = HasRepository(ProjectRoot)
            };

            if (!snapshot.GitAvailable)
            {
                snapshot.LastError = "Git was not found on PATH. Install Git and reopen Unity.";
                return snapshot;
            }

            if (!snapshot.IsUnityProject)
            {
                snapshot.LastError = "No Unity project markers were found beside the Assets folder.";
                return snapshot;
            }

            if (!snapshot.HasRepository)
            {
                snapshot.LastError = "No Git repository exists at the Unity project root.";
                return snapshot;
            }

            UnitGitProjectInitializer.EnsureDefaultRules(ProjectRoot);

            var status = RunGit(DefaultTimeoutMilliseconds, "status", "--porcelain=v1", "-b", "-uall");
            if (!status.Success)
            {
                snapshot.HasCommits = HasCommits();
                snapshot.LastError = status.Message;
                return snapshot;
            }

            snapshot.HasCommits = HasCommits();
            ParseStatus(status.StandardOutput, snapshot);
            snapshot.ChangeList = UnitGitFileList.FromChanges(snapshot.Changes);
            snapshot.Branches = GetBranches();
            snapshot.Releases = UnitGitReleases.Load(ProjectRoot);
            if (snapshot.HasCommits)
            {
                historyLimit = Math.Max(1, historyLimit);
                snapshot.Commits = GetCommits(logSearch, historyLimit + 1);
                snapshot.HasMoreCommits = snapshot.Commits.Count > historyLimit;
                if (snapshot.HasMoreCommits) snapshot.Commits.RemoveAt(historyLimit);
                var message = GetHeadCommitMessage();
                if (message.Success)
                    snapshot.HeadMessage = message.StandardOutput;
                var shelves = RunGit(DefaultTimeoutMilliseconds, "stash", "list");
                if (shelves.Success)
                    snapshot.Shelves = ParseStashList(shelves.StandardOutput);
                ReadTrackedIgnoredDownloads(snapshot);
            }

            return snapshot;
        }

        private void ReadTrackedIgnoredDownloads(UnitGitSnapshot snapshot)
        {
            if (!Directory.Exists(Path.Combine(ProjectRoot, UnitGitProjectInitializer.McbDownloadsFolder))) return;
            var tracked = RunGit(DefaultTimeoutMilliseconds, "ls-files", "-z", "--cached", "--ignored", "--exclude-standard", "--", UnitGitProjectInitializer.McbDownloadsFolder);
            if (!tracked.Success) return;
            foreach (string entry in tracked.StandardOutput.Split('\0'))
            {
                string path = entry.Trim('\r', '\n');
                if (path.Length == 0) continue;
                snapshot.TrackedIgnoredDownloads.Add(path);
                var file = new FileInfo(Path.Combine(ProjectRoot, path));
                if (file.Exists) snapshot.TrackedIgnoredDownloadBytes += file.Length;
            }
        }

        /// <summary>
        /// Writes one version of a file byte for byte ("HEAD:path", ":2:path", "hash:path"): models and textures are binary,
        /// and <see cref="RunGit"/> reads output as text. False when that version does not exist.
        /// </summary>
        public bool WriteBlob(string revisionPath, string destination)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--literal-pathspecs cat-file blob " + EscapeArgument(revisionPath),
                WorkingDirectory = ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            using (var process = Process.Start(start))
            {
                var error = process.StandardError.ReadToEndAsync();
                using (var file = File.Create(destination)) process.StandardOutput.BaseStream.CopyTo(file);
                if (!process.WaitForExit(LongTimeoutMilliseconds))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    File.Delete(destination);
                    return false;
                }
                error.Wait();
                if (process.ExitCode == 0) return true;
            }
            File.Delete(destination);
            return false;
        }

        /// <summary>Stops tracking these files: they stay on disk and their removal is staged for the next commit.</summary>
        public GitCommandResult StopTracking(IList<string> paths)
        {
            var result = new GitCommandResult { ExitCode = 0 };
            for (int start = 0; start < paths.Count; start += 50)
            {
                var args = new List<string> { "rm", "--cached", "--quiet", "--ignore-unmatch", "--" };
                for (int i = start; i < Math.Min(paths.Count, start + 50); i++) args.Add(paths[i]);
                result = RunGit(LongTimeoutMilliseconds, args.ToArray());
                if (!result.Success) return result;
            }
            result.StandardOutput = "Stopped tracking " + paths.Count + " files. They stay on disk; commit to record it.";
            return result;
        }

        public UnitGitCommitDetails GetCommitDetails(string fullHash)
        {
            if (string.IsNullOrWhiteSpace(fullHash))
            {
                return null;
            }

            var result = RunGit(
                DefaultTimeoutMilliseconds,
                "show",
                "--no-ext-diff",
                "--name-only",
                "--date=format-local:%m/%d/%Y %I:%M %p",
                "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ad%x1f%D%x1f%cn%x1f%ce%x1f%cd%x1f%(trailers:key=" + UnitGitReleases.TrailerKey + ",valueonly,separator=%x2C)",
                fullHash);

            if (!result.Success)
            {
                // Older Git versions do not support the %(trailers) pretty-format placeholder.
                result = RunGit(
                    DefaultTimeoutMilliseconds,
                    "show",
                    "--no-ext-diff",
                    "--name-only",
                    "--date=format-local:%m/%d/%Y %I:%M %p",
                    "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ad%x1f%D%x1f%cn%x1f%ce%x1f%cd",
                    fullHash);
            }

            if (!result.Success)
            {
                return new UnitGitCommitDetails
                {
                    Commit = new UnitGitCommit
                    {
                        FullHash = fullHash,
                        Subject = result.Message
                    }
                };
            }

            var lines = SplitLines(result.StandardOutput).ToList();
            if (lines.Count == 0)
            {
                return null;
            }

            var fields = lines[0].Split(FieldSeparator);
            var details = new UnitGitCommitDetails();
            if (fields.Length >= 10)
            {
                details.Commit = new UnitGitCommit
                {
                    ShortHash = fields[0],
                    FullHash = fields[1],
                    Subject = fields[2],
                    AuthorName = fields[3],
                    AuthorEmail = fields[4],
                    Decorations = fields[6],
                    ReleaseId = fields.Length >= 11 ? fields[10].Trim() : string.Empty
                };
                details.AuthorDate = fields[5];
                details.CommitterName = fields[7];
                details.CommitterEmail = fields[8];
                details.CommitterDate = fields[9];
            }

            bool seenBlank = false;
            for (int i = 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (!seenBlank && string.IsNullOrWhiteSpace(line))
                {
                    seenBlank = true;
                    continue;
                }

                if (seenBlank && !string.IsNullOrWhiteSpace(line))
                {
                    details.ChangedFiles.Add(line.Trim());
                }
            }

            if (details.ChangedFiles.Count == 0)
            {
                foreach (string line in lines.Skip(1))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        details.ChangedFiles.Add(line.Trim());
                    }
                }
            }

            return details;
        }

        public GitCommandResult Fetch()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before fetching.");
            }

            return RunGit(LongTimeoutMilliseconds, "fetch", "--all", "--prune");
        }

        public GitCommandResult PullFastForward()
        {
            if (!HasRemotes())
            {
                return Failure("No Git remote is configured. Use GitHub remote setup or add a remote before pulling.");
            }

            return RunGit(LongTimeoutMilliseconds, "pull", "--ff-only");
        }

        public GitCommandResult StageAll()
        {
            return RunGit(LongTimeoutMilliseconds, "add", "-A");
        }

        public GitCommandResult UnstageAll()
        {
            return RunGit(DefaultTimeoutMilliseconds, "reset");
        }

        public GitCommandResult Commit(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RunCommitWithMessageFile(new[] { "commit" }, message);
        }

        public GitCommandResult Commit(string message, string trailingParagraph)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            if (string.IsNullOrWhiteSpace(trailingParagraph))
            {
                return Commit(message);
            }

            return RunCommitWithMessageFile(new[] { "commit" }, message.Trim() + "\n\n" + trailingParagraph.Trim());
        }

        public GitCommandResult CommitAmend(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RunCommitWithMessageFile(new[] { "commit", "--amend" }, message);
        }

        public GitCommandResult GetHeadCommitMessage()
        {
            return GetCommitMessage("HEAD");
        }

        public GitCommandResult GetCommitMessage(string commitHash)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            var result = RunGit(DefaultTimeoutMilliseconds, "log", "-1", "--format=%B", commitHash);
            if (result.Success)
            {
                result.StandardOutput = TrimTrailingNewlines(result.StandardOutput);
            }

            return result;
        }

        internal GitCommandResult RenameCommit(string commitHash, string message, UnitGitHeadState expected)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RewriteHeadHistory(new[] { commitHash.Trim() }, message, false, expected);
        }

        internal GitCommandResult SquashCommits(IList<string> commitHashes, string message, UnitGitHeadState expected)
        {
            if (commitHashes == null || commitHashes.Count < 2)
            {
                return Failure("Select at least two commits to squash.");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return Failure("Commit message is required.");
            }

            return RewriteHeadHistory(commitHashes, message, true, expected);
        }

        internal GitCommandResult ReadHeadState(out UnitGitHeadState state)
        {
            state = null;
            GitCommandResult result = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "HEAD", "--symbolic-full-name", "HEAD");
            if (!result.Success)
            {
                return result;
            }

            string[] lines = SplitLines(result.StandardOutput).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            if (lines.Length < 2)
            {
                return Failure("Could not read the current branch.");
            }

            state = new UnitGitHeadState { Head = lines[0], BranchRef = lines[1] == "HEAD" ? string.Empty : lines[1] };
            return result;
        }

        /// <summary>Resets HEAD only while it is still the branch and commit the user confirmed.</summary>
        internal GitCommandResult ResetCurrentBranch(string commitHash, UnitGitResetMode mode, UnitGitHeadState expected)
        {
            if (string.IsNullOrWhiteSpace(commitHash))
            {
                return Failure("Commit hash is required.");
            }

            GitCommandResult resolved = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", commitHash.Trim() + "^{commit}");
            if (!resolved.Success)
            {
                return resolved;
            }

            GitCommandResult current = ReadHeadState(out UnitGitHeadState state);
            if (!current.Success)
            {
                return current;
            }

            GitCommandResult changed = CheckHeadUnchanged("Reset", expected, state, true);
            if (changed != null)
            {
                return changed;
            }

            string targetHash = FirstNonEmptyLine(resolved.StandardOutput);
            return RunGit(LongTimeoutMilliseconds, "reset", GetResetModeArgument(mode), targetHash);
        }

        private static GitCommandResult CheckHeadUnchanged(string operation, UnitGitHeadState expected, UnitGitHeadState current, bool sameCommit)
        {
            if (expected == null)
            {
                return Failure(operation + " has no confirmed branch. Nothing was changed; reopen the dialog to try again.");
            }

            if (!string.Equals(expected.BranchRef, current.BranchRef, StringComparison.Ordinal))
            {
                return Failure(operation + " was prepared for " + DescribeBranch(expected) + ", but " + DescribeBranch(current) +
                               " is checked out now. Nothing was changed; reopen the dialog to try again.");
            }

            if (sameCommit && !string.Equals(expected.Head, current.Head, StringComparison.Ordinal))
            {
                return Failure(operation + " was prepared for " + DescribeBranch(expected) + " at " + ShortHash(expected.Head) +
                               ", but it now points to " + ShortHash(current.Head) + ". Nothing was changed; reopen the dialog to try again.");
            }

            return null;
        }

        private static string DescribeBranch(UnitGitHeadState state)
        {
            return string.IsNullOrEmpty(state.BranchRef) ? "a detached HEAD" : "branch '" + state.BranchName + "'";
        }

        public GitCommandResult ShelveAll(string message)
        {
            string shelfMessage = string.IsNullOrWhiteSpace(message)
                ? "Unit Git shelf " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                : message.Trim();

            return RunGit(LongTimeoutMilliseconds, "stash", "push", "-u", "-m", shelfMessage);
        }

        private static string GetResetModeArgument(UnitGitResetMode mode)
        {
            switch (mode)
            {
                case UnitGitResetMode.Soft:
                    return "--soft";
                case UnitGitResetMode.Hard:
                    return "--hard";
                case UnitGitResetMode.Keep:
                    return "--keep";
                default:
                    return "--mixed";
            }
        }

        internal GitCommandResult RunCommitWithMessageFile(IEnumerable<string> baseArguments, string message,
            IDictionary<string, string> environment = null)
        {
            string messagePath = WriteTempCommitMessage(message);
            try
            {
                var args = new List<string>(baseArguments) { "-F", messagePath };
                return environment == null
                    ? RunGit(LongTimeoutMilliseconds, args.ToArray())
                    : RunGitWithEnvironment(environment, LongTimeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(messagePath);
            }
        }

        private GitCommandResult RewriteHeadHistory(IList<string> commitHashes, string message, bool squash, UnitGitHeadState expected)
        {
            GitCommandResult branchResult = RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            if (!branchResult.Success)
                return Failure("Check out a branch before rewriting history.");
            string branchRef = FirstNonEmptyLine(branchResult.StandardOutput);
            // Only the confirmed branch may be rewritten. Commits added to it meanwhile are rewritten
            // with the rest, and update-ref below refuses if it moves while the rewrite runs.
            GitCommandResult changed = CheckHeadUnchanged(squash ? "Squash" : "Rename", expected,
                new UnitGitHeadState { BranchRef = branchRef }, false);
            if (changed != null)
                return changed;
            GitCommandResult headResult = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", branchRef);
            if (!headResult.Success)
            {
                return headResult;
            }

            string oldHead = FirstNonEmptyLine(headResult.StandardOutput);
            GitCommandResult historyResult = RunGit(DefaultTimeoutMilliseconds, "rev-list", "--topo-order", "--reverse", oldHead);
            if (!historyResult.Success)
            {
                return historyResult;
            }

            List<string> history = SplitLines(historyResult.StandardOutput)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (history.Count == 0)
            {
                return Failure("No commits were found on HEAD.");
            }

            var historySet = new HashSet<string>(history, StringComparer.Ordinal);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string hash in commitHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)))
            {
                GitCommandResult resolved = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", hash.Trim() + "^{commit}");
                if (!resolved.Success)
                {
                    return resolved;
                }

                string resolvedHash = FirstNonEmptyLine(resolved.StandardOutput);
                if (!historySet.Contains(resolvedHash))
                {
                    return Failure("Commit " + ShortHash(resolvedHash) + " is not reachable from the current HEAD.");
                }

                selected.Add(resolvedHash);
            }

            if (!squash && selected.Count != 1)
            {
                return Failure("Select exactly one commit to rename.");
            }

            if (squash && selected.Count < 2)
            {
                return Failure("Select at least two commits to squash.");
            }

            int startIndex = history.FindIndex(hash => selected.Contains(hash));
            int endIndex = history.FindLastIndex(hash => selected.Contains(hash));
            if (startIndex < 0 || endIndex < startIndex)
            {
                return Failure("Selected commits were not found in HEAD history.");
            }

            if (squash)
            {
                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (!selected.Contains(history[i]))
                    {
                        return Failure("Selected commits must be contiguous in the current HEAD history to squash.");
                    }
                }
            }

            var infos = new Dictionary<string, CommitRewriteInfo>(StringComparer.Ordinal);
            for (int i = startIndex; i < history.Count; i++)
            {
                GitCommandResult infoResult = GetCommitRewriteInfo(history[i], out CommitRewriteInfo info);
                if (!infoResult.Success)
                {
                    return infoResult;
                }

                infos[history[i]] = info;
            }

            if (squash)
            {
                GitCommandResult topology = CheckSquashTopology(history, startIndex, endIndex, selected, infos);
                if (!topology.Success)
                {
                    return topology;
                }
            }

            var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
            string squashedHash = string.Empty;
            for (int i = startIndex; i < history.Count; i++)
            {
                string originalHash = history[i];
                if (squash && i > startIndex && i <= endIndex)
                {
                    rewritten[originalHash] = squashedHash;
                    continue;
                }

                CommitRewriteInfo source = infos[originalHash];
                List<string> parents = RewriteParents(source.Parents, rewritten);
                if (!selected.Contains(originalHash) && parents.SequenceEqual(source.Parents, StringComparer.Ordinal))
                {
                    // Nothing below this commit changed (e.g. a side branch merged later): keep it
                    // instead of creating a copy that would diverge from the branch that owns it.
                    rewritten[originalHash] = originalHash;
                    continue;
                }

                string treeHash = squash && i == startIndex
                    ? infos[history[endIndex]].TreeHash
                    : source.TreeHash;
                string commitMessage = squash && i == startIndex
                    ? message
                    : (!squash && selected.Contains(originalHash) ? message : source.Message);

                GitCommandResult createResult = CreateCommitFromTree(source, treeHash, parents, commitMessage);
                if (!createResult.Success)
                {
                    return createResult;
                }

                string newHash = FirstNonEmptyLine(createResult.StandardOutput);
                if (string.IsNullOrWhiteSpace(newHash))
                {
                    return Failure("Git did not return a rewritten commit hash.");
                }

                rewritten[originalHash] = newHash;
                if (squash && i == startIndex)
                {
                    squashedHash = newHash;
                }
            }

            if (!rewritten.TryGetValue(oldHead, out string newHead) || string.IsNullOrWhiteSpace(newHead))
            {
                return Failure("Could not resolve the rewritten HEAD.");
            }

            // Commit trees are unchanged. Move only the captured branch, and only if nobody advanced it.
            // Do not reset the index or whichever branch another process has since checked out.
            GitCommandResult updateResult = RunGit(DefaultTimeoutMilliseconds, "update-ref", "--no-deref",
                "-m", "Unit Git: rewrite history", branchRef, newHead, oldHead);
            if (!updateResult.Success)
            {
                return updateResult;
            }

            updateResult.StandardOutput = "Rewrote " + branchRef + " " + ShortHash(oldHead) + " -> " + ShortHash(newHead) + ".";
            return updateResult;
        }

        /// <summary>
        /// The squashed commit gets the oldest selected commit's parents and the newest one's tree.
        /// Refuse selections where that would drop or re-parent history outside the selection.
        /// </summary>
        private static GitCommandResult CheckSquashTopology(IList<string> history, int startIndex, int endIndex,
            ICollection<string> selected, IDictionary<string, CommitRewriteInfo> infos)
        {
            string tip = history[endIndex];
            var reached = new HashSet<string>(StringComparer.Ordinal) { tip };
            var pending = new Stack<string>(reached);
            while (pending.Count > 0)
            {
                foreach (string parent in infos[pending.Pop()].Parents)
                {
                    if (selected.Contains(parent) && reached.Add(parent))
                        pending.Push(parent);
                }
            }

            string sideCommit = selected.FirstOrDefault(hash => !reached.Contains(hash));
            if (sideCommit != null)
            {
                return Failure("Selected commits must form a single line of history to squash: " + ShortHash(sideCommit) +
                               " is not an ancestor of " + ShortHash(tip) + ".");
            }

            List<string> baseParents = infos[history[startIndex]].Parents;
            for (int i = startIndex + 1; i <= endIndex; i++)
            {
                string merged = infos[history[i]].Parents.FirstOrDefault(parent => !selected.Contains(parent) && !baseParents.Contains(parent));
                if (merged != null)
                {
                    return Failure("Squashing would drop merged history: " + ShortHash(history[i]) + " merges " + ShortHash(merged) +
                                   ", which is not selected. Select the merged commits too, or leave the merge out of the selection.");
                }
            }

            for (int i = endIndex + 1; i < history.Count; i++)
            {
                string forkPoint = infos[history[i]].Parents.FirstOrDefault(parent => parent != tip && selected.Contains(parent));
                if (forkPoint != null)
                {
                    return Failure("Squashing would re-parent " + ShortHash(history[i]) + ": it branches off from selected commit " + ShortHash(forkPoint) +
                                   ". Select its commits too, or pick commits nothing else branches off from.");
                }
            }

            return new GitCommandResult();
        }

        private GitCommandResult GetCommitRewriteInfo(string hash, out CommitRewriteInfo info)
        {
            info = null;
            GitCommandResult meta = RunGit(
                DefaultTimeoutMilliseconds,
                "show",
                "-s",
                "--format=%H%x1f%T%x1f%P%x1f%an%x1f%ae%x1f%aI",
                hash);
            if (!meta.Success)
            {
                return meta;
            }

            string firstLine = FirstNonEmptyLine(meta.StandardOutput);
            string[] fields = firstLine.Split(FieldSeparator);
            if (fields.Length < 6)
            {
                return Failure("Could not parse commit metadata for " + ShortHash(hash) + ".");
            }

            GitCommandResult message = GetCommitMessage(hash);
            if (!message.Success)
            {
                return message;
            }

            info = new CommitRewriteInfo
            {
                Hash = fields[0],
                TreeHash = fields[1],
                Parents = fields[2]
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList(),
                AuthorName = fields[3],
                AuthorEmail = fields[4],
                AuthorDate = fields[5],
                Message = message.StandardOutput
            };
            return new GitCommandResult();
        }

        private GitCommandResult CreateCommitFromTree(CommitRewriteInfo source, string treeHash, IList<string> parents, string message)
        {
            string messagePath = WriteTempCommitMessage(message);
            try
            {
                var args = new List<string> { "commit-tree", treeHash };
                foreach (string parent in parents)
                {
                    args.Add("-p");
                    args.Add(parent);
                }

                args.Add("-F");
                args.Add(messagePath);

                var environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "GIT_AUTHOR_NAME", source.AuthorName },
                    { "GIT_AUTHOR_EMAIL", source.AuthorEmail },
                    { "GIT_AUTHOR_DATE", source.AuthorDate }
                };
                return RunGitWithEnvironment(environment, LongTimeoutMilliseconds, args.ToArray());
            }
            finally
            {
                DeleteTempFile(messagePath);
            }
        }

        private static List<string> RewriteParents(IEnumerable<string> parents, IDictionary<string, string> rewritten)
        {
            var result = new List<string>();
            foreach (string parent in parents)
            {
                string next = rewritten.TryGetValue(parent, out string rewrittenParent)
                    ? rewrittenParent
                    : parent;
                if (!result.Contains(next))
                {
                    result.Add(next);
                }
            }

            return result;
        }

        public UnitGitDiff GetFileDiff(UnitGitStatusEntry change)
        {
            var diff = new UnitGitDiff();
            if (change == null || string.IsNullOrWhiteSpace(change.Path))
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Select a changed file to inspect its diff.",
                    Kind = UnitGitDiffLineKind.Context
                });
                return diff;
            }

            diff.Path = change.Path;
            diff.LeftTitle = "Repository";
            diff.RightTitle = "Current version";

            GitCommandResult unstagedResult = null;
            GitCommandResult stagedResult = null;
            string unstagedOutput = string.Empty;
            string stagedOutput = string.Empty;

            if (change.IsUnstaged && !change.IsUntracked)
            {
                unstagedResult = RunGit(DefaultTimeoutMilliseconds, "--literal-pathspecs", "diff", "--no-ext-diff", "--unified=80", "--", change.Path);
                if (unstagedResult.Success)
                {
                    unstagedOutput = unstagedResult.StandardOutput;
                }
            }

            if (change.IsStaged)
            {
                stagedResult = RunGit(DefaultTimeoutMilliseconds, "--literal-pathspecs", "diff", "--cached", "--no-ext-diff", "--unified=80", "--", change.Path);
                if (stagedResult.Success)
                {
                    stagedOutput = stagedResult.StandardOutput;
                }
            }

            bool hasStagedDiff = !string.IsNullOrWhiteSpace(stagedOutput);
            bool hasUnstagedDiff = !string.IsNullOrWhiteSpace(unstagedOutput);
            if (hasStagedDiff || hasUnstagedDiff)
            {
                if (hasStagedDiff && hasUnstagedDiff)
                {
                    diff.LeftTitle = "HEAD / Index";
                    diff.RightTitle = "Index / Working tree";
                    AppendDiffSection(diff, "Staged changes (HEAD -> index)");
                    ParseUnifiedDiff(stagedOutput, diff);
                    AppendDiffSection(diff, "Unstaged changes (index -> working tree)");
                    ParseUnifiedDiff(unstagedOutput, diff);
                }
                else if (hasStagedDiff)
                {
                    diff.LeftTitle = "HEAD";
                    diff.RightTitle = "Staged";
                    ParseUnifiedDiff(stagedOutput, diff);
                }
                else
                {
                    ParseUnifiedDiff(unstagedOutput, diff);
                }

                if (diff.Lines.Count == 0)
                {
                    diff.Lines.Add(new UnitGitDiffLine
                    {
                        Right = "No textual differences to display.",
                        Kind = UnitGitDiffLineKind.Context
                    });
                }

                return diff;
            }

            if (change.IsUntracked)
            {
                PopulateUntrackedDiff(diff, change.Path);
                return diff;
            }

            GitCommandResult failedResult = stagedResult != null && !stagedResult.Success
                ? stagedResult
                : unstagedResult;
            diff.Lines.Add(new UnitGitDiffLine
            {
                Right = failedResult != null && !string.IsNullOrWhiteSpace(failedResult.Message)
                    ? failedResult.Message
                    : "No textual differences to display.",
                Kind = UnitGitDiffLineKind.Context
            });

            return diff;
        }

        public GitCommandResult Checkout(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = "No branch selected."
                };
            }

            if (!branch.IsRemote)
            {
                return RunGit(DefaultTimeoutMilliseconds, "checkout", branch.Name);
            }

            return RunGit(DefaultTimeoutMilliseconds, "checkout", "-t", branch.Name);
        }

        public GitCommandResult CreateBranch(string branchName, string startPoint)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return Failure("Branch name is required.");
            }

            branchName = branchName.Trim();
            var validation = RunGit(DefaultTimeoutMilliseconds, "check-ref-format", "--branch", branchName);
            if (!validation.Success)
            {
                return Failure("Invalid branch name: " + branchName);
            }

            if (string.IsNullOrWhiteSpace(startPoint))
            {
                return RunGit(DefaultTimeoutMilliseconds, "checkout", "-b", branchName);
            }

            return RunGit(DefaultTimeoutMilliseconds, "checkout", "-b", branchName, startPoint);
        }

        public GitCommandResult UpdateBranch(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return Failure("Select a branch first.");
            }

            if (branch.IsRemote)
            {
                return Fetch();
            }

            branch = GetBranches().FirstOrDefault(candidate => !candidate.IsRemote && candidate.FullRef == branch.FullRef);
            if (branch == null)
                return Failure("The selected branch no longer exists. Refresh the branch list.");
            if (string.IsNullOrWhiteSpace(branch.Upstream))
                return Failure("Selected branch has no upstream. Fetch still works, but there is nothing to fast-forward.");

            GitCommandResult checkout = RunGit(DefaultTimeoutMilliseconds, "checkout", branch.Name);
            if (!checkout.Success)
            {
                return checkout;
            }

            GitCommandResult current = RunGit(DefaultTimeoutMilliseconds, "symbolic-ref", "--quiet", "HEAD");
            if (!current.Success || FirstNonEmptyLine(current.StandardOutput) != branch.FullRef)
            {
                return Failure("The current branch changed during Update. Refresh and try again.");
            }

            return PullFastForward();
        }

        public GitCommandResult DeleteBranch(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return Failure("Select a branch first.");
            }

            if (branch.IsCurrent)
            {
                return Failure("Cannot delete the current branch. Checkout another branch first.");
            }

            if (branch.IsRemote)
            {
                return RunGit(DefaultTimeoutMilliseconds, "branch", "-dr", branch.Name);
            }

            return RunGit(DefaultTimeoutMilliseconds, "branch", "-d", branch.Name);
        }

        public GitCommandResult RunGit(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess("git", "Git command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, null, ReadSuperseded, arguments);
        }

        internal GitCommandResult RunGitWithEnvironment(IDictionary<string, string> environment, int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess("git", "Git command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, environment, null, arguments);
        }

        public bool IsGitAvailable()
        {
            var result = RunProcess("git", "Git command timed out.", ProjectRoot, 10000, "--version");
            return result.Success;
        }

        public GitCommandResult GitHubLogin()
        {
            if (!IsGitHubCliAvailable())
            {
                return Failure("GitHub CLI was not found. Unit Git checked PATH and common Windows install locations. Install GitHub CLI, then reopen Unity.");
            }

            return RunGitHubCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "github.com", "--web", "--clipboard", "--git-protocol", "https", "--skip-ssh-key");
        }

        public GitCommandResult CreateGitHubRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            if (!IsGitHubCliAvailable())
            {
                return Failure("GitHub CLI was not found on PATH. Install GitHub CLI, then reopen Unity.");
            }

            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return Failure("GitHub repository name is required.");
            }

            remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
            if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("Remote '" + remoteName + "' already exists.");
            }

            return RunGitHubCli(
                LongTimeoutMilliseconds,
                "repo",
                "create",
                repositoryName.Trim(),
                "--source",
                ProjectRoot,
                "--remote",
                remoteName,
                isPrivate ? "--private" : "--public");
        }

        private GitCommandResult RunGitHubCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitHubCliExecutable(), "GitHub CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitHubCliAvailable()
        {
            return RunGitHubCli(10000, "--version").Success;
        }

        internal bool IsGitHubCliSignedIn()
        {
            return RunGitHubCli(10000, "auth", "status", "--hostname", "github.com").Success;
        }

        internal bool IsGitLabCliSignedIn()
        {
            return RunGitLabCli(10000, "auth", "status", "--hostname", "gitlab.com").Success;
        }

        public GitCommandResult GitLabLogin()
        {
            if (!IsGitLabCliAvailable())
            {
                return Failure("GitLab CLI was not found. Unit Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
            }

            return RunGitLabCli(LongTimeoutMilliseconds, "auth", "login", "--hostname", "gitlab.com", "--web", "--git-protocol", "https");
        }

        public GitCommandResult CreateGitLabRemoteRepository(string repositoryName, string remoteName, bool isPrivate)
        {
            if (!IsGitLabCliAvailable())
            {
                return Failure("GitLab CLI was not found. Unit Git checked PATH and common Windows install locations. Install GitLab CLI, then reopen Unity.");
            }

            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return Failure("GitLab repository path is required.");
            }

            remoteName = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();
            if (GetRemoteNames().Any(remote => string.Equals(remote, remoteName, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("Remote '" + remoteName + "' already exists.");
            }

            return RunGitLabCli(
                LongTimeoutMilliseconds,
                "repo",
                "create",
                repositoryName.Trim(),
                "--remoteName",
                remoteName,
                isPrivate ? "--private" : "--public");
        }

        private GitCommandResult RunGitLabCli(int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(GetGitLabCliExecutable(), "GitLab CLI command timed out.", ProjectRoot, timeoutMilliseconds, ProcessLogReceived, arguments);
        }

        internal bool IsGitLabCliAvailable()
        {
            return RunGitLabCli(10000, "--version").Success;
        }

        private static string GetGitHubCliExecutable()
        {
            const string executableName = "gh.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "GitHub CLI", executableName),
                Path.Combine(localAppData, "GitHub CLI", executableName),
                Path.Combine(localAppData, "Programs", "GitHub CLI", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "gh";
        }

        private static string GetGitLabCliExecutable()
        {
            const string executableName = "glab.exe";
            string pathMatch = FindExecutableOnPath(executableName);
            if (!string.IsNullOrWhiteSpace(pathMatch))
            {
                return pathMatch;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string chocolateyInstall = Environment.GetEnvironmentVariable("ChocolateyInstall");
            string[] candidates =
            {
                Path.Combine(programFiles, "glab", "bin", executableName),
                Path.Combine(localAppData, "Programs", "glab", executableName),
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", executableName),
                Path.Combine(userProfile, "scoop", "shims", executableName),
                Path.Combine(string.IsNullOrWhiteSpace(chocolateyInstall) ? @"C:\ProgramData\chocolatey" : chocolateyInstall, "bin", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "glab";
        }

        private static string FindExecutableOnPath(string executableName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        public static string GetUnityProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        public static bool IsUnityProject(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(projectRoot, "Assets")) &&
                   Directory.Exists(Path.Combine(projectRoot, "ProjectSettings")) &&
                   File.Exists(Path.Combine(projectRoot, "Packages", "manifest.json"));
        }

        public static bool HasRepository(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            string gitPath = Path.Combine(projectRoot, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        private bool HasRemotes()
        {
            return GetRemoteNames().Count > 0;
        }

        internal List<string> GetRemoteNames()
        {
            var result = RunGit(DefaultTimeoutMilliseconds, "remote");
            if (!result.Success)
            {
                return new List<string>();
            }

            return SplitLines(result.StandardOutput)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<UnitGitBranch> GetBranches()
        {
            var result = RunGit(
                DefaultTimeoutMilliseconds,
                "branch",
                "--all",
                "--format=%(refname)\t%(refname:short)\t%(HEAD)\t%(upstream:short)\t%(objectname:short)");

            if (!result.Success)
            {
                return new List<UnitGitBranch>();
            }

            return ParseBranches(result.StandardOutput);
        }

        internal bool HasCommits()
        {
            var result = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", "HEAD");
            return result.Success;
        }

        internal List<UnitGitCommit> GetCommits(string logSearch, int maxCount = 250)
        {
            var commits = new List<UnitGitCommit>();
            if (maxCount <= 0) return commits;
            bool searching = !string.IsNullOrWhiteSpace(logSearch);
            int chunkSize = searching ? 500 : maxCount;
            int skipped = 0;
            while (commits.Count < maxCount && !(ReadSuperseded?.Invoke() ?? false))
            {
                var args = new List<string>
                {
                    "log",
                    "--all",
                    "--max-count=" + chunkSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--skip=" + skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--date=relative",
                    "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ar%x1f%D%x1f%(trailers:key=" + UnitGitReleases.TrailerKey + ",valueonly,separator=%x2C)"
                };

                var result = RunGit(DefaultTimeoutMilliseconds, args.ToArray());
                if (!result.Success && !(ReadSuperseded?.Invoke() ?? false))
                {
                    // Older Git versions do not support the %(trailers) pretty-format placeholder.
                    args[args.Count - 1] = "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ar%x1f%D";
                    result = RunGit(DefaultTimeoutMilliseconds, args.ToArray());
                }

                if (ReadSuperseded?.Invoke() ?? false) break;
                if (!result.Success)
                    throw new InvalidOperationException("Could not read Git history: " + result.Message);

                var chunk = ParseCommits(result.StandardOutput, string.Empty);
                commits.AddRange(chunk.Where(commit => MatchesSearch(commit, logSearch)).Take(maxCount - commits.Count));
                if (!searching || chunk.Count < chunkSize) break;
                skipped += chunk.Count;
            }
            return commits;
        }

        internal static UnitGitSnapshot ParseStatusOutput(string output)
        {
            var snapshot = new UnitGitSnapshot();
            ParseStatus(output, snapshot);
            return snapshot;
        }

        internal static List<UnitGitBranch> ParseBranches(string output)
        {
            var branches = new List<UnitGitBranch>();
            foreach (string line in SplitLines(output))
            {
                var fields = line.Split(BranchFieldSeparator);
                if (fields.Length < 5)
                {
                    continue;
                }

                string fullRef = fields[0];
                string name = fields[1];
                if (name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                branches.Add(new UnitGitBranch
                {
                    FullRef = fullRef,
                    Name = name,
                    IsCurrent = fields[2] == "*",
                    Upstream = fields[3],
                    ShortHash = fields[4],
                    IsRemote = fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal)
                });
            }

            return branches
                .OrderBy(branch => branch.IsRemote)
                .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<UnitGitCommit> ParseCommits(string output, string logSearch)
        {
            var commits = new List<UnitGitCommit>();
            foreach (string line in SplitLines(output))
            {
                var fields = line.Split(FieldSeparator);
                if (fields.Length < 7)
                {
                    continue;
                }

                var commit = new UnitGitCommit
                {
                    ShortHash = fields[0],
                    FullHash = fields[1],
                    Subject = fields[2],
                    AuthorName = fields[3],
                    AuthorEmail = fields[4],
                    RelativeDate = fields[5],
                    Decorations = fields[6],
                    ReleaseId = fields.Length >= 8 ? fields[7].Trim() : string.Empty
                };

                if (MatchesSearch(commit, logSearch))
                {
                    commits.Add(commit);
                }
            }

            return commits;
        }

        private static bool MatchesSearch(UnitGitCommit commit, string logSearch)
        {
            if (commit == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(logSearch))
            {
                return true;
            }

            string query = logSearch.Trim();
            return Contains(commit.Subject, query) ||
                   Contains(commit.AuthorName, query) ||
                   Contains(commit.ShortHash, query) ||
                   Contains(commit.FullHash, query) ||
                   Contains(commit.Decorations, query);
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static List<string> ParseStashList(string output)
        {
            return SplitLines(output)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        internal static void ParseStatus(string output, UnitGitSnapshot snapshot)
        {
            foreach (string line in SplitLines(output))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    ParseBranchStatus(line.Substring(3), snapshot);
                    continue;
                }

                if (line.Length < 3)
                {
                    continue;
                }

                string path = line.Substring(3);
                string originalPath = string.Empty;
                int renameIndex = line[0] == 'R' || line[0] == 'C' || line[1] == 'R' || line[1] == 'C'
                    ? FindRenameSeparator(path)
                    : -1;
                if (renameIndex >= 0)
                {
                    originalPath = UnquotePath(path.Substring(0, renameIndex));
                    path = path.Substring(renameIndex + 4);
                }

                snapshot.Changes.Add(new UnitGitStatusEntry
                {
                    IndexStatus = line[0],
                    WorkTreeStatus = line[1],
                    Path = UnquotePath(path),
                    OriginalPath = originalPath
                });
            }
        }

        internal static void ParseBranchStatus(string branchStatus, UnitGitSnapshot snapshot)
        {
            string text = branchStatus;
            int bracketIndex = text.IndexOf(" [", StringComparison.Ordinal);
            string trackingPart = bracketIndex >= 0 ? text.Substring(bracketIndex) : string.Empty;
            string branchPart = bracketIndex >= 0 ? text.Substring(0, bracketIndex) : text;

            int upstreamIndex = branchPart.IndexOf("...", StringComparison.Ordinal);
            const string noCommitsPrefix = "No commits yet on ";
            if (branchPart.StartsWith(noCommitsPrefix, StringComparison.Ordinal))
            {
                snapshot.CurrentBranch = branchPart.Substring(noCommitsPrefix.Length);
                return;
            }

            if (upstreamIndex >= 0)
            {
                snapshot.CurrentBranch = branchPart.Substring(0, upstreamIndex);
                snapshot.Upstream = branchPart.Substring(upstreamIndex + 3);
            }
            else
            {
                snapshot.CurrentBranch = branchPart;
            }

            Match ahead = AheadRegex.Match(trackingPart);
            if (ahead.Success)
            {
                int.TryParse(ahead.Groups[1].Value, out snapshot.Ahead);
            }

            Match behind = BehindRegex.Match(trackingPart);
            if (behind.Success)
            {
                int.TryParse(behind.Groups[1].Value, out snapshot.Behind);
            }
        }

        private static int FindRenameSeparator(string path)
        {
            bool quoted = false;
            for (int i = 0; i < path.Length; i++)
            {
                if (quoted && path[i] == '\\') { i++; continue; }
                if (path[i] == '"') quoted = !quoted;
                if (!quoted && i + 4 <= path.Length && path.Substring(i, 4) == " -> ")
                    return i;
            }
            return -1;
        }

        internal static string UnquotePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            {
                var decoded = new StringBuilder();
                for (int i = 1; i < path.Length - 1; i++)
                {
                    if (path[i] != '\\') { decoded.Append(path[i]); continue; }
                    i++;
                    if (path[i] >= '0' && path[i] <= '7')
                    {
                        // Git quotes UTF-8 bytes as octal escapes, not Unicode code points.
                        var bytes = new List<byte>();
                        while (true)
                        {
                            int value = 0;
                            int digits = 0;
                            while (i < path.Length - 1 && digits < 3 && path[i] >= '0' && path[i] <= '7')
                            { value = value * 8 + path[i++] - '0'; digits++; }
                            bytes.Add((byte)value);
                            if (i + 1 >= path.Length - 1 || path[i] != '\\' || path[i + 1] < '0' || path[i + 1] > '7')
                                break;
                            i++;
                        }
                        decoded.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                        i--;
                    }
                    else
                    {
                        switch (path[i])
                        {
                            case 'a': decoded.Append('\a'); break;
                            case 'b': decoded.Append('\b'); break;
                            case 't': decoded.Append('\t'); break;
                            case 'n': decoded.Append('\n'); break;
                            case 'v': decoded.Append('\v'); break;
                            case 'f': decoded.Append('\f'); break;
                            case 'r': decoded.Append('\r'); break;
                            default: decoded.Append(path[i]); break;
                        }
                    }
                }
                return decoded.ToString();
            }

            return path;
        }

        internal static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                yield return line;
            }
        }

        private void PopulateUntrackedDiff(UnitGitDiff diff, string projectPath)
        {
            string fullPath = GetSafeProjectPath(projectPath);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Untracked file is not available on disk.",
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (info.Length > MaxUntrackedDiffPreviewBytes)
                {
                    AddSkippedUntrackedPreview(diff, "large file", info.Length);
                    return;
                }

                if (LooksBinaryFile(fullPath))
                {
                    AddSkippedUntrackedPreview(diff, "binary file", info.Length);
                    return;
                }
            }
            catch (Exception ex)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Unable to inspect this untracked file: " + ex.Message,
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath);
            }
            catch (Exception ex)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = "Unable to display this untracked file: " + ex.Message,
                    Kind = UnitGitDiffLineKind.Context
                });
                return;
            }

            foreach (string line in lines)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = line,
                    Kind = UnitGitDiffLineKind.Added
                });
                diff.DifferenceCount++;
            }
        }

        private static void AddSkippedUntrackedPreview(UnitGitDiff diff, string reason, long bytes)
        {
            diff.Lines.Add(new UnitGitDiffLine
            {
                Right = "Untracked " + reason + " preview skipped (" + FormatBytes(bytes) + ").",
                Kind = UnitGitDiffLineKind.Context
            });
        }

        private static bool LooksBinaryFile(string fullPath)
        {
            var buffer = new byte[BinarySniffBytes];
            int bytesRead;
            using (var stream = File.OpenRead(fullPath))
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);
            }

            int suspiciousControls = 0;
            for (int i = 0; i < bytesRead; i++)
            {
                byte value = buffer[i];
                if (value == 0)
                {
                    return true;
                }

                if (value < 32 && value != 9 && value != 10 && value != 12 && value != 13)
                {
                    suspiciousControls++;
                }
            }

            return bytesRead > 0 && suspiciousControls > bytesRead / 10;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024f).ToString("0.#") + " KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (1024f * 1024f)).ToString("0.#") + " MB";
            }

            return (bytes / (1024f * 1024f * 1024f)).ToString("0.#") + " GB";
        }

        private string GetSafeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return string.Empty;
            }

            string root = Path.GetFullPath(ProjectRoot);
            string fullPath = Path.GetFullPath(Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return fullPath;
        }

        private static void AppendDiffSection(UnitGitDiff diff, string title)
        {
            if (diff.Lines.Count > 0)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Kind = UnitGitDiffLineKind.Context
                });
            }

            diff.Lines.Add(new UnitGitDiffLine
            {
                Left = title,
                Right = title,
                Kind = UnitGitDiffLineKind.Hunk
            });
        }

        internal static UnitGitDiff ParseUnifiedDiff(string output)
        {
            var diff = new UnitGitDiff();
            ParseUnifiedDiff(output, diff);
            return diff;
        }

        internal static void ParseUnifiedDiff(string output, UnitGitDiff diff)
        {
            var pendingRemoved = new Queue<string>();
            bool inHunk = false;
            foreach (string rawLine in SplitLines(output))
            {
                if (rawLine.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    FlushRemoved(diff, pendingRemoved);
                    inHunk = false;
                    continue;
                }
                if (!inHunk && (
                    rawLine.StartsWith("index ", StringComparison.Ordinal) ||
                    rawLine.StartsWith("--- ", StringComparison.Ordinal) ||
                    rawLine.StartsWith("+++ ", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (rawLine.StartsWith("@@", StringComparison.Ordinal))
                {
                    inHunk = true;
                    FlushRemoved(diff, pendingRemoved);
                    diff.Lines.Add(new UnitGitDiffLine
                    {
                        Left = rawLine,
                        Right = rawLine,
                        Kind = UnitGitDiffLineKind.Hunk
                    });
                    continue;
                }

                if (rawLine.StartsWith("-", StringComparison.Ordinal))
                {
                    pendingRemoved.Enqueue(rawLine.Length > 1 ? rawLine.Substring(1) : string.Empty);
                    continue;
                }

                if (rawLine.StartsWith("+", StringComparison.Ordinal))
                {
                    string added = rawLine.Length > 1 ? rawLine.Substring(1) : string.Empty;
                    if (pendingRemoved.Count > 0)
                    {
                        diff.Lines.Add(new UnitGitDiffLine
                        {
                            Left = pendingRemoved.Dequeue(),
                            Right = added,
                            Kind = UnitGitDiffLineKind.Changed
                        });
                    }
                    else
                    {
                        diff.Lines.Add(new UnitGitDiffLine
                        {
                            Right = added,
                            Kind = UnitGitDiffLineKind.Added
                        });
                    }

                    diff.DifferenceCount++;
                    continue;
                }

                FlushRemoved(diff, pendingRemoved);

                string context = rawLine.StartsWith(" ", StringComparison.Ordinal)
                    ? rawLine.Substring(1)
                    : rawLine;
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Left = context,
                    Right = context,
                    Kind = UnitGitDiffLineKind.Context
                });
            }

            FlushRemoved(diff, pendingRemoved);
        }

        private static void FlushRemoved(UnitGitDiff diff, Queue<string> pendingRemoved)
        {
            while (pendingRemoved.Count > 0)
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Left = pendingRemoved.Dequeue(),
                    Kind = UnitGitDiffLineKind.Removed
                });
                diff.DifferenceCount++;
            }
        }

        private static string WriteTempCommitMessage(string message)
        {
            string path = Path.Combine(Path.GetTempPath(), "unitgit-message-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, NormalizeCommitMessage(message) + "\n", new UTF8Encoding(false));
            return path;
        }

        private static string NormalizeCommitMessage(string message)
        {
            return string.IsNullOrWhiteSpace(message)
                ? string.Empty
                : message.Trim();
        }

        private static void DeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string FirstNonEmptyLine(string text)
        {
            return SplitLines(text)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
        }

        private static string TrimTrailingNewlines(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.TrimEnd('\r', '\n');
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return string.Empty;
            }

            string trimmed = hash.Trim();
            return trimmed.Length <= 8 ? trimmed : trimmed.Substring(0, 8);
        }

        private static GitCommandResult RunProcess(string fileName, string timeoutMessage, string workingDirectory, int timeoutMilliseconds, params string[] arguments)
        {
            return RunProcess(fileName, timeoutMessage, workingDirectory, timeoutMilliseconds, null, arguments);
        }

        private static GitCommandResult RunProcess(
            string fileName,
            string timeoutMessage,
            string workingDirectory,
            int timeoutMilliseconds,
            UnitGitProcessLogHandler logHandler,
            params string[] arguments)
        {
            return RunProcess(fileName, timeoutMessage, workingDirectory, timeoutMilliseconds, logHandler, null, null, arguments);
        }

        private static GitCommandResult RunProcess(
            string fileName,
            string timeoutMessage,
            string workingDirectory,
            int timeoutMilliseconds,
            UnitGitProcessLogHandler logHandler,
            IDictionary<string, string> environment,
            Func<bool> superseded,
            params string[] arguments)
        {
            if (superseded != null && superseded())
                return Failure("Read superseded.");
            var result = new GitCommandResult();
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = string.Join(" ", arguments.Select(EscapeArgument).ToArray()),
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                if (environment != null)
                {
                    foreach (KeyValuePair<string, string> item in environment)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Key))
                        {
                            startInfo.EnvironmentVariables[item.Key] = item.Value ?? string.Empty;
                        }
                    }
                }

                logHandler?.Invoke("> " + FormatCommandLine(fileName, arguments));
                logHandler?.Invoke("cwd: " + startInfo.WorkingDirectory);

                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            output.AppendLine(args.Data);
                            logHandler?.Invoke("stdout: " + args.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            error.AppendLine(args.Data);
                            logHandler?.Invoke("stderr: " + args.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var elapsed = Stopwatch.StartNew();
                    bool exited;
                    bool cancelled = false;
                    if (superseded == null)
                        exited = process.WaitForExit(timeoutMilliseconds);
                    else
                    {
                        while (!(exited = process.WaitForExit(50)))
                        {
                            cancelled = superseded();
                            if (cancelled || elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                                break;
                        }
                    }
                    if (!exited)
                    {
                        result.TimedOut = !cancelled;
                        result.ExitCode = 1;
                        if (cancelled)
                            error.AppendLine("Read superseded.");
                        else
                            logHandler?.Invoke("timed out after " + (timeoutMilliseconds / 1000f).ToString("0.#") + " seconds");
                        try
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                        catch
                        {
                        }
                        if (cancelled)
                            return Failure("Read superseded.");
                    }
                    else
                    {
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                        logHandler?.Invoke("exit: " + result.ExitCode);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                error.AppendLine(ex.Message);
                logHandler?.Invoke("error: " + ex.Message);
            }

            result.StandardOutput = output.ToString();
            result.StandardError = result.TimedOut
                ? timeoutMessage
                : error.ToString();
            return result;
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult
            {
                ExitCode = 1,
                StandardError = message
            };
        }

        internal static string FormatCommandLine(string fileName, params string[] arguments)
        {
            string suffix = arguments == null || arguments.Length == 0
                ? string.Empty
                : " " + string.Join(" ", arguments.Select(EscapeArgument).ToArray());
            return EscapeArgument(fileName) + suffix;
        }

        internal static string EscapeArgument(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            if (argument.Length == 0)
            {
                return "\"\"";
            }

            bool requiresQuotes = argument.Any(char.IsWhiteSpace) ||
                                  argument.IndexOf('"') >= 0 ||
                                  argument.IndexOf('%') >= 0 ||
                                  argument.IndexOf(';') >= 0;

            if (!requiresQuotes)
            {
                return argument;
            }

            var escaped = new StringBuilder();
            escaped.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    escaped.Append('\\', backslashes * 2 + 1);
                    escaped.Append('"');
                    backslashes = 0;
                    continue;
                }

                escaped.Append('\\', backslashes);
                backslashes = 0;
                escaped.Append(c);
            }

            escaped.Append('\\', backslashes * 2);
            escaped.Append('"');
            return escaped.ToString();
        }
    }
}
