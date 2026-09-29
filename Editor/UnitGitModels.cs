using System;
using System.Collections.Generic;

namespace Orbiters.UnitGit.Editor
{
    internal delegate void UnitGitProcessLogHandler(string line);

    /// <summary>The checked-out branch and commit a prompt was opened for.</summary>
    internal sealed class UnitGitHeadState
    {
        public string BranchRef = string.Empty; // empty for a detached HEAD
        public string Head = string.Empty;

        public string BranchName
        {
            get { return BranchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? BranchRef.Substring(11) : BranchRef; }
        }
    }

    internal sealed class GitCommandResult
    {
        public int ExitCode;
        public string StandardOutput = string.Empty;
        public string StandardError = string.Empty;
        public bool TimedOut;

        public bool Success
        {
            get { return ExitCode == 0 && !TimedOut; }
        }

        public string Message
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(StandardError))
                {
                    return StandardError.Trim();
                }

                return StandardOutput.Trim();
            }
        }
    }

    internal sealed class UnitGitSnapshot
    {
        public bool GitAvailable;
        public bool IsUnityProject;
        public bool HasRepository;
        public bool HasCommits;
        public string ProjectRoot = string.Empty;
        public string CurrentBranch = string.Empty;
        public string Upstream = string.Empty;
        public int Ahead;
        public int Behind;
        public string LastError = string.Empty;
        public List<UnitGitBranch> Branches = new List<UnitGitBranch>();
        public List<UnitGitStatusEntry> Changes = new List<UnitGitStatusEntry>();
        public UnitGitFileList ChangeList = new UnitGitFileList();
        public List<UnitGitCommit> Commits = new List<UnitGitCommit>();
        public bool HasMoreCommits;
        public UnitGitReleaseFile Releases = new UnitGitReleaseFile();
        public List<string> Shelves = new List<string>();
        public string HeadMessage = string.Empty;
    }

    internal sealed class UnitGitBranch
    {
        public string FullRef = string.Empty;
        public string Name = string.Empty;
        public string Upstream = string.Empty;
        public string ShortHash = string.Empty;
        public bool IsCurrent;
        public bool IsRemote;
    }

    internal sealed class UnitGitStatusEntry
    {
        public string Path = string.Empty;
        public string OriginalPath = string.Empty;
        public char IndexStatus = ' ';
        public char WorkTreeStatus = ' ';

        public bool IsUntracked
        {
            get { return IndexStatus == '?' && WorkTreeStatus == '?'; }
        }

        public bool IsStaged
        {
            get { return IndexStatus != ' ' && IndexStatus != '?'; }
        }

        public bool IsUnstaged
        {
            get { return WorkTreeStatus != ' ' || IsUntracked; }
        }

        public string DisplayStatus
        {
            get
            {
                if (IndexStatus == 'U' || WorkTreeStatus == 'U' ||
                    (IndexStatus == 'A' && WorkTreeStatus == 'A') ||
                    (IndexStatus == 'D' && WorkTreeStatus == 'D'))
                    return "conflict";

                if (IsUntracked)
                {
                    return "untracked";
                }

                if (IndexStatus == 'A' || WorkTreeStatus == 'A')
                {
                    return "added";
                }

                if (IndexStatus == 'M' || WorkTreeStatus == 'M')
                {
                    return "modified";
                }

                if (IndexStatus == 'D' || WorkTreeStatus == 'D')
                {
                    return "deleted";
                }

                if (IndexStatus == 'R' || WorkTreeStatus == 'R')
                {
                    return "renamed";
                }

                if (IndexStatus == 'C' || WorkTreeStatus == 'C')
                {
                    return "copied";
                }

                return (IndexStatus.ToString() + WorkTreeStatus).Trim();
            }
        }
    }

    internal sealed class UnitGitCommit
    {
        public string ShortHash = string.Empty;
        public string FullHash = string.Empty;
        public string Subject = string.Empty;
        public string AuthorName = string.Empty;
        public string AuthorEmail = string.Empty;
        public string RelativeDate = string.Empty;
        public string Decorations = string.Empty;
        public string ReleaseId = string.Empty;

        public bool HasRelease
        {
            get { return !string.IsNullOrWhiteSpace(ReleaseId); }
        }
    }

    internal sealed class UnitGitCommitDetails
    {
        public UnitGitCommit Commit;
        public string AuthorDate = string.Empty;
        public string CommitterName = string.Empty;
        public string CommitterEmail = string.Empty;
        public string CommitterDate = string.Empty;
        public List<string> ChangedFiles = new List<string>();
    }

    internal sealed class UnitGitDiff
    {
        public string Path = string.Empty;
        public string LeftTitle = "Repository";
        public string RightTitle = "Current version";
        public List<UnitGitDiffLine> Lines = new List<UnitGitDiffLine>();
        public int DifferenceCount;
        public int MaxLineLength;
    }

    internal sealed class UnitGitDiffLine
    {
        public string Left = string.Empty;
        public string Right = string.Empty;
        public UnitGitDiffLineKind Kind;
    }

    internal enum UnitGitDiffLineKind
    {
        Context,
        Added,
        Removed,
        Changed,
        Hunk
    }

    public enum UnitGitRemoteProvider
    {
        GitHub,
        GitLab
    }

    internal enum UnitGitResetMode
    {
        Soft,
        Mixed,
        Hard,
        Keep
    }
}
