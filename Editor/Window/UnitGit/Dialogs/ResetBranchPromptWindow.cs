using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitResetBranchPromptWindow : EditorWindow
    {
        private readonly Dictionary<UnitGitResetMode, RadioButton> modeButtons = new Dictionary<UnitGitResetMode, RadioButton>();
        private Action<UnitGitResetMode> onSubmit;
        private UnitGitResetMode selectedMode = UnitGitResetMode.Mixed;
        private string branchName = string.Empty;
        private string projectRoot = string.Empty;
        private UnitGitCommit commit;

        public static void Open(string branchName, string projectRoot, UnitGitCommit commit, Action<UnitGitResetMode> onSubmit)
        {
            var window = CreateInstance<UnitGitResetBranchPromptWindow>();
            window.titleContent = new GUIContent("Git Reset");
            window.branchName = branchName ?? string.Empty;
            window.projectRoot = projectRoot ?? string.Empty;
            window.commit = commit;
            window.onSubmit = onSubmit;
            window.minSize = new Vector2(620f, 438f);
            window.maxSize = new Vector2(760f, 520f);
            window.ShowUtility();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.unitgit/Editor/Styles/unitgit.uss");
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
            }

            root.AddToClassList("unitgit-reset-prompt");
            root.Add(BuildTargetSummary());

            var body = new Label("This will reset the current branch head to the selected commit,\nand update the working tree and the index according to the selected mode:");
            body.AddToClassList("unitgit-reset-body");
            root.Add(body);

            root.Add(BuildModeOption(
                UnitGitResetMode.Soft,
                "Soft",
                "Files won't change, differences will be staged for commit."));
            root.Add(BuildModeOption(
                UnitGitResetMode.Mixed,
                "Mixed",
                "Files won't change, differences won't be staged."));
            root.Add(BuildModeOption(
                UnitGitResetMode.Hard,
                "Hard",
                "Files will be reverted to the state of the selected commit.\nWarning: any local changes will be lost."));
            root.Add(BuildModeOption(
                UnitGitResetMode.Keep,
                "Keep",
                "Files will be reverted to the state of the selected commit,\nbut local changes will be kept intact."));

            var spacer = new VisualElement();
            spacer.AddToClassList("unitgit-reset-spacer");
            root.Add(spacer);

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-reset-actions");

            var reset = new Button(() =>
            {
                onSubmit?.Invoke(selectedMode);
                Close();
            })
            {
                text = "Reset"
            };
            reset.AddToClassList("unitgit-button");
            reset.AddToClassList("unitgit-button--primary");
            actions.Add(reset);

            var cancel = new Button(Close)
            {
                text = "Cancel"
            };
            cancel.AddToClassList("unitgit-button");
            actions.Add(cancel);

            root.Add(actions);
            SelectMode(UnitGitResetMode.Mixed);
        }

        private VisualElement BuildTargetSummary()
        {
            var label = new Label(GetBranchLabel() + " in " + GetRepositoryName() + " -> " + GetCommitLabel());
            label.AddToClassList("unitgit-reset-target");
            return label;
        }

        private VisualElement BuildModeOption(UnitGitResetMode mode, string title, string description)
        {
            var option = new VisualElement();
            option.AddToClassList("unitgit-reset-option");

            var button = new RadioButton(title);
            button.value = selectedMode == mode;
            button.AddToClassList("unitgit-reset-radio");
            button.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    SelectMode(mode);
                }
            });
            modeButtons[mode] = button;
            option.Add(button);

            var details = new Label(description);
            details.AddToClassList("unitgit-reset-detail");
            option.Add(details);

            return option;
        }

        private void SelectMode(UnitGitResetMode mode)
        {
            selectedMode = mode;
            foreach (KeyValuePair<UnitGitResetMode, RadioButton> item in modeButtons)
            {
                item.Value.SetValueWithoutNotify(item.Key == selectedMode);
            }
        }

        private string GetBranchLabel()
        {
            return string.IsNullOrWhiteSpace(branchName) ? "(detached HEAD)" : branchName.Trim();
        }

        private string GetRepositoryName()
        {
            string normalized = string.IsNullOrWhiteSpace(projectRoot)
                ? string.Empty
                : projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = string.IsNullOrWhiteSpace(normalized) ? string.Empty : Path.GetFileName(normalized);
            return string.IsNullOrWhiteSpace(name) ? "repository" : name;
        }

        private string GetCommitLabel()
        {
            if (commit == null)
            {
                return "(no commit)";
            }

            string subject = string.IsNullOrWhiteSpace(commit.Subject) ? "(no subject)" : Shorten(commit.Subject.Trim(), 58);
            string author = string.IsNullOrWhiteSpace(commit.AuthorName) ? string.Empty : " by " + commit.AuthorName.Trim();
            return commit.ShortHash + " \"" + subject + "\"" + author;
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }
    }
}
