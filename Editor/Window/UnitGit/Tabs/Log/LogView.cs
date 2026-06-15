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
            logDetailsSplit.Add(BuildDetailsPane());

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

                    scroll.Add(BuildCommitRow(commit, index, visibleCommits.Count, release != null));
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
            selectedCommit = commit;
            selectedDetails = gitService.GetCommitDetails(commit.FullHash);
            selectedReleaseId = selectRelease ? commit.ReleaseId : string.Empty;
            RebuildContent();
        }

        // Selection listens to pointer-down (instead of the button's click-on-release) so the
        // list feels snappier.
        private static void RegisterRowSelection(VisualElement row, Action select)
        {
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 0)
                {
                    evt.StopPropagation();
                    select();
                }
            });
        }

        private VisualElement BuildCommitRow(UnitGitCommit commit, int index, int totalCommits, bool isReleaseCommit)
        {
            var row = new Button();
            RegisterRowSelection(row, () => SelectCommitFromRow(commit, false));
            row.AddToClassList("unitgit-log-row");
            bool isSelected = selectedCommit != null && selectedCommit.FullHash == commit.FullHash;
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
