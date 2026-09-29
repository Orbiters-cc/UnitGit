using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitConflictTests
    {
        private const string Prefab = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100\nGameObject:\n  m_Name: {0}\n  m_IsActive: 1\n--- !u!4 &101\nTransform:\n  m_GameObject: {{fileID: 100}}\n  m_LocalPosition: {{x: 0, y: {1}, z: 0}}\n  m_Father: {{fileID: 0}}\n";

        private static UnitGitTestRepo Diverged(string mineName, string mineY, string theirName, string theirY, out string branch)
        {
            var repo = UnitGitTestRepo.Create();
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, "Hat", "0"));
            repo.Write("Assets/notes.txt", "one\ntwo\nthree\n");
            repo.Commit("base");
            branch = "theirs";
            repo.Run("branch", branch);
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, mineName, mineY));
            repo.Write("Assets/notes.txt", "one\nmine\nthree\n");
            repo.Commit("mine");
            repo.Run("checkout", "-q", branch);
            repo.Write("Assets/Hat.prefab", string.Format(Prefab, theirName, theirY));
            repo.Write("Assets/notes.txt", "one\ntheirs\nthree\n");
            repo.Commit("theirs");
            repo.Run("checkout", "-q", "main");
            return repo;
        }

        [Test]
        public void MergeWithConflictsIsResolvedPartByPartAndFinished()
        {
            using (var repo = Diverged("Hat", "1", "Hat", "0", out string branch))
            {
                var merge = UnitGitConflicts.MergeBranch(repo.Git, branch);
                Assert.That(merge.Success, Is.True, merge.Message);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.Merge));
                var conflicted = repo.Git.BuildSnapshot(string.Empty).Changes.Where(UnitGitConflicts.IsConflict).Select(c => c.Path).ToList();
                CollectionAssert.Contains(conflicted, "Assets/notes.txt");

                string mine = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 2);
                string baseText = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 1);
                string theirs = UnitGitConflicts.Stage(repo.Git, "Assets/notes.txt", 3);
                var blocks = UnitGitConflicts.Blocks(repo.Git, mine, baseText, theirs);
                var part = blocks.Single(b => b.IsConflict);
                Assert.That(part.Mine, Is.EqualTo("mine\n"));
                Assert.That(part.Theirs, Is.EqualTo("theirs\n"));
                Assert.That(UnitGitConflicts.Compose(blocks), Is.Null, "Nothing is written while a part has no choice.");
                part.Choice = ConflictChoice.Both;
                Assert.That(UnitGitConflicts.Resolve(repo.Git, "Assets/notes.txt", UnitGitConflicts.Compose(blocks)).Success, Is.True);
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\nmine\ntheirs\nthree\n"));

                // Only the prefab changed on one side: Git merged it. Anything left is resolved by keeping theirs.
                foreach (string path in repo.Git.BuildSnapshot(string.Empty).Changes.Where(UnitGitConflicts.IsConflict).Select(c => c.Path).ToList())
                    Assert.That(UnitGitConflicts.TakeSide(repo.Git, path, false).Success, Is.True);
                Assert.That(repo.Git.BuildSnapshot(string.Empty).Changes.Any(UnitGitConflicts.IsConflict), Is.False);
                var finish = UnitGitConflicts.Finish(repo.Git, UnitGitOperation.Merge);
                Assert.That(finish.Success, Is.True, finish.Message);
                Assert.That(UnitGitConflicts.Operation(repo.Root, out _), Is.EqualTo(UnitGitOperation.None));
                Assert.That(repo.Run("log", "-1", "--format=%P").Trim().Split(' ').Length, Is.EqualTo(2), "A merge commit with both parents.");
            }
        }

        [Test]
        public void MergeCanBeCancelled()
        {
            using (var repo = Diverged("Hat", "1", "Cap", "2", out string branch))
            {
                string before = repo.Run("rev-parse", "HEAD");
                UnitGitConflicts.MergeBranch(repo.Git, branch);
                Assert.That(UnitGitConflicts.Abort(repo.Git, UnitGitOperation.Merge).Success, Is.True);
                Assert.That(repo.Run("rev-parse", "HEAD"), Is.EqualTo(before));
                Assert.That(repo.Read("Assets/notes.txt"), Is.EqualTo("one\nmine\nthree\n"));
            }
        }

        // Different properties changed on each side: Unity merges them by object, where Git sees one conflict.
        [Test]
        public void UnityMergesDifferentPropertiesOfOneObject()
        {
            string tool = UnitGitConflicts.UnityMergeTool();
            if (tool == null) Assert.Ignore("UnityYAMLMerge is not installed with this editor.");
            string baseText = string.Format(Prefab, "Hat", "0");
            var clean = UnitGitConflicts.UnityMerge(tool, string.Format(Prefab, "Hat", "1"), baseText, string.Format(Prefab, "Cap", "0"), ".prefab");
            Assert.That(clean.Clean, Is.True, clean.Message);
            StringAssert.Contains("m_Name: Cap", clean.Output);
            StringAssert.Contains("y: 1", clean.Output);

            var conflict = UnitGitConflicts.UnityMerge(tool, string.Format(Prefab, "Mine", "0"), baseText, string.Format(Prefab, "Theirs", "0"), ".prefab");
            Assert.That(conflict.Clean, Is.False);
            var name = conflict.Conflicts.Single();
            Assert.That(name.Property, Is.EqualTo("m_Name"));
            Assert.That(name.Mine, Is.EqualTo("Mine"));
            Assert.That(name.Theirs, Is.EqualTo("Theirs"));
        }

        [Test]
        public void UnityPartsNameTheirObject()
        {
            string merged = "--- !u!1 &100\nGameObject:\n  m_Name: Hips\n<<<<<<< Mine\n  m_IsActive: 1\n||||||| Base\n  m_IsActive: 0\n=======\n  m_IsActive: 0\n>>>>>>> Theirs\n";
            var part = UnitGitConflicts.Parse(merged).Single(b => b.IsConflict);
            Assert.That(part.Context, Is.EqualTo("Hips (Game Object)"));
        }
    }
}
