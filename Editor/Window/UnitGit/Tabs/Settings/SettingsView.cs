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
        private VisualElement BuildSettingsBody()
        {
            var root = new ScrollView();
            root.AddToClassList("unitgit-settings-root");

            var panel = new VisualElement();
            panel.AddToClassList("unitgit-settings-panel");
            panel.Add(BuildSectionHeader("Settings", "VRChat avatar uploads"));

            panel.Add(BuildSettingsToggle(
                "Create commit after avatar upload",
                UnitGitSettings.AvatarUploadCommitEnabled,
                value =>
                {
                    UnitGitSettings.AvatarUploadCommitEnabled = value;
                    RebuildContent();
                }));

            Toggle releaseRowToggle = BuildSettingsToggle(
                "Insert avatar upload release row",
                UnitGitSettings.AvatarUploadReleaseRowEnabled,
                value =>
                {
                    UnitGitSettings.AvatarUploadReleaseRowEnabled = value;
                    RebuildContent();
                });
            releaseRowToggle.SetEnabled(UnitGitSettings.AvatarUploadCommitEnabled);
            panel.Add(releaseRowToggle);

            var releaseDetail = new Label(UnitGitSettings.AvatarUploadCommitEnabled
                ? "Release rows are linked to the avatar upload commit."
                : "Release rows require the avatar upload commit setting.");
            releaseDetail.AddToClassList("unitgit-settings-detail");
            panel.Add(releaseDetail);

            root.Add(panel);
            return root;
        }

        private static Toggle BuildSettingsToggle(string label, bool value, Action<bool> changed)
        {
            var toggle = new Toggle(label)
            {
                value = value
            };
            toggle.AddToClassList("unitgit-settings-toggle");
            toggle.RegisterValueChangedCallback(evt => changed(evt.newValue));
            return toggle;
        }
    }
}
