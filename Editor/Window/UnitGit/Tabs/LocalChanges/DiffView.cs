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
        private VisualElement BuildDiffViewerPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-diff-pane");

            UnitGitStatusEntry selectedChange = GetSelectedChange();
            UnitGitDiff diff = gitService.GetFileDiff(selectedChange);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("unitgit-diff-toolbar");
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.PreviousDifference, "Previous search match", PreviousDiffSearchMatch));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.NextDifference, "Next search match", NextDiffSearchMatch));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.Search, "Focus diff search", FocusDiffSearch));
            diffSearchField = new TextField();
            diffSearchField.value = diffSearch;
            diffSearchField.tooltip = "Search in diff";
            diffSearchField.AddToClassList("unitgit-diff-search");
            diffSearchField.RegisterValueChangedCallback(evt =>
            {
                diffSearch = evt.newValue;
                diffSearchMatchIndex = 0;
                RebuildLocalDiffPane();
            });
            toolbar.Add(diffSearchField);
            var spacer = new VisualElement();
            spacer.AddToClassList("unitgit-spacer");
            toolbar.Add(spacer);
            toolbar.Add(BuildToolbarChip(diff.DifferenceCount + " differences"));
            pane.Add(toolbar);

            var pathRow = new VisualElement();
            pathRow.AddToClassList("unitgit-diff-path-row");
            pathRow.Add(new Label(selectedChange != null ? selectedChange.Path : "No file selected"));
            pane.Add(pathRow);

            var header = new VisualElement();
            header.AddToClassList("unitgit-diff-header");
            var leftTitle = new Label(diff.LeftTitle);
            leftTitle.AddToClassList("unitgit-diff-header-cell");
            header.Add(leftTitle);
            var rightTitle = new Label(diff.RightTitle);
            rightTitle.AddToClassList("unitgit-diff-header-cell");
            header.Add(rightTitle);
            pane.Add(header);

            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroll.name = "unitgit-diff-scroll";
            scroll.AddToClassList("unitgit-diff-scroll");
            int matchCount = diff.Lines.Count(MatchesDiffSearch);
            if (matchCount == 0)
            {
                diffSearchMatchIndex = 0;
            }
            else if (diffSearchMatchIndex >= matchCount)
            {
                diffSearchMatchIndex = matchCount - 1;
            }

            int matchIndex = 0;
            foreach (UnitGitDiffLine line in diff.Lines)
            {
                bool isMatch = MatchesDiffSearch(line);
                bool isCurrentMatch = isMatch && matchIndex == diffSearchMatchIndex;
                VisualElement row = BuildDiffLine(line, isMatch, isCurrentMatch);
                scroll.Add(row);
                if (isCurrentMatch)
                {
                    scroll.schedule.Execute(() => scroll.ScrollTo(row));
                }

                if (isMatch)
                {
                    matchIndex++;
                }
            }

            pane.Add(scroll);
            return pane;
        }

        private Button BuildDiffToolButton(UnitGitIconKind iconKind, string tooltip, Action action)
        {
            var button = new Button(() => action?.Invoke());
            button.tooltip = tooltip;
            button.AddToClassList("unitgit-diff-tool-button");
            var icon = new UnitGitIconElement(iconKind);
            icon.AddToClassList("unitgit-diff-tool-icon");
            button.Add(icon);
            return button;
        }

        private VisualElement BuildDiffLine(UnitGitDiffLine line, bool isSearchMatch, bool isCurrentSearchMatch)
        {
            var row = new VisualElement();
            row.AddToClassList("unitgit-diff-line");
            row.AddToClassList("unitgit-diff-line--" + line.Kind.ToString().ToLowerInvariant());
            if (isSearchMatch)
            {
                row.AddToClassList("unitgit-diff-line--search-match");
            }

            if (isCurrentSearchMatch)
            {
                row.AddToClassList("unitgit-diff-line--search-current");
            }

            var left = new Label(line.Left);
            left.AddToClassList("unitgit-diff-cell");
            row.Add(left);

            var right = new Label(line.Right);
            right.AddToClassList("unitgit-diff-cell");
            row.Add(right);

            return row;
        }

        private void PreviousDiffSearchMatch()
        {
            int matchCount = GetCurrentDiffSearchMatchCount();
            if (matchCount == 0)
            {
                FocusDiffSearch();
                return;
            }

            diffSearchMatchIndex = (diffSearchMatchIndex + matchCount - 1) % matchCount;
            RebuildLocalDiffPane();
        }

        private void NextDiffSearchMatch()
        {
            int matchCount = GetCurrentDiffSearchMatchCount();
            if (matchCount == 0)
            {
                FocusDiffSearch();
                return;
            }

            diffSearchMatchIndex = (diffSearchMatchIndex + 1) % matchCount;
            RebuildLocalDiffPane();
        }

        private void FocusDiffSearch()
        {
            diffSearchField?.Focus();
            if (!string.IsNullOrWhiteSpace(diffSearch))
            {
                diffSearchMatchIndex = 0;
                RebuildLocalDiffPane();
            }
        }

        private int GetCurrentDiffSearchMatchCount()
        {
            UnitGitStatusEntry selectedChange = GetSelectedChange();
            UnitGitDiff diff = gitService.GetFileDiff(selectedChange);
            return diff.Lines.Count(MatchesDiffSearch);
        }

        private bool MatchesDiffSearch(UnitGitDiffLine line)
        {
            if (line == null || string.IsNullOrWhiteSpace(diffSearch))
            {
                return false;
            }

            return (line.Left != null && line.Left.IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (line.Right != null && line.Right.IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
