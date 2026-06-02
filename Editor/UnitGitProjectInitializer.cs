using System;
using System.IO;
using System.Text;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitProjectInitializer
    {
        private const string InitialCommitMessage = "Initial commit";
        private const string ExcludeRootPackagePattern = "/[Pp]ackage/";

        private static readonly string[] VrchatUnityGitIgnorePatterns =
        {
            "",
            "# Unit Git VRChat Unity project rules",
            ".utmp/",
            "/[Ll]ibrary/",
            "/[Tt]emp/",
            "/[Oo]bj/",
            "/[Bb]uild/",
            "/[Bb]uilds/",
            "/[Ll]ogs/",
            "/[Uu]ser[Ss]ettings/",
            "/[Mm]emoryCaptures/",
            "/[Rr]ecordings/",
            ".vs/",
            ".idea/",
            ".gradle/",
            "ExportedObj/",
            "*.csproj",
            "*.unityproj",
            "*.sln",
            "*.suo",
            "*.tmp",
            "*.user",
            "*.userprefs",
            "*.pidb",
            "*.booproj",
            "*.svd",
            "*.pdb",
            "*.mdb",
            "*.opendb",
            "*.VC.db",
            "*.pidb.meta",
            "*.pdb.meta",
            "*.mdb.meta",
            "sysinfo.txt",
            "*.apk",
            "*.aab",
            "*.unitypackage",
            "*.unitypackage.meta",
            "*.app",
            "crashlytics-build.properties",
            "/[Aa]ssets/[Aa]ddressable[Aa]ssets[Dd]ata/*/*.bin*",
            "/[Aa]ssets/[Ss]treamingAssets/aa.meta",
            "/[Aa]ssets/[Ss]treamingAssets/aa/*",
            "Packages/com.vrchat.*",
            "!Packages/com.vrchat.core.vpm-resolver/",
            "!Packages/com.vrchat.core.vpm-resolver/**",
            "Packages/com.vrcfury.*",
            "Packages/com.poiyomi.*",
            "!Packages/manifest.json",
            "!Packages/packages-lock.json",
            "!Packages/vpm-manifest.json"
        };

        public GitCommandResult Initialize(UnitGitService gitService, bool excludeRootPackageFolder)
        {
            if (gitService == null)
            {
                return Failure("Git service is not available.");
            }

            if (!UnitGitService.IsUnityProject(gitService.ProjectRoot))
            {
                return Failure("Cannot initialize Git because this folder is not a Unity project.");
            }

            EnsureVrchatGitIgnore(gitService.ProjectRoot, excludeRootPackageFolder);

            var initResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "init");
            if (!initResult.Success)
            {
                return initResult;
            }

            var addResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "add", "-A", "--", ".");
            if (!addResult.Success)
            {
                return addResult;
            }

            var commitResult = gitService.RunGit(UnitGitService.LongTimeoutMilliseconds, "commit", "-m", InitialCommitMessage);
            if (!commitResult.Success)
            {
                return commitResult;
            }

            return new GitCommandResult
            {
                ExitCode = 0,
                StandardOutput = "Initialized project repository and created the first commit."
            };
        }

        private static void EnsureVrchatGitIgnore(string projectRoot, bool excludeRootPackageFolder)
        {
            string path = Path.Combine(projectRoot, ".gitignore");
            string existing = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : string.Empty;

            var builder = new StringBuilder(existing.TrimEnd());
            bool changed = false;
            foreach (string pattern in VrchatUnityGitIgnorePatterns)
            {
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (existing.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (!changed)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    changed = true;
                }

                builder.AppendLine(pattern);
            }

            if (excludeRootPackageFolder &&
                existing.IndexOf(ExcludeRootPackagePattern, StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (!changed)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    changed = true;
                }

                builder.AppendLine("# Unit Git optional first-commit exclusion");
                builder.AppendLine(ExcludeRootPackagePattern);
            }

            if (!File.Exists(path) || changed)
            {
                File.WriteAllText(path, builder.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static GitCommandResult Failure(string message)
        {
            return new GitCommandResult
            {
                ExitCode = 1,
                StandardError = message
            };
        }
    }
}
