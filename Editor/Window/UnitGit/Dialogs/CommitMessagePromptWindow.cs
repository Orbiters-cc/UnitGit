using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed class UnitGitCommitMessagePromptWindow : EditorWindow
    {
        private Action<string> onSubmit;
        private string message = string.Empty;
        private string summary = string.Empty;
        private string submitLabel = "Apply";

        public static void Open(string title, string summary, string initialMessage, string submitLabel, Action<string> onSubmit)
        {
            var window = CreateInstance<UnitGitCommitMessagePromptWindow>();
            window.titleContent = new GUIContent(title);
            window.summary = summary ?? string.Empty;
            window.message = initialMessage ?? string.Empty;
            window.submitLabel = string.IsNullOrWhiteSpace(submitLabel) ? "Apply" : submitLabel;
            window.onSubmit = onSubmit;
            window.minSize = new Vector2(520f, 340f);
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

            root.AddToClassList("unitgit-commit-message-prompt");

            var title = new Label(titleContent != null ? titleContent.text : "Commit Message");
            title.AddToClassList("unitgit-commit-message-title");
            root.Add(title);

            if (!string.IsNullOrWhiteSpace(summary))
            {
                var summaryLabel = new Label(summary);
                summaryLabel.AddToClassList("unitgit-commit-message-summary");
                root.Add(summaryLabel);
            }

            var field = new TextField
            {
                multiline = true,
                value = message
            };
            field.AddToClassList("unitgit-commit-message-field");
            field.RegisterValueChangedCallback(evt => message = evt.newValue);
            root.Add(field);

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-commit-message-actions");
            actions.Add(new Button(() => Close()) { text = "Cancel" });
            actions.Add(new Button(() =>
            {
                onSubmit?.Invoke(message);
                Close();
            })
            {
                text = submitLabel
            });
            root.Add(actions);

            field.schedule.Execute(() => field.Focus());
        }
    }
}
