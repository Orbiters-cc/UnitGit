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
        private sealed class UnitGitGitHubSetupWindow : EditorWindow
        {
            private UnitGitRemoteProvider provider = UnitGitRemoteProvider.GitHub;
            private string repositoryName;
            private string remoteName;
            private bool isPrivate = true;
            private Action<UnitGitRemoteProvider> onLogin;
            private Action<UnitGitRemoteProvider, string, string, bool> onCreateRemote;

            public static void Open(string defaultRepositoryName, Action<UnitGitRemoteProvider> onLogin, Action<UnitGitRemoteProvider, string, string, bool> onCreateRemote)
            {
                var window = CreateInstance<UnitGitGitHubSetupWindow>();
                window.titleContent = new GUIContent("Remote Setup");
                window.repositoryName = defaultRepositoryName;
                window.remoteName = "origin";
                window.onLogin = onLogin;
                window.onCreateRemote = onCreateRemote;
                window.minSize = new Vector2(420f, 260f);
                window.maxSize = new Vector2(620f, 320f);
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

                var title = new Label("Remote CLI setup");
                title.AddToClassList("unitgit-github-setup-title");
                rootVisualElement.Add(title);

                var body = new Label("Use GitHub CLI or GitLab CLI to sign in, create a remote repository, and set the local remote. This does not push commits.");
                body.AddToClassList("unitgit-github-setup-body");
                rootVisualElement.Add(body);

                var providerField = new EnumField("Provider", provider);
                providerField.AddToClassList("unitgit-github-setup-field");
                providerField.RegisterValueChangedCallback(evt => provider = (UnitGitRemoteProvider)evt.newValue);
                rootVisualElement.Add(providerField);

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
                actions.Add(BuildSetupButton("Login with Selected CLI", () => onLogin?.Invoke(provider)));
                actions.Add(BuildSetupButton("Create Remote", () =>
                {
                    onCreateRemote?.Invoke(provider, repositoryName, remoteName, isPrivate);
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
    }
}
