using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow : EditorWindow
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
        private const double EditorEventRefreshDelaySeconds = 0.35d;
        private const double EditorEventRefreshCooldownSeconds = 1.25d;
        private const double LogSearchRefreshDelaySeconds = 0.25d;
        private const int CommitPageSize = 100;

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
        private readonly ConcurrentQueue<string> pendingConsoleLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<Action> pendingMainThreadActions = new ConcurrentQueue<Action>();
        private readonly Dictionary<string, Vector2> scrollOffsets = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> splitSizes = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> branchGraphColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private UnitGitService gitService;
        private UnitGitProjectInitializer initializer;
        private UnitGitSnapshot snapshot;
        private UnitGitCommit selectedCommit;
        private readonly HashSet<string> selectedCommitHashes = new HashSet<string>(StringComparer.Ordinal);
        private string selectionAnchorHash = string.Empty;
        private string selectedReleaseId = string.Empty;
        private UnitGitCommitDetails selectedDetails;
        private UnitGitBranch selectedBranch;
        private string selectedChangePath = string.Empty;
        private string selectedShelf = string.Empty;
        private VisualElement contentRoot;
        private VisualElement localChangesListRoot;
        private VisualElement localDiffPaneRoot;
        private UnitGitTab activeTab = UnitGitTab.Log;
        private string branchSearch = string.Empty;
        private string logSearch = string.Empty;
        private string logSearchDraft = string.Empty;
        private int logPage;
        private string diffSearch = string.Empty;
        private int diffSearchMatchIndex;
        private TextField diffSearchField;
        private string commitMessage = string.Empty;
        private string commitMessageBeforeAmend = string.Empty;
        private bool commitAmend;
        private bool excludePackageFolderFromInitialCommit = true;
        private bool busy;
        private bool refreshQueued;
        private bool refreshingSnapshot;
        private bool refreshAgainRequested;
        private bool editorUpdatePumpActive;
        private bool logSearchRefreshQueued;
        private double queuedRefreshTime;
        private double queuedLogSearchRefreshTime;
        private double lastEditorEventRefreshTime = -1000d;
        private int refreshRequestId;

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
            ResetTransientAsyncState();
            gitService = new UnitGitService();
            initializer = new UnitGitProjectInitializer();
            EditorApplication.projectChanged += QueueRefreshFromEditorEvent;
            EditorApplication.focusChanged += OnEditorFocusChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            UnitGitReleases.ChangedExternally += QueueRefreshFromEditorEvent;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= QueueRefreshFromEditorEvent;
            EditorApplication.focusChanged -= OnEditorFocusChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            UnitGitReleases.ChangedExternally -= QueueRefreshFromEditorEvent;
            EditorApplication.delayCall -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedLogSearchRefresh;
            EditorApplication.update -= DrainEditorQueues;
        }

        private void ResetTransientAsyncState()
        {
            busy = false;
            refreshQueued = false;
            refreshingSnapshot = false;
            refreshAgainRequested = false;
            editorUpdatePumpActive = false;
            logSearchRefreshQueued = false;
            while (pendingConsoleLines.TryDequeue(out _))
            {
            }

            while (pendingMainThreadActions.TryDequeue(out _))
            {
            }

            EditorApplication.update -= RunQueuedRefresh;
            EditorApplication.update -= RunQueuedLogSearchRefresh;
            EditorApplication.update -= DrainEditorQueues;
        }

        public void CreateGUI()
        {
            BuildShell();
            refreshAgainRequested = false;
            RefreshSnapshot();
        }

        private enum UnitGitTab
        {
            LocalChanges,
            Shelf,
            Log,
            Console,
            Settings
        }
    }
}
