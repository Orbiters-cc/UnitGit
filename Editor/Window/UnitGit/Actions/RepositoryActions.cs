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
            // The prompt is modeless: branch from the commit selected when it opened, not whatever is selected on submit.
            string startPoint = selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.FullHash)
                ? selectedCommit.FullHash
                : null;
            UnitGitBranchPromptWindow.Open(defaultName, branchName =>
                RunAction("create branch " + branchName, () => gitService.CreateBranch(branchName, startPoint)));
        }

        private void UpdateSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                AppendConsole("update branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            RunAction("update " + branch.Name, () => gitService.UpdateBranch(branch));
        }

        private void DeleteSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                AppendConsole("delete branch", "Select a branch first.");
                RebuildContent();
                return;
            }

            string command = branch.IsRemote
                ? GitCommand("branch", "-dr", branch.Name)
                : GitCommand("branch", "-d", branch.Name);
            string warning = branch.IsRemote
                ? "This deletes only the local remote-tracking branch. It does not push a remote deletion."
                : "This removes the local branch ref. Git may refuse if the branch is not merged.";
            if (!ConfirmGitOperation(
                    "Delete Branch",
                    "Delete",
                    command,
                    "Delete branch " + branch.Name + ".",
                    warning,
                    0))
            {
                return;
            }

            RunAction("delete " + branch.Name, () => gitService.DeleteBranch(branch));
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
            bool amend = commitAmend;
            if (!ConfirmGitOperation(
                    amend ? "Amend Commit" : "Commit Staged Changes",
                    amend ? "Amend" : "Commit",
                    amend ? GitCommand("commit", "--amend", "-m", message) : GitCommand("commit", "-m", message),
                    amend ? "Amend the previous commit with the staged changes." : "Create a commit from the staged changes.",
                    amend ? HistoryRewriteWarning : string.Empty,
                    GetStagedChangeCount()))
            {
                return;
            }

            RunAction(
                amend ? "amend commit" : "commit",
                () => amend ? gitService.CommitAmend(message) : gitService.Commit(message),
                result =>
                {
                    if (result != null && result.Success)
                    {
                        if (commitMessage.Trim() == message)
                            commitMessage = string.Empty;
                        commitMessageBeforeAmend = string.Empty;
                        commitAmend = false;
                        contentRoot?.Q<TextField>(className: "unitgit-local-commit-message")?.SetValueWithoutNotify(commitMessage);
                        contentRoot?.Q<Toggle>(className: "unitgit-local-amend-toggle")?.SetValueWithoutNotify(false);
                        var submit = contentRoot?.Q<Button>("unitgit-commit-submit");
                        if (submit != null)
                            submit.text = "Commit Staged";
                    }
                });
        }

    }
}
