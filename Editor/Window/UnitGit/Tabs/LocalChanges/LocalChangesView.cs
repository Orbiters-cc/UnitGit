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

        private VisualElement BuildConflictsNotice()
        {
            int count = Conflicted().Count;
            var notice = new VisualElement();
            notice.AddToClassList("unitgit-notice");
            notice.AddToClassList("unitgit-notice--conflict");
            var texts = new VisualElement();
            texts.AddToClassList("unitgit-notice__texts");
            notice.Add(texts);
            var title = new Label(count + " file" + (count == 1 ? " has" : "s have") + " conflicts");
            title.AddToClassList("unitgit-notice__title");
            texts.Add(title);
            var body = new Label("Both sides changed the same parts. Resolve them before committing.");
            body.AddToClassList("unitgit-notice__body");
            texts.Add(body);
            var button = BuildActionButton("Resolve", "unitgit-button--primary", () => SetActiveTab(UnitGitTab.Conflicts));
            button.AddToClassList("unitgit-notice__button");
            notice.Add(button);
            return notice;
        }

        // MCB version downloads committed before Unit Git ignored them: Git keeps tracking them until told otherwise.
        private VisualElement BuildTrackedDownloadsNotice()
        {
            var notice = new VisualElement();
            notice.AddToClassList("unitgit-notice");
            var texts = new VisualElement();
            texts.AddToClassList("unitgit-notice__texts");
            notice.Add(texts);
            var title = new Label("MCB downloads are in Git (" + UnitGitService.FormatBytes(snapshot.TrackedIgnoredDownloadBytes) + ")");
            title.AddToClassList("unitgit-notice__title");
            texts.Add(title);
            var body = new Label(snapshot.TrackedIgnoredDownloads.Count + " version files MCB downloads again when needed. Stop tracking them to keep your repository small: they stay on disk.");
            body.AddToClassList("unitgit-notice__body");
            texts.Add(body);
            var paths = new List<string>(snapshot.TrackedIgnoredDownloads);
            var button = BuildActionButton("Stop tracking", "unitgit-button--primary", () =>
                RunAction("Stop tracking MCB downloads", () => gitService.StopTracking(paths)));
            button.tooltip = "Runs git rm --cached on these files: they stay on disk, and their removal is staged for your next commit. Commits already made keep their copies.";
            button.AddToClassList("unitgit-notice__button");
            notice.Add(button);
            return notice;
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

            if (Conflicted().Count > 0)
            {
                pane.Add(BuildConflictsNotice());
            }

            if (snapshot.TrackedIgnoredDownloads.Count > 0)
            {
                pane.Add(BuildTrackedDownloadsNotice());
            }

            var list = new VisualElement();
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
            var submit = BuildActionButton(commitAmend ? "Amend Commit" : "Commit Staged", "unitgit-button--primary", CommitStaged);
            submit.name = "unitgit-commit-submit";
            commitPanel.Add(submit);
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
                if (snapshot == null || !snapshot.HasCommits)
                {
                    EditorUtility.DisplayDialog("Amend Commit", "No previous commit is available.", "OK");
                    commitAmend = false;
                    RebuildContent();
                    return;
                }

                commitMessage = snapshot.HeadMessage;
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
            var rows = snapshot.ChangeList.GetVisibleRows(row =>
                GetFoldoutExpanded(GetFoldoutPrefKey("ChangesFolder", row.Path), true));
            parent.Add(BuildVirtualList(rows, 28f, row =>
            {
                if (!row.IsFolder)
                    return BuildChangeFileRow(row.Change, row.Depth > 0);
                string prefKey = GetFoldoutPrefKey("ChangesFolder", row.Path);
                return BuildFoldoutButton(row.Name, row.FileCount + " files",
                    GetFoldoutExpanded(prefKey, true), () => ToggleFoldout(prefKey, true), "unitgit-change-folder");
            }, "unitgit-local-changes-list"));
        }

        private VisualElement BuildChangeFileRow(UnitGitStatusEntry change, bool indented)
        {
            var row = new Button(() =>
            {
                SelectLocalChange(change);
            });
            row.userData = change.Path;
            row.tooltip = change.Path;
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0)
                    SelectLocalChange(change);
            });
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

            if (string.Equals(selectedChangePath, change.Path, StringComparison.Ordinal))
                return;

            selectedChangePath = change.Path;
            diffSearchMatchIndex = 0;
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
