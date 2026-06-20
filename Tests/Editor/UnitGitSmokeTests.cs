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

        [Test]
        public void PublishReleaseAllStagesProjectChangesAndReleaseTrailer()
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

                string assetPath = Path.Combine(root, "Assets", "avatar.txt");
                string settingsPath = Path.Combine(root, "ProjectSettings", "AvatarUpload.asset");
                File.WriteAllText(assetPath, "initial\n");
                File.WriteAllText(settingsPath, "initial\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("initial"));

                File.WriteAllText(assetPath, "uploaded avatar\n");
                File.WriteAllText(settingsPath, "uploaded settings\n");

                var entry = new UnitGitReleaseEntry
                {
                    id = "avatar-upload-test",
                    tool = "VRChat SDK",
                    type = "avatar upload",
                    name = "Test Avatar",
                    scope = "PC"
                };

                UnitGitReleaseResult result = UnitGitReleases.PublishReleaseAll(
                    entry,
                    "vrchat avatar upload: Test Avatar",
                    service,
                    false);

                Assert.That(result.Success, Is.True, result.Message);
                Assert.That(result.ReleaseId, Is.EqualTo("avatar-upload-test"));

                UnitGitCommitDetails details = service.GetCommitDetails(result.CommitHash);
                Assert.That(details.Commit.ReleaseId, Is.EqualTo("avatar-upload-test"));
                Assert.That(details.ChangedFiles, Does.Contain("Assets/avatar.txt"));
                Assert.That(details.ChangedFiles, Does.Contain("ProjectSettings/AvatarUpload.asset"));
                Assert.That(details.ChangedFiles, Does.Contain(UnitGitReleases.ReleasesFileName));

                UnitGitReleaseEntry saved = UnitGitReleases.FindById(UnitGitReleases.Load(root), "avatar-upload-test");
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved.type, Is.EqualTo("avatar upload"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void CommitMessageWithSpacesAndAmendUseMessageFile()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("message with spaces"));

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.CommitAmend("amended message with spaces"));

                GitCommandResult message = service.GetHeadCommitMessage();
                AssertGit(message);
                Assert.That(message.StandardOutput, Is.EqualTo("amended message with spaces"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void HideReleaseStoresHiddenIdWithoutDeletingReleaseEntry()
        {
            string root = CreateTempUnityProjectFolder();
            try
            {
                var file = new UnitGitReleaseFile();
                file.releases.Add(new UnitGitReleaseEntry
                {
                    id = "release-hide-test",
                    name = "Hidden Release"
                });
                File.WriteAllText(UnitGitReleases.GetReleasesFilePath(root), UnityEngine.JsonUtility.ToJson(file, true));

                UnitGitReleaseResult result = UnitGitReleases.HideRelease(root, "release-hide-test");

                Assert.That(result.Success, Is.True, result.Message);
                UnitGitReleaseFile loaded = UnitGitReleases.Load(root);
                Assert.That(UnitGitReleases.FindById(loaded, "release-hide-test"), Is.Not.Null);
                Assert.That(UnitGitReleases.IsHidden(loaded, "release-hide-test"), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void RenameCommitRewritesSelectedCommitMessage()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));

                AssertGit(service.RenameCommit(first, "renamed one"));

                GitCommandResult log = service.RunGit(30000, "log", "--format=%s");
                AssertGit(log);
                Assert.That(log.StandardOutput, Does.Contain("renamed one"));
                Assert.That(log.StandardOutput, Does.Contain("two"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\none\n"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void RenameCommitKeepsSnapshotUsableWithLocalChanges()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                string assetPath = Path.Combine(root, "Assets", "file.txt");
                File.WriteAllText(assetPath, "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(assetPath, "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));

                File.WriteAllText(assetPath, "dirty local change\n");

                AssertGit(service.RenameCommit(first, "renamed one"));
                UnitGitSnapshot snapshot = service.BuildSnapshot(string.Empty);

                Assert.That(snapshot.HasRepository, Is.True);
                Assert.That(snapshot.HasCommits, Is.True);
                Assert.That(snapshot.LastError, Is.Empty);
                Assert.That(snapshot.Commits, Is.Not.Empty);
                Assert.That(snapshot.Changes.Any(change => change.Path == "Assets/file.txt"), Is.True);
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void SquashCommitsRewritesContiguousSelectionIntoOneCommit()
        {
            RequireGit();
            string root = CreateTempUnityProjectFolder();
            try
            {
                var service = new UnitGitService(root);
                ConfigureTempRepository(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "one\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("one"));
                string first = GetHead(service);

                File.WriteAllText(Path.Combine(root, "Assets", "file.txt"), "two\n");
                AssertGit(service.StageAll());
                AssertGit(service.Commit("two"));
                string second = GetHead(service);

                AssertGit(service.SquashCommits(new[] { first, second }, "one and two"));

                GitCommandResult log = service.RunGit(30000, "log", "--format=%s");
                AssertGit(log);
                Assert.That(log.StandardOutput, Does.Contain("one and two"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\none\n"));
                Assert.That(log.StandardOutput, Does.Not.Contain("\ntwo\n"));
            }
            finally
            {
                DeleteTempFolder(root);
            }
        }

        [Test]
        public void ResetCurrentBranchSupportsSoftMixedHardAndKeep()
        {
            RequireGit();

            string softRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(softRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Soft));
                Assert.That(GetPorcelainStatus(service), Does.Contain("M  Assets/file.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("two\n"));
            }
            finally
            {
                DeleteTempFolder(softRoot);
            }

            string mixedRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(mixedRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Mixed));
                Assert.That(GetPorcelainStatus(service), Does.Contain(" M Assets/file.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("two\n"));
            }
            finally
            {
                DeleteTempFolder(mixedRoot);
            }

            string hardRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(hardRoot, out string first, out _, out string assetPath);
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Hard));
                Assert.That(GetPorcelainStatus(service).Trim(), Is.Empty);
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("one\n"));
            }
            finally
            {
                DeleteTempFolder(hardRoot);
            }

            string keepRoot = CreateTempUnityProjectFolder();
            try
            {
                UnitGitService service = CreateTwoCommitRepo(keepRoot, out string first, out _, out string assetPath);
                File.WriteAllText(Path.Combine(keepRoot, "Assets", "local.txt"), "local\n");
                AssertGit(service.ResetCurrentBranch(first, UnitGitResetMode.Keep));
                Assert.That(GetPorcelainStatus(service), Does.Contain("?? Assets/local.txt"));
                Assert.That(File.ReadAllText(assetPath), Is.EqualTo("one\n"));
            }
            finally
            {
                DeleteTempFolder(keepRoot);
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

        private static UnitGitService CreateTwoCommitRepo(string root, out string first, out string second, out string assetPath)
        {
            var service = new UnitGitService(root);
            ConfigureTempRepository(service);

            assetPath = Path.Combine(root, "Assets", "file.txt");
            File.WriteAllText(assetPath, "one\n");
            AssertGit(service.StageAll());
            AssertGit(service.Commit("one"));
            first = GetHead(service);

            File.WriteAllText(assetPath, "two\n");
            AssertGit(service.StageAll());
            AssertGit(service.Commit("two"));
            second = GetHead(service);

            return service;
        }

        private static string GetPorcelainStatus(UnitGitService service)
        {
            GitCommandResult result = service.RunGit(30000, "status", "--porcelain=v1", "-uall");
            AssertGit(result);
            return result.StandardOutput;
        }

        private static void ConfigureTempRepository(UnitGitService service)
        {
            AssertGit(service.RunGit(30000, "init"));
            AssertGit(service.RunGit(30000, "config", "user.email", "unitgit@example.test"));
            AssertGit(service.RunGit(30000, "config", "user.name", "Unit Git Tests"));
            AssertGit(service.RunGit(30000, "config", "core.autocrlf", "false"));
        }

        private static string GetHead(UnitGitService service)
        {
            GitCommandResult result = service.RunGit(30000, "rev-parse", "HEAD");
            AssertGit(result);
            return result.StandardOutput.Trim();
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
