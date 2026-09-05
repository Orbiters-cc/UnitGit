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
        private void OnEditorFocusChanged(bool focused)
        {
            if (focused)
            {
                QueueRefreshFromEditorEvent();
            }
        }

        private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            QueueRefreshFromEditorEvent();
        }

        private void QueueRefreshFromEditorEvent()
        {
            double now = EditorApplication.timeSinceStartup;
            double nextAllowedRefresh = lastEditorEventRefreshTime + EditorEventRefreshCooldownSeconds;
            queuedRefreshTime = Math.Max(now + EditorEventRefreshDelaySeconds, nextAllowedRefresh);
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.update += RunQueuedRefresh;
        }

        private void RunQueuedRefresh()
        {
            if (this == null)
            {
                EditorApplication.update -= RunQueuedRefresh;
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < queuedRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RunQueuedRefresh;
            refreshQueued = false;
            if (busy)
            {
                refreshAgainRequested = true;
                return;
            }

            lastEditorEventRefreshTime = EditorApplication.timeSinceStartup;
            RefreshSnapshot();
        }

        private void QueueLogSearchRefresh()
        {
            logPage = 0;
            queuedLogSearchRefreshTime = EditorApplication.timeSinceStartup + LogSearchRefreshDelaySeconds;
            if (logSearchRefreshQueued)
            {
                return;
            }

            logSearchRefreshQueued = true;
            EditorApplication.update += RunQueuedLogSearchRefresh;
        }

        private void RunQueuedLogSearchRefresh()
        {
            if (this == null)
            {
                EditorApplication.update -= RunQueuedLogSearchRefresh;
                return;
            }

            if (EditorApplication.timeSinceStartup < queuedLogSearchRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RunQueuedLogSearchRefresh;
            logSearchRefreshQueued = false;
            if (string.Equals(logSearch, logSearchDraft, StringComparison.Ordinal))
            {
                return;
            }

            logSearch = logSearchDraft;
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            if (gitService == null)
            {
                return;
            }

            RememberScrollOffsets();
            if (busy)
            {
                refreshAgainRequested = true;
                return;
            }
            if (refreshingSnapshot)
            {
                if (!editorUpdatePumpActive && pendingMainThreadActions.IsEmpty)
                {
                    refreshingSnapshot = false;
                    AppendConsole("refresh", "recovered stale refresh state");
                }
                else
                {
                    refreshAgainRequested = true;
                    return;
                }
            }

            if (refreshingSnapshot)
            {
                refreshAgainRequested = true;
                return;
            }

            refreshingSnapshot = true;
            int requestId = ++refreshRequestId;
            string projectRoot = gitService.ProjectRoot;
            string search = logSearch;
            UnitGitSnapshot previousSnapshot = snapshot;
            try
            {
                EnsureEditorUpdatePump();
                Task.Run(() => BuildSnapshotRefreshResult(
                        projectRoot,
                        search,
                        previousSnapshot,
                        line =>
                        {
                            if (ShouldLogRefreshProcessLine(line))
                            {
                                QueueConsoleLine("refresh", line);
                            }
                        }))
                    .ContinueWith(task => QueueMainThreadAction(() => ApplySnapshotRefresh(requestId, task)));
            }
            catch (Exception ex)
            {
                refreshingSnapshot = false;
                AppendConsole("refresh", "error: " + ex.Message);
                RebuildContent();
            }
        }

        private static SnapshotRefreshResult BuildSnapshotRefreshResult(
            string projectRoot,
            string search,
            UnitGitSnapshot previousSnapshot,
            UnitGitProcessLogHandler logHandler)
        {
            var service = new UnitGitService(projectRoot);
            service.ProcessLogReceived = logHandler;
            UnitGitSnapshot nextSnapshot = service.BuildSnapshot(search);
            var result = new SnapshotRefreshResult
            {
                Snapshot = nextSnapshot,
                ChangesUnchanged = previousSnapshot != null &&
                    previousSnapshot.Changes.Count == nextSnapshot.Changes.Count &&
                    previousSnapshot.Changes.Zip(nextSnapshot.Changes, (a, b) =>
                        a.Path == b.Path && a.OriginalPath == b.OriginalPath &&
                        a.IndexStatus == b.IndexStatus && a.WorkTreeStatus == b.WorkTreeStatus).All(equal => equal)
            };

            return result;
        }

        private void ApplySnapshotRefresh(int requestId, Task<SnapshotRefreshResult> task)
        {
            if (this == null)
            {
                return;
            }

            if (requestId != refreshRequestId)
            {
                return;
            }

            refreshingSnapshot = false;
            if (task.Status != TaskStatus.RanToCompletion)
            {
                Exception error = task.Exception != null ? task.Exception.GetBaseException() : null;
                AppendConsole("refresh", "error: " + (error != null ? error.Message : "Failed to refresh Git state."));
            }
            else if (task.Result != null && task.Result.Snapshot != null)
            {
                snapshot = task.Result.Snapshot;
                ReconcileSnapshotSelection();
            }

            bool preserveLocalView = activeTab == UnitGitTab.LocalChanges && localChangesListRoot != null &&
                task.Status == TaskStatus.RanToCompletion && snapshot.HasRepository && snapshot.HasCommits &&
                string.IsNullOrWhiteSpace(snapshot.LastError);
            if (preserveLocalView)
            {
                if (!task.Result.ChangesUnchanged)
                {
                    localChangesListRoot.Clear();
                    if (snapshot.Changes.Count == 0)
                        localChangesListRoot.Add(BuildEmptyState("Working tree is clean."));
                    else if (GetFoldoutExpanded(ChangesFoldPref, true))
                        AddChangedFileGroups(localChangesListRoot);
                    var count = contentRoot.Q<Label>(className: "unitgit-foldout-detail");
                    if (count != null)
                        count.text = snapshot.Changes.Count + " files";
                }
                var change = GetSelectedChange();
                bool changedPath = requestedDiffPath != (change != null ? change.Path : string.Empty);
                RequestLocalDiff(change);
                if (changedPath)
                    RebuildLocalDiffPane();
                RefreshTopBar();
            }
            else
            {
                requestedDiffPath = null;
                BuildShell();
                RebuildContent();
            }
            RestoreScrollOffsets();

            if (refreshAgainRequested)
            {
                refreshAgainRequested = false;
                RefreshSnapshot();
            }
        }

        private void ReconcileSnapshotSelection()
        {
            if (snapshot.Branches.Count > 0 &&
                (selectedBranch == null || snapshot.Branches.All(branch => branch.FullRef != selectedBranch.FullRef)))
            {
                selectedBranch = snapshot.Branches.FirstOrDefault(branch => branch.IsCurrent) ?? snapshot.Branches[0];
            }

            if (snapshot.Changes.Count > 0 &&
                (string.IsNullOrWhiteSpace(selectedChangePath) ||
                 snapshot.Changes.All(change => !string.Equals(change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase))))
            {
                selectedChangePath = snapshot.Changes[0].Path;
            }
            else if (snapshot.Changes.Count == 0)
            {
                selectedChangePath = string.Empty;
            }

            if (snapshot.HasRepository && snapshot.Commits.Count > 0)
            {
                var availableHashes = new HashSet<string>(snapshot.Commits.Select(commit => commit.FullHash), StringComparer.Ordinal);
                selectedCommitHashes.RemoveWhere(hash => !availableHashes.Contains(hash));
                if (!string.IsNullOrWhiteSpace(selectionAnchorHash) &&
                    snapshot.Commits.All(commit => commit.FullHash != selectionAnchorHash))
                {
                    selectionAnchorHash = string.Empty;
                }

                if (selectedCommit == null || snapshot.Commits.All(commit => commit.FullHash != selectedCommit.FullHash))
                {
                    selectedCommit = selectedCommitHashes.Count > 0
                        ? snapshot.Commits.FirstOrDefault(commit => selectedCommitHashes.Contains(commit.FullHash)) ?? snapshot.Commits[0]
                        : snapshot.Commits[0];
                    selectedReleaseId = string.Empty;
                }

                if (selectedCommitHashes.Count == 0 && selectedCommit != null && string.IsNullOrEmpty(selectedReleaseId))
                {
                    selectedCommitHashes.Add(selectedCommit.FullHash);
                    selectionAnchorHash = selectedCommit.FullHash;
                }

                if (selectedDetails == null ||
                         selectedDetails.Commit == null ||
                         selectedCommit == null ||
                         selectedDetails.Commit.FullHash != selectedCommit.FullHash)
                {
                    selectedDetails = null;
                }
            }
            else
            {
                selectedCommit = null;
                selectedCommitHashes.Clear();
                selectionAnchorHash = string.Empty;
                selectedReleaseId = string.Empty;
                selectedDetails = null;
            }
        }

        private sealed class SnapshotRefreshResult
        {
            public UnitGitSnapshot Snapshot;
            public bool ChangesUnchanged;
        }
    }
}
