using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitSmokeTests
    {
        [Test]
        public void TempRepoStagesCommitsShowsMixedDiffAndStashes()
        {
            RequireGit();
            string root = CreateTempFolder();
            try
            {
                var service = new UnitGitService(root);
                AssertGit(service.RunGit(30000, "init"));
                AssertGit(service.RunGit(30000, "config", "user.email", "unitgit@example.test"));
                AssertGit(service.RunGit(30000, "config", "user.name", "Unit Git Tests"));
                AssertGit(service.RunGit(30000, "config", "core.autocrlf", "false"));

                string filePath = Path.Combine(root, "file.txt");
                File.WriteAllText(filePath, "one\nbase\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                File.WriteAllText(filePath, "one\nstaged\n");
                AssertGit(service.StageAll());
                File.WriteAllText(filePath, "one\nunstaged\n");

                UnitGitDiff diff = service.GetFileDiff(new UnitGitStatusEntry
                {
                    Path = "file.txt",
                    IndexStatus = 'M',
                    WorkTreeStatus = 'M'
                });

                Assert.That(diff.Lines.Any(line => line.Right.Contains("Staged changes")), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right.Contains("Unstaged changes")), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right == "staged"), Is.True);
                Assert.That(diff.Lines.Any(line => line.Right == "unstaged"), Is.True);

                AssertGit(service.ShelveAll("unitgit smoke shelf"));
                GitCommandResult stashList = service.RunGit(30000, "stash", "list");
                AssertGit(stashList);
                Assert.That(UnitGitService.ParseStashList(stashList.StandardOutput).Any(line => line.Contains("unitgit smoke shelf")), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void UntrackedLargeBinaryPreviewIsSkipped()
        {
            string root = CreateTempFolder();
            try
            {
                var service = new UnitGitService(root);
                var bytes = new byte[600 * 1024];
                bytes[0] = 0;
                File.WriteAllBytes(Path.Combine(root, "large.bin"), bytes);

                UnitGitDiff diff = service.GetFileDiff(new UnitGitStatusEntry
                {
                    Path = "large.bin",
                    IndexStatus = '?',
                    WorkTreeStatus = '?'
                });

                Assert.That(diff.Lines, Has.Count.EqualTo(1));
                Assert.That(diff.Lines[0].Right, Does.Contain("preview skipped"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void PublishReleaseRollsBackReleaseFileWhenScopedCommitFails()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                AssertGit(service.RunGit(30000, "init"));
                AssertGit(service.RunGit(30000, "config", "user.email", "unitgit@example.test"));
                AssertGit(service.RunGit(30000, "config", "user.name", "Unit Git Tests"));
                AssertGit(service.RunGit(30000, "config", "core.autocrlf", "false"));

                string assetPath = Path.Combine(root, "Assets", "file.txt");
                File.WriteAllText(assetPath, "initial\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                var entry = new UnitGitReleaseEntry
                {
                    id = "release-rollback-test",
                    tool = "MCB",
                    type = "mcb-version",
                    name = "Test Asset",
                    version = "1.0.0"
                };

                UnitGitReleaseResult result = UnitGitReleases.PublishRelease(
                    entry,
                    "MCB : v1.0.0",
                    service,
                    "Assets/file.txt",
                    "Assets/missing-file.txt");

                Assert.That(result.Success, Is.False);
                Assert.That(result.Message, Does.Contain("Staging files failed"));
                Assert.That(File.Exists(UnitGitReleases.GetReleasesFilePath(root)), Is.False);

                GitCommandResult releaseStatus = service.RunGit(30000, "status", "--porcelain=v1", "--", UnitGitReleases.ReleasesFileName);
                AssertGit(releaseStatus);
                Assert.That(releaseStatus.StandardOutput.Trim(), Is.Empty);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        private static void RequireGit()
        {
            var service = new UnitGitService(Path.GetTempPath());
            if (!service.IsGitAvailable())
            {
                Assert.Ignore("Git is not available on PATH.");
            }
        }

        private static string CreateTempFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "unitgit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string CreateTempUnityProjectFolder()
        {
            string root = CreateTempFolder();
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{}\n");
            return root;
        }

        private static void DeleteTempFolder(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.SetAttributes(root, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        private static void AssertGit(GitCommandResult result)
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
