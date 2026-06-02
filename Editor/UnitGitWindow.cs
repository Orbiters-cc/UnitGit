using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.unitgit/Editor/Styles/unitgit.uss";
        private const int MaxConsoleLines = 80;
        private const string SplitPrefPrefix = "Orbiters.UnitGit.Split.";
        private const string BranchLogSplitPref = SplitPrefPrefix + "BranchLog";
        private const string LogDetailsSplitPref = SplitPrefPrefix + "LogDetails";
        private const string LocalChangesDiffSplitPref = SplitPrefPrefix + "LocalChangesDiff";
        private const string ShelfConsoleSplitPref = SplitPrefPrefix + "ShelfConsole";
        private const string FoldPrefPrefix = "Orbiters.UnitGit.Fold.";
        private const string BranchLocalFoldPref = FoldPrefPrefix + "Branch.Local";
        private const string BranchRemoteFoldPref = FoldPrefPrefix + "Branch.Remote";
        private const string ChangesFoldPref = FoldPrefPrefix + "Changes";

        private static readonly Color[] BranchGraphPalette =
        {
            new Color(0.32f, 0.56f, 0.94f, 1f),
            new Color(0.33f, 0.73f, 0.52f, 1f),
            new Color(0.79f, 0.46f, 0.89f, 1f),
            new Color(0.87f, 0.62f, 0.28f, 1f),
            new Color(0.86f, 0.36f, 0.47f, 1f),
            new Color(0.34f, 0.75f, 0.80f, 1f)
        };

        private readonly List<string> consoleLines = new List<string>();
        private readonly Dictionary<string, Vector2> scrollOffsets = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> splitSizes = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> branchGraphColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private UnitGitService gitService;
        private UnitGitProjectInitializer initializer;
        private UnitGitSnapshot snapshot;
        private UnitGitCommit selectedCommit;
        private UnitGitCommitDetails selectedDetails;
        private UnitGitBranch selectedBranch;
        private string selectedChangePath = string.Empty;
        private string selectedShelf = string.Empty;
        private VisualElement contentRoot;
        private UnitGitTab activeTab = UnitGitTab.Log;
        private string branchSearch = string.Empty;
        private string logSearch = string.Empty;
        private string commitMessage = string.Empty;
        private bool excludePackageFolderFromInitialCommit = true;
        private bool busy;
        private bool refreshQueued;

        [MenuItem("Tools/Orbiters/Unit Git")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnitGitWindow>();
            window.titleContent = new GUIContent(UnitGitInfo.DisplayName);
            window.minSize = new Vector2(980f, 540f);
            window.Show();
        }

        private void OnEnable()
        {
            gitService = new UnitGitService();
            initializer = new UnitGitProjectInitializer();
            EditorApplication.projectChanged += QueueRefreshFromEditorEvent;
            EditorApplication.focusChanged += OnEditorFocusChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= QueueRefreshFromEditorEvent;
            EditorApplication.focusChanged -= OnEditorFocusChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.delayCall -= RunQueuedRefresh;
        }

        public void CreateGUI()
        {
            BuildShell();
            RefreshSnapshot();
        }

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
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.delayCall += RunQueuedRefresh;
        }

        private void RunQueuedRefresh()
        {
            refreshQueued = false;
            if (this == null || busy)
            {
                return;
            }

            RefreshSnapshot();
        }

        private void RememberScrollOffsets()
        {
            if (contentRoot == null)
            {
                return;
            }

            foreach (var scrollView in contentRoot.Query<ScrollView>().ToList())
            {
                if (!string.IsNullOrEmpty(scrollView.name))
                {
                    scrollOffsets[scrollView.name] = scrollView.scrollOffset;
                }
            }
        }

        private void RestoreScrollOffsets()
        {
            if (contentRoot == null)
            {
                return;
            }

            foreach (var scrollView in contentRoot.Query<ScrollView>().ToList())
            {
                if (string.IsNullOrEmpty(scrollView.name) ||
                    !scrollOffsets.TryGetValue(scrollView.name, out var offset))
                {
                    continue;
                }

                scrollView.schedule.Execute(() => scrollView.scrollOffset = offset);
            }
        }

        private void BuildShell()
        {
            rootVisualElement.Clear();
            rootVisualElement.AddToClassList("unitgit-root");

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            rootVisualElement.Add(BuildTopBar());

            contentRoot = new VisualElement();
            contentRoot.AddToClassList("unitgit-content");
            rootVisualElement.Add(contentRoot);
        }

        private VisualElement BuildTopBar()
        {
            var topBar = new VisualElement();
            topBar.AddToClassList("unitgit-topbar");

            var title = new Label("Git");
            title.AddToClassList("unitgit-title");
            topBar.Add(title);

            topBar.Add(BuildTabButton(UnitGitTab.LocalChanges, "Local Changes"));
            topBar.Add(BuildTabButton(UnitGitTab.Shelf, "Shelf"));
            topBar.Add(BuildTabButton(UnitGitTab.Log, GetLogTabTitle()));
            topBar.Add(BuildTabButton(UnitGitTab.Console, "Console", UnitGitIconKind.Close));

            var spacer = new VisualElement();
            spacer.AddToClassList("unitgit-spacer");
            topBar.Add(spacer);

            var status = new Label(busy ? "running git..." : GetTopStatusText());
            status.AddToClassList("unitgit-top-status");
            topBar.Add(status);

            return topBar;
        }

        private Button BuildTabButton(UnitGitTab tab, string text, UnitGitIconKind? iconKind = null)
        {
            var button = new Button(() => SetActiveTab(tab));
            button.AddToClassList("unitgit-tab");
            if (activeTab == tab)
            {
                button.AddToClassList("unitgit-tab--active");
            }

            var label = new Label(text);
            label.AddToClassList("unitgit-tab-label");
            button.Add(label);

            if (iconKind.HasValue)
            {
                var icon = new UnitGitIconElement(iconKind.Value);
                icon.AddToClassList("unitgit-tab-icon");
                button.Add(icon);
            }

            return button;
        }

        private string GetLogTabTitle()
        {
            string branch = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.CurrentBranch)
                ? snapshot.CurrentBranch
                : "Log";

            if (branch.Length > 24)
            {
                branch = branch.Substring(0, 21) + "...";
            }

            return "Log: " + branch;
        }

        private string GetTopStatusText()
        {
            if (snapshot == null)
            {
                return string.Empty;
            }

            if (!snapshot.GitAvailable)
            {
                return "Git unavailable";
            }

            if (!snapshot.IsUnityProject)
            {
                return "No Unity project";
            }

            if (!snapshot.HasRepository)
            {
                return "No root repo";
            }

            string tracking = string.Empty;
            if (snapshot.Ahead > 0 || snapshot.Behind > 0)
            {
                tracking = "  ahead " + snapshot.Ahead + " / behind " + snapshot.Behind;
            }

            return snapshot.CurrentBranch + tracking;
        }

        private void SetActiveTab(UnitGitTab tab)
        {
            activeTab = tab;
            BuildShell();
            RebuildContent();
        }

        private void RefreshSnapshot()
        {
            if (gitService == null)
            {
                return;
            }

            RememberScrollOffsets();
            snapshot = gitService.BuildSnapshot(logSearch);
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
                if (selectedCommit == null || snapshot.Commits.All(commit => commit.FullHash != selectedCommit.FullHash))
                {
                    selectedCommit = snapshot.Commits[0];
                    selectedDetails = gitService.GetCommitDetails(selectedCommit.FullHash);
                }
            }
            else
            {
                selectedCommit = null;
                selectedDetails = null;
            }

            BuildShell();
            RebuildContent();
            RestoreScrollOffsets();
        }

        private void RebuildContent()
        {
            if (contentRoot == null)
            {
                return;
            }

            contentRoot.Clear();

            if (snapshot == null)
            {
                contentRoot.Add(BuildMessagePanel("Loading Git state", "Unit Git is reading the current Unity project."));
                return;
            }

            if (!snapshot.GitAvailable)
            {
                contentRoot.Add(BuildMessagePanel("Git is not available", snapshot.LastError));
                return;
            }

            if (!snapshot.IsUnityProject)
            {
                contentRoot.Add(BuildMessagePanel("No Unity project found", snapshot.LastError));
                return;
            }

            if (!snapshot.HasRepository || !snapshot.HasCommits)
            {
                contentRoot.Add(BuildInitializerPanel());
                return;
            }

            switch (activeTab)
            {
                case UnitGitTab.LocalChanges:
                    contentRoot.Add(BuildLocalChangesBody());
                    break;
                case UnitGitTab.Shelf:
                    contentRoot.Add(BuildShelfBody());
                    break;
                case UnitGitTab.Console:
                    contentRoot.Add(BuildConsoleBody());
                    break;
                default:
                    contentRoot.Add(BuildLogBody());
                    break;
            }
        }

        private VisualElement BuildInitializerPanel()
        {
            bool hasPartialRepository = snapshot != null && snapshot.HasRepository && !snapshot.HasCommits;
            var panel = BuildMessagePanel(
                hasPartialRepository ? "Finish the first Git commit" : "Initialize Git for this Unity project",
                hasPartialRepository
                    ? "A Git repository exists at the Unity project root, but it has no commits yet. Unit Git will ensure the VRChat Unity .gitignore, stage the current project state, and create the first commit."
                    : "Unit Git will create a repository at the project root, ensure a VRChat Unity .gitignore, stage the current project state, and create an initial commit. Large Unity projects can take several minutes.");

            var rootPath = new Label(snapshot.ProjectRoot);
            rootPath.AddToClassList("unitgit-path-label");
            panel.Add(rootPath);

            var excludePackageToggle = new Toggle("Do not include the project-root Package folder in the first commit");
            excludePackageToggle.value = excludePackageFolderFromInitialCommit;
            excludePackageToggle.AddToClassList("unitgit-init-toggle");
            excludePackageToggle.RegisterValueChangedCallback(evt => excludePackageFolderFromInitialCommit = evt.newValue);
            panel.Add(excludePackageToggle);

            var excludePackageDetail = new Label("Adds /[Pp]ackage/ to .gitignore before staging. This does not ignore Unity's Packages folder.");
            excludePackageDetail.AddToClassList("unitgit-init-toggle-detail");
            panel.Add(excludePackageDetail);

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-message-actions");
            actions.Add(BuildActionButton(hasPartialRepository ? "Create First Commit" : "Initialize Project Git", "unitgit-button--primary", InitializeProjectGit));
            actions.Add(BuildActionButton("Refresh", string.Empty, RefreshSnapshot));
            panel.Add(actions);

            return panel;
        }

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

        private VisualElement BuildLocalChangesBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var split = BuildTrackedSplit(LocalChangesDiffSplitPref, 0, 620f);
            split.Add(BuildLocalChangesListPane());
            split.Add(BuildDiffViewerPane());
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

            var message = new TextField();
            message.multiline = false;
            message.value = commitMessage;
            message.AddToClassList("unitgit-local-commit-message");
            message.RegisterValueChangedCallback(evt => commitMessage = evt.newValue);
            commitPanel.Add(message);
            commitPanel.Add(BuildActionButton("Commit Staged", "unitgit-button--primary", CommitStaged));
            pane.Add(commitPanel);

            return pane;
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
                selectedChangePath = change.Path;
                RebuildContent();
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

        private VisualElement BuildDiffViewerPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-diff-pane");

            UnitGitStatusEntry selectedChange = GetSelectedChange();
            UnitGitDiff diff = gitService.GetFileDiff(selectedChange);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("unitgit-diff-toolbar");
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.PreviousDifference, "Previous difference"));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.NextDifference, "Next difference"));
            toolbar.Add(BuildDiffToolButton(UnitGitIconKind.Search, "Search in diff"));
            toolbar.Add(BuildToolbarChip("Side-by-side viewer"));
            toolbar.Add(BuildToolbarChip("Ignore whitespaces and empty lines"));
            toolbar.Add(BuildToolbarChip("Highlight words"));
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
            foreach (UnitGitDiffLine line in diff.Lines)
            {
                scroll.Add(BuildDiffLine(line));
            }

            pane.Add(scroll);
            return pane;
        }

        private Button BuildDiffToolButton(UnitGitIconKind iconKind, string tooltip)
        {
            var button = new Button();
            button.tooltip = tooltip;
            button.AddToClassList("unitgit-diff-tool-button");
            var icon = new UnitGitIconElement(iconKind);
            icon.AddToClassList("unitgit-diff-tool-icon");
            button.Add(icon);
            return button;
        }

        private VisualElement BuildDiffLine(UnitGitDiffLine line)
        {
            var row = new VisualElement();
            row.AddToClassList("unitgit-diff-line");
            row.AddToClassList("unitgit-diff-line--" + line.Kind.ToString().ToLowerInvariant());

            var left = new Label(line.Left);
            left.AddToClassList("unitgit-diff-cell");
            row.Add(left);

            var right = new Label(line.Right);
            right.AddToClassList("unitgit-diff-cell");
            row.Add(right);

            return row;
        }

        private VisualElement BuildShelfBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var branchSplit = BuildTrackedSplit(BranchLogSplitPref, 0, 448f);
            branchSplit.Add(BuildBranchesPane());

            var shelfPane = new VisualElement();
            shelfPane.AddToClassList("unitgit-local-pane");
            shelfPane.Add(BuildSectionHeader("Shelf", "Git stash entries"));

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-row-actions");
            actions.Add(BuildActionButton("Shelve All Changes", "unitgit-button--primary", ShelveAll));
            actions.Add(BuildActionButton("Apply Selected", string.Empty, ApplySelectedShelf));
            actions.Add(BuildActionButton("Drop Selected", string.Empty, DropSelectedShelf));
            shelfPane.Add(actions);

            var shelves = GetShelves();
            var list = new ScrollView();
            list.name = "unitgit-shelf-list";
            list.AddToClassList("unitgit-scroll");
            if (shelves.Count == 0)
            {
                list.Add(BuildEmptyState("No shelf entries."));
            }
            else
            {
                foreach (string shelf in shelves)
                {
                    list.Add(BuildShelfRow(shelf));
                }
            }

            shelfPane.Add(list);

            var console = new VisualElement();
            console.AddToClassList("unitgit-commit-pane");
            console.Add(BuildSectionHeader("Console", "Latest Git output"));
            console.Add(BuildConsoleTail());

            var shelfConsoleSplit = BuildTrackedSplit(ShelfConsoleSplitPref, 1, 420f);
            shelfConsoleSplit.Add(shelfPane);
            shelfConsoleSplit.Add(console);

            branchSplit.Add(shelfConsoleSplit);
            workspace.Add(branchSplit);

            return workspace;
        }

        private VisualElement BuildConsoleBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-console-full");
            workspace.Add(BuildSectionHeader("Console", snapshot.ProjectRoot));

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-row-actions");
            actions.Add(BuildActionButton("Refresh", string.Empty, RefreshSnapshot));
            actions.Add(BuildActionButton("Clear", string.Empty, () =>
            {
                consoleLines.Clear();
                RebuildContent();
            }));
            workspace.Add(actions);

            var scroll = new ScrollView();
            scroll.name = "unitgit-console-scroll";
            scroll.AddToClassList("unitgit-console-scroll");
            if (consoleLines.Count == 0)
            {
                scroll.Add(BuildEmptyState("Git command output will appear here."));
            }
            else
            {
                foreach (string line in consoleLines)
                {
                    scroll.Add(BuildSelectableConsoleLine(line));
                }
            }

            workspace.Add(scroll);
            return workspace;
        }

        private TwoPaneSplitView BuildTrackedSplit(string prefKey, int fixedPaneIndex, float defaultFixedPaneSize)
        {
            float fixedPaneSize = splitSizes.TryGetValue(prefKey, out float sessionSize)
                ? sessionSize
                : EditorPrefs.GetFloat(prefKey, defaultFixedPaneSize);
            var split = new TwoPaneSplitView(
                fixedPaneIndex,
                fixedPaneSize,
                TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("unitgit-split-view");
            split.RegisterCallback<GeometryChangedEvent>(_ => SaveSplitSize(split, prefKey, fixedPaneIndex));
            return split;
        }

        private void SaveSplitSize(TwoPaneSplitView split, string prefKey, int fixedPaneIndex)
        {
            if (split == null || split.childCount <= fixedPaneIndex)
            {
                return;
            }

            var fixedPane = split[fixedPaneIndex];
            float width = fixedPane.resolvedStyle.width;
            if (width >= 160f && width <= 1400f)
            {
                splitSizes[prefKey] = width;
                EditorPrefs.SetFloat(prefKey, width);
            }
        }

        private VisualElement BuildLogToolRail()
        {
            var rail = new VisualElement();
            rail.AddToClassList("unitgit-toolrail");
            rail.Add(BuildIconRailButton(UnitGitIconKind.CreateBranch, "Create new branch", PromptCreateBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Update, "Update selected branch", UpdateSelectedBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Delete, "Delete selected branch", DeleteSelectedBranch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.Fetch, "Fetch", Fetch));
            rail.Add(BuildIconRailButton(UnitGitIconKind.GitHub, "GitHub remote setup", OpenGitHubSetup));
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

        private VisualElement BuildBranchesPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-branches-pane");

            var search = new TextField();
            search.value = branchSearch;
            search.AddToClassList("unitgit-search");
            search.RegisterValueChangedCallback(evt =>
            {
                branchSearch = evt.newValue;
                RebuildContent();
            });
            pane.Add(search);

            var scroll = new ScrollView();
            scroll.name = "unitgit-branch-scroll";
            scroll.AddToClassList("unitgit-branch-scroll");

            var current = new Label("HEAD (Current Branch)");
            current.AddToClassList("unitgit-tree-head");
            scroll.Add(current);

            AddBranchSection(scroll, "Local", snapshot.Branches.Where(branch => !branch.IsRemote), BranchLocalFoldPref);
            AddBranchSection(scroll, "Remote", snapshot.Branches.Where(branch => branch.IsRemote), BranchRemoteFoldPref);
            pane.Add(scroll);

            var footer = new VisualElement();
            footer.AddToClassList("unitgit-branch-footer");
            footer.Add(BuildActionButton("Checkout", string.Empty, CheckoutSelectedBranch));
            pane.Add(footer);

            return pane;
        }

        private void AddBranchSection(VisualElement parent, string title, IEnumerable<UnitGitBranch> branches, string prefKey)
        {
            var section = new VisualElement();
            section.AddToClassList("unitgit-tree-section");

            bool expanded = GetFoldoutExpanded(prefKey, true);
            section.Add(BuildFoldoutButton(
                title,
                string.Empty,
                expanded,
                () => ToggleFoldout(prefKey, true),
                "unitgit-tree-section-title"));

            var filtered = branches
                .Where(branch => MatchesBranchSearch(branch))
                .ToList();

            if (!expanded)
            {
                parent.Add(section);
                return;
            }

            if (filtered.Count == 0)
            {
                var empty = new Label("No " + title.ToLowerInvariant() + " branches");
                empty.AddToClassList("unitgit-branch-empty");
                section.Add(empty);
                parent.Add(section);
                return;
            }

            foreach (IGrouping<string, UnitGitBranch> group in filtered.GroupBy(GetBranchFolder).OrderBy(group => group.Key))
            {
                if (!string.IsNullOrEmpty(group.Key))
                {
                    string folderPrefKey = GetFoldoutPrefKey("BranchFolder." + title, group.Key);
                    bool folderExpanded = GetFoldoutExpanded(folderPrefKey, true);
                    section.Add(BuildFoldoutButton(
                        group.Key,
                        string.Empty,
                        folderExpanded,
                        () => ToggleFoldout(folderPrefKey, true),
                        "unitgit-tree-folder"));
                    if (!folderExpanded)
                    {
                        continue;
                    }
                }

                foreach (UnitGitBranch branch in group.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                {
                    section.Add(BuildBranchRow(branch, !string.IsNullOrEmpty(group.Key)));
                }
            }

            parent.Add(section);
        }

        private bool MatchesBranchSearch(UnitGitBranch branch)
        {
            return branch != null &&
                   (string.IsNullOrWhiteSpace(branchSearch) ||
                    branch.Name.IndexOf(branchSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetBranchFolder(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.IsRemote && branch.Name.Contains("/")
                ? branch.Name.Substring(branch.Name.IndexOf("/", StringComparison.Ordinal) + 1)
                : branch.Name;

            int slash = name.IndexOf("/", StringComparison.Ordinal);
            return slash > 0 ? name.Substring(0, slash) : string.Empty;
        }

        private VisualElement BuildBranchRow(UnitGitBranch branch, bool indented)
        {
            var row = new Button(() =>
            {
                selectedBranch = branch;
                RebuildContent();
            });
            row.AddToClassList("unitgit-branch-row");
            row.tooltip = branch.Name;
            if (indented)
            {
                row.AddToClassList("unitgit-branch-row--indented");
            }

            if (branch.IsCurrent)
            {
                row.AddToClassList("unitgit-branch-row--current");
            }

            if (selectedBranch != null && selectedBranch.FullRef == branch.FullRef)
            {
                row.AddToClassList("unitgit-branch-row--selected");
            }

            var dot = new VisualElement();
            dot.AddToClassList("unitgit-branch-dot");
            dot.style.backgroundColor = GetGraphColorForKey(branch.Name);
            row.Add(dot);

            var name = new Label(GetBranchLeafName(branch));
            name.AddToClassList("unitgit-branch-name");
            row.Add(name);

            if (!string.IsNullOrWhiteSpace(branch.Upstream))
            {
                var upstream = new Label(branch.Upstream);
                upstream.AddToClassList("unitgit-branch-upstream");
                row.Add(upstream);
            }

            if (!string.IsNullOrWhiteSpace(branch.ShortHash))
            {
                var hash = new Label(branch.ShortHash);
                hash.AddToClassList("unitgit-branch-hash");
                row.Add(hash);
            }

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                {
                    selectedBranch = branch;
                    CheckoutSelectedBranch();
                    evt.StopPropagation();
                }
            });

            return row;
        }

        private static string GetBranchLeafName(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.Name;
            if (branch.IsRemote && name.Contains("/"))
            {
                name = name.Substring(name.IndexOf("/", StringComparison.Ordinal) + 1);
            }

            int slash = name.LastIndexOf("/", StringComparison.Ordinal);
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }

        private VisualElement BuildLogPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-log-pane");

            var toolbar = new VisualElement();
            toolbar.AddToClassList("unitgit-log-toolbar");

            var search = new TextField();
            search.value = logSearch;
            search.AddToClassList("unitgit-log-search");
            search.RegisterValueChangedCallback(evt =>
            {
                logSearch = evt.newValue;
                RefreshSnapshot();
            });
            toolbar.Add(search);

            toolbar.Add(BuildToolbarChip("Branch: " + Shorten(snapshot.CurrentBranch, 28)));
            toolbar.Add(BuildToolbarChip("User"));
            toolbar.Add(BuildToolbarChip("Date"));
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
                int totalCommits = snapshot.Commits.Count;
                foreach (UnitGitCommit commit in snapshot.Commits)
                {
                    scroll.Add(BuildCommitRow(commit, index, totalCommits));
                    index++;
                }
            }

            pane.Add(scroll);
            return pane;
        }

        private VisualElement BuildDetailsPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-details-pane");

            pane.Add(BuildChangedFilesTree());
            pane.Add(BuildCommitDetailsCard());

            return pane;
        }

        private VisualElement BuildChangedFilesTree()
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");
            panel.Add(BuildSectionHeader("Changed Files", selectedDetails != null ? selectedDetails.ChangedFiles.Count + " files" : "No commit selected"));

            var scroll = new ScrollView();
            scroll.name = "unitgit-changed-files-scroll";
            scroll.AddToClassList("unitgit-files-scroll");

            if (selectedDetails == null || selectedDetails.ChangedFiles.Count == 0)
            {
                scroll.Add(BuildEmptyState("Select a commit to inspect changed files."));
            }
            else
            {
                AddChangedFileTree(scroll, selectedDetails.ChangedFiles);
            }

            panel.Add(scroll);
            return panel;
        }

        private void AddChangedFileTree(VisualElement parent, IEnumerable<string> files)
        {
            var root = ChangedFileTreeNode.CreateRoot();
            foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                root.AddFile(file);
            }

            AddChangedFileTreeNode(parent, root, 0);
        }

        private void AddChangedFileTreeNode(VisualElement parent, ChangedFileTreeNode node, int depth)
        {
            foreach (ChangedFileTreeNode folder in node.Folders.Values)
            {
                string prefKey = GetFoldoutPrefKey("ChangedFilesFolder", folder.FullPath);
                bool expanded = GetFoldoutExpanded(prefKey, true);
                var folderButton = BuildFoldoutButton(
                    folder.Name,
                    folder.FileCount + " files",
                    expanded,
                    () => ToggleFoldout(prefKey, true),
                    "unitgit-file-folder");
                folderButton.style.marginLeft = 8f + depth * 18f;
                parent.Add(folderButton);

                if (expanded)
                {
                    AddChangedFileTreeNode(parent, folder, depth + 1);
                }
            }

            foreach (string file in node.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                parent.Add(BuildChangedFileButton(file, depth));
            }
        }

        private VisualElement BuildChangedFileButton(string file, int depth)
        {
            var row = new Button(() => OpenProjectPath(file));
            row.tooltip = file;
            row.AddToClassList("unitgit-file-row");
            row.style.marginLeft = 24f + depth * 18f;

            row.Add(BuildProjectFileIcon(file, "unitgit-file-row-icon"));

            var name = new Label(GetFileLeaf(file));
            name.AddToClassList("unitgit-file-row-name");
            row.Add(name);

            return row;
        }

        private VisualElement BuildCommitDetailsCard()
        {
            var card = new VisualElement();
            card.AddToClassList("unitgit-details-card");

            if (selectedDetails == null || selectedDetails.Commit == null)
            {
                card.Add(BuildEmptyState("No commit selected."));
                return card;
            }

            var subject = new Label(selectedDetails.Commit.Subject);
            subject.AddToClassList("unitgit-details-title");
            card.Add(subject);

            var hash = new Label(selectedDetails.Commit.ShortHash + "  " + selectedDetails.Commit.AuthorName);
            hash.AddToClassList("unitgit-details-hash");
            card.Add(hash);

            var author = new Label("<" + selectedDetails.Commit.AuthorEmail + "> on " + selectedDetails.AuthorDate);
            author.AddToClassList("unitgit-details-text");
            card.Add(author);

            if (!string.IsNullOrWhiteSpace(selectedDetails.CommitterName))
            {
                var committer = new Label("committed by " + selectedDetails.CommitterName + " on " + selectedDetails.CommitterDate);
                committer.AddToClassList("unitgit-details-muted");
                card.Add(committer);
            }

            if (!string.IsNullOrWhiteSpace(selectedDetails.Commit.Decorations))
            {
                var decorations = new Label(selectedDetails.Commit.Decorations);
                decorations.AddToClassList("unitgit-decoration-line");
                card.Add(decorations);
            }

            return card;
        }

        private VisualElement BuildSectionHeader(string title, string detail)
        {
            var header = new VisualElement();
            header.AddToClassList("unitgit-section-header");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-section-title");
            header.Add(titleLabel);

            var detailLabel = new Label(detail);
            detailLabel.AddToClassList("unitgit-section-detail");
            header.Add(detailLabel);

            return header;
        }

        private VisualElement BuildCommitRow(UnitGitCommit commit, int index, int totalCommits)
        {
            var row = new Button(() =>
            {
                selectedCommit = commit;
                selectedDetails = gitService.GetCommitDetails(commit.FullHash);
                RebuildContent();
            });
            row.AddToClassList("unitgit-log-row");
            if (selectedCommit != null && selectedCommit.FullHash == commit.FullHash)
            {
                row.AddToClassList("unitgit-log-row--selected");
            }

            var graphColor = GetGraphColor(commit);
            var graph = BuildCommitGraphCell(graphColor, index == 0, index == totalCommits - 1);
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

        private VisualElement BuildCommitGraphCell(Color graphColor, bool isNewestVisibleCommit, bool isOldestVisibleCommit)
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

            var dot = new VisualElement();
            dot.AddToClassList("unitgit-graph-dot");
            dot.style.backgroundColor = graphColor;
            graph.Add(dot);

            return graph;
        }

        private VisualElement BuildShelfRow(string shelf)
        {
            var row = new Button(() =>
            {
                selectedShelf = ExtractShelfRef(shelf);
                RebuildContent();
            });
            row.text = shelf;
            row.AddToClassList("unitgit-shelf-row");
            if (!string.IsNullOrEmpty(selectedShelf) && shelf.StartsWith(selectedShelf + ":", StringComparison.Ordinal))
            {
                row.AddToClassList("unitgit-shelf-row--selected");
            }

            return row;
        }

        private VisualElement BuildConsoleTail()
        {
            var scroll = new ScrollView();
            scroll.name = "unitgit-console-tail";
            scroll.AddToClassList("unitgit-console-tail");
            if (consoleLines.Count == 0)
            {
                scroll.Add(BuildEmptyState("No Git output yet."));
                return scroll;
            }

            foreach (string line in consoleLines.Take(12))
            {
                scroll.Add(BuildSelectableConsoleLine(line));
            }

            return scroll;
        }

        private VisualElement BuildMessagePanel(string title, string body)
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-message-panel");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-message-title");
            panel.Add(titleLabel);

            var bodyLabel = new Label(body);
            bodyLabel.AddToClassList("unitgit-message-body");
            panel.Add(bodyLabel);

            return panel;
        }

        private VisualElement BuildEmptyState(string text)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-empty");
            return label;
        }

        private TextField BuildSelectableConsoleLine(string text)
        {
            var field = new TextField();
            field.value = text;
            field.isReadOnly = true;
            field.AddToClassList("unitgit-console-line");
            return field;
        }

        private Button BuildFoldoutButton(string title, string detail, bool expanded, Action toggle, params string[] classNames)
        {
            var button = new Button();
            button.AddToClassList("unitgit-foldout-button");
            foreach (string className in classNames)
            {
                if (!string.IsNullOrWhiteSpace(className))
                {
                    button.AddToClassList(className);
                }
            }

            var arrow = new UnitGitIconElement(expanded ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed);
            arrow.AddToClassList("unitgit-foldout-arrow");
            button.Add(arrow);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-foldout-title");
            button.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                var detailLabel = new Label(detail);
                detailLabel.AddToClassList("unitgit-foldout-detail");
                button.Add(detailLabel);
            }

            bool handledOnMouseDown = false;
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                handledOnMouseDown = true;
                toggle();
                evt.StopPropagation();
            });
            button.clicked += () =>
            {
                if (handledOnMouseDown)
                {
                    handledOnMouseDown = false;
                    return;
                }

                toggle();
            };
            return button;
        }

        private Label BuildToolbarChip(string text)
        {
            var chip = new Label(text);
            chip.AddToClassList("unitgit-toolbar-chip");
            return chip;
        }

        private Label BuildHeaderLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-log-header-label");
            label.AddToClassList(className);
            return label;
        }

        private Button BuildActionButton(string text, string extraClass, Action action)
        {
            var button = new Button(() => action());
            button.text = text;
            button.AddToClassList("unitgit-button");
            if (!string.IsNullOrWhiteSpace(extraClass))
            {
                button.AddToClassList(extraClass);
            }

            return button;
        }

        private void InitializeProjectGit()
        {
            if (!EditorUtility.DisplayDialog(
                    "Initialize Project Git",
                    "This will create a Git repository at the Unity project root, update .gitignore if needed, stage the current state, and create an initial commit.",
                    "Initialize",
                    "Cancel"))
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

            string message = selectedBranch.IsRemote
                ? "Delete the local remote-tracking branch " + selectedBranch.Name + "? This will not push a remote deletion."
                : "Delete local branch " + selectedBranch.Name + "?";
            if (!EditorUtility.DisplayDialog("Delete Branch", message, "Delete", "Cancel"))
            {
                return;
            }

            RunAction("delete " + selectedBranch.Name, () => gitService.DeleteBranch(selectedBranch));
        }

        private void Fetch()
        {
            RunAction("fetch", () => gitService.Fetch());
        }

        private void OpenGitHubSetup()
        {
            UnitGitGitHubSetupWindow.Open(
                GetDefaultGitHubRepositoryName(),
                RunGitHubLogin,
                CreateGitHubRemote);
        }

        private void RunGitHubLogin()
        {
            RunAction("github login", () => gitService.GitHubLogin());
        }

        private void CreateGitHubRemote(string repositoryName, string remoteName, bool isPrivate)
        {
            RunAction("github create remote", () => gitService.CreateGitHubRemoteRepository(repositoryName, remoteName, isPrivate));
        }

        private void PullFastForward()
        {
            if (!EditorUtility.DisplayDialog(
                    "Pull",
                    "Run git pull --ff-only on the current branch?",
                    "Pull",
                    "Cancel"))
            {
                return;
            }

            RunAction("pull", () => gitService.PullFastForward());
        }

        private void StageAll()
        {
            RunAction("stage all", () => gitService.StageAll());
        }

        private void UnstageAll()
        {
            RunAction("unstage all", () => gitService.UnstageAll());
        }

        private void CommitStaged()
        {
            RunAction("commit", () => gitService.Commit(commitMessage));
            if (snapshot != null && snapshot.HasRepository)
            {
                commitMessage = string.Empty;
            }
        }

        private void ShelveAll()
        {
            RunAction("shelve", () => gitService.ShelveAll("Unit Git shelf " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        }

        private void CheckoutSelectedBranch()
        {
            if (selectedBranch == null)
            {
                AppendConsole("checkout", "Select a branch first.");
                RebuildContent();
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Checkout Branch",
                    "Checkout " + selectedBranch.Name + "?",
                    "Checkout",
                    "Cancel"))
            {
                return;
            }

            RunAction("checkout " + selectedBranch.Name, () => gitService.Checkout(selectedBranch));
        }

        private void ApplySelectedShelf()
        {
            if (string.IsNullOrWhiteSpace(selectedShelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            RunAction("stash apply " + selectedShelf, () => gitService.RunGit(30000, "stash", "apply", selectedShelf));
        }

        private void DropSelectedShelf()
        {
            if (string.IsNullOrWhiteSpace(selectedShelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Drop Shelf",
                    "Drop " + selectedShelf + "?",
                    "Drop",
                    "Cancel"))
            {
                return;
            }

            RunAction("stash drop " + selectedShelf, () => gitService.RunGit(30000, "stash", "drop", selectedShelf));
            selectedShelf = string.Empty;
        }

        private void RunAction(string label, Func<GitCommandResult> action)
        {
            busy = true;
            BuildShell();
            RebuildContent();

            GitCommandResult result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                result = new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = ex.Message
                };
            }
            finally
            {
                busy = false;
            }

            AppendConsole(label, result);
            if (result == null || !result.Success)
            {
                string message = result != null && !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : "The command failed.";
                EditorUtility.DisplayDialog("Unit Git", message, "OK");
            }

            RefreshSnapshot();
        }

        private void AppendConsole(string label, GitCommandResult result)
        {
            string status = result != null && result.Success ? "ok" : "error";
            string message = result != null ? result.Message : "No result.";
            AppendConsole(label, status + ": " + message);
        }

        private void AppendConsole(string label, string message)
        {
            consoleLines.Insert(0, DateTime.Now.ToString("HH:mm:ss") + " " + label + " - " + message);
            while (consoleLines.Count > MaxConsoleLines)
            {
                consoleLines.RemoveAt(consoleLines.Count - 1);
            }
        }

        private List<string> GetShelves()
        {
            var result = gitService.RunGit(30000, "stash", "list");
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return new List<string>();
            }

            return result.StandardOutput
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private bool GetFoldoutExpanded(string prefKey, bool defaultValue)
        {
            return EditorPrefs.GetBool(prefKey, defaultValue);
        }

        private void ToggleFoldout(string prefKey, bool defaultValue)
        {
            EditorPrefs.SetBool(prefKey, !GetFoldoutExpanded(prefKey, defaultValue));
            RebuildContent();
        }

        private static string GetFoldoutPrefKey(string scope, string value)
        {
            return FoldPrefPrefix + scope + "." + (value ?? string.Empty)
                .Replace('\\', '/')
                .Replace('/', '.')
                .Replace(' ', '_');
        }

        private static string ExtractShelfRef(string shelf)
        {
            if (string.IsNullOrWhiteSpace(shelf))
            {
                return string.Empty;
            }

            int colon = shelf.IndexOf(':');
            return colon > 0 ? shelf.Substring(0, colon) : shelf;
        }

        private static string GetTopFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "root";
            }

            string normalized = path.Replace('\\', '/');
            int slash = normalized.IndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : "root";
        }

        private UnitGitStatusEntry GetSelectedChange()
        {
            if (snapshot == null || snapshot.Changes.Count == 0)
            {
                return null;
            }

            var selected = snapshot.Changes.FirstOrDefault(change =>
                string.Equals(change.Path, selectedChangePath, StringComparison.OrdinalIgnoreCase));
            return selected ?? snapshot.Changes[0];
        }

        private static string GetFileLeaf(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.GetFileName(path.Replace('\\', '/'));
        }

        private static string GetDirectoryLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            return slash > 0 ? normalized.Substring(0, slash) : string.Empty;
        }

        private void OpenProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || snapshot == null)
            {
                return;
            }

            string normalized = projectPath.Replace('\\', '/');
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return;
            }

            string fullPath = Path.GetFullPath(Path.Combine(snapshot.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                EditorUtility.RevealInFinder(fullPath);
            }
        }

        private Color GetGraphColor(UnitGitCommit commit)
        {
            string key = GetCommitBranchKey(commit);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "HEAD";
            }

            return GetGraphColorForKey(key);
        }

        private Color GetGraphColorForKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "HEAD";
            }

            if (!branchGraphColors.TryGetValue(key, out var color))
            {
                color = BranchGraphPalette[branchGraphColors.Count % BranchGraphPalette.Length];
                branchGraphColors[key] = color;
            }

            return color;
        }

        private string GetCommitBranchKey(UnitGitCommit commit)
        {
            string decorationBranch = GetFirstDecorationBranch(commit != null ? commit.Decorations : null);
            if (!string.IsNullOrWhiteSpace(decorationBranch))
            {
                return decorationBranch;
            }

            if (selectedBranch != null && !string.IsNullOrWhiteSpace(selectedBranch.Name))
            {
                return selectedBranch.Name;
            }

            return snapshot != null ? snapshot.CurrentBranch : "HEAD";
        }

        private static string GetFirstDecorationBranch(string decorations)
        {
            if (string.IsNullOrWhiteSpace(decorations))
            {
                return string.Empty;
            }

            string[] parts = decorations.Split(',');
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int arrowIndex = part.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIndex >= 0)
                {
                    part = part.Substring(arrowIndex + 4);
                }

                return part;
            }

            return string.Empty;
        }

        private Image BuildProjectFileIcon(string path, string className)
        {
            var icon = new Image();
            icon.AddToClassList(className);
            icon.image = GetProjectFileIcon(path);
            return icon;
        }

        private Texture2D GetProjectFileIcon(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var cached = AssetDatabase.GetCachedIcon(path.Replace('\\', '/')) as Texture2D;
                if (cached != null)
                {
                    return cached;
                }
            }

            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            string iconName;
            switch (extension)
            {
                case ".cs":
                    iconName = "cs Script Icon";
                    break;
                case ".js":
                case ".jsx":
                case ".ts":
                case ".tsx":
                    iconName = "TextAsset Icon";
                    break;
                case ".prefab":
                    iconName = "Prefab Icon";
                    break;
                case ".mat":
                    iconName = "Material Icon";
                    break;
                case ".anim":
                    iconName = "AnimationClip Icon";
                    break;
                case ".controller":
                    iconName = "AnimatorController Icon";
                    break;
                case ".fbx":
                case ".obj":
                    iconName = "PrefabModel Icon";
                    break;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".psd":
                    iconName = "Texture2D Icon";
                    break;
                case ".shader":
                    iconName = "Shader Icon";
                    break;
                default:
                    iconName = "DefaultAsset Icon";
                    break;
            }

            return (EditorGUIUtility.IconContent(iconName).image as Texture2D)
                   ?? EditorGUIUtility.FindTexture(iconName)
                   ?? (EditorGUIUtility.IconContent("DefaultAsset Icon").image as Texture2D)
                   ?? EditorGUIUtility.FindTexture("DefaultAsset Icon");
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength <= 3)
            {
                return value.Substring(0, maxLength);
            }

            return value.Substring(0, maxLength - 3) + "...";
        }

        private string GetDefaultGitHubRepositoryName()
        {
            string root = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ProjectRoot)
                ? snapshot.ProjectRoot
                : UnitGitService.GetUnityProjectRoot();
            string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unity-project";
            }

            var characters = name
                .Trim()
                .Select(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'
                    ? character
                    : '-')
                .ToArray();
            return new string(characters).Trim('-', '.', '_').ToLowerInvariant();
        }

        private enum UnitGitTab
        {
            LocalChanges,
            Shelf,
            Log,
            Console
        }

        private sealed class ChangedFileTreeNode
        {
            public readonly SortedDictionary<string, ChangedFileTreeNode> Folders =
                new SortedDictionary<string, ChangedFileTreeNode>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> Files = new List<string>();
            public string Name;
            public string FullPath;

            public int FileCount
            {
                get { return Files.Count + Folders.Values.Sum(folder => folder.FileCount); }
            }

            public static ChangedFileTreeNode CreateRoot()
            {
                return new ChangedFileTreeNode
                {
                    Name = string.Empty,
                    FullPath = string.Empty
                };
            }

            public void AddFile(string file)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    return;
                }

                string normalized = file.Replace('\\', '/');
                string[] parts = normalized
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 1)
                {
                    Files.Add(normalized);
                    return;
                }

                ChangedFileTreeNode current = this;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string folderName = parts[i];
                    if (!current.Folders.TryGetValue(folderName, out ChangedFileTreeNode child))
                    {
                        string fullPath = string.IsNullOrWhiteSpace(current.FullPath)
                            ? folderName
                            : current.FullPath + "/" + folderName;
                        child = new ChangedFileTreeNode
                        {
                            Name = folderName,
                            FullPath = fullPath
                        };
                        current.Folders[folderName] = child;
                    }

                    current = child;
                }

                current.Files.Add(normalized);
            }
        }

        private sealed class UnitGitGitHubSetupWindow : EditorWindow
        {
            private string repositoryName;
            private string remoteName;
            private bool isPrivate = true;
            private Action onLogin;
            private Action<string, string, bool> onCreateRemote;

            public static void Open(string defaultRepositoryName, Action onLogin, Action<string, string, bool> onCreateRemote)
            {
                var window = CreateInstance<UnitGitGitHubSetupWindow>();
                window.titleContent = new GUIContent("GitHub Remote");
                window.repositoryName = defaultRepositoryName;
                window.remoteName = "origin";
                window.onLogin = onLogin;
                window.onCreateRemote = onCreateRemote;
                window.minSize = new Vector2(380f, 220f);
                window.maxSize = new Vector2(560f, 260f);
                window.ShowUtility();
            }

            public void CreateGUI()
            {
                var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
                if (styleSheet != null)
                {
                    rootVisualElement.styleSheets.Add(styleSheet);
                }

                rootVisualElement.AddToClassList("unitgit-github-setup");

                var title = new Label("GitHub CLI remote setup");
                title.AddToClassList("unitgit-github-setup-title");
                rootVisualElement.Add(title);

                var body = new Label("Use GitHub CLI to sign in, create a GitHub repository, and set the local remote. This does not push commits.");
                body.AddToClassList("unitgit-github-setup-body");
                rootVisualElement.Add(body);

                var repositoryField = new TextField("Repository");
                repositoryField.value = repositoryName;
                repositoryField.AddToClassList("unitgit-github-setup-field");
                repositoryField.RegisterValueChangedCallback(evt => repositoryName = evt.newValue);
                rootVisualElement.Add(repositoryField);

                var remoteField = new TextField("Remote");
                remoteField.value = remoteName;
                remoteField.AddToClassList("unitgit-github-setup-field");
                remoteField.RegisterValueChangedCallback(evt => remoteName = evt.newValue);
                rootVisualElement.Add(remoteField);

                var privateToggle = new Toggle("Private repository");
                privateToggle.value = isPrivate;
                privateToggle.AddToClassList("unitgit-github-setup-toggle");
                privateToggle.RegisterValueChangedCallback(evt => isPrivate = evt.newValue);
                rootVisualElement.Add(privateToggle);

                var actions = new VisualElement();
                actions.AddToClassList("unitgit-row-actions");
                actions.Add(BuildSetupButton("Login with GitHub CLI", () => onLogin?.Invoke()));
                actions.Add(BuildSetupButton("Create Remote", () =>
                {
                    onCreateRemote?.Invoke(repositoryName, remoteName, isPrivate);
                    Close();
                }));
                actions.Add(BuildSetupButton("Cancel", Close));
                rootVisualElement.Add(actions);
            }

            private static Button BuildSetupButton(string text, Action onClick)
            {
                var button = new Button(() => onClick?.Invoke())
                {
                    text = text
                };
                button.AddToClassList("unitgit-button");
                return button;
            }
        }

        private sealed class UnitGitBranchPromptWindow : EditorWindow
        {
            private string branchName;
            private Action<string> onCreate;

            public static void Open(string defaultName, Action<string> onCreate)
            {
                var window = CreateInstance<UnitGitBranchPromptWindow>();
                window.titleContent = new GUIContent("New Branch");
                window.branchName = defaultName;
                window.onCreate = onCreate;
                window.minSize = new Vector2(360f, 118f);
                window.maxSize = new Vector2(520f, 118f);
                window.ShowUtility();
            }

            public void CreateGUI()
            {
                var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
                if (styleSheet != null)
                {
                    rootVisualElement.styleSheets.Add(styleSheet);
                }

                rootVisualElement.AddToClassList("unitgit-branch-prompt");

                var label = new Label("Create new branch from the selected commit");
                label.AddToClassList("unitgit-branch-prompt-title");
                rootVisualElement.Add(label);

                var field = new TextField();
                field.value = branchName;
                field.AddToClassList("unitgit-branch-prompt-field");
                field.RegisterValueChangedCallback(evt => branchName = evt.newValue);
                rootVisualElement.Add(field);

                var actions = new VisualElement();
                actions.AddToClassList("unitgit-row-actions");
                actions.Add(new Button(() =>
                {
                    onCreate?.Invoke(branchName);
                    Close();
                })
                {
                    text = "Create"
                });
                actions.Add(new Button(Close)
                {
                    text = "Cancel"
                });
                rootVisualElement.Add(actions);
            }
        }
    }
}
