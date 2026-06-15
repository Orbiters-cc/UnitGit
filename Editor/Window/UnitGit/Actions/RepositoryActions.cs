using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private string GetInitializeCommandPreview()
        {
            return GitCommand("init") + "\n" +
                   GitCommand("add", "-A", "--", ".") + "\n" +
                   GitCommand("commit", "-m", "Initial commit");
        }

        private void InitializeProjectGit()
        {
            if (!ConfirmGitOperation(
                    "Initialize Project Git",
                    "Initialize",
                    GetInitializeCommandPreview(),
                    "Create a Git repository at the Unity project root, update .gitignore if needed, stage the current project state, and create the first commit.",
                    "This can take several minutes on large Unity projects.",
                    null))
            {
                return;
            }

            RunAction("init", () => initializer.Initialize(gitService, excludePackageFolderFromInitialCommit));
        }

        private void PromptCreateBranch()
        {
            string defaultName = "feature/new-branch";
            UnitGitBranchPromptWindow.Open(defaultName, branchName =>
            {
                string startPoint = selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.FullHash)
                    ? selectedCommit.FullHash
                    : null;
                RunAction("create branch " + branchName, () => gitService.CreateBranch(branchName, startPoint));
            });
        }

        private void UpdateSelectedBranch()
        {
            if (selectedBranch == null)
            {
                AppendConsole("update branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            RunAction("update " + selectedBranch.Name, () => gitService.UpdateBranch(selectedBranch));
        }

        private void DeleteSelectedBranch()
        {
            if (selectedBranch == null)
            {
                AppendConsole("delete branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            string command = selectedBranch.IsRemote
                ? GitCommand("branch", "-dr", selectedBranch.Name)
                : GitCommand("branch", "-d", selectedBranch.Name);
            string warning = selectedBranch.IsRemote
                ? "This deletes only the local remote-tracking branch. It does not push a remote deletion."
                : "This removes the local branch ref. Git may refuse if the branch is not merged.";
            if (!ConfirmGitOperation(
                    "Delete Branch",
                    "Delete",
                    command,
                    "Delete branch " + selectedBranch.Name + ".",
                    warning,
                    0))
            {
                return;
            }

            RunAction("delete " + selectedBranch.Name, () => gitService.DeleteBranch(selectedBranch));
        }

        private void Fetch()
        {
            RunAction("fetch", () => gitService.Fetch());
        }

        private void OpenRemoteSetup()
        {
            UnitGitGitHubSetupWindow.Open(
                GetDefaultRemoteRepositoryName(),
                RunRemoteProviderLogin,
                CreateRemoteRepository);
        }

        private void PullFastForward()
        {
            if (!ConfirmGitOperation(
                    "Pull",
                    "Pull",
                    GitCommand("pull", "--ff-only"),
                    "Fast-forward the current branch from its upstream.",
                    "This updates the branch and working tree. Git will refuse non-fast-forward merges.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("pull", () => gitService.PullFastForward());
        }

        private void StageAll()
        {
            if (!ConfirmGitOperation(
                    "Stage All Changes",
                    "Stage",
                    GitCommand("add", "-A"),
                    "Stage all tracked, untracked, modified, and deleted files.",
                    string.Empty,
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("stage all", () => gitService.StageAll());
        }

        private void UnstageAll()
        {
            if (!ConfirmGitOperation(
                    "Unstage All Changes",
                    "Unstage",
                    GitCommand("reset"),
                    "Move all staged changes back to the working tree.",
                    string.Empty,
                    GetStagedChangeCount()))
            {
                return;
            }

            RunAction("unstage all", () => gitService.UnstageAll());
        }

        private void CommitStaged()
        {
            string message = string.IsNullOrWhiteSpace(commitMessage) ? string.Empty : commitMessage.Trim();
            if (!ConfirmGitOperation(
                    "Commit Staged Changes",
                    "Commit",
                    GitCommand("commit", "-m", message),
                    "Create a commit from the staged changes.",
                    string.Empty,
                    GetStagedChangeCount()))
            {
                return;
            }

            RunAction("commit", () => gitService.Commit(commitMessage));
            if (snapshot != null && snapshot.HasRepository)
            {
                commitMessage = string.Empty;
            }
        }

    }
}
