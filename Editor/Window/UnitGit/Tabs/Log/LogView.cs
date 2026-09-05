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
        private VisualElement BuildLogBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            workspace.Add(BuildLogToolRail());

            var branchSplit = BuildTrackedSplit(BranchLogSplitPref, 0, 448f);
            branchSplit.Add(BuildBranchesPane());

            var logDetailsSplit = BuildTrackedSplit(LogDetailsSplitPref, 1, 420f);
            logDetailsSplit.Add(BuildLogPane());
            commitDetailsRoot = new VisualElement();
            commitDetailsRoot.style.flexGrow = 1;
            commitDetailsRoot.style.minHeight = 0;
            commitDetailsRoot.Add(BuildDetailsPane());
            logDetailsSplit.Add(commitDetailsRoot);

            branchSplit.Add(logDetailsSplit);
            workspace.Add(branchSplit);

            return workspace;
        }

        private VisualElement BuildLogToolRail()
        {
            var rail = new VisualElement();
            rail.AddToClassList("unitgit-toolrail");
            rail.Add(BuildIconRailButton(UnitGitIconKind.CreateBranch, "Create new branch", PromptCreateBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Update, "Update selected branch", UpdateSelectedBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Delete, "Delete selected branch", DeleteSelectedBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Fetch, "Fetch", Fetch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.GitHub, "Remote setup", OpenRemoteSetup));
            return rail;
        }

        private Button BuildIconRailButton(UnitGitIconKind iconKind, string tooltip, Action action)
        {
            var button = new Button(() => action());
            button.tooltip = tooltip;
            button.AddToClassList("unitgit-rail-button");
            var icon = new UnitGitIconElement(iconKind);
            icon.AddToClassList("unitgit-rail-icon");
            button.Add(icon);

            return button;
        }

        private VisualElement BuildLogPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-log-pane");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("unitgit-log-toolbar");

            var search = new TextField();
            search.value = logSearchDraft;
            search.AddToClassList("unitgit-log-search");
            search.RegisterValueChangedCallback(evt =>
            {
                logSearchDraft = evt.newValue;
                QueueLogSearchRefresh();
            });
            toolbar.Add(search);

            int totalCommits = snapshot.Commits.Count;
            int pageCount = Math.Max(1, (totalCommits + CommitPageSize - 1) / CommitPageSize);
            logPage = Mathf.Clamp(logPage, 0, pageCount - 1);

            toolbar.Add(BuildToolbarChip("Branch: " + Shorten(snapshot.CurrentBranch, 28)));
            toolbar.Add(BuildToolbarChip(totalCommits + " commits"));
            if (pageCount > 1)
            {
                toolbar.Add(BuildToolbarChip("Page " + (logPage + 1) + "/" + pageCount));
                toolbar.Add(BuildActionButton("Prev", string.Empty, PreviousLogPage));
                toolbar.Add(BuildActionButton("Next", string.Empty, NextLogPage));
            }

            toolbar.Add(BuildActionButton("Refresh", string.Empty, RefreshSnapshot));
            toolbar.Add(BuildActionButton("Fetch", string.Empty, Fetch));
            toolbar.Add(BuildActionButton("Pull", string.Empty, PullFastForward));

            pane.Add(toolbar);

            var header = new VisualElement();
            header.AddToClassList("unitgit-log-header");
            header.Add(BuildHeaderLabel("Commit", "unitgit-log-header__commit"));
            header.Add(BuildHeaderLabel("Author", "unitgit-log-header__author"));
            header.Add(BuildHeaderLabel("Date", "unitgit-log-header__date"));
            header.Add(BuildHeaderLabel("Hash", "unitgit-log-header__hash"));
            pane.Add(header);

            var scroll = new ScrollView();
            scroll.name = "unitgit-log-scroll";
            scroll.AddToClassList("unitgit-log-scroll");
            if (snapshot.Commits.Count == 0)
            {
                scroll.Add(BuildEmptyState("No commits found."));
            }
            else
            {
                int index = 0;
                var visibleCommits = snapshot.Commits
                    .Skip(logPage * CommitPageSize)
                    .Take(CommitPageSize)
                    .ToList();
                foreach (UnitGitCommit commit in visibleCommits)
                {
                    UnitGitReleaseEntry release = GetReleaseForCommit(commit);
                    if (release != null)
                    {
                        scroll.Add(BuildReleaseCheckpointRow(commit, release));
                    }

                    scroll.Add(BuildCommitRow(commit, index, visibleCommits.Count));
                    index++;
                }
            }

            pane.Add(scroll);
            return pane;
        }

        private void PreviousLogPage()
        {
            if (logPage <= 0)
            {
                return;
            }

            logPage--;
            RebuildContent();
        }

        private void NextLogPage()
        {
            int totalCommits = snapshot != null ? snapshot.Commits.Count : 0;
            int pageCount = Math.Max(1, (totalCommits + CommitPageSize - 1) / CommitPageSize);
            if (logPage >= pageCount - 1)
            {
                return;
            }

            logPage++;
            RebuildContent();
        }


        private void SelectCommitFromRow(UnitGitCommit commit, bool selectRelease)
        {
            SelectCommitFromRow(commit, selectRelease, false, false);
        }

        private void SelectCommitFromRow(UnitGitCommit commit, bool selectRelease, bool rangeSelect, bool toggleSelect)
        {
            if (commit == null)
            {
                return;
            }

            if (selectRelease)
            {
                selectedCommitHashes.Clear();
                selectionAnchorHash = string.Empty;
                SetPrimarySelectedCommit(commit, commit.ReleaseId);
                RebuildContent();
                return;
            }

            selectedReleaseId = string.Empty;
            if (rangeSelect)
            {
                ApplyRangeCommitSelection(commit, toggleSelect);
            }
            else if (toggleSelect)
            {
                ApplyToggleCommitSelection(commit);
            }
            else
            {
                selectedCommitHashes.Clear();
                selectedCommitHashes.Add(commit.FullHash);
                selectionAnchorHash = commit.FullHash;
                SetPrimarySelectedCommit(commit, string.Empty);
            }

            RebuildContent();
        }

        private void SetPrimarySelectedCommit(UnitGitCommit commit, string releaseId)
        {
            selectedCommit = commit;
            selectedDetails = null;
            selectedReleaseId = string.IsNullOrWhiteSpace(releaseId) ? string.Empty : releaseId;
        }

        private void ApplyToggleCommitSelection(UnitGitCommit commit)
        {
            if (selectedCommitHashes.Contains(commit.FullHash))
            {
                selectedCommitHashes.Remove(commit.FullHash);
                UnitGitCommit nextCommit = GetSelectedCommitsInLogOrder().FirstOrDefault();
                SetPrimarySelectedCommit(nextCommit, string.Empty);
            }
            else
            {
                selectedCommitHashes.Add(commit.FullHash);
                selectionAnchorHash = commit.FullHash;
                SetPrimarySelectedCommit(commit, string.Empty);
            }
        }

        private void ApplyRangeCommitSelection(UnitGitCommit commit, bool addToExistingSelection)
        {
            string anchorHash = !string.IsNullOrWhiteSpace(selectionAnchorHash)
                ? selectionAnchorHash
                : (selectedCommit != null ? selectedCommit.FullHash : commit.FullHash);
            List<UnitGitCommit> range = GetCommitRange(anchorHash, commit.FullHash);
            if (!addToExistingSelection)
            {
                selectedCommitHashes.Clear();
            }

            foreach (UnitGitCommit rangeCommit in range)
            {
                selectedCommitHashes.Add(rangeCommit.FullHash);
            }

            if (string.IsNullOrWhiteSpace(selectionAnchorHash))
            {
                selectionAnchorHash = anchorHash;
            }

            SetPrimarySelectedCommit(commit, string.Empty);
        }

        private List<UnitGitCommit> GetCommitRange(string firstHash, string secondHash)
        {
            if (snapshot == null || snapshot.Commits == null || snapshot.Commits.Count == 0)
            {
                return new List<UnitGitCommit>();
            }

            int firstIndex = snapshot.Commits.FindIndex(commit => string.Equals(commit.FullHash, firstHash, StringComparison.Ordinal));
            int secondIndex = snapshot.Commits.FindIndex(commit => string.Equals(commit.FullHash, secondHash, StringComparison.Ordinal));
            if (firstIndex < 0 || secondIndex < 0)
            {
                return snapshot.Commits
                    .Where(commit => string.Equals(commit.FullHash, secondHash, StringComparison.Ordinal))
                    .ToList();
            }

            int start = Math.Min(firstIndex, secondIndex);
            int count = Math.Abs(firstIndex - secondIndex) + 1;
            return snapshot.Commits.Skip(start).Take(count).ToList();
        }

        private List<UnitGitCommit> GetSelectedCommitsInLogOrder()
        {
            if (snapshot == null || snapshot.Commits == null || selectedCommitHashes.Count == 0)
            {
                return new List<UnitGitCommit>();
            }

            return snapshot.Commits
                .Where(commit => selectedCommitHashes.Contains(commit.FullHash))
                .ToList();
        }

        private void HandleCommitRowMouseDown(MouseDownEvent evt, UnitGitCommit commit)
        {
            if (evt.button != 0 && evt.button != 1)
            {
                return;
            }

            evt.StopPropagation();
            bool isContextClick = evt.button == 1;
            bool selectedByContext = isContextClick && selectedCommitHashes.Contains(commit.FullHash);
            if (selectedByContext)
            {
                SetPrimarySelectedCommit(commit, string.Empty);
                RebuildContent();
            }
            else
            {
                SelectCommitFromRow(commit, false, evt.shiftKey, evt.ctrlKey || evt.commandKey);
            }

            if (isContextClick)
            {
                ShowCommitContextMenu(commit);
            }
        }

        private void HandleReleaseRowMouseDown(MouseDownEvent evt, UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            if (evt.button != 0 && evt.button != 1)
            {
                return;
            }

            evt.StopPropagation();
            SelectCommitFromRow(commit, true);
            if (evt.button == 1)
            {
                ShowReleaseContextMenu(commit, release);
            }
        }

        private static VisualElement BuildSelectableRow(Action<MouseDownEvent> handleMouseDown, Action select)
        {
            var row = new VisualElement
            {
                focusable = true,
                pickingMode = PickingMode.Position
            };

            RegisterRowSelection(row, handleMouseDown, select);
            return row;
        }

        // Selection listens to mouse-down so row feedback happens before any expensive details work.
        private static void RegisterRowSelection(VisualElement row, Action<MouseDownEvent> handleMouseDown, Action select)
        {
            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0 || evt.button == 1)
                {
                    row.Focus();
                    handleMouseDown(evt);
                }
            });

            row.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space)
                {
                    evt.StopPropagation();
                    select();
                }
            });
        }

        private VisualElement BuildCommitRow(UnitGitCommit commit, int index, int totalCommits)
        {
            VisualElement row = BuildSelectableRow(evt => HandleCommitRowMouseDown(evt, commit), () => SelectCommitFromRow(commit, false));
            row.AddToClassList("unitgit-log-row");
            bool isSelected = selectedCommitHashes.Contains(commit.FullHash) && string.IsNullOrEmpty(selectedReleaseId);
            if (isSelected)
            {
                row.AddToClassList("unitgit-log-row--selected");
            }

            var graphColor = GetGraphColor(commit);
            var graph = BuildCommitGraphCell(graphColor, index == 0, index == totalCommits - 1, isSelected);
            row.Add(graph);

            var subjectWrap = new VisualElement();
            subjectWrap.AddToClassList("unitgit-log-subject-wrap");

            var subject = new Label(commit.Subject);
            subject.AddToClassList("unitgit-log-subject");
            subjectWrap.Add(subject);

            if (!string.IsNullOrWhiteSpace(commit.Decorations))
            {
                var decorations = new Label(Shorten(commit.Decorations, 36));
                decorations.AddToClassList("unitgit-log-decoration");
                subjectWrap.Add(decorations);
            }

            row.Add(subjectWrap);

            var author = new Label(commit.AuthorName);
            author.AddToClassList("unitgit-log-author");
            row.Add(author);

            var date = new Label(commit.RelativeDate);
            date.AddToClassList("unitgit-log-date");
            row.Add(date);

            var hash = new Label(commit.ShortHash);
            hash.AddToClassList("unitgit-log-hash");
            row.Add(hash);

            return row;
        }

        private VisualElement BuildCommitGraphCell(Color graphColor, bool isNewestVisibleCommit, bool isOldestVisibleCommit, bool isSelectedCommit = false)
        {
            var graph = new VisualElement();
            graph.AddToClassList("unitgit-graph-cell");

            if (!isNewestVisibleCommit)
            {
                var topLine = new VisualElement();
                topLine.AddToClassList("unitgit-graph-line");
                topLine.AddToClassList("unitgit-graph-line--top");
                topLine.style.backgroundColor = graphColor;
                graph.Add(topLine);
            }

            if (!isOldestVisibleCommit)
            {
                var bottomLine = new VisualElement();
                bottomLine.AddToClassList("unitgit-graph-line");
                bottomLine.AddToClassList("unitgit-graph-line--bottom");
                bottomLine.style.backgroundColor = graphColor;
                graph.Add(bottomLine);
            }

            // The ring marks the currently selected commit, colored like its branch.
            if (isSelectedCommit)
            {
                var ring = new VisualElement();
                ring.AddToClassList("unitgit-graph-ring");
                SetBorderColor(ring, graphColor);
                graph.Add(ring);
            }

            var dot = new VisualElement();
            dot.AddToClassList("unitgit-graph-dot");
            dot.style.backgroundColor = graphColor;
            graph.Add(dot);

            return graph;
        }
    }
}
