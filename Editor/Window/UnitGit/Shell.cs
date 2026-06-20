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
            topBar.Add(BuildTabButton(UnitGitTab.Console, "Console"));
            topBar.Add(BuildTabButton(UnitGitTab.Settings, "Settings"));

            var spacer = new VisualElement();
            spacer.AddToClassList("unitgit-spacer");
            topBar.Add(spacer);

            var status = new Label(busy ? "running git..." : GetTopStatusText());
            status.AddToClassList("unitgit-top-status");
            topBar.Add(status);

            return topBar;
        }

        private Button BuildTabButton(UnitGitTab tab, string text)
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

        private void RebuildContent()
        {
            if (contentRoot == null)
            {
                return;
            }

            localChangesListRoot = null;
            localDiffPaneRoot = null;
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

            if (!snapshot.HasRepository)
            {
                contentRoot.Add(BuildInitializerPanel());
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.LastError))
            {
                VisualElement panel = BuildMessagePanel("Git state unavailable", snapshot.LastError);
                var actions = new VisualElement();
                actions.AddToClassList("unitgit-message-actions");
                actions.Add(BuildActionButton("Refresh", "unitgit-button--primary", RefreshSnapshot));
                panel.Add(actions);
                contentRoot.Add(panel);
                return;
            }

            if (!snapshot.HasCommits)
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
                case UnitGitTab.Settings:
                    contentRoot.Add(BuildSettingsBody());
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
    }
}
