using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitBackupTests
    {
        // A backup holds every branch, reads back as healthy, and restores into a working copy of the project.
        [Test]
        public void BackupHoldsEveryBranchAndRestoresTheProject()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string folder = Path.Combine(Path.GetTempPath(), "unitgit-backups-" + Guid.NewGuid().ToString("N"));
                string restored = Path.Combine(Path.GetTempPath(), "unitgit-restored-" + Guid.NewGuid().ToString("N"));
                try
                {
                    repo.Write("Assets/Avatar.prefab", "v1");
                    repo.Commit("first");
                    repo.Run("branch", "experiment");
                    repo.Write("Assets/Avatar.prefab", "v2");
                    repo.Commit("second");

                    Assert.That(UnitGitBackups.IsDue(repo.Git, folder), Is.True);
                    var result = UnitGitBackups.Create(repo.Git, folder, 10);
                    Assert.That(result.Success, Is.True, result.Message);

                    var backup = UnitGitBackups.List(folder).Single();
                    Assert.That(backup.Healthy, Is.True);
                    Assert.That(backup.Branches, Is.EqualTo(2));
                    Assert.That(UnitGitBackups.IsDue(repo.Git, folder), Is.False, "A fresh backup is not due again.");

                    var restore = UnitGitBackups.RestoreCopy(repo.Git, backup.Path, restored);
                    Assert.That(restore.Success, Is.True, restore.Message);
                    Assert.That(File.ReadAllText(Path.Combine(restored, "Assets", "Avatar.prefab")), Is.EqualTo("v2"));
                    var copy = new UnitGitService(restored);
                    Assert.That(copy.RunGit(30000, "rev-parse", "--verify", "experiment").Success, Is.True);
                    Assert.That(copy.RunGit(30000, "remote").StandardOutput.Trim(), Is.Empty);
                }
                finally
                {
                    Delete(folder);
                    Delete(restored);
                }
            }
        }

        [Test]
        public void DamagedBackupIsReportedAndOldOnesArePruned()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string folder = Path.Combine(Path.GetTempPath(), "unitgit-backups-" + Guid.NewGuid().ToString("N"));
                try
                {
                    repo.Write("Assets/a.txt", "a");
                    repo.Commit("a");
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(UnitGitBackups.Create(repo.Git, folder, 2).Success, Is.True);
                    }
                    var backups = UnitGitBackups.List(folder);
                    Assert.That(backups.Count, Is.EqualTo(2));

                    byte[] bytes = File.ReadAllBytes(backups[0].Path);
                    File.WriteAllBytes(backups[0].Path, bytes.Take(bytes.Length / 2).ToArray());
                    Assert.That(UnitGitBackups.Check(repo.Git, backups[0].Path).Healthy, Is.False);

                    string restored = Path.Combine(folder, "not-empty");
                    Directory.CreateDirectory(restored);
                    File.WriteAllText(Path.Combine(restored, "keep.txt"), "x");
                    Assert.That(UnitGitBackups.RestoreCopy(repo.Git, backups[1].Path, restored).Success, Is.False);
                }
                finally
                {
                    Delete(folder);
                }
            }
        }

        [Test]
        public void FolderInsideTheProjectIsDetected()
        {
            Assert.That(UnitGitBackups.IsInsideProject(@"C:\Projects\Avatar\Backups", @"C:\Projects\Avatar"), Is.True);
            Assert.That(UnitGitBackups.IsInsideProject(@"C:\Projects\Avatar Backups", @"C:\Projects\Avatar"), Is.False);
        }

        private static void Delete(string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
    }
}
