using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private readonly UnitGitLatestRequest<UnitGitDiff> diffRead = new UnitGitLatestRequest<UnitGitDiff>();
        private UnitGitDiff loadedDiff;
        private string requestedDiffPath;
        private ListView diffList;
        private readonly List<int> diffMatches = new List<int>();
        private readonly HashSet<int> diffMatchRows = new HashSet<int>();
        private string indexedDiffSearch;
        private UnitGitDiff indexedDiff;
        private Label diffCountLabel;
        private Label diffLeftTitle;
        private Label diffRightTitle;
        private Font diffFont;
        private float diffCharacterWidth;
        private float diffColumnWidth;

        private VisualElement BuildDiffViewerPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-diff-pane");
            UnitGitStatusEntry change = GetSelectedChange();
            string path = change != null ? change.Path : string.Empty;
            if (!string.Equals(requestedDiffPath, path, StringComparison.Ordinal))
            {
                loadedDiff = null;
                RequestLocalDiff(change);
            }
            var toolbar = new VisualElement();
            toolbar.AddToClassList("unitgit-diff-toolbar");
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.PreviousDifference, "Previous search match", PreviousDiffSearchMatch));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.NextDifference, "Next search match", NextDiffSearchMatch));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.Search, "Focus diff search", () => diffSearchField?.Focus()));
            diffSearchField = new TextField { value = diffSearch, tooltip = "Search in diff" };
            diffSearchField.AddToClassList("unitgit-diff-search");
            var searchField = diffSearchField;
            IVisualElementScheduledItem searchJob = null;
            diffSearchField.RegisterValueChangedCallback(evt =>
            {
                diffSearch = evt.newValue;
                diffSearchMatchIndex = 0;
                searchJob?.Pause();
                searchJob = searchField.schedule.Execute(() => UpdateDiffSearch(true)).StartingIn(150);
            });
            toolbar.Add(diffSearchField);
            var spacer = new VisualElement();
            spacer.AddToClassList("unitgit-spacer");
            toolbar.Add(spacer);
            diffCountLabel = BuildToolbarChip(loadedDiff == null ? "Loading..." : loadedDiff.DifferenceCount + " differences");
            toolbar.Add(diffCountLabel);
            pane.Add(toolbar);
            var pathRow = new VisualElement();
            pathRow.AddToClassList("unitgit-diff-path-row");
            pathRow.Add(new Label(change != null ? path : "No file selected"));
            pane.Add(pathRow);
            diffList = null;
            diffLeftTitle = null;
            diffRightTitle = null;
            if (loadedDiff == null)
            {
                pane.Add(BuildEmptyState("Loading diff..."));
                return pane;
            }
            var header = new VisualElement();
            header.AddToClassList("unitgit-diff-header");
            diffLeftTitle = new Label(loadedDiff.LeftTitle);
            diffRightTitle = new Label(loadedDiff.RightTitle);
            foreach (var label in new[] { diffLeftTitle, diffRightTitle })
            {
                label.AddToClassList("unitgit-diff-header-cell");
                header.Add(label);
            }
            pane.Add(header);
            UpdateDiffSearch(false);
            if (diffFont == null)
            {
                diffFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono" }, 12);
                diffCharacterWidth = new GUIStyle { font = diffFont, fontSize = 12 }.CalcSize(new GUIContent("M")).x;
                diffCharacterWidth = Mathf.Max(1, diffCharacterWidth);
            }
            diffColumnWidth = Mathf.Max(420, loadedDiff.MaxLineLength * diffCharacterWidth + 24);
            // Recycle the labels too: layout and rendering are bounded by the viewport.
            diffList = new ListView
            {
                itemsSource = loadedDiff.Lines,
                fixedItemHeight = 20,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                horizontalScrollingEnabled = true,
                makeItem = () =>
                {
                    var row = new VisualElement();
                    row.AddToClassList("unitgit-diff-line");
                    for (int i = 0; i < 2; i++)
                    {
                        var cell = new VisualElement();
                        cell.AddToClassList("unitgit-diff-cell");
                        var label = new Label();
                        label.style.position = Position.Absolute;
                        label.style.unityFont = diffFont;
                        label.style.fontSize = 12;
                        label.style.whiteSpace = WhiteSpace.NoWrap;
                        cell.Add(label);
                        row.Add(cell);
                    }
                    row.AddManipulator(new ContextualMenuManipulator(evt =>
                    {
                        var line = row.userData as UnitGitDiffLine;
                        if (line == null)
                            return;
                        evt.menu.AppendAction("Copy left line", _ => EditorGUIUtility.systemCopyBuffer = line.Left);
                        evt.menu.AppendAction("Copy right line", _ => EditorGUIUtility.systemCopyBuffer = line.Right);
                    }));
                    return row;
                },
                bindItem = (row, index) =>
                {
                    var line = loadedDiff.Lines[index];
                    row.userData = line;
                    row.style.minWidth = diffColumnWidth * 2;
                    row.ClearClassList();
                    row.AddToClassList("unitgit-diff-line");
                    row.AddToClassList("unitgit-diff-line--" + line.Kind.ToString().ToLowerInvariant());
                    row.EnableInClassList("unitgit-diff-line--search-match", diffMatchRows.Contains(index));
                    row.EnableInClassList("unitgit-diff-line--search-current", diffMatches.Count > 0 && diffMatches[diffSearchMatchIndex] == index);
                    BindDiffCell(row[0], line.Left, 0);
                    BindDiffCell(row[1], line.Right, diffColumnWidth);
                }
            };
            diffList.style.flexGrow = 1;
            diffList.style.flexBasis = 0;
            diffList.style.minHeight = 0;
            var diffScroll = diffList.Q<ScrollView>();
            diffScroll.name = "unitgit-diff-scroll";
            diffScroll.horizontalScroller.valueChanged += _ => diffList?.RefreshItems();
            diffScroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => diffList?.RefreshItems());
            pane.Add(diffList);
            return pane;
        }

        private void RequestLocalDiff(UnitGitStatusEntry change)
        {
            string path = change != null ? change.Path : string.Empty;
            bool changedPath = requestedDiffPath != path;
            requestedDiffPath = path;
            if (changedPath)
                loadedDiff = null;
            var previous = loadedDiff;
            string root = gitService.ProjectRoot;
            diffRead.Request(stale =>
            {
                var next = new UnitGitService(root) { ReadSuperseded = stale }.GetFileDiff(change);
                foreach (var line in next.Lines)
                {
                    line.Left = (line.Left ?? string.Empty).Replace("\t", "    ");
                    line.Right = (line.Right ?? string.Empty).Replace("\t", "    ");
                    next.MaxLineLength = Math.Max(next.MaxLineLength, Math.Max(line.Left.Length, line.Right.Length));
                }
                if (previous != null && SameDiff(previous, next))
                    return previous;
                return next;
            });
            EnsureEditorUpdatePump();
        }

        internal static bool SameDiff(UnitGitDiff a, UnitGitDiff b)
        {
            if (a.Path != b.Path || a.LeftTitle != b.LeftTitle || a.RightTitle != b.RightTitle ||
                a.DifferenceCount != b.DifferenceCount || a.Lines.Count != b.Lines.Count)
                return false;
            for (int i = 0; i < a.Lines.Count; i++)
                if (a.Lines[i].Left != b.Lines[i].Left || a.Lines[i].Right != b.Lines[i].Right || a.Lines[i].Kind != b.Lines[i].Kind)
                    return false;
            return true;
        }

        private void PollDiffRead()
        {
            if (!diffRead.Poll(out UnitGitDiff diff, out Exception error))
                return;
            if (ReferenceEquals(loadedDiff, diff) && error == null)
                return;
            loadedDiff = diff ?? new UnitGitDiff();
            if (error != null)
                loadedDiff.Lines.Add(new UnitGitDiffLine { Right = "Could not load diff: " + error.Message });
            indexedDiff = null;
            if (diffLeftTitle != null)
                diffLeftTitle.text = loadedDiff.LeftTitle;
            if (diffRightTitle != null)
                diffRightTitle.text = loadedDiff.RightTitle;
            if (activeTab == UnitGitTab.LocalChanges)
            {
                if (diffList != null && diffList.panel != null)
                {
                    diffColumnWidth = Mathf.Max(420, loadedDiff.MaxLineLength * diffCharacterWidth + 24);
                    diffList.itemsSource = loadedDiff.Lines;
                    diffCountLabel.text = loadedDiff.DifferenceCount + " differences";
                    UpdateDiffSearch(false);
                    return;
                }
                RememberScrollOffsets();
                RebuildLocalDiffPane();
                RestoreScrollOffsets();
            }
        }

        private void UpdateDiffSearch(bool scrollToMatch)
        {
            if (indexedDiff != loadedDiff || indexedDiffSearch != diffSearch)
            {
                diffMatches.Clear();
                diffMatchRows.Clear();
                if (loadedDiff != null && !string.IsNullOrWhiteSpace(diffSearch))
                {
                    for (int i = 0; i < loadedDiff.Lines.Count; i++)
                    {
                        var line = loadedDiff.Lines[i];
                        if ((line.Left ?? string.Empty).IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                            (line.Right ?? string.Empty).IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        diffMatches.Add(i);
                        diffMatchRows.Add(i);
                    }
                }
                indexedDiff = loadedDiff;
                indexedDiffSearch = diffSearch;
            }
            diffSearchMatchIndex = diffMatches.Count == 0 ? 0 : Math.Min(diffSearchMatchIndex, diffMatches.Count - 1);
            diffList?.RefreshItems();
            if (scrollToMatch && diffMatches.Count > 0)
            {
                diffList?.ScrollToItem(diffMatches[diffSearchMatchIndex]);
                var line = loadedDiff.Lines[diffMatches[diffSearchMatchIndex]];
                int column = line.Left.IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase);
                float offset = 0;
                if (column < 0)
                {
                    column = line.Right.IndexOf(diffSearch, StringComparison.OrdinalIgnoreCase);
                    offset = diffColumnWidth;
                }
                var scroll = diffList?.Q<ScrollView>();
                if (scroll != null)
                    scroll.scrollOffset = new Vector2(offset + Mathf.Max(0, column - 8) * diffCharacterWidth, scroll.scrollOffset.y);
            }
        }

        private void BindDiffCell(VisualElement cell, string text, float columnOffset)
        {
            var scroll = diffList?.Q<ScrollView>();
            float x = scroll != null ? scroll.scrollOffset.x : 0;
            float width = scroll != null ? scroll.contentViewport.layout.width : 1000;
            if (float.IsNaN(width) || width <= 0)
                width = 1000;
            int start = Mathf.Clamp(Mathf.FloorToInt((x - columnOffset - 12) / diffCharacterWidth), 0, text.Length);
            int count = Math.Min(text.Length - start, Mathf.CeilToInt(width / diffCharacterWidth) + 2);
            var label = (Label)cell[0];
            // Horizontally virtualize long YAML/JSON lines too; Unity cannot mesh unlimited text.
            label.text = columnOffset > x + width ? string.Empty : text.Substring(start, count);
            label.style.left = 12 + start * diffCharacterWidth;
            cell.style.width = diffColumnWidth;
            cell.style.minWidth = diffColumnWidth;
            cell.style.flexGrow = 0;
            cell.style.flexShrink = 0;
        }

        private Button BuildDiffToolButton(UnitGitIconKind kind, string tooltip, Action action)
        {
            var button = new Button(action) { tooltip = tooltip };
            button.AddToClassList("unitgit-diff-tool-button");
            var icon = new UnitGitIconElement(kind);
            icon.AddToClassList("unitgit-diff-tool-icon");
            button.Add(icon);
            return button;
        }

        private void PreviousDiffSearchMatch() { MoveDiffSearchMatch(-1); }
        private void NextDiffSearchMatch() { MoveDiffSearchMatch(1); }
        private void MoveDiffSearchMatch(int step)
        {
            UpdateDiffSearch(false);
            if (diffMatches.Count == 0)
            {
                diffSearchField?.Focus();
                return;
            }
            diffSearchMatchIndex = (diffSearchMatchIndex + diffMatches.Count + step) % diffMatches.Count;
            UpdateDiffSearch(true);
        }
    }
}
