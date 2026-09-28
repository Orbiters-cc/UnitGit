using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class UnitGitAuditRegressionTests
    {
        private string root;
        private UnitGitService git;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "unitgit-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            File.WriteAllText(Path.Combine(root, "Packages/manifest.json"), "{}");
            git = new UnitGitService(root);
            if (!git.IsGitAvailable()) Assert.Ignore("Git is required.");
            Run("init", "-b", "main");
            Run("config", "user.name", "Unit Git regression");
            Run("config", "user.email", "unitgit@example.test");
            Run("config", "commit.gpgsign", "false");
            Run("config", "core.autocrlf", "false");
        }

        [TearDown] public void TearDown()
        {
            git.ProcessLogReceived = null;
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        private string Run(params string[] args)
        {
            var result = git.RunGit(30000, args);
            Assert.That(result.Success, Is.True, result.Message);
            return result.StandardOutput.Trim();
        }
        private string Commit(string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets/file.txt"), text);
            Run("add", "--", "Assets/file.txt"); Run("commit", "-m", text);
            return Run("rev-parse", "HEAD");
        }

        [TestCase(false)] [TestCase(true)]
        public void RewriteUpdatesCapturedBranchAfterConcurrentCheckout(bool squash)
        {
            string first = Commit("first"), second = Commit("second");
            Run("branch", "other", first);
            bool raced = false;
            git.ProcessLogReceived = line =>
            {
                if (raced || !line.StartsWith("> git update-ref ")) return;
                raced = true;
                var otherProcess = new UnitGitService(root);
                Assert.That(otherProcess.RunGit(30000, "checkout", "other").Success, Is.True);
            };
            var result = squash ? git.SquashCommits(new[] { first, second }, "rewritten") : git.RenameCommit(first, "rewritten");
            git.ProcessLogReceived = null;
            Assert.That(raced, Is.True); Assert.That(result.Success, Is.True, result.Message);
            Assert.That(Run("symbolic-ref", "--short", "HEAD"), Is.EqualTo("other"));
            Assert.That(Run("rev-parse", "other"), Is.EqualTo(first));
            Assert.That(Run("rev-parse", "main"), Is.Not.EqualTo(second));
            Assert.That(Run("rev-parse", "main^{tree}"), Is.EqualTo(Run("rev-parse", second + "^{tree}")));
            Assert.That(File.ReadAllText(Path.Combine(root, "Assets/file.txt")), Is.EqualTo("first"));
        }

        [Test]
        public void RewriteRefusesToOverwriteConcurrentBranchAdvance()
        {
            string first = Commit("first"), second = Commit("second"), concurrent = null;
            git.ProcessLogReceived = line =>
            {
                if (concurrent != null || !line.StartsWith("> git update-ref ")) return;
                var process = new UnitGitService(root);
                var commit = process.RunGit(30000, "commit-tree", second + "^{tree}", "-p", second, "-m", "concurrent");
                Assert.That(commit.Success, Is.True, commit.Message); concurrent = commit.StandardOutput.Trim();
                Assert.That(process.RunGit(30000, "update-ref", "refs/heads/main", concurrent, second).Success, Is.True);
            };
            var result = git.RenameCommit(first, "renamed");
            git.ProcessLogReceived = null;
            Assert.That(concurrent, Is.Not.Null); Assert.That(result.Success, Is.False);
            Assert.That(Run("rev-parse", "main"), Is.EqualTo(concurrent));
        }

        [Test]
        public void RewritePreservesStagedAndUnstagedContent()
        {
            string first = Commit("first"); Commit("second");
            File.WriteAllText(Path.Combine(root, "Assets/file.txt"), "staged"); Run("add", "Assets/file.txt");
            File.WriteAllText(Path.Combine(root, "Assets/file.txt"), "unstaged");
            Assert.That(git.RenameCommit(first, "renamed").Success, Is.True);
            Assert.That(Run("show", ":Assets/file.txt"), Is.EqualTo("staged"));
            Assert.That(File.ReadAllText(Path.Combine(root, "Assets/file.txt")), Is.EqualTo("unstaged"));
        }

        [Test]
        public void UpdateChecksOutSelectedBranchEvenWhenSnapshotSaysCurrent()
        {
            string first = Commit("first"), target = Commit("target");
            Run("branch", "source", target); Run("reset", "--hard", first);
            Run("remote", "add", "origin", root);
            Run("config", "branch.main.remote", "."); Run("config", "branch.main.merge", "refs/heads/source");
            var selected = new UnitGitBranch { Name = "main", FullRef = "refs/heads/main", IsCurrent = true, Upstream = "obsolete" };
            Run("checkout", "-b", "other");
            var result = git.UpdateBranch(selected);
            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(Run("symbolic-ref", "--short", "HEAD"), Is.EqualTo("main"));
            Assert.That(Run("rev-parse", "main"), Is.EqualTo(target));
            Assert.That(Run("rev-parse", "other"), Is.EqualTo(first));
        }

        [Test]
        public void RefreshReplacesStaleSelectedBranchObject()
        {
            var window = ScriptableObject.CreateInstance<UnitGitWindow>();
            try
            {
                var stale = new UnitGitBranch { FullRef = "refs/heads/main", IsCurrent = true };
                var fresh = new UnitGitBranch { FullRef = stale.FullRef, IsCurrent = false, Upstream = "new" };
                var snapshot = new UnitGitSnapshot(); snapshot.Branches.Add(fresh);
                typeof(UnitGitWindow).GetField("snapshot", Private).SetValue(window, snapshot);
                typeof(UnitGitWindow).GetField("selectedBranch", Private).SetValue(window, stale);
                typeof(UnitGitWindow).GetMethod("ReconcileSnapshotSelection", Private).Invoke(window, null);
                Assert.That(typeof(UnitGitWindow).GetField("selectedBranch", Private).GetValue(window), Is.SameAs(fresh));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void HistorySearchAndLoadingReachPastFirstChunk()
        {
            var input = new StringBuilder();
            for (int i = 1; i <= 602; i++)
            {
                string subject = i == 1 ? "ancient needle" : "commit " + i;
                input.Append("commit refs/heads/main\nmark :").Append(i)
                    .Append("\ncommitter Test <unitgit@example.test> 1700000000 +0000\ndata ").Append(subject.Length)
                    .Append('\n').Append(subject).Append('\n');
                if (i > 1) input.Append("from :").Append(i - 1).Append('\n');
                input.Append('\n');
            }
            using (var process = Process.Start(new ProcessStartInfo("git", "fast-import --quiet") {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true }))
            {
                process.StandardInput.Write(input.ToString()); process.StandardInput.Close();
                string error = process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero, error);
            }
            Assert.That(git.GetCommits("ancient needle").Single().Subject, Is.EqualTo("ancient needle"));
            var initial = git.BuildSnapshot(""); Assert.That(initial.Commits.Count, Is.EqualTo(300)); Assert.That(initial.HasMoreCommits, Is.True);
            var expanded = git.BuildSnapshot("", 900); Assert.That(expanded.Commits.Count, Is.EqualTo(602)); Assert.That(expanded.HasMoreCommits, Is.False);
            Assert.That(expanded.Commits.Last().Subject, Is.EqualTo("ancient needle"));

            var window = ScriptableObject.CreateInstance<UnitGitWindow>();
            try
            {
                typeof(UnitGitWindow).GetField("snapshot", Private).SetValue(window, initial);
                typeof(UnitGitWindow).GetField("gitService", Private).SetValue(window, null);
                typeof(UnitGitWindow).GetField("logPage", Private).SetValue(window, 2);
                typeof(UnitGitWindow).GetMethod("NextLogPage", Private).Invoke(window, null);
                Assert.That(typeof(UnitGitWindow).GetField("historyLimit", Private).GetValue(window), Is.EqualTo(600));
                Assert.That(typeof(UnitGitWindow).GetField("logPage", Private).GetValue(window), Is.EqualTo(3));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void DiffRetainsHeaderLikeContentAcrossFiles()
        {
            var diff = new UnitGitDiff();
            UnitGitService.ParseUnifiedDiff("diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n--- old text\n+++ new text\ndiff --git a/b b/b\n--- a/b\n+++ b/b\n@@ -1 +1 @@\n-old\n+new\n", diff);
            Assert.That(diff.DifferenceCount, Is.EqualTo(2));
            Assert.That(diff.Lines.Any(line => line.Left == "-- old text" && line.Right == "++ new text"), Is.True);
            Assert.That(diff.Lines.Any(line => line.Left == "a/b" || line.Right == "b/b"), Is.False);
        }

        [TestCase("DD")] [TestCase("AU")] [TestCase("UD")] [TestCase("UA")]
        [TestCase("DU")] [TestCase("AA")] [TestCase("UU")]
        public void AllUnmergedPairsAreConflicts(string pair)
        {
            var entry = UnitGitService.ParseStatusOutput(pair + " Assets/file.txt\n").Changes.Single();
            Assert.That(entry.DisplayStatus, Is.EqualTo("conflict"));
        }

        [UnityTest]
        public IEnumerator UnchangedCheckpointSucceedsAndPreservesUnrelatedIndex()
        {
            string original = Commit("first");
            File.WriteAllText(Path.Combine(root, "Assets/unrelated.txt"), "staged elsewhere"); Run("add", "Assets/unrelated.txt");
            byte[] index = File.ReadAllBytes(Path.Combine(root, ".git/index"));
            var save = UnitGitReleases.CommitProjectFilesAsync(root, "texture change", "Assets/file.txt");
            while (!save.IsCompleted) yield return null;
            Assert.That(save.Result.Success, Is.True, save.Result.Message); Assert.That(save.Result.NoChanges, Is.True);
            Assert.That(Run("rev-parse", "HEAD"), Is.EqualTo(original));
            Assert.That(File.ReadAllBytes(Path.Combine(root, ".git/index")), Is.EqualTo(index));
        }
    }
}
