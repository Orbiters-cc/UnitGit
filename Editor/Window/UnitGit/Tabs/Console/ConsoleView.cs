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

        private TextField BuildSelectableConsoleLine(string text)
        {
            var field = new TextField();
            field.value = text;
            field.isReadOnly = true;
            field.AddToClassList("unitgit-console-line");
            return field;
        }
    }
}
