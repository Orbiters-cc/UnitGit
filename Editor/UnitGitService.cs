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
        private const int DefaultTimeoutMilliseconds = 30000;
        public const int LongTimeoutMilliseconds = 900000;
        private static readonly Regex AheadRegex = new Regex(@"ahead\s+(\d+)", RegexOptions.Compiled);
        private static readonly Regex BehindRegex = new Regex(@"behind\s+(\d+)", RegexOptions.Compiled);

        public UnitGitService()
        {
            ProjectRoot = GetUnityProjectRoot();
        }

        public string ProjectRoot { get; private set; }

        public UnitGitSnapshot BuildSnapshot(string logSearch)
        {
            ProjectRoot = GetUnityProjectRoot();

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

            var status = RunGit(DefaultTimeoutMilliseconds, "status", "--porcelain=v1", "-b", "-uall");
            if (!status.Success)
            {
                snapshot.LastError = status.Message;
                return snapshot;
            }

            snapshot.HasCommits = HasCommits();
            ParseStatus(status.StandardOutput, snapshot);
            snapshot.Branches = GetBranches();
            if (snapshot.HasCommits)
            {
                snapshot.Commits = GetCommits(logSearch);
            }

            return snapshot;
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
                "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ad%x1f%D%x1f%cn%x1f%ce%x1f%cd",
                fullHash);

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
                    Decorations = fields[6]
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
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = "Commit message is required."
                };
            }

            return RunGit(LongTimeoutMilliseconds, "commit", "-m", message.Trim());
        }

        public GitCommandResult ShelveAll(string message)
        {
            string shelfMessage = string.IsNullOrWhiteSpace(message)
                ? "Unit Git shelf " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                : message.Trim();

            return RunGit(LongTimeoutMilliseconds, "stash", "push", "-u", "-m", shelfMessage);
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
            diff.LeftTitle = change.IsStaged && !change.IsUnstaged ? "HEAD" : "Repository";
            diff.RightTitle = "Current version";

            string output = string.Empty;
            var result = RunGit(DefaultTimeoutMilliseconds, "diff", "--no-ext-diff", "--unified=80", "--", change.Path);
            if (result.Success)
            {
                output = result.StandardOutput;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                var cached = RunGit(DefaultTimeoutMilliseconds, "diff", "--cached", "--no-ext-diff", "--unified=80", "--", change.Path);
                if (cached.Success)
                {
                    output = cached.StandardOutput;
                    diff.LeftTitle = "HEAD";
                    diff.RightTitle = "Staged";
                }
            }

            if (string.IsNullOrWhiteSpace(output) && change.IsUntracked)
            {
                PopulateUntrackedDiff(diff, change.Path);
                return diff;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                diff.Lines.Add(new UnitGitDiffLine
                {
                    Right = result.Message,
                    Kind = UnitGitDiffLineKind.Context
                });
                return diff;
            }

            ParseUnifiedDiff(output, diff);
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

            GitCommandResult checkout = branch.IsCurrent
                ? new GitCommandResult { ExitCode = 0 }
                : RunGit(DefaultTimeoutMilliseconds, "checkout", branch.Name);
            if (!checkout.Success)
            {
                return checkout;
            }

            if (string.IsNullOrWhiteSpace(branch.Upstream))
            {
                return Failure("Selected branch has no upstream. Fetch still works, but there is nothing to fast-forward.");
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
            return RunProcess("git", "Git command timed out.", ProjectRoot, timeoutMilliseconds, arguments);
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
                return Failure("GitHub CLI was not found on PATH. Install GitHub CLI, then reopen Unity.");
            }

            return RunGitHubCli(LongTimeoutMilliseconds, "auth", "login", "--web", "--git-protocol", "https");
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
            return RunProcess(GetGitHubCliExecutable(), "GitHub CLI command timed out.", ProjectRoot, timeoutMilliseconds, arguments);
        }

        private bool IsGitHubCliAvailable()
        {
            return RunGitHubCli(10000, "--version").Success;
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
            string[] candidates =
            {
                Path.Combine(programFiles, "GitHub CLI", executableName),
                Path.Combine(localAppData, "GitHub CLI", executableName)
            };

            return candidates.FirstOrDefault(File.Exists) ?? "gh";
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

        private List<string> GetRemoteNames()
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

            var branches = new List<UnitGitBranch>();
            if (!result.Success)
            {
                return branches;
            }

            foreach (string line in SplitLines(result.StandardOutput))
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

        private bool HasCommits()
        {
            var result = RunGit(DefaultTimeoutMilliseconds, "rev-parse", "--verify", "HEAD");
            return result.Success;
        }

        private List<UnitGitCommit> GetCommits(string logSearch)
        {
            var args = new List<string>
            {
                "log",
                "--all",
                "--max-count=250",
                "--date=relative",
                "--pretty=format:%h%x1f%H%x1f%s%x1f%an%x1f%ae%x1f%ar%x1f%D"
            };

            var result = RunGit(DefaultTimeoutMilliseconds, args.ToArray());
            var commits = new List<UnitGitCommit>();
            if (!result.Success)
            {
                return commits;
            }

            foreach (string line in SplitLines(result.StandardOutput))
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
                    Decorations = fields[6]
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

        private static void ParseStatus(string output, UnitGitSnapshot snapshot)
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
                int renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
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

        private static void ParseBranchStatus(string branchStatus, UnitGitSnapshot snapshot)
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

        private static string UnquotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            {
                path = path.Substring(1, path.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
            }

            return path;
        }

        private static IEnumerable<string> SplitLines(string text)
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

        private static void ParseUnifiedDiff(string output, UnitGitDiff diff)
        {
            var pendingRemoved = new Queue<string>();
            foreach (string rawLine in SplitLines(output))
            {
                if (rawLine.StartsWith("diff --git ", StringComparison.Ordinal) ||
                    rawLine.StartsWith("index ", StringComparison.Ordinal) ||
                    rawLine.StartsWith("--- ", StringComparison.Ordinal) ||
                    rawLine.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (rawLine.StartsWith("@@", StringComparison.Ordinal))
                {
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

        private static GitCommandResult RunProcess(string fileName, string timeoutMessage, string workingDirectory, int timeoutMilliseconds, params string[] arguments)
        {
            var result = new GitCommandResult();
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = string.Join(" ", arguments.Select(EscapeArgument).ToArray()),
                    WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Application.dataPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            output.AppendLine(args.Data);
                        }
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            error.AppendLine(args.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        result.TimedOut = true;
                        result.ExitCode = 1;
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                error.AppendLine(ex.Message);
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

        private static string EscapeArgument(string argument)
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
            foreach (char c in argument)
            {
                if (c == '"' || c == '\\')
                {
                    escaped.Append('\\');
                }

                escaped.Append(c);
            }

            escaped.Append('"');
            return escaped.ToString();
        }
    }
}
