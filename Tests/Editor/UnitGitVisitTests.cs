using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // Visit This Commit: an older commit checked out on its own, the later commits kept, and the way back with the shelf.
    public sealed class UnitGitVisitTests
    {
        [Test]
        public void VisitKeepsTheLaterCommitsOnTheBranchAndInTheLog()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                string third = Commit(repo, "three\n", "third");

                AssertOk(repo.Git.VisitCommit(first, null, false, out bool shelved));
                Assert.That(shelved, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
                Assert.That(repo.Run("rev-parse", "main").Trim(), Is.EqualTo(third), "Visiting never moves the branch.");
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\n"));
                Assert.That(repo.Git.GetCommits(string.Empty).Select(commit => commit.FullHash), Is.SupersetOf(new[] { first, second, third }));

                UnitGitSnapshot snapshot = repo.Git.BuildSnapshot(string.Empty);
                Assert.That(snapshot.IsDetached, Is.True);
                Assert.That(snapshot.CurrentBranch, Is.Empty);
                Assert.That(snapshot.DetachedCommit, Is.Not.Null);
                Assert.That(snapshot.DetachedCommit.FullHash, Is.EqualTo(first));
                Assert.That(snapshot.DetachedCommit.Subject, Is.EqualTo("first"));
                Assert.That(snapshot.Branches.Select(branch => branch.Name), Is.EqualTo(new[] { "main" }), "No '(HEAD detached at …)' pseudo-branch.");
                Assert.That(snapshot.Branches.Any(branch => branch.IsCurrent), Is.False);
                Assert.That(snapshot.PreviousBranch, Is.EqualTo("main"));
                Assert.That(snapshot.ReturnBranch, Is.EqualTo("main"));
                Assert.That(snapshot.NewerCommits, Is.EqualTo(2));
                Assert.That(snapshot.Commits.Select(commit => commit.FullHash), Is.SupersetOf(new[] { first, second, third }));
                Assert.That(UnitGitCommitGraph.IsHead(snapshot.Commits.Single(commit => commit.FullHash == first).Decorations), Is.True);
                Assert.That(UnitGitCommitGraph.IsHead(snapshot.Commits.Single(commit => commit.FullHash == third).Decorations), Is.False);
            }
        }

        [Test]
        public void TheVisitsOwnBranchIsTheWayBackAfterAnotherDetachedCheckout()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                Commit(repo, "three\n", "third");

                AssertOk(repo.Git.VisitCommit(second, null, false, out _));
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                // Git's previous checkout is now a commit, not a branch: only the visit remembers main.
                UnitGitSnapshot fromGit = repo.Git.BuildSnapshot(string.Empty);
                Assert.That(fromGit.PreviousBranch, Is.Empty);
                Assert.That(fromGit.ReturnBranch, Is.Empty);
                Assert.That(fromGit.NewerCommits, Is.EqualTo(-1));

                UnitGitSnapshot visit = repo.Git.BuildSnapshot(string.Empty, 300, null, "main");
                Assert.That(visit.ReturnBranch, Is.EqualTo("main"));
                Assert.That(visit.NewerCommits, Is.EqualTo(2));
                Assert.That(repo.Git.BuildSnapshot(string.Empty, 300, null, "gone").ReturnBranch, Is.Empty, "A deleted branch is no way back.");
            }
        }

        [Test]
        public void ReturnFromVisitChecksOutTheBranchAtItsLatestCommit()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string latest = Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));

                AssertOk(repo.Git.ReturnFromVisit("main", false, null));
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(latest));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("two\n"));
                Assert.That(repo.Git.BuildSnapshot(string.Empty).IsDetached, Is.False);
            }
        }

        [Test]
        public void ChangesShelvedBeforeVisitingComeBackWithTheBranch()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                repo.Write("Assets/file.txt", "work in progress\n");
                repo.Write("Assets/new.txt", "new file\n");
                const string shelf = "Before visiting test (2026-10-20 12:00:00)";

                AssertOk(repo.Git.VisitCommit(first, shelf, false, out bool shelved));
                Assert.That(shelved, Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("one\n"), "The visited commit as it was committed.");
                Assert.That(File.Exists(repo.Full("Assets/new.txt")), Is.False, "New files are shelved too.");
                Assert.That(repo.Git.FindShelf(shelf), Is.EqualTo("stash@{0}"));

                AssertOk(repo.Git.ReturnFromVisit("main", false, shelf));
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("work in progress\n"));
                Assert.That(repo.Read("Assets/new.txt"), Is.EqualTo("new file\n"));
                Assert.That(repo.Git.FindShelf(shelf), Is.Empty);
                Assert.That(repo.Run("stash", "list").Trim(), Is.Empty);
            }
        }

        [Test]
        public void VisitRefusesEditedFilesWithoutAShelf()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                string second = Commit(repo, "two\n", "second");
                repo.Write("Assets/file.txt", "unsaved work\n");

                GitCommandResult result = repo.Git.VisitCommit(first, null, false, out bool shelved);
                Assert.That(result.Success, Is.False);
                Assert.That(shelved, Is.False);
                Assert.That(repo.Run("symbolic-ref", "HEAD").Trim(), Is.EqualTo("refs/heads/main"));
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(second));
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("unsaved work\n"));
            }
        }

        [Test]
        public void ReturnRefusesFilesChangedWhileVisitingUnlessTheyAreDiscarded()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/file.txt", "re-saved by Unity\n");
                repo.Write("Assets/created.txt", "made while visiting\n");

                GitCommandResult refused = repo.Git.ReturnFromVisit("main", false, null);
                Assert.That(refused.Success, Is.False);
                Assert.That(repo.Git.IsHeadDetached(), Is.True);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("re-saved by Unity\n"));

                AssertOk(repo.Git.ReturnFromVisit("main", true, null));
                Assert.That(repo.Git.IsHeadDetached(), Is.False);
                Assert.That(repo.Read("Assets/file.txt"), Is.EqualTo("two\n"));
                Assert.That(repo.Read("Assets/created.txt"), Is.EqualTo("made while visiting\n"), "Nothing is cleaned: new files stay.");
            }
        }

        [Test]
        public void ReturnRefusesABranchThatDoesNotExist()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));

                Assert.That(repo.Git.ReturnFromVisit("gone", false, null).Success, Is.False);
                Assert.That(repo.Git.ReturnFromVisit("--force", false, null).Success, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
            }
        }

        [Test]
        public void CheckpointsAreRefusedWhileVisiting()
        {
            using (var repo = UnitGitTestRepo.Create())
            {
                string first = Commit(repo, "one\n", "first");
                Commit(repo, "two\n", "second");
                AssertOk(repo.Git.VisitCommit(first, null, false, out _));
                repo.Write("Assets/other.txt", "would be lost\n");

                UnitGitReleaseResult result = UnitGitReleases.CommitAll("checkpoint", null, repo.Git, false);
                Assert.That(result.Success, Is.False);
                Assert.That(repo.Run("rev-parse", "HEAD").Trim(), Is.EqualTo(first));
            }
        }

        private static string Commit(UnitGitTestRepo repo, string content, string message)
        {
            repo.Write("Assets/file.txt", content);
            return repo.Commit(message);
        }

        private static void AssertOk(GitCommandResult result)
        {
            Assert.That(result.Success, Is.True, result.Message);
        }
    }
}
