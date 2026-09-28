using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>The project's history in brief, for other tools to show beside their own UI.</summary>
    public sealed class UnitGitSummary
    {
        public bool GitAvailable;
        public bool HasRepository;
        public bool HasCommits;
        public string Branch = string.Empty;
        /// <summary>Where "origin" (or the first remote) points; empty when the history lives only on this computer.</summary>
        public string RemoteUrl = string.Empty;
        public int Ahead, Behind, Changes;
        public string Error = string.Empty;
        /// <summary>Newest first.</summary>
        public List<UnitGitSummaryCommit> Commits = new List<UnitGitSummaryCommit>();
    }

    public sealed class UnitGitSummaryCommit
    {
        public string ShortHash = string.Empty;
        public string Subject = string.Empty;
        public string Author = string.Empty;
        public string RelativeDate = string.Empty;
        /// <summary>Branches and tags pointing at this commit, e.g. "HEAD -> main, origin/main".</summary>
        public string Decorations = string.Empty;
        public bool IsHead;
        /// <summary>Title of the Unit Git release checkpoint this commit records, if any.</summary>
        public string Release = string.Empty;
    }

    /// <summary>
    /// Unit Git for other Orbiters tools: a summary of the history with its latest commits, creating the history, and
    /// putting it online on GitHub or GitLab (through their command-line tools) or on an existing remote. Nothing here
    /// pushes commits. Every call runs Git off the main thread.
    /// </summary>
    public static class UnitGitOverview
    {
        /// <summary>Raised on the main thread after this API or Unit Git changed the history or its remotes.</summary>
        public static event Action Changed;

        static UnitGitOverview()
        {
            UnitGitReleases.ChangedExternally += RaiseChanged;
        }

        public static string ProjectRoot => UnitGitService.GetUnityProjectRoot();

        /// <summary>A repository name made from the Unity project's folder, e.g. "mcb-test".</summary>
        public static string DefaultRepositoryName
        {
            get
            {
                string name = Path.GetFileName(ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name)) return "unity-project";
                var characters = name.Trim()
                    .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-')
                    .ToArray();
                string cleaned = new string(characters).Trim('-', '.', '_').ToLowerInvariant();
                return string.IsNullOrEmpty(cleaned) ? "unity-project" : cleaned;
            }
        }

        public static void OpenWindow() => UnitGitWindow.OpenWindow();

        public static Task<UnitGitSummary> LoadAsync(int commitCount = 6)
        {
            string root = ProjectRoot;
            return Task.Run(() =>
            {
                var service = new UnitGitService(root);
                var summary = new UnitGitSummary { GitAvailable = service.IsGitAvailable(), HasRepository = UnitGitService.HasRepository(root) };
                if (!summary.GitAvailable) { summary.Error = "Git is not installed."; return summary; }
                if (!summary.HasRepository) return summary;

                summary.HasCommits = service.HasCommits();
                var branch = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-parse", "--abbrev-ref", "HEAD");
                if (branch.Success) summary.Branch = branch.StandardOutput.Trim();
                var status = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "status", "--porcelain=v1");
                if (status.Success) summary.Changes = status.StandardOutput.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
                else summary.Error = status.Message;
                var remotes = service.GetRemoteNames();
                string remote = remotes.FirstOrDefault(r => string.Equals(r, "origin", StringComparison.OrdinalIgnoreCase)) ?? remotes.FirstOrDefault();
                if (remote != null)
                {
                    var url = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "remote", "get-url", remote);
                    if (url.Success) summary.RemoteUrl = url.StandardOutput.Trim();
                }
                var counts = service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "rev-list", "--left-right", "--count", "HEAD...@{upstream}");
                var parts = counts.Success ? counts.StandardOutput.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
                if (parts.Length == 2 && int.TryParse(parts[0], out int ahead) && int.TryParse(parts[1], out int behind)) { summary.Ahead = ahead; summary.Behind = behind; }

                if (summary.HasCommits)
                {
                    var releases = UnitGitReleases.Load(root).releases;
                    foreach (var commit in service.GetCommits(string.Empty, commitCount))
                    {
                        var release = commit.HasRelease ? releases.FirstOrDefault(r => r.id == commit.ReleaseId) : null;
                        summary.Commits.Add(new UnitGitSummaryCommit
                        {
                            ShortHash = commit.ShortHash,
                            Subject = commit.Subject,
                            Author = commit.AuthorName,
                            RelativeDate = commit.RelativeDate,
                            Decorations = commit.Decorations,
                            IsHead = commit.Decorations.StartsWith("HEAD", StringComparison.Ordinal),
                            Release = release == null ? string.Empty : string.IsNullOrWhiteSpace(release.title) ? release.name + " " + release.version : release.title,
                        });
                    }
                }
                return summary;
            });
        }

        /// <summary>Creates the history with a VRChat-ready .gitignore and a first commit of the whole project.</summary>
        public static Task<string> InitializeAsync()
        {
            AssetDatabase.SaveAssets();
            string root = ProjectRoot;
            return Run(() => new UnitGitProjectInitializer().Initialize(new UnitGitService(root), false));
        }

        public static Task<bool> IsCliAvailableAsync(UnitGitRemoteProvider provider)
        {
            string root = ProjectRoot;
            return Task.Run(() =>
            {
                var service = new UnitGitService(root);
                return provider == UnitGitRemoteProvider.GitLab ? service.IsGitLabCliAvailable() : service.IsGitHubCliAvailable();
            });
        }

        /// <summary>
        /// Creates an empty repository on GitHub or GitLab and registers it as "origin". Signs in first through the
        /// browser when the command-line tool is not signed in yet. Nothing is pushed.
        /// </summary>
        public static Task<string> CreateRemoteAsync(UnitGitRemoteProvider provider, string repositoryName, bool isPrivate)
        {
            string root = ProjectRoot;
            return Run(() =>
            {
                var service = new UnitGitService(root);
                bool gitLab = provider == UnitGitRemoteProvider.GitLab;
                if (!(gitLab ? service.IsGitLabCliSignedIn() : service.IsGitHubCliSignedIn()))
                {
                    var login = gitLab ? service.GitLabLogin() : service.GitHubLogin();
                    if (!login.Success) return login;
                }
                return gitLab ? service.CreateGitLabRemoteRepository(repositoryName, "origin", isPrivate)
                    : service.CreateGitHubRemoteRepository(repositoryName, "origin", isPrivate);
            });
        }

        /// <summary>Points "origin" at an existing repository URL, replacing what it pointed at. Nothing is pushed.</summary>
        public static Task<string> ConnectRemoteAsync(string url)
        {
            string root = ProjectRoot;
            return Run(() =>
            {
                if (string.IsNullOrWhiteSpace(url)) return new GitCommandResult { ExitCode = 1, StandardError = "Paste the repository URL first." };
                var service = new UnitGitService(root);
                bool exists = service.GetRemoteNames().Any(r => string.Equals(r, "origin", StringComparison.OrdinalIgnoreCase));
                return service.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "remote", exists ? "set-url" : "add", "origin", url.Trim());
            });
        }

        // Runs a Git operation off the main thread; throws with Git's message when it fails, and tells listeners after.
        private static async Task<string> Run(Func<GitCommandResult> operation)
        {
            var result = await Task.Run(operation);
            RaiseChanged();
            if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Message) ? "Git failed." : result.Message.Trim());
            return result.Message.Trim();
        }

        private static void RaiseChanged() => EditorApplication.delayCall += () => Changed?.Invoke();
    }
}
