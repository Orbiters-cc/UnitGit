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
