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
        private VisualElement BuildLocalChangesBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var split = BuildTrackedSplit(LocalChangesDiffSplitPref, 0, 620f);
            split.Add(BuildLocalChangesListPane());
            localDiffPaneRoot = new VisualElement();
            localDiffPaneRoot.AddToClassList("unitgit-diff-pane-container");
            localDiffPaneRoot.Add(BuildDiffViewerPane());
            split.Add(localDiffPaneRoot);
            workspace.Add(split);

            return workspace;
        }

        private VisualElement BuildLocalChangesListPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-changes-pane");

            bool changesExpanded = GetFoldoutExpanded(ChangesFoldPref, true);
            pane.Add(BuildFoldoutButton(
                "Changes",
                snapshot.Changes.Count + " files",
                changesExpanded,
                () => ToggleFoldout(ChangesFoldPref, true),
                "unitgit-changes-header",
                "unitgit-changes-foldout"));

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-changes-actions");
            actions.Add(BuildActionButton("Refresh", string.Empty, RefreshSnapshot));
            actions.Add(BuildActionButton("Stage All", "unitgit-button--primary", StageAll));
            actions.Add(BuildActionButton("Unstage All", string.Empty, UnstageAll));
            actions.Add(BuildActionButton("Shelve", string.Empty, ShelveAll));
            pane.Add(actions);

            var list = new ScrollView();
            list.name = "unitgit-local-changes-list";
            list.AddToClassList("unitgit-changes-list");
            localChangesListRoot = list;
            if (snapshot.Changes.Count == 0)
            {
                list.Add(BuildEmptyState("Working tree is clean."));
            }
            else if (!changesExpanded)
            {
                list.Add(BuildEmptyState("Changes collapsed."));
            }
            else
            {
                AddChangedFileGroups(list);
            }

            pane.Add(list);

            var commitPanel = new VisualElement();
            commitPanel.AddToClassList("unitgit-local-commit-panel");

            var amendToggle = new Toggle("Amend");
            amendToggle.value = commitAmend;
            amendToggle.AddToClassList("unitgit-local-amend-toggle");
            amendToggle.RegisterValueChangedCallback(evt => SetAmendCommit(evt.newValue));
            commitPanel.Add(amendToggle);

            var message = new TextField();
            message.multiline = false;
            message.value = commitMessage;
            message.AddToClassList("unitgit-local-commit-message");
            message.RegisterValueChangedCallback(evt => commitMessage = evt.newValue);
            commitPanel.Add(message);
            commitPanel.Add(BuildActionButton(commitAmend ? "Amend Commit" : "Commit Staged", "unitgit-button--primary", CommitStaged));
            pane.Add(commitPanel);

            return pane;
        }

        private void SetAmendCommit(bool value)
        {
            if (commitAmend == value)
            {
                return;
            }

            if (value)
            {
                commitMessageBeforeAmend = commitMessage;
                GitCommandResult result = gitService != null ? gitService.GetHeadCommitMessage() : null;
                if (result == null || !result.Success)
                {
                    string message = result != null && !string.IsNullOrWhiteSpace(result.Message)
                        ? result.Message
                        : "Could not read the previous commit message.";
                    EditorUtility.DisplayDialog("Amend Commit", message, "OK");
                    commitAmend = false;
                    RebuildContent();
                    return;
                }

                commitMessage = result.StandardOutput;
                commitAmend = true;
            }
            else
            {
                commitAmend = false;
                commitMessage = commitMessageBeforeAmend;
                commitMessageBeforeAmend = string.Empty;
            }

            RebuildContent();
        }

        private void AddChangedFileGroups(VisualElement parent)
        {
            var changes = snapshot.Changes
                .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(change => GetTopFolder(change.Path))
                .OrderBy(group => string.Equals(group.Key, "root", StringComparison.OrdinalIgnoreCase) ? string.Empty : group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, UnitGitStatusEntry> group in changes)
            {
                bool isRoot = string.Equals(group.Key, "root", StringComparison.OrdinalIgnoreCase);
                if (!isRoot)
                {
                    string prefKey = GetFoldoutPrefKey("ChangesFolder", group.Key);
                    bool expanded = GetFoldoutExpanded(prefKey, true);
                    parent.Add(BuildFoldoutButton(
                        group.Key,
                        group.Count() + " files",
                        expanded,
                        () => ToggleFoldout(prefKey, true),
                        "unitgit-change-folder"));
                    if (!expanded)
                    {
                        continue;
                    }
                }

                foreach (UnitGitStatusEntry change in group)
                {
                    parent.Add(BuildChangeFileRow(change, !isRoot));
                }
            }
        }

        private VisualElement BuildChangeFileRow(UnitGitStatusEntry change, bool indented)
        {
            var row = new Button(() =>
            {
                SelectLocalChange(change);
            });
            row.userData = change.Path;
            row.AddToClassList("unitgit-change-file-row");
            if (indented)
            {
                row.AddToClassList("unitgit-change-file-row--indented");
            }

            if (string.Equals(selectedChangePath, change.Path, StringComparison.OrdinalIgnoreCase))
            {
                row.AddToClassList("unitgit-change-file-row--selected");
            }

            row.Add(BuildProjectFileIcon(change.Path, "unitgit-change-file-icon"));

            var name = new Label(GetFileLeaf(change.Path));
            name.AddToClassList("unitgit-change-file-name");
            row.Add(name);

            var directory = new Label(GetDirectoryLabel(change.Path));
            directory.AddToClassList("unitgit-change-file-directory");
            row.Add(directory);

            var status = new Label(change.DisplayStatus);
            status.AddToClassList("unitgit-change-file-status");
            row.Add(status);

            return row;
        }

        private void SelectLocalChange(UnitGitStatusEntry change)
        {
            if (change == null)
            {
                return;
            }

            selectedChangePath = change.Path;
            UpdateLocalChangeSelectionState();
            RebuildLocalDiffPane();
        }

        private void UpdateLocalChangeSelectionState()
        {
            if (localChangesListRoot == null)
            {
                return;
            }

            localChangesListRoot.Query<Button>(className: "unitgit-change-file-row").ForEach(row =>
            {
                string rowPath = row.userData as string;
                row.EnableInClassList(
                    "unitgit-change-file-row--selected",
                    string.Equals(rowPath, selectedChangePath, StringComparison.OrdinalIgnoreCase));
            });
        }

        private void RebuildLocalDiffPane()
        {
            if (localDiffPaneRoot == null)
            {
                RebuildContent();
                return;
            }

            localDiffPaneRoot.Clear();
            localDiffPaneRoot.Add(BuildDiffViewerPane());
        }
    }
}
