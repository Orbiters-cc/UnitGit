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

        private static void DeleteTempFolder(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            Directory.Delete(root, true);
        }

        private static void AssertGit(GitCommandResult result)
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
